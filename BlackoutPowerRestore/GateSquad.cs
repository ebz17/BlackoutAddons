using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BepInEx.Logging;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using EFT.Game.Spawning;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// When the gates are opened with Wedge's key, Black Division sends soldiers in through them: one per gate
    /// (hangar and parking) by default. They don't hunt - they hold their gate for the rest of the raid. They fight
    /// anyone they see, chase a little, then walk back to their post. Separate from the second wave.
    /// </summary>
    internal static class GateSquad
    {
        // set by the plugin
        public static Func<bool> Enabled = () => false;
        public static Func<int> Count = () => 2;
        public static Func<float> Delay = () => 15f;
        public static Func<string> RoleName = () => "blackDivAssault";
        public static Action OnArrive;

        private static readonly string[] Gates = { "lab_Hangar_Gate", "lab_Parking_Gate" };
        private static readonly System.Random Rng = new System.Random();
        private static ManualLogSource _log;

        private sealed class Order
        {
            public string Gate;
            public Vector3 GatePos;
            public int Count;
            public Task<BotCreationData> Task;
            public float RequestedAt, SpawnAt;
            public bool Done, Retried, Waited;
            public Vector3 Center;
            public float SpawnedAt = -1f;
        }

        private static readonly List<Order> Orders = new List<Order>();
        private static readonly Dictionary<string, Vector3> Posts = new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, string> PostGate = new Dictionary<string, string>();
        private static bool _triggered, _announced;
        private static WildSpawnType _role;
        private static float _nextScan, _nextStatus;

        public static void Init(ManualLogSource log) => _log = log;

        private static Vector3? _inside;

        /// <summary>Where a guard at this post looks: towards the middle of the facility (where you come from).</summary>
        public static Vector3 Watch(Vector3 post)
        {
            if (!_inside.HasValue)
            {
                var all = new List<Vector3>();
                foreach (var z in UnityEngine.Object.FindObjectsOfType<BotZone>())
                {
                    if (z?.SpawnPointMarkers == null) continue;
                    foreach (var m in z.SpawnPointMarkers) if (m != null) all.Add(m.Position);
                }
                _inside = all.Count > 0 ? all.Aggregate(Vector3.zero, (a, b) => a + b) / all.Count : post;
            }
            var dir = _inside.Value - post;
            dir.y = 0f;
            return dir.sqrMagnitude < 1f ? post + Vector3.forward * 12f : post + dir.normalized * 12f;
        }

        public static bool IsGuard(BotOwner b) => b != null && Posts.ContainsKey(b.ProfileId);

        public static bool PostOf(BotOwner b, out Vector3 post)
        {
            post = default;
            return b != null && Posts.TryGetValue(b.ProfileId, out post);
        }

        /// <summary>A bot standing where one of our gate soldiers just came in.</summary>
        public static bool IsOurSpawn(Vector3 p) =>
            Orders.Any(o => o.SpawnedAt > 0f && Time.time - o.SpawnedAt < 60f && (p - o.Center).sqrMagnitude < 15f * 15f);

        public static bool Pending => Orders.Any(o => !o.Done || (o.SpawnedAt > 0f && Time.time - o.SpawnedAt < 20f));

        /// <summary>Gates opened with the key: soldiers are on their way to them.</summary>
        public static void Trigger()
        {
            if (_triggered || !Enabled()) return;
            _triggered = true;
            try
            {
                var spawner = Singleton<IBotGame>.Instance?.BotsController?.BotSpawner;
                var role = SecondWave.ResolveRole(RoleName());
                var gates = GatePositions();
                if (spawner == null || !role.HasValue || gates.Count == 0)
                {
                    _log?.LogWarning($"[PowerRestore] Gate squad skipped ({(spawner == null ? "no bot spawner" : !role.HasValue ? "unknown role '" + RoleName() + "'" : "no gates found")}).");
                    return;
                }
                _role = role.Value;
                int total = Mathf.Clamp(Count(), 1, 6);
                // spread over the gates: 2 = one each, 3 = two at a random gate and one at the other, ...
                var order = gates.OrderBy(_ => Rng.Next()).ToList();
                for (int i = 0; i < order.Count; i++)
                {
                    int n = total / order.Count + (i < total % order.Count ? 1 : 0);
                    if (n <= 0) continue;
                    var o = new Order { Gate = order[i].Key, GatePos = order[i].Value, Count = n };
                    Request(spawner, o);
                    o.SpawnAt = Time.time + Mathf.Max(3f, Delay()) * (0.8f + (float)Rng.NextDouble() * 0.4f);
                    Orders.Add(o);
                }
                _log?.LogInfo($"[PowerRestore] Gate squad: {total} Black Division coming in through the open gates ({string.Join(", ", Orders.Select(o => $"{o.Count} at {Short(o.Gate)}"))}).");
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[PowerRestore] Gate squad failed: {ex.Message}");
            }
        }

        private static string Short(string gate) => gate == "lab_Hangar_Gate" ? "the hangar gate" : gate == "lab_Parking_Gate" ? "the parking gate" : gate;

        private static void Request(BotSpawner spawner, Order o)
        {
            var prms = new BotSpawnParams
            {
                Id_spawn = "blackout_gate_squad",
                ShallBeGroup = new ShallBeGroupParams(true, false, o.Count),
            };
            o.Task = BotCreationData.Create(new GetProfileDataParams(EPlayerSide.Savage, _role, BotDifficulty.normal, 0f, prms),
                spawner._botCreator, o.Count, spawner);
            o.RequestedAt = Time.time;
        }

        public static void Tick(GameWorld gw)
        {
            if (!_triggered) return;
            foreach (var o in Orders) if (!o.Done && Time.time >= o.SpawnAt) TrySpawn(o, gw?.MainPlayer?.Position);
            if (Time.time >= _nextScan)
            {
                _nextScan = Time.time + 1f;
                FindGuards(gw);
            }
            if (Posts.Count > 0 && Time.time >= _nextStatus)
            {
                _nextStatus = Time.time + 30f;
                var parts = new List<string>();
                foreach (var p in gw?.AllAlivePlayersList ?? new List<Player>())
                {
                    var b = p?.AIData?.BotOwner;
                    if (b == null || !PostOf(b, out var post)) continue;
                    string layer;
                    try { layer = b.Brain?.ActiveLayerName() ?? "?"; } catch (Exception) { layer = "?"; }
                    parts.Add($"{p.Profile?.Nickname} at {Short(PostGate[b.ProfileId])}: {(b.Position - post).magnitude:0} m from his post, layer {layer}");
                }
                if (parts.Count > 0) _log?.LogInfo("[PowerRestore] Gate squad status: " + string.Join(" | ", parts));
            }
        }

        private static void TrySpawn(Order o, Vector3? me)
        {
            if (!o.Task.IsCompleted)
            {
                if (Time.time - o.RequestedAt < 90f) return;
                var sp = Singleton<IBotGame>.Instance?.BotsController?.BotSpawner;
                if (!o.Retried && sp != null)
                {
                    o.Retried = true;
                    try
                    {
                        Request(sp, o);
                        _log?.LogWarning($"[PowerRestore] Gate squad ({Short(o.Gate)}): profiles didn't arrive in 90 s - asking again.");
                        return;
                    }
                    catch (Exception)
                    {
                        // fall through
                    }
                }
                o.Done = true;
                _log?.LogWarning($"[PowerRestore] Gate squad ({Short(o.Gate)}): profiles never arrived - skipped.");
                return;
            }
            // you're right at this gate: wait a bit (up to 45 s) so he doesn't pop up in front of you
            if (me.HasValue && (me.Value - o.GatePos).sqrMagnitude < 25f * 25f && Time.time - o.SpawnAt < 45f)
            {
                if (!o.Waited)
                {
                    o.Waited = true;
                    _log?.LogInfo($"[PowerRestore] Gate squad ({Short(o.Gate)}): you're at that gate - they wait until you move off (max 45 s).");
                }
                return;
            }
            o.Done = true;
            try
            {
                if (o.Task.IsFaulted || o.Task.Result == null)
                {
                    _log?.LogWarning($"[PowerRestore] Gate squad ({Short(o.Gate)}): profiles failed ({o.Task.Exception?.GetBaseException().Message}).");
                    return;
                }
                var data = o.Task.Result;
                var spawner = Singleton<IBotGame>.Instance?.BotsController?.BotSpawner;
                var spots = WedgeSpawn.FindSpots(o.Count, o.GatePos, 8f);
                if (spawner == null || spots.Count == 0)
                {
                    _log?.LogWarning($"[PowerRestore] Gate squad ({Short(o.Gate)}): no free floor at the gate - skipped.");
                    return;
                }
                o.Center = spots[0];
                var zone = spawner.GetClosestZone(o.Center, out _);
                if (zone == null)
                {
                    _log?.LogWarning($"[PowerRestore] Gate squad ({Short(o.Gate)}): no bot zone near the gate - skipped.");
                    return;
                }
                var points = new List<ISpawnPoint>();
                for (int i = 0; i < data.Count; i++) points.Add(WedgeSpawn.MakePoint(spots[i % spots.Count], zone, 70 + i));
                o.SpawnedAt = Time.time;
                spawner.TryToSpawnInZoneAndDelay(zone, data, withCheckMinMax: false, newWave: true, points, forcedSpawn: true);
                float dist = me.HasValue ? (o.Center - me.Value).magnitude : -1f;
                _log?.LogInfo($"[PowerRestore] Gate squad: {data.Count} Black Division at {Short(o.Gate)} ({dist:0} m from you).");
                if (!_announced)
                {
                    _announced = true;
                    OnArrive?.Invoke();
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[PowerRestore] Gate squad spawn failed: {ex.Message}");
            }
        }

        /// <summary>The bots that just appeared at a gate get that gate as their post.</summary>
        private static void FindGuards(GameWorld gw)
        {
            if (gw == null) return;
            foreach (var o in Orders)
            {
                if (o.SpawnedAt < 0f || Time.time - o.SpawnedAt > 60f) continue;
                foreach (var p in gw.AllAlivePlayersList)
                {
                    var b = p?.AIData?.BotOwner;
                    if (b == null || !p.IsAI || Posts.ContainsKey(p.ProfileId)) continue;
                    if ((p.Position - o.Center).sqrMagnitude > 15f * 15f) continue;
                    string role = p.Profile?.Info?.Settings?.Role.ToString() ?? "";
                    if (role.IndexOf("blackdiv", StringComparison.OrdinalIgnoreCase) < 0 && (int)p.Profile.Info.Settings.Role != (int)_role) continue;
                    if (Posts.Count(kv => PostGate[kv.Key] == o.Gate) >= o.Count) continue;
                    Posts[p.ProfileId] = p.Position;
                    PostGate[p.ProfileId] = o.Gate;
                    _log?.LogInfo($"[PowerRestore] Gate squad: {p.Profile?.Nickname} holds {Short(o.Gate)}.");
                }
            }
        }

        private static List<KeyValuePair<string, Vector3>> GatePositions()
        {
            var list = new List<KeyValuePair<string, Vector3>>();
            try
            {
                var points = ExfiltrationController.Instance?.ExfiltrationPoints;
                if (points == null) return list;
                foreach (var p in points)
                {
                    if (p?.Settings != null && Gates.Contains(p.Settings.Name)) list.Add(new KeyValuePair<string, Vector3>(p.Settings.Name, p.transform.position));
                }
            }
            catch (Exception)
            {
                // none
            }
            return list;
        }

        public static void ResetRaid()
        {
            _triggered = _announced = false;
            _inside = null;
            Orders.Clear();
            Posts.Clear();
            PostGate.Clear();
            _nextScan = _nextStatus = 0f;
        }
    }

    /// <summary>
    /// A gate soldier with nobody to fight walks back to his post and holds it, facing into the facility.
    /// Sits above the hunt layer; the combat layers (game AI / SAIN) are above it, so he still fights.
    /// </summary>
    internal sealed class GateGuardLayer : CustomLayer
    {
        public GateGuardLayer(BotOwner bot, int priority) : base(bot, priority) { }
        public override string GetName() => "BlackoutGateGuard";
        public override bool IsActive() => GateGuardLogic.Wants(BotOwner);
        public override Action GetNextAction() => new Action(typeof(GateGuardLogic), "holding the gate");
        public override bool IsCurrentActionEnding() => !GateGuardLogic.Wants(BotOwner);
    }

    internal sealed class GateGuardLogic : CustomLogic
    {
        private float _nextPath, _nextLook;
        private Vector3 _lookAt;
        public GateGuardLogic(BotOwner bot) : base(bot) { }

        public static bool Wants(BotOwner b)
        {
            try
            {
                if (b == null || b.IsDead || !GateSquad.PostOf(b, out _)) return false;
                var enemy = b.Memory?.GoalEnemy;
                if (enemy != null && (enemy.IsVisible || Time.time - enemy.PersonalLastSeenTime < 10f)) return false;   // fighting
                if (b.Memory != null && b.Memory.IsUnderFire) return false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public override void Start()
        {
            _nextPath = 0f;
            BotOwner.PatrollingData?.Pause();
            if (GateSquad.PostOf(BotOwner, out var post)) _lookAt = GateSquad.Watch(post);
        }

        public override void Stop()
        {
            BotOwner.PatrollingData?.Unpause();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            if (!GateSquad.PostOf(BotOwner, out var post)) return;
            float dist = (post - BotOwner.Position).magnitude;
            if (dist > 2.5f)
            {
                BotOwner.SetPose(1f);
                BotOwner.Sprint(dist > 25f);
                if (Time.time >= _nextPath)
                {
                    _nextPath = Time.time + 2f;
                    BotOwner.Mover.GoToPoint(post, true, 1f, false, false);
                }
                BotOwner.Steering.LookToMovingDirection();
                return;
            }
            BotOwner.Sprint(false);
            BotOwner.StopMove();
            BotOwner.SetPose(0.7f);
            if (Time.time >= _nextLook)
            {
                _nextLook = Time.time + 4f + UnityEngine.Random.value * 4f;
                Vector2 jitter = UnityEngine.Random.insideUnitCircle * 4f;
                BotOwner.Steering.LookToPoint(_lookAt + new Vector3(jitter.x, 1.4f, jitter.y));
            }
        }
    }

    internal static class GateGuardSetup
    {
        public static bool Install(ManualLogSource log, int priority)
        {
            try
            {
                var roles = new List<WildSpawnType>();
                foreach (int id in new[] { 848420, 848421, 848422, 848423, 848424, 848426 }) roles.Add((WildSpawnType)id);
                var brains = new List<string> { "PMC", "ExUsec", "PmcUsec", "PmcBear", "Assault", "BossKilla", "BossKnight", "FollowerBully" };
                BrainManager.AddCustomLayer(typeof(GateGuardLayer), brains, priority, roles);
                log.LogInfo($"[PowerRestore] Gate guard layer registered (BigBrain, priority {priority}).");
                return true;
            }
            catch (Exception ex)
            {
                log.LogWarning($"[PowerRestore] gate guard layer not registered ({ex.Message}) - the gate squad will hunt instead.");
                return false;
            }
        }
    }
}
