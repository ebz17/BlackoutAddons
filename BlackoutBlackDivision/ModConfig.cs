using System.Text.Json;

namespace BlackoutBlackDivision;

public class ModConfig
{
    /// <summary>Master switch for the whole mod.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Chance (0-100) that Wedge spawns on a Labs raid that rolled BLACKOUT.</summary>
    public double WedgeChance { get; set; } = 100;

    /// <summary>Minimum Black Division (blackDivIb) guards escorting Wedge.</summary>
    public int MinGuards { get; set; } = 4;

    /// <summary>Maximum Black Division (blackDivIb) guards escorting Wedge.</summary>
    public int MaxGuards { get; set; } = 4;

    /// <summary>Labs bot zones Wedge's group can spawn in (comma separated).</summary>
    public string SpawnZones { get; set; } = "BotZoneFloor2,BotZoneFloor1,BotZoneBasement";

    /// <summary>
    /// Blackout raids: Wedge's group starts in the basement generator room (BossZone = BotZoneBasement;
    /// the Blackout Power Restore client mod picks the exact spot). Overrides SpawnZones.
    /// </summary>
    public bool SpawnInGeneratorRoom { get; set; } = true;

    /// <summary>Blackout raids: Wedge always has Blackout's key on him (the locked door to the gate switch room).</summary>
    public bool WedgeCarriesKey { get; set; } = true;

    /// <summary>Blackout raids: chance (0-100) that each Black Division soldier (not Wedge) has a Labs keycard in his pockets.</summary>
    public double KeycardChance { get; set; } = 25;

    /// <summary>Which keycards they can carry (template id -> weight; higher = more common).</summary>
    public Dictionary<string, double> Keycards { get; set; } = new()
    {
        { "5c94bbff86f7747ee735c08f", 30 }, // Labs access keycard
        { "5c1d0dc586f7744baf2e7b79", 18 }, // green
        { "5c1d0d6d86f7744bb2683e1f", 18 }, // yellow
        { "5c1d0c5f86f7744bb2683cf0", 14 }, // blue
        { "5c1e495a86f7743109743dfb", 10 }, // violet
        { "5c1d0efb86f7744baf2e7b7b", 5 },  // red
        { "5c1d0f4986f7744bb01837fa", 5 },  // black
    };

    /// <summary>Blackout raids: Killa sneaks in through the sewage conduit (basement) at a random time.</summary>
    public bool KillaEnabled { get; set; } = true;

    /// <summary>"vengeful" = Vengeful Killa (Shadow of Tagilla's partner from the Labyrinth: 960 HP, RPK), "killa" = normal Killa.</summary>
    public string KillaType { get; set; } = "vengeful";

    /// <summary>Chance (0-100) that Killa comes in on a blackout raid.</summary>
    public double KillaChance { get; set; } = 100;

    /// <summary>
    /// Killa is the surprise final boss: he comes in once Black Division is wiped out (the Blackout Power
    /// Restore client mod holds him back; it also has a backup time). false = he comes at a random time (below).
    /// </summary>
    public bool KillaAfterBlackDivision { get; set; } = true;

    /// <summary>Only when KillaAfterBlackDivision is false: earliest / latest Killa arrives, in minutes after the raid starts.</summary>
    public double KillaMinMinutes { get; set; } = 5;
    public double KillaMaxMinutes { get; set; } = 20;

    /// <summary>
    /// Tagilla comes in with Killa (same sewage conduit) and guards that exit. Only when Killa is held back
    /// for after Black Division (killaAfterBlackDivision) - the client mod releases them together.
    /// </summary>
    public bool TagillaEnabled { get; set; } = true;

    /// <summary>"shadow" = Shadow of Tagilla (the Labyrinth boss: faster, quieter, much tougher), "tagilla" = normal Tagilla.</summary>
    public string TagillaType { get; set; } = "shadow";

    /// <summary>Chance (0-100) that Tagilla comes with Killa.</summary>
    public double TagillaChance { get; set; } = 100;

    /// <summary>Let the Wedge group spawn even if the Labs bot cap is already reached.</summary>
    public bool IgnoreMaxBots { get; set; } = true;

    /// <summary>
    /// If true, Wedge ONLY appears in blackout raids. Black Division's own random
    /// Labs Wedge spawn (labsStartChance) is suppressed on normal Labs raids.
    /// </summary>
    public bool WedgeOnlyDuringBlackout { get; set; } = false;

    /// <summary>Extra logging in the server console.</summary>
    public bool Debug { get; set; } = false;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string ConfigPath =>
        Path.Combine(Path.GetDirectoryName(typeof(ModConfig).Assembly.Location) ?? ".", "config.jsonc");

    /// <summary>Reads config.jsonc. Returns defaults (and an error message) if it can't be read.</summary>
    public static ModConfig Load(out string? error)
    {
        error = null;
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<ModConfig>(File.ReadAllText(ConfigPath), JsonOptions) ?? new ModConfig();
                cfg.Sanitise();
                return cfg;
            }
            error = $"config.jsonc not found at {ConfigPath}, using defaults.";
        }
        catch (Exception ex)
        {
            error = $"config.jsonc unreadable, using defaults: {ex.Message}";
        }

        var defaults = new ModConfig();
        defaults.Sanitise();
        return defaults;
    }

    private void Sanitise()
    {
        WedgeChance = Math.Clamp(WedgeChance, 0, 100);
        MinGuards = Math.Max(0, MinGuards);
        MaxGuards = Math.Max(MinGuards, MaxGuards);
        KeycardChance = Math.Clamp(KeycardChance, 0, 100);
        Keycards ??= new Dictionary<string, double>();
        KillaChance = Math.Clamp(KillaChance, 0, 100);
        KillaType = string.IsNullOrWhiteSpace(KillaType) ? "vengeful" : KillaType.Trim().ToLowerInvariant();
        TagillaChance = Math.Clamp(TagillaChance, 0, 100);
        TagillaType = string.IsNullOrWhiteSpace(TagillaType) ? "shadow" : TagillaType.Trim().ToLowerInvariant();
        KillaMinMinutes = Math.Clamp(KillaMinMinutes, 0, 120);
        KillaMaxMinutes = Math.Clamp(Math.Max(KillaMinMinutes, KillaMaxMinutes), 0, 120);
        if (string.IsNullOrWhiteSpace(SpawnZones))
        {
            SpawnZones = "BotZoneFloor2,BotZoneFloor1,BotZoneBasement";
        }
    }

    /// <summary>"4,5" style escort list; EFT picks one entry at random.</summary>
    public string EscortAmountString() =>
        string.Join(",", Enumerable.Range(MinGuards, MaxGuards - MinGuards + 1));
}
