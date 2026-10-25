using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using CommonAssets.Scripts.Game;
using EFT.Game.Spawning;
using EFT.Interactive;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Black Division doesn't give up: once you get the power back (or open the gates with the key),
    /// a small squad (3-4) comes back in. Near the gates if they are open, otherwise somewhere in Labs
    /// well away from you. Once per raid. They hunt like the first team (BlackDivHunt picks them up).
    /// </summary>
    internal static class SecondWave
    {
        // set by the plugin
        public static Func<bool> Enabled = () => false;
        public static Func<int> MinCount = () => 3, MaxCount = () => 4;
        public static Func<float> Delay = () => 30f;
        public static Func<string> RoleName = () => "blackDivAssault";
        public static Action OnArrive;

        private static readonly Dictionary<string, int> KnownRoles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "blackDivLead", 848420 }, { "blackDivAssault", 848421 }, { "blackDivBreacher", 848422 }, { "blackDivSupport", 848423 },
        };
        private static readonly string[] Gates = { "lab_Hangar_Gate", "lab_Parking_Gate" };

        private static ManualLogSource _log;
        private static readonly System.Random Rng = new System.Random();
        private static bool _triggered, _spawned;
        private static string _reason;
        private static bool _atGates;
        private static float _spawnAt = -1f, _requestedAt;
        private static Task<BotCreationData> _task;
        private static int _count;
        private static float _spawnedAt = -1f;
        private static Vector3? _center;

        /// <summary>A Black Division bot standing where our second wave just came in (not Black Division's own gate spawns).</summary>
        public static bool IsOurSpawn(Vector3 p) =>
            _center.HasValue && _spawnedAt > 0f && Time.time - _spawnedAt < 60f && (p - _center.Value).sqrMagnitude < 20f * 20f;

        public static void Init(ManualLogSource log) => _log = log;

        /// <summary>On its way but not in yet.</summary>
        public static bool Pending => _triggered && _task != null && (!_spawned || Time.time - _spawnedAt < 20f);   // bots take a few s to appear

        /// <summary>Full power or gates opened: the squad is on its way (profiles requested now, they arrive after the delay).</summary>
        public static void Trigger(string reason, bool atGates)
        {
            if (_triggered || !Enabled()) return;
            _triggered = true;
            _reason = reason;
            _atGates = atGates;
            try
            {
                var spawner = Singleton<IBotGame>.Instance?.BotsController?.BotSpawner;
                var role = ResolveRole(RoleName());
                if (spawner == null || !role.HasValue)
                {
                    _log.LogWarning($"[PowerRestore] Second wave skipped ({(spawner == null ? "no bot spawner" : "unknown role '" + RoleName() + "'")}).");
                    return;
                }
                int min = Mathf.Max(1, MinCount()), max = Mathf.Max(min, MaxCount());
                _count = Rng.Next(min, max + 1);
                _role = role.Value;
                Request(spawner);
                _spawnAt = Time.time + Mathf.Max(5f, Delay()) * (0.8f + (float)Rng.NextDouble() * 0.4f);
                _log.LogInfo($"[PowerRestore] Second wave: {reason} - {_count} Black Division coming back in ~{_spawnAt - Time.time:0} s " +
                             $"({(atGates ? "near the gates" : "away from you")}).");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] Second wave failed: {ex.Message}");
            }
        }

        private static WildSpawnType _role;
        private static bool _retried;

        private static void Request(BotSpawner spawner)
        {
            var prms = new BotSpawnParams
            {
                Id_spawn = "blackout_second_wave",     // other mods expect an id here
                ShallBeGroup = new ShallBeGroupParams(true, false, _count),
            };
            _task = BotCreationData.Create(new GetProfileDataParams(EPlayerSide.Savage, _role, BotDifficulty.normal, 0f, prms),
                spawner._botCreator, _count, spawner);
            _requestedAt = Time.time;
        }

        public static void Tick(Vector3? playerPos)
        {
            if (!_triggered || _spawned || _task == null || _spawnAt < 0f || Time.time < _spawnAt) return;
            if (!_task.IsCompleted)
            {
                if (Time.time - _requestedAt < 90f) return;
                var sp = Singleton<IBotGame>.Instance?.BotsController?.BotSpawner;
                if (!_retried && sp != null)
                {
                    _retried = true;
                    try
                    {
                        Request(sp);
                        _log.LogWarning("[PowerRestore] Second wave: profiles didn't arrive in 90 s - asking again.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning($"[PowerRestore] Second wave: second request failed ({ex.Message}).");
                    }
                }
                _spawned = true;
                _log.LogWarning("[PowerRestore] Second wave: profiles never arrived - skipped.");
                return;
            }
            _spawned = true;
            _spawnedAt = Time.time;
            try
            {
                if (_task.IsFaulted || _task.Result == null)
                {
                    _log.LogWarning($"[PowerRestore] Second wave: profiles failed ({_task.Exception?.GetBaseException().Message}).");
                    return;
                }
                var data = _task.Result;
                var spawner = Singleton<IBotGame>.Instance?.BotsController?.BotSpawner;
                var center = PickCenter(playerPos);
                if (spawner == null || !center.HasValue)
                {
                    _log.LogWarning("[PowerRestore] Second wave: no spawn spot found - skipped.");
                    return;
                }
                _center = center;
                var zone = spawner.GetClosestZone(center.Value, out _);
                var spots = WedgeSpawn.FindSpots(_count, center.Value);
                if (zone == null || spots.Count == 0)
                {
                    _log.LogWarning("[PowerRestore] Second wave: no free floor at the spawn spot - skipped.");
                    return;
                }
                var points = new List<ISpawnPoint>();
                for (int i = 0; i < data.Count; i++) points.Add(WedgeSpawn.MakePoint(spots[i % spots.Count], zone, 50 + i));
                spawner.TryToSpawnInZoneAndDelay(zone, data, withCheckMinMax: false, newWave: true, points, forcedSpawn: true);
                float dist = playerPos.HasValue ? (center.Value - playerPos.Value).magnitude : -1f;
                _log.LogInfo($"[PowerRestore] Second wave: {data.Count} Black Division arriving at {center.Value:F1} ({dist:0} m from you, zone {zone.name}).");
                OnArrive?.Invoke();
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] Second wave spawn failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Gates open: the map spawn point nearest a gate, 50+ m from you. Otherwise any map spawn point
        /// 60-160 m from you (so they have to come and find you).
        /// </summary>
        private static Vector3? PickCenter(Vector3? me)
        {
            var all = new List<Vector3>();
            foreach (var z in UnityEngine.Object.FindObjectsOfType<BotZone>())
            {
                if (z?.SpawnPointMarkers == null) continue;
                foreach (var m in z.SpawnPointMarkers) if (m != null) all.Add(m.Position);
            }
            if (all.Count == 0) return null;
            var pool = me.HasValue ? all.Where(p => (p - me.Value).sqrMagnitude > 50f * 50f).ToList() : all;
            if (pool.Count == 0) pool = all;

            if (_atGates)
            {
                var gates = GatePositions();
                if (gates.Count > 0)
                {
                    var best = pool.OrderBy(p => gates.Min(g => (g - p).sqrMagnitude)).Take(3).ToList();
                    if (best.Count > 0 && gates.Min(g => (g - best[0]).sqrMagnitude) < 80f * 80f) return best[Rng.Next(best.Count)];
                }
            }
            var mid = me.HasValue ? pool.Where(p => (p - me.Value).sqrMagnitude > 60f * 60f && (p - me.Value).sqrMagnitude < 160f * 160f).ToList() : pool;
            if (mid.Count > 0) pool = mid;
            return pool[Rng.Next(pool.Count)];
        }

        private static List<Vector3> GatePositions()
        {
            var list = new List<Vector3>();
            try
            {
                var points = ExfiltrationController.Instance?.ExfiltrationPoints;
                if (points == null) return list;
                foreach (var p in points)
                {
                    if (p?.Settings != null && Gates.Contains(p.Settings.Name)) list.Add(p.transform.position);
                }
            }
            catch (Exception)
            {
                // no gates - fall back to "away from you"
            }
            return list;
        }

        /// <summary>The bot type by name: a game type, or a custom one added by MoreBotsAPI.</summary>
        internal static WildSpawnType? ResolveRole(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            if (Enum.TryParse(name, true, out WildSpawnType vanilla) && Enum.IsDefined(typeof(WildSpawnType), vanilla)) return vanilla;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name.IndexOf("MoreBots", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Type mgr = null;
                    try
                    {
                        mgr = asm.GetType("MoreBotsAPI.CustomWildSpawnTypeManager", false) ??
                              asm.GetTypes().FirstOrDefault(t => t.Name == "CustomWildSpawnTypeManager");
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    var get = mgr?.GetMethod("GetCustomWildSpawnTypes", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (!(get?.Invoke(null, null) is IEnumerable types)) continue;
                    foreach (var ct in types)
                    {
                        var tt = ct.GetType();
                        var n = (tt.GetProperty("WildSpawnTypeName")?.GetValue(ct) ?? tt.GetField("WildSpawnTypeName")?.GetValue(ct)) as string;
                        var v = tt.GetProperty("WildSpawnTypeValue")?.GetValue(ct) ?? tt.GetField("WildSpawnTypeValue")?.GetValue(ct);
                        if (n != null && v != null && string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return (WildSpawnType)Convert.ToInt32(v);
                    }
                }
            }
            catch (Exception)
            {
                // fall through to the known ids
            }
            return KnownRoles.TryGetValue(name, out int id) ? (WildSpawnType)id : (WildSpawnType?)null;
        }

        public static void ResetRaid()
        {
            _triggered = _spawned = _retried = false;
            _center = null;
            _reason = null;
            _atGates = false;
            _spawnAt = -1f;
            _task = null;
            _count = 0;
        }
    }
}
