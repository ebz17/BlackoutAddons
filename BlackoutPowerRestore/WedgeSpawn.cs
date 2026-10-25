using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Logging;
using EFT;
using EFT.Game.Spawning;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Black Division caused the blackout - so on blackout raids Wedge and his guards appear in the
    /// basement generator room right after the power cuts. Every other bot spawns as normal.
    ///
    /// The server mod sends the blackout Wedge wave with BossZone = "BotZoneBasement". When the game
    /// picks spawn points for that wave, we hand it points inside the generator room instead.
    /// </summary>
    internal static class WedgeSpawn
    {
        public const string Zone = "BotZoneBasement";

        // set by the plugin
        public static Func<bool> Enabled = () => false;
        public static Func<int> Chance = () => 100;
        public static Func<Vector3?> HandSpot = () => null;   // placed with the key (player position)
        public static Func<List<Vector3>> RoomHints = () => new List<Vector3>();   // generators + switch
        public static Func<bool> PowerCut = () => true;      // hold the wave until this is true
        public static Func<bool> HoldUntilCut = () => false; // "after the blackout" mode
        public static Func<bool> PlayerNearRoom = () => false;
        public static Func<Vector3?> PlayerPos = () => null;
        private static bool _far;           // you're near the generator room: they come in far away from you
        public static Func<bool> KillaEnabled = () => false;
        public static Func<Vector3?> KillaSpot = () => null;  // sewage conduit (or the spot set by hand)
        private static bool _killa;         // the wave picking points right now is Killa's
        // Killa held back (surprise final boss) until the plugin releases him
        private static object _killaSpawner, _killaParams;
        private static BossLocationSpawn _killaWave;
        private static bool _releasingKilla;
        public static bool KillaHeld => _killaWave != null || _tagWave != null;
        // Tagilla (server mod) comes in right behind Killa and guards the sewage exit
        public static Func<bool> TagillaEnabled = () => true;
        private static object _tagSpawner, _tagParams;
        private static BossLocationSpawn _tagWave;
        private static float _tagReleaseAt = -1f;
        private static bool _tagilla;       // the wave picking points right now is Tagilla's

        /// <summary>Our wave has started spawning (time), and how many bots it brings.</summary>
        public static float WaveSeenAt { get; private set; } = -1f;
        public static int Expected { get; private set; }

        // wave held back until the blackout
        private static object _heldSpawner, _heldParams;
        private static BossLocationSpawn _heldWave;
        private static MethodInfo _spawnMethod;

        // guards' profiles are requested at raid start together with Wedge's (the game normally asks only
        // after Wedge is in, and then they wait behind every other bot being generated - minutes)
        private static Task<BotCreationData> _guardsTask;
        private static BossLocationSpawn _guardsWave;
        private static float _guardsRequestedAt;
        // Wedge waits (his profile already made) until the guards' profiles are ready too - they arrive together
        private static MethodInfo _trySpawn;
        private static BotBossSpawn _pendingSpawner;
        private static object[] _pendingArgs;
        private static bool _trySpawnBypass;
        private static bool _releasing, _noRoom;
        // the game only sends the guards once Wedge's AI is "active" - that can take a minute or more.
        // If it hasn't happened 8 s after Wedge went in, we send them ourselves (same profiles, same spot).
        private static BotZone _lastZone;
        private static List<ISpawnPoint> _lastPoints;
        private static float _bossSpawnAt = -1f;
        private static bool _guardsSent, _guardsRetried;
        private static BotBossSpawn _guardsBoss;
        private static BotSpawnParams _guardsParams;

        /// <summary>The server sometimes never answers the guards' profile request (busy making other bots): ask once more.</summary>
        private static void RetryGuardsIfStuck()
        {
            if (_guardsRetried || _guardsSent || _guardsTask == null || _guardsTask.IsCompleted || _guardsBoss == null || _guardsWave == null) return;
            if (Time.time - _guardsRequestedAt < 75f) return;
            _guardsRetried = true;
            try
            {
                _guardsTask = BotCreationData.Create(
                    new GetProfileDataParams(EPlayerSide.Savage, _guardsWave.EscortType, _guardsWave.EscortDif, _guardsWave.Time, _guardsParams),
                    _guardsBoss._botCreator, _guardsWave.EscortCount, _guardsBoss._spawner);
                _guardsRequestedAt = Time.time;
                _log.LogWarning("[PowerRestore] Black Division guard profiles didn't arrive in 75 s - asking again.");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] guard profile retry failed: {ex.Message}");
            }
        }
        public static bool Holding => _heldWave != null;

        private static ManualLogSource _log;
        private static readonly System.Random Rng = new System.Random();
        private static int _wanted = -1;    // >= 0 while our Wedge wave is picking spawn points
        private static readonly List<GameObject> Markers = new List<GameObject>();
        private static readonly List<Vector3> Used = new List<Vector3>();
        private static float _usedAt = -1000f;

        /// <summary>A bot standing on one of our spawn spots shortly after the wave = Black Division.</summary>
        public static bool IsOurSpawn(Vector3 p)
        {
            if (Time.time - _usedAt > 90f) return false;
            foreach (var u in Used)
            {
                if ((u - p).sqrMagnitude < 3.5f * 3.5f) return true;
            }
            return false;
        }

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            var trySpawn = AccessTools.Method(typeof(BotBossSpawn), "TrySpawn");
            _trySpawn = trySpawn;
            var select = AccessTools.GetDeclaredMethods(typeof(SpawnSystem)).FirstOrDefault(m =>
                m.Name == "SelectAISpawnPoints" && m.GetParameters().Length == 6 && m.GetParameters()[3].ParameterType == typeof(float));
            if (trySpawn == null || select == null)
            {
                log.LogWarning($"[PowerRestore] Black Division spawn hook not found (TrySpawn={trySpawn != null}, Select={select != null}) - they spawn at random.");
                return;
            }
            harmony.Patch(trySpawn,
                prefix: new HarmonyMethod(typeof(WedgeSpawn), nameof(TrySpawnPrefix)),
                finalizer: new HarmonyMethod(typeof(WedgeSpawn), nameof(TrySpawnFinalizer)));
            harmony.Patch(select, postfix: new HarmonyMethod(typeof(WedgeSpawn), nameof(SelectPostfix)));
            _spawnMethod = AccessTools.GetDeclaredMethods(typeof(BotBossSpawn)).FirstOrDefault(m =>
                m.Name == "Spawn" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(BossLocationSpawn));
            if (_spawnMethod != null)
            {
                harmony.Patch(_spawnMethod, prefix: new HarmonyMethod(typeof(WedgeSpawn), nameof(SpawnPrefix)));
            }
            else
            {
                log.LogWarning("[PowerRestore] Boss Spawn() not found - Black Division spawns at raid start instead of at the power cut.");
            }
            var supports = AccessTools.Method(typeof(BotBossSpawn), "SpawnBossSupports");
            if (supports != null)
            {
                harmony.Patch(supports, prefix: new HarmonyMethod(typeof(WedgeSpawn), nameof(SupportsPrefix)));
            }
            else
            {
                log.LogWarning("[PowerRestore] SpawnBossSupports not found - Black Division guards arrive at the game's own pace.");
            }
            log.LogInfo("[PowerRestore] Black Division generator-room spawn hook installed.");
        }

        private static bool SupportsPrefix(BotBossSpawn __instance, BotZone zone, BossLocationSpawn wave, List<ISpawnPoint> pointsToSpawn,
            bool forceSpawn, ref Task __result)
        {
            if (_guardsSent && ReferenceEquals(wave, _guardsWave))
            {
                __result = Task.CompletedTask;     // already sent them ourselves - no second set
                return false;
            }
            if (_guardsTask == null || !ReferenceEquals(wave, _guardsWave)) return true;
            _guardsSent = true;
            var task = _guardsTask;
            _guardsTask = null;
            __result = SpawnPrefetched(__instance, task, wave, zone, pointsToSpawn, forceSpawn);
            return false;
        }

        private static async Task SpawnPrefetched(BotBossSpawn boss, Task<BotCreationData> task, BossLocationSpawn wave, BotZone zone,
            List<ISpawnPoint> points, bool forceSpawn)
        {
            BotCreationData data = null;
            try
            {
                data = await task;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] early guard profiles failed ({ex.Message}) - requesting them again.");
            }
            if (data == null)
            {
                data = await BotCreationData.Create(new GetProfileDataParams(EPlayerSide.Savage, wave.EscortType, wave.EscortDif, wave.Time, null),
                    boss._botCreator, wave.EscortCount, boss._spawner);
            }
            _log.LogInfo($"[PowerRestore] Black Division guards ready ({data.Count}) - spawning with Wedge.");
            boss._spawner.TryToSpawnInZoneAndDelay(zone, data, withCheckMinMax: false, newWave: true, points, forceSpawn);
        }

        /// <summary>Killa never spawns in Labs normally: a Killa wave in the basement zone is the server mod's blackout Killa.</summary>
        private static bool IsKillaWave(BossLocationSpawn wave) =>
            wave != null && (string.Equals(wave.BossName, "bossKilla", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(wave.BossName, "bossKillaAgro", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(wave.BossZone?.Trim(), Zone, StringComparison.OrdinalIgnoreCase);

        private static bool IsTagillaWave(BossLocationSpawn wave) =>
            wave != null && (string.Equals(wave.BossName, "bossTagilla", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(wave.BossName, "bossTagillaAgro", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(wave.BossZone?.Trim(), Zone, StringComparison.OrdinalIgnoreCase);

        private static bool IsOurWave(BossLocationSpawn wave) =>
            wave != null && string.Equals(wave.BossName, "bossWedge", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(wave.BossZone?.Trim(), Zone, StringComparison.OrdinalIgnoreCase);   // only the blackout wave uses this zone alone

        /// <summary>Raid start: keep the blackout Wedge wave back until the power actually cuts.</summary>
        private static bool SpawnPrefix(BotBossSpawn __instance, BossLocationSpawn wave, object spawnParams, ref Task __result)
        {
            try
            {
                // server sends the surprise Killa at raid start (Time -1): keep him back until Black Division is gone
                if (IsKillaWave(wave) && wave.Time < 0f && KillaEnabled() && !_releasingKilla)
                {
                    if (_killaWave == null)
                    {
                        _killaSpawner = __instance;
                        _killaWave = wave;
                        _killaParams = spawnParams;
                        _log.LogInfo("[PowerRestore] Killa held back - he breaks in once Black Division is wiped out.");
                    }
                    __result = Task.CompletedTask;
                    return false;
                }
                if (IsTagillaWave(wave) && wave.Time < 0f && KillaEnabled() && !_releasingKilla)
                {
                    if (!TagillaEnabled())
                    {
                        _log.LogInfo("[PowerRestore] Tagilla turned off in the client config - he stays out.");
                    }
                    else if (_tagWave == null)
                    {
                        _tagSpawner = __instance;
                        _tagWave = wave;
                        _tagParams = spawnParams;
                        _log.LogInfo("[PowerRestore] Tagilla held back - he comes in behind Killa and guards the sewage exit.");
                    }
                    __result = Task.CompletedTask;
                    return false;
                }
                if (IsOurWave(wave) && Enabled() && _guardsTask == null && wave.GetEscors() == null && wave.EscortCount > 0)
                {
                    _guardsWave = wave;
                    _guardsBoss = __instance;
                    _guardsParams = spawnParams as BotSpawnParams;
                    _guardsTask = BotCreationData.Create(
                        new GetProfileDataParams(EPlayerSide.Savage, wave.EscortType, wave.EscortDif, wave.Time, spawnParams as BotSpawnParams),
                        __instance._botCreator, wave.EscortCount, __instance._spawner);
                    _guardsRequestedAt = Time.time;
                    _log.LogInfo($"[PowerRestore] Black Division: {wave.EscortCount} guard profiles requested now, together with Wedge.");
                }
                if (IsOurWave(wave) && Enabled() && WaveSeenAt < 0f && (_releasing || !HoldUntilCut() || PowerCut()))
                {
                    WaveSeenAt = Time.time;
                    Expected = 1 + Math.Max(0, wave.EscortCount);
                }
                if (_releasing || !IsOurWave(wave) || !Enabled() || !HoldUntilCut() || PowerCut()) return true;
                if (_heldWave == null)
                {
                    _heldSpawner = __instance;
                    _heldWave = wave;
                    _heldParams = spawnParams;
                    _log.LogInfo("[PowerRestore] Black Division held back - they arrive when the power cuts.");
                }
                __result = Task.CompletedTask;
                return false;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] Black Division hold failed ({ex.Message}) - normal spawn.");
                return true;
            }
        }

        /// <summary>Power is out: send them in. useRoom = false spawns them elsewhere in the basement (player too close).</summary>
        public static void Release(bool useRoom)
        {
            if (_heldWave == null) return;
            var spawner = _heldSpawner;
            var wave = _heldWave;
            var prms = _heldParams;
            _heldSpawner = _heldParams = null;
            _heldWave = null;
            _noRoom = !useRoom;
            _releasing = true;
            try
            {
                var task = _spawnMethod.Invoke(spawner, new object[] { wave, prms, null }) as Task;
                task?.ContinueWith(t => _log.LogWarning($"[PowerRestore] Black Division spawn error: {t.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
                _log.LogInfo($"[PowerRestore] Power is out - Black Division moving in ({(useRoom ? "generator room" : "basement, you were too close to the generator room")}).");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] Black Division release failed: {ex.InnerException?.Message ?? ex.Message}");
            }
            finally
            {
                _releasing = false;
            }
        }

        /// <summary>Let Killa in (through the sewage conduit - TrySpawn/Select pick the spot).</summary>
        public static void ReleaseKilla(string why)
        {
            if (_tagWave != null && _tagReleaseAt < 0f) _tagReleaseAt = Time.time + (_killaWave != null ? 8f : 0f);   // right behind him
            if (_killaWave == null) return;
            var spawner = _killaSpawner;
            var wave = _killaWave;
            var prms = _killaParams;
            _killaSpawner = _killaParams = null;
            _killaWave = null;
            _releasingKilla = true;
            try
            {
                var task = _spawnMethod.Invoke(spawner, new object[] { wave, prms, null }) as Task;
                task?.ContinueWith(t => _log.LogWarning($"[PowerRestore] Killa spawn error: {t.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
                _log.LogInfo($"[PowerRestore] Killa is coming ({why}).");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] Killa release failed: {ex.InnerException?.Message ?? ex.Message}");
            }
            finally
            {
                _releasingKilla = false;
            }
        }

        private static void ReleaseTagilla()
        {
            if (_tagWave == null || _tagReleaseAt < 0f || Time.time < _tagReleaseAt) return;
            var spawner = _tagSpawner;
            var wave = _tagWave;
            var prms = _tagParams;
            _tagSpawner = _tagParams = null;
            _tagWave = null;
            _releasingKilla = true;
            try
            {
                var task = _spawnMethod.Invoke(spawner, new object[] { wave, prms, null }) as Task;
                task?.ContinueWith(t => _log.LogWarning($"[PowerRestore] Tagilla spawn error: {t.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
                _log.LogInfo("[PowerRestore] Tagilla is coming in behind Killa.");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] Tagilla release failed: {ex.InnerException?.Message ?? ex.Message}");
            }
            finally
            {
                _releasingKilla = false;
            }
        }

        /// <summary>Middle of the generator room (for the "player too close" check).</summary>
        public static Vector3? RoomCenter()
        {
            var hand = HandSpot();
            if (hand.HasValue) return hand.Value;
            var hints = RoomHints() ?? new List<Vector3>();
            if (hints.Count == 0) return null;
            return hints.Aggregate(Vector3.zero, (a, b) => a + b) / hints.Count;
        }

        private static void CapturePoints(BotZone zone, List<ISpawnPoint> points)
        {
            if (_killa) return;
            _lastZone = zone;
            _lastPoints = points != null ? new List<ISpawnPoint>(points) : null;
        }

        /// <summary>Wedge is in but the game still hasn't sent his guards: send them now.</summary>
        private static void SendGuardsIfLate()
        {
            if (_guardsSent || _bossSpawnAt < 0f || Time.time - _bossSpawnAt < 8f || _guardsTask == null || !_guardsTask.IsCompleted) return;
            _guardsSent = true;
            try
            {
                var data = _guardsTask.Status == TaskStatus.RanToCompletion ? _guardsTask.Result : null;
                var spawner = Comfort.Common.Singleton<IBotGame>.Instance?.BotsController?.BotSpawner;
                if (data == null || spawner == null || _lastZone == null)
                {
                    _log.LogInfo($"[PowerRestore] Black Division guards: can't send them myself ({(data == null ? "no profiles" : spawner == null ? "no spawner" : "no spawn zone")}) - the game sends them.");
                    _guardsSent = false;      // leave it to the game
                    _bossSpawnAt = float.MaxValue;
                    return;
                }
                spawner.TryToSpawnInZoneAndDelay(_lastZone, data, withCheckMinMax: false, newWave: true, _lastPoints, forcedSpawn: true);
                _log.LogInfo($"[PowerRestore] Black Division guards ({data.Count}) sent in right after Wedge (the game was holding them back).");
            }
            catch (Exception ex)
            {
                _guardsSent = false;
                _bossSpawnAt = float.MaxValue;
                _log.LogWarning($"[PowerRestore] sending the guards failed ({ex.Message}) - the game sends them.");
            }
        }

        /// <summary>Call every frame: lets Wedge in once his guards are ready (or after 60 s at most).</summary>
        public static void Tick()
        {
            ReleaseTagilla();
            RetryGuardsIfStuck();
            SendGuardsIfLate();
            if (_pendingArgs == null) return;
            bool ready = _guardsTask == null || _guardsTask.IsCompleted || Time.time - _guardsRequestedAt > 60f;
            if (!ready) return;
            var spawner = _pendingSpawner;
            var args = _pendingArgs;
            _pendingSpawner = null;
            _pendingArgs = null;
            _log.LogInfo("[PowerRestore] Black Division guards are ready - Wedge and his guards move in together.");
            _trySpawnBypass = true;
            try
            {
                _trySpawn.Invoke(spawner, args);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] Black Division spawn after waiting failed: {ex.InnerException?.Message ?? ex.Message}");
            }
            finally
            {
                _trySpawnBypass = false;
            }
        }

        private static bool TrySpawnPrefix(BotBossSpawn __instance, BossLocationSpawn wave, object[] __args, ref bool __result)
        {
            _wanted = -1;
            _killa = _tagilla = false;
            try
            {
                if (IsKillaWave(wave) || IsTagillaWave(wave))
                {
                    if (KillaEnabled() && KillaSpot().HasValue)
                    {
                        _killa = true;
                        _tagilla = IsTagillaWave(wave);
                        _wanted = 1;
                    }
                    return true;
                }
                if (!IsOurWave(wave)) return true;
                if (!_trySpawnBypass && Enabled() && _guardsTask != null && !_guardsTask.IsCompleted && Time.time - _guardsRequestedAt < 60f)
                {
                    if (_pendingArgs == null)
                    {
                        _pendingSpawner = __instance;
                        _pendingArgs = (object[])__args.Clone();
                        _log.LogInfo("[PowerRestore] Wedge is ready - waiting for his guards so they arrive together.");
                    }
                    __result = true;        // "spawned" as far as the game knows - no retry queue
                    return false;
                }
                if (Enabled() && _bossSpawnAt < 0f) _bossSpawnAt = Time.time;
                return TrySpawnPrefixInner(wave);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] Black Division spawn check failed: {ex.Message}");
                return true;
            }
        }

        private static bool TrySpawnPrefixInner(BossLocationSpawn wave)
        {
            try
            {
                if (!Enabled() || _noRoom) return true;
                _far = PlayerNearRoom();
                if (_far)
                {
                    _log.LogInfo("[PowerRestore] Black Division: you are near the generator room - they come in somewhere well away from you.");
                    _wanted = 1 + Math.Max(0, wave.EscortCount);
                    return true;
                }
                if (Rng.Next(100) >= Chance())
                {
                    _log.LogInfo("[PowerRestore] Black Division: random spawn this time (generator room chance missed).");
                    return true;
                }
                _wanted = 1 + Math.Max(0, wave.EscortCount);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] Black Division spawn check failed: {ex.Message}");
            }
            return true;
        }

        private static Exception TrySpawnFinalizer(Exception __exception)
        {
            _wanted = -1;
            _killa = _tagilla = false;
            return __exception;
        }

        private static void SelectPostfix(BotZone zone, ref List<ISpawnPoint> __result)
        {
            if (_wanted < 0) return;
            try { CapturePoints(zone, __result); } catch (Exception) { }
            int count = _wanted;
            _wanted = -1;     // once per wave
            if (_killa)
            {
                _killa = false;
                bool tag = _tagilla;
                _tagilla = false;
                KillaPoints(zone, ref __result, tag);
                return;
            }
            try
            {
                var center = _far ? FarCenter() : RoomCenter();
                var spots = center.HasValue ? FindSpots(count, center.Value) : new List<Vector3>();
                if (spots.Count == 0)
                {
                    _log.LogWarning($"[PowerRestore] Black Division: no free floor found {(_far ? "away from you" : "in the generator room")} - random spawn.");
                    return;
                }
                var points = new List<ISpawnPoint>();
                for (int i = 0; i < count; i++) points.Add(MakePoint(spots[i % spots.Count], zone, i));
                __result = points;
                CapturePoints(zone, points);
                Used.Clear();
                Used.AddRange(spots);
                _usedAt = Time.time;
                _log.LogInfo($"[PowerRestore] Black Division ({count}) spawning {(_far ? "away from you" : "in the generator room")} at {spots[0]}.");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] Black Division generator-room spawn failed ({ex.Message}) - random spawn.");
            }
        }

        private static void KillaPoints(BotZone zone, ref List<ISpawnPoint> result, bool tagilla = false)
        {
            string who = tagilla ? "Tagilla" : "Killa";
            try
            {
                var spot = KillaSpot();
                var spots = spot.HasValue ? FindSpots(tagilla ? 2 : 1, spot.Value, 12f) : new List<Vector3>();
                if (spots.Count == 0)
                {
                    _log.LogWarning($"[PowerRestore] {who}: no floor found at the sewage conduit - he comes in somewhere in the basement.");
                    return;
                }
                var at = spots[tagilla ? spots.Count - 1 : 0];
                result = new List<ISpawnPoint> { MakePoint(at, zone, tagilla ? 91 : 90) };
                _log.LogInfo($"[PowerRestore] {who} is {(tagilla ? "coming" : "sneaking")} in through the sewage conduit at {at:F1}.");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] {who} spawn spot failed ({ex.Message}) - normal spawn.");
            }
        }

        internal static ISpawnPoint MakePoint(Vector3 pos, BotZone zone, int i)
        {
            var go = new GameObject("blackout_blackdiv_spawn_" + i);
            Markers.Add(go);
            var p = new SpawnPointParams
            {
                Id = "blackout_blackdiv_" + i + "_" + Rng.Next(100000),
                Position = pos,
                Rotation = (float)Rng.NextDouble() * 360f,
                Sides = EPlayerSideMask.All,
                Categories = ESpawnCategoryMask.Boss | ESpawnCategoryMask.Bot,
                Infiltration = "",
                BotZoneName = "",     // don't add it to the zone's normal spawn list
                DelayToCanSpawnSec = 0f,
                ColliderParams = new SpawnSphereParams { Center = Vector3.zero, Radius = 1f },
                CorePointId = NearestCorePoint(pos),     // the AI's map position: a wrong one leaves bots lost and standing still
            };
            var marker = SpawnPointMarker.Create(p, go);
            marker.BotZone = zone;
            return marker.SpawnPoint;
        }

        /// <summary>
        /// The AI cover/navigation graph point closest to the spot (same floor). Every bot is born "at" a core
        /// point; vanilla spawn points carry theirs. Ours must too, or the AI thinks the bot is somewhere else.
        /// </summary>
        private static int NearestCorePoint(Vector3 pos)
        {
            try
            {
                var holder = Comfort.Common.Singleton<IBotGame>.Instance?.BotsController?.CoversData?.AICorePointsHolder;
                var points = holder?.CorePoints;
                if (points == null || points.Count == 0) return 0;
                AICorePoint best = null;
                float bestScore = float.MaxValue;
                foreach (var cp in points)
                {
                    if (cp == null) continue;
                    var d = cp.Position - pos;
                    float score = d.x * d.x + d.z * d.z + d.y * d.y * 25f;   // strongly prefer the same floor
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = cp;
                    }
                }
                if (best != null)
                {
                    _log?.LogInfo($"[PowerRestore] Spawn at {pos:F1} -> AI core point {best.Id} ({Mathf.Sqrt(bestScore):0.0} m).");
                    return best.Id;
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[PowerRestore] core point lookup failed: {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// You spawned near the generator room: pick one of the map's own spawn points at least 70 m away
        /// (basement first, otherwise anywhere in Labs).
        /// </summary>
        private static Vector3? FarCenter()
        {
            var me = PlayerPos();
            if (!me.HasValue) return null;
            var room = RoomCenter();
            var all = new List<Vector3>();
            foreach (var z in UnityEngine.Object.FindObjectsOfType<BotZone>())
            {
                if (z?.SpawnPointMarkers == null) continue;
                foreach (var m in z.SpawnPointMarkers) if (m != null) all.Add(m.Position);
            }
            var far = all.Where(p => (p - me.Value).sqrMagnitude > 70f * 70f).ToList();
            if (far.Count == 0) return null;
            var basement = room.HasValue ? far.Where(p => Mathf.Abs(p.y - room.Value.y) < 3f).ToList() : new List<Vector3>();
            var pool = basement.Count > 0 ? basement : far;
            return pool[Rng.Next(pool.Count)];
        }

        /// <summary>Spread-out points on the navmesh around a spot (the generator room, or the far point).</summary>
        internal static List<Vector3> FindSpots(int count, Vector3 center, float searchRadius = 4f)
        {
            if (!NavMesh.SamplePosition(center, out var hit, searchRadius, NavMesh.AllAreas)) return new List<Vector3>();
            Vector3 origin = hit.position;

            var found = new List<Vector3> { origin };
            var path = new NavMeshPath();
            for (int attempt = 0; attempt < 120 && found.Count < Math.Max(count, 6); attempt++)
            {
                float a = (float)Rng.NextDouble() * Mathf.PI * 2f;
                float r = 1.5f + (float)Rng.NextDouble() * 6f;
                var cand = origin + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                if (!NavMesh.SamplePosition(cand, out var h, 1.2f, NavMesh.AllAreas)) continue;
                var p = h.position;
                if (Mathf.Abs(p.y - origin.y) > 1.2f) continue;                                // same floor
                if (found.Any(f => (f - p).sqrMagnitude < 1.2f * 1.2f)) continue;              // spread out
                if (!NavMesh.CalculatePath(origin, p, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) continue;
                if (PathLength(path) > (p - origin).magnitude * 1.6f + 1.5f) continue;          // behind a wall = other room
                found.Add(p);
            }
            return found;
        }

        private static float PathLength(NavMeshPath path)
        {
            float len = 0f;
            var c = path.corners;
            for (int i = 1; i < c.Length; i++) len += (c[i] - c[i - 1]).magnitude;
            return len;
        }

        public static void ResetRaid()
        {
            _wanted = -1;
            foreach (var go in Markers)
            {
                if (go != null) UnityEngine.Object.Destroy(go);
            }
            Markers.Clear();
            Used.Clear();
            _usedAt = -1000f;
            _heldSpawner = _heldParams = null;
            _heldWave = null;
            _noRoom = false;
            _far = false;
            _killa = false;
            _killaSpawner = _killaParams = null;
            _killaWave = null;
            _releasingKilla = false;
            _tagSpawner = _tagParams = null;
            _tagWave = null;
            _tagReleaseAt = -1f;
            _tagilla = false;
            _guardsTask = null;
            _guardsWave = null;
            _lastZone = null;
            _lastPoints = null;
            _bossSpawnAt = -1f;
            _guardsSent = _guardsRetried = false;
            _guardsBoss = null;
            _guardsParams = null;
            _pendingSpawner = null;
            _pendingArgs = null;
            _trySpawnBypass = false;
            _releasing = false;
            WaveSeenAt = -1f;
            Expected = 0;
        }
    }
}
