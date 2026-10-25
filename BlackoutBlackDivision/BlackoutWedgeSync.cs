using BlackoutServer;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services.Bot;
using SPTarkov.Common.Models.Logging;

namespace BlackoutBlackDivision;

/// <summary>
/// Makes the Labs boss-spawn table match Blackout's roll for the upcoming raid.
///
/// Blackout rolls "blackout / normal" on server start and after every raid end,
/// so by the time a Labs raid is requested the answer is already known
/// (BlackoutSpawnController.CurrentRaidBlackout). Black Division rebuilds its own
/// Labs Wedge spawn (labsStartChance) on server start and after every raid end too.
///
/// Right before SPT clones the Labs data for the raid, this class:
///   BLACKOUT raid -> swaps Black Division's random Wedge entry for a guaranteed
///                    Wedge + 4-5 blackDivIb guards (configurable).
///   normal raid   -> leaves Black Division alone (or removes Wedge if
///                    wedgeOnlyDuringBlackout is on).
/// Every change is undone at the next raid start, so nothing leaks between raids.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class BlackoutWedgeSync(
    BlackoutSpawnController blackout,
    LocationTable locations,
    BotTable bots,
    BotLootCacheService lootCache,
    ISptLogger<BlackoutWedgeSync> logger)
{
    public const string Tag = "blackout-blackdivision-compat";
    private const string LogPrefix = "[Blackout x BlackDiv]";

    private readonly object _lock = new();

    // Blackout's key (opens the locked medical corridor door to the admin room with the gate switch)
    private const string BlackoutKeyTpl = "6a33c17933cff6b88c08902e";
    private GenerationData? _wedgeSpecialBackup;
    private bool _wedgeKeyApplied;
    private readonly List<(GenerationWeightingItems Items, GenerationData Backup)> _keycardBackups = new();
    private List<BossLocationSpawn> _stashed = new();

    public static bool IsWedge(BossLocationSpawn? s) =>
        s?.BossName != null && s.BossName.Equals("bossWedge", StringComparison.OrdinalIgnoreCase);

    public const string KillaTag = "blackout-killa";
    public const string TagillaTag = "blackout-tagilla";
    private static readonly Random Rng = new();

    public static bool IsOurs(BossLocationSpawn? s) => s?.SptId == Tag || s?.SptId == KillaTag || s?.SptId == TagillaTag;

    public void Apply(string? location)
    {
        if (location != null && !location.Equals("laboratory", StringComparison.OrdinalIgnoreCase))
        {
            // another map: just make sure nobody still carries the blackout-only loot
            lock (_lock)
            {
                TakeTheKeyBack();
                TakeTheKeycardsBack();
            }
            return;
        }

        lock (_lock)
        {
            var cfg = ModConfig.Load(out var cfgError);
            if (cfgError != null)
            {
                logger.Warning($"{LogPrefix} {cfgError}");
            }

            var spawns = locations.Laboratory?.Base?.BossLocationSpawn;
            if (spawns == null)
            {
                logger.Error($"{LogPrefix} Labs boss spawn table unavailable - nothing changed.");
                return;
            }

            RestorePrevious(spawns, cfg);

            if (!cfg.Enabled)
            {
                Debug(cfg, "disabled in config.jsonc - Labs left untouched.");
                return;
            }

            var isBlackout = blackout.CurrentRaidBlackout;

            if (isBlackout)
            {
                var template = spawns.FirstOrDefault(IsWedge);
                _stashed = spawns.Where(IsWedge).ToList();
                spawns.RemoveAll(IsWedge);

                var wedge = BuildBlackoutWedge(template, cfg);
                spawns.Add(wedge);

                logger.Success(
                    $"{LogPrefix} BLACKOUT raid - Wedge ({cfg.WedgeChance}% chance) + {cfg.MinGuards}-{cfg.MaxGuards} Black Division guards added to Labs.");
                if (cfg.WedgeCarriesKey)
                {
                    GiveWedgeTheKey();
                }
                GiveKeycards(cfg);
                if (cfg.KillaEnabled && cfg.KillaChance > 0)
                {
                    var killa = BuildKilla(cfg);
                    spawns.Add(killa);
                    string kName = killa.BossName == VengefulKilla ? "Vengeful Killa" : "Killa";
                    logger.Info(cfg.KillaAfterBlackDivision
                        ? $"{LogPrefix} {kName} ({cfg.KillaChance}% chance) breaks in through the sewage conduit once Black Division is wiped out."
                        : $"{LogPrefix} {kName} ({cfg.KillaChance}% chance) sneaks in through the sewage conduit at {killa.Time / 60:0.0} min.");
                    if (cfg.KillaAfterBlackDivision && cfg.TagillaEnabled && cfg.TagillaChance > 0)
                    {
                        var tag = BuildTagilla(cfg);
                        spawns.Add(tag);
                        logger.Info($"{LogPrefix} {(tag.BossName == ShadowTagilla ? "Shadow of Tagilla" : "Tagilla")} ({cfg.TagillaChance}% chance) comes in with Killa and guards the sewage exit.");
                    }
                }
                Debug(cfg, $"zones={wedge.BossZone}, escorts={wedge.BossEscortAmount}, ignoreMaxBots={wedge.IgnoreMaxBots}, " +
                           $"replaced {_stashed.Count} Black Division Wedge entr{(_stashed.Count == 1 ? "y" : "ies")}.");
            }
            else if (cfg.WedgeOnlyDuringBlackout)
            {
                _stashed = spawns.Where(IsWedge).ToList();
                spawns.RemoveAll(IsWedge);
                logger.Info($"{LogPrefix} Normal Labs raid - Wedge suppressed (wedgeOnlyDuringBlackout = true).");
            }
            else
            {
                logger.Info($"{LogPrefix} Normal Labs raid - Black Division spawns left as rolled by Black Division.");
            }
        }
    }

    /// <summary>Wedge always carries Blackout's key (as his one "special" loot item) in blackout raids.</summary>
    private void GiveWedgeTheKey()
    {
        var wedge = FindWedgeType();
        var items = wedge?.BotGeneration?.Items;
        if (items == null)
        {
            logger.Warning($"{LogPrefix} Wedge bot type not found - he won't carry the Blackout key.");
            return;
        }
        _wedgeSpecialBackup = items.SpecialItems;
        items.SpecialItems = new GenerationData
        {
            Weights = new Dictionary<double, double> { { 1, 1 } },                              // exactly one special item...
            Whitelist = new Dictionary<MongoId, double> { { new MongoId(BlackoutKeyTpl), 1 } }, // ...and it's the key
        };
        _wedgeKeyApplied = true;
        lootCache.ClearCache();
        logger.Info($"{LogPrefix} Wedge carries the Blackout key this raid.");
    }

    private void TakeTheKeyBack()
    {
        if (!_wedgeKeyApplied) return;
        var items = FindWedgeType()?.BotGeneration?.Items;
        if (items != null && _wedgeSpecialBackup != null) items.SpecialItems = _wedgeSpecialBackup;
        _wedgeKeyApplied = false;
        _wedgeSpecialBackup = null;
        lootCache.ClearCache();
    }

    /// <summary>
    /// Blackout raids: each Black Division soldier (guards, second wave - not Wedge, he has the key) has a
    /// chance of one Labs keycard in his pockets.
    /// </summary>
    private void GiveKeycards(ModConfig cfg)
    {
        if (cfg.KeycardChance <= 0) return;
        var whitelist = new Dictionary<MongoId, double>();
        foreach (var kv in cfg.Keycards)
        {
            if (kv.Value <= 0 || string.IsNullOrWhiteSpace(kv.Key)) continue;
            try
            {
                whitelist[new MongoId(kv.Key.Trim())] = kv.Value;
            }
            catch (Exception)
            {
                logger.Warning($"{LogPrefix} keycards: '{kv.Key}' is not a valid item id - skipped.");
            }
        }
        if (whitelist.Count == 0) return;
        var weights = cfg.KeycardChance >= 100
            ? new Dictionary<double, double> { { 1, 1 } }
            : new Dictionary<double, double> { { 0, 100 - cfg.KeycardChance }, { 1, cfg.KeycardChance } };

        var names = new List<string>();
        foreach (var kv in bots?.Types ?? new Dictionary<string, BotType>())
        {
            if (!kv.Key.StartsWith("blackdiv", StringComparison.OrdinalIgnoreCase)) continue;
            var items = kv.Value?.BotGeneration?.Items;
            if (items == null || _keycardBackups.Any(b => ReferenceEquals(b.Items, items))) continue;   // shared template: once
            _keycardBackups.Add((items, items.SpecialItems));
            items.SpecialItems = new GenerationData { Weights = weights, Whitelist = whitelist };
            names.Add(kv.Key);
        }
        if (names.Count == 0) return;
        lootCache.ClearCache();
        logger.Info($"{LogPrefix} Black Division ({string.Join(", ", names)}): {cfg.KeycardChance}% chance of a Labs keycard each this raid.");
    }

    private void TakeTheKeycardsBack()
    {
        if (_keycardBackups.Count == 0) return;
        for (int i = _keycardBackups.Count - 1; i >= 0; i--) _keycardBackups[i].Items.SpecialItems = _keycardBackups[i].Backup;
        _keycardBackups.Clear();
        lootCache.ClearCache();
    }

    private BotType? FindWedgeType()
    {
        var types = bots?.Types;
        if (types == null) return null;
        foreach (var kv in types)
        {
            if (kv.Key.Equals("bosswedge", StringComparison.OrdinalIgnoreCase)) return kv.Value;
        }
        return null;
    }

    /// <summary>Undo whatever this mod did for the previous Labs raid.</summary>
    private void RestorePrevious(List<BossLocationSpawn> spawns, ModConfig cfg)
    {
        TakeTheKeyBack();
        TakeTheKeycardsBack();
        var removedOurs = spawns.RemoveAll(IsOurs);

        if (_stashed.Count > 0)
        {
            // If Black Division already rebuilt its Wedge entry since (it does on raid end),
            // our stashed copy is stale - keep Black Division's fresh roll instead.
            if (!spawns.Any(IsWedge))
            {
                spawns.AddRange(_stashed);
                Debug(cfg, $"restored {_stashed.Count} stashed Black Division Wedge entr{(_stashed.Count == 1 ? "y" : "ies")}.");
            }

            _stashed = new List<BossLocationSpawn>();
        }

        if (removedOurs > 0)
        {
            Debug(cfg, "removed previous blackout Wedge entry.");
        }
    }

    /// <summary>Killa, alone, in the basement (the client mod puts him at the sewage conduit), at a random time.</summary>
    private const string VengefulKilla = "bossKillaAgro";

    private BossLocationSpawn BuildKilla(ModConfig cfg)
    {
        bool vengeful = cfg.KillaType != "killa";
        if (vengeful && bots?.Types != null && !bots.Types.Keys.Any(k => k.Equals("bosskillaagro", StringComparison.OrdinalIgnoreCase)))
        {
            logger.Warning($"{LogPrefix} Vengeful Killa's bot type isn't in this SPT - using normal Killa.");
            vengeful = false;
        }
        string boss = vengeful ? VengefulKilla : "bossKilla";
        var template = vengeful ? null : locations.Interchange?.Base?.BossLocationSpawn?.FirstOrDefault(s =>
            s?.BossName != null && s.BossName.Equals("bossKilla", StringComparison.OrdinalIgnoreCase));
        var baseSpawn = template ?? new BossLocationSpawn
        {
            BossName = boss,
            BossDifficulty = "normal",
            BossEscortDifficulty = "normal",
            BossEscortType = "followerBully",
            IsBossPlayer = false,
            IsRandomTimeSpawn = false,
            Supports = null!,
        };
        double minutes = cfg.KillaMinMinutes + Rng.NextDouble() * (cfg.KillaMaxMinutes - cfg.KillaMinMinutes);
        return baseSpawn with
        {
            SptId = KillaTag,
            BossName = boss,
            BossChance = cfg.KillaChance,
            BossEscortAmount = "0",
            BossZone = "BotZoneBasement",
            Time = cfg.KillaAfterBlackDivision ? -1 : Math.Round(minutes * 60),   // -1: at raid start, the client mod holds him back
            Delay = 0,
            IsRandomTimeSpawn = false,
            TriggerId = "",
            TriggerName = "",
            Supports = null!,
            ForceSpawn = true,
            IgnoreMaxBots = true,
            SpawnMode = new List<string> { "regular", "pve" },
        };
    }

    /// <summary>Tagilla, alone, held back at raid start (Time -1): the client mod releases him with Killa at the sewage conduit.</summary>
    private const string ShadowTagilla = "bossTagillaAgro";

    private BossLocationSpawn BuildTagilla(ModConfig cfg)
    {
        bool shadow = cfg.TagillaType != "tagilla";
        string boss = shadow ? ShadowTagilla : "bossTagilla";
        if (shadow && bots?.Types != null && !bots.Types.Keys.Any(k => k.Equals("bosstagillaagro", StringComparison.OrdinalIgnoreCase)))
        {
            logger.Warning($"{LogPrefix} Shadow of Tagilla's bot type isn't in this SPT - using normal Tagilla.");
            shadow = false;
            boss = "bossTagilla";
        }
        var template = shadow ? null : locations.Factory4Day?.Base?.BossLocationSpawn?.FirstOrDefault(s =>
            s?.BossName != null && s.BossName.Equals("bossTagilla", StringComparison.OrdinalIgnoreCase));
        var baseSpawn = template ?? new BossLocationSpawn
        {
            BossName = boss,
            BossDifficulty = "normal",
            BossEscortDifficulty = "normal",
            BossEscortType = "followerBully",
            IsBossPlayer = false,
            IsRandomTimeSpawn = false,
            Supports = null!,
        };
        return baseSpawn with
        {
            SptId = TagillaTag,
            BossName = boss,
            BossChance = cfg.TagillaChance,
            BossEscortAmount = "0",
            BossZone = "BotZoneBasement",
            Time = -1,
            Delay = 0,
            IsRandomTimeSpawn = false,
            TriggerId = "",
            TriggerName = "",
            Supports = null!,
            ForceSpawn = true,
            IgnoreMaxBots = true,
            SpawnMode = new List<string> { "regular", "pve" },
        };
    }

    private static BossLocationSpawn BuildBlackoutWedge(BossLocationSpawn? blackDivTemplate, ModConfig cfg)
    {
        // Start from Black Division's own Wedge entry when it exists, so any fields
        // its author relies on carry over; otherwise mirror its settings by hand.
        var baseSpawn = blackDivTemplate ?? new BossLocationSpawn
        {
            BossName = "bossWedge",
            BossDifficulty = "normal",
            BossEscortDifficulty = "normal",
            BossEscortType = "blackDivIb",
            IsBossPlayer = false,
            IsRandomTimeSpawn = false,
            Supports = null!,
            TriggerId = "hunt",
        };

        return baseSpawn with
        {
            SptId = Tag,
            BossName = "bossWedge",
            BossEscortType = "blackDivIb",
            BossChance = cfg.WedgeChance,
            BossEscortAmount = cfg.EscortAmountString(),
            BossZone = cfg.SpawnInGeneratorRoom ? "BotZoneBasement" : cfg.SpawnZones,
            ForceSpawn = true,
            IgnoreMaxBots = cfg.IgnoreMaxBots,
            Time = -1,
            Delay = 0,
            SpawnMode = new List<string> { "regular", "pve" },
        };
    }

    private void Debug(ModConfig cfg, string message)
    {
        if (cfg.Debug)
        {
            logger.Info($"{LogPrefix} {message}");
        }
    }
}
