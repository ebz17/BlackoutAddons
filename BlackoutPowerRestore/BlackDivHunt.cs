using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using EFT;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Black Division doesn't sit in the generator room: shortly after spawning they move out and hunt
    /// whoever is closest - you, PMC bots, scavs, raiders. Every so often each group gets a rough idea
    /// (not the exact spot) of where its nearest target is and goes to check it.
    /// In blackout raids they are hostile to everyone (no "warn first"), and everyone is hostile to them.
    /// </summary>
    internal static class BlackDivHunt
    {
        public static bool Enabled = true;
        public static bool HostileToAll = true;
        public static Func<bool> MayHunt = () => true;     // e.g. only once the lights are out
        public static int Count => Bots.Count;
        public static int TotalSeen => BotIds.Count;      // including any already killed
        // "ours": Wedge's team and our second wave - not the extra squads Black Division's own mod sends at the gates
        private static readonly HashSet<string> OurIds = new HashSet<string>();
        public static int OurSeen => OurIds.Count;
        public static int OurAlive => Bots.Count(b => b != null && !b.IsDead && OurIds.Contains(b.ProfileId));

        /// <summary>Ours, alive, and not frozen in SAIN's Avoid Threat for 2+ minutes (those don't hold the raid up).</summary>
        public static int OurActive => Bots.Count(b => b != null && !b.IsDead && OurIds.Contains(b.ProfileId) && !FrozenLong(b) && !GateSquad.IsGuard(b));
        public static int OurFrozen => Bots.Count(b => b != null && !b.IsDead && OurIds.Contains(b.ProfileId) && FrozenLong(b));
        private static readonly Dictionary<string, float> FrozenSince = new Dictionary<string, float>();
        private static bool FrozenLong(BotOwner b) => FrozenSince.TryGetValue(b.ProfileId, out var t) && Time.time - t >= 120f;
        public static Action OnFirstHunt;
        private static bool _huntAnnounced;
        public static float MinInterval = 30f, MaxInterval = 60f;
        public static float Accuracy = 15f;     // metres of error in what they "know"

        private static readonly List<BotOwner> Bots = new List<BotOwner>();
        private static readonly HashSet<string> BotIds = new HashSet<string>();
        private static readonly HashSet<string> HostilePairs = new HashSet<string>();
        private static readonly System.Random Rng = new System.Random();
        private static float _nextScan, _nextHint, _nextStatus;
        private static readonly Dictionary<string, Vector3> LastPos = new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, int> StillCount = new Dictionary<string, int>();
        private static readonly HashSet<string> Logged = new HashSet<string>();   // status checks (20 s) without moving
        public static bool Unstick = true;
        public static Func<bool> StripCamp = () => false;      // blackout raids: take the raider "hold your spot" layer out
        private static readonly HashSet<string> BrainLogged = new HashSet<string>();
        private static readonly Dictionary<string, float> FirstSeen = new Dictionary<string, float>();
        private static ManualLogSource _log;
        private static bool _first = true;

        public static void Init(ManualLogSource log) => _log = log;

        public static void Tick(GameWorld gw)
        {
            if (gw == null || gw.MainPlayer == null) return;

            if (Time.time >= _nextScan)
            {
                _nextScan = Time.time + 5f;
                Scan(gw);                                 // always: the plugin also counts them
                foreach (var b in Bots) StripCampLayer(b);
                if (Enabled && HostileToAll) MakeHostile(gw);
            }
            if (!Enabled) return;
            if (Bots.Count > 0 && Time.time >= _nextStatus)
            {
                _nextStatus = Time.time + 20f;
                LogStatus();
            }
            if (!MayHunt())
            {
                // waiting for the blackout: first order a few seconds after the lights go out
                if (_nextHint < Time.time + 6f) _nextHint = Time.time + 6f + (float)Rng.NextDouble() * 6f;
                return;
            }
            if (Bots.Count == 0 || Time.time < _nextHint) return;
            _nextHint = Time.time + Mathf.Lerp(MinInterval, MaxInterval, (float)Rng.NextDouble());
            bool wasFirst = _first;

            // one order per group (followers go where the boss goes)
            var done = new HashSet<object>();
            foreach (var bot in Bots)
            {
                try
                {
                    if (bot == null || bot.IsDead || bot.BotState != EBotState.Active) continue;
                    if (GateSquad.IsGuard(bot)) continue;           // holds his gate, no hunt orders
                    var group = bot.BotsGroup;
                    if (group == null || !done.Add(group)) continue;
                    var goal = bot.Memory?.GoalEnemy;
                    if (goal != null && (goal.IsVisible || Time.time - goal.PersonalLastSeenTime < 15f)) continue;   // in a fight right now

                    var target = NearestTarget(gw, bot.Position);
                    if (target == null) continue;

                    // first push is fairly accurate, later ones rougher
                    Vector2 off = UnityEngine.Random.insideUnitCircle * (_first ? 6f : Accuracy);
                    Vector3 guess = target.Position + new Vector3(off.x, 0f, off.y);
                    if (UnityEngine.AI.NavMesh.SamplePosition(guess, out var hit, 8f, UnityEngine.AI.NavMesh.AllAreas)) guess = hit.position;

                    group.AddEnemy(target, EBotEnemyCause.initial);    // both SAIN and the game's AI only hunt real enemies
                    foreach (var mate in Bots)
                    {
                        if (mate != null && !mate.IsDead && mate.BotsGroup == group) HuntGoals.SetGoal(mate, guess);   // the hunt layer walks them there
                    }
                    bool viaSain = SainBridge.SendToSearch(bot, guess, target, 140f);
                    if (!viaSain)
                    {
                        // game AI: "the enemy was last seen around here, go and check" - its own search logic takes it from there
                        if (group.Enemies.TryGetValue(target, out var info) && info != null)
                        {
                            info.EnemyLastPosition = guess;
                            info.IsLastPositionChecked = false;
                        }
                        group.AddPointToSearch(guess, 140f, bot);
                    }

                    string who = target.IsYourPlayer ? "you" : $"{target.Profile?.Nickname} ({target.Profile?.Info?.Settings?.Role})";
                    _log?.LogInfo($"[PowerRestore] Black Division ({bot.Profile?.Nickname}) hunting {who}, " +
                                  $"{(target.Position - bot.Position).magnitude:0} m away{(viaSain ? " (SAIN)" : "")}.");
                }
                catch (Exception ex)
                {
                    _log?.LogWarning($"[PowerRestore] Black Division hunt order failed: {ex.Message}");
                }
            }
            if (wasFirst) _first = false;
            if (!_huntAnnounced)
            {
                _huntAnnounced = true;
                OnFirstHunt?.Invoke();
            }
        }

        /// <summary>Every 20 s: where each Black Division bot is, how far it moved, what it's doing.</summary>
        private static void LogStatus()
        {
            var parts = new List<string>();
            foreach (var b in Bots)
            {
                try
                {
                    if (b == null || b.IsDead) continue;
                    var pos = b.Position;
                    float moved = LastPos.TryGetValue(b.ProfileId, out var last) ? (pos - last).magnitude : 0f;
                    LastPos[b.ProfileId] = pos;
                    StillCount[b.ProfileId] = moved < 1.5f ? (StillCount.TryGetValue(b.ProfileId, out var n) ? n + 1 : 1) : 0;
                    // just spawned and not moving (bad spawn spot): 40 s; later on: 2 min. Only once they may hunt.
                    bool fresh = FirstSeen.TryGetValue(b.ProfileId, out var seen) && Time.time - seen < 180f;
                    if (Unstick && MayHunt() && !GateSquad.IsGuard(b) && StillCount[b.ProfileId] >= (fresh ? 2 : 6)) TryUnstick(b);
                    string enemy = "none";
                    try { enemy = b.Memory?.GoalEnemy?.Person?.Profile?.Nickname ?? "none"; } catch (Exception) { }
                    string layer = "?";
                    try { layer = b.Brain?.ActiveLayerName() ?? "?"; } catch (Exception) { layer = "starting"; }   // brain not set up yet (just spawned)
                    string reason = "";
                    try { reason = b.Brain?.GetActiveNodeReason() ?? ""; } catch (Exception) { }
                    bool avoiding = layer.IndexOf("Avoid Threat", StringComparison.OrdinalIgnoreCase) >= 0;
                    // frozen = stuck in Avoid Threat (our 20 s walk-outs don't reset it; doing anything else does)
                    if (avoiding && moved < 1.5f) { if (!FrozenSince.ContainsKey(b.ProfileId)) FrozenSince[b.ProfileId] = Time.time - 20f; }
                    else if (!avoiding && layer != "BlackoutUnfreeze") FrozenSince.Remove(b.ProfileId);
                    if (Unstick && avoiding && StillCount[b.ProfileId] >= 3) Unfreeze(b, reason);
                    parts.Add($"{b.Profile?.Nickname}: moved {moved:0} m, enemy {enemy}, layer {layer}{(reason.Length > 0 ? " (" + reason + ")" : "")}, {(SainBridge.Controls(b) ? "SAIN" : "game AI")}");
                }
                catch (Exception ex)
                {
                    parts.Add($"? ({ex.Message})");
                }
            }
            if (parts.Count > 0) _log?.LogInfo("[PowerRestore] Black Division status: " + string.Join(" | ", parts));
        }

        /// <summary>A minute frozen in SAIN's "Avoid Threat" with nobody in sight: end the stuck dog fight.</summary>
        private static void Unfreeze(BotOwner b, string reason)
        {
            try
            {
                var goal = b.Memory?.GoalEnemy;
                if (goal != null && goal.IsVisible) return;          // a real fight
                bool ok = SainBridge.ClearDogFight(b, out var why);
                StillCount[b.ProfileId] = 0;
                if (!ok && HuntGoals.Installed)
                {
                    // grenade danger that never ends (or something else): walk him out of it for 20 s
                    var spot = BreakFreeSpot(b);
                    if (spot.HasValue)
                    {
                        try { b.BewareGrenade.GrenadeDangerPoint = null; } catch (Exception) { }
                        HuntGoals.Force(b, spot.Value, 20f);
                        ok = true;
                        why = $"walking him out ({(spot.Value - b.Position).magnitude:0} m)";
                    }
                }
                if (ok) _nextHint = Mathf.Min(_nextHint, Time.time + 2f);   // and a fresh hunt order
                _log?.LogInfo($"[PowerRestore] Black Division ({b.Profile?.Nickname}) frozen in SAIN's Avoid Threat for a minute ({reason}) - {(ok ? "unfrozen: " : "couldn't unfreeze: ")}{why}.");
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[PowerRestore] unfreeze failed: {ex.Message}");
            }
        }

        /// <summary>Where to walk a frozen soldier: toward his target if there's a path, else 15-25 m to free floor.</summary>
        private static Vector3? BreakFreeSpot(BotOwner b)
        {
            var from = b.Position;
            var path = new UnityEngine.AI.NavMeshPath();
            var target = NearestTarget(Comfort.Common.Singleton<GameWorld>.Instance, from);
            if (target != null)
            {
                var dir = target.Position - from;
                var cand = from + dir.normalized * Mathf.Min(25f, dir.magnitude * 0.6f);
                if (UnityEngine.AI.NavMesh.SamplePosition(cand, out var hit, 6f, UnityEngine.AI.NavMesh.AllAreas) &&
                    UnityEngine.AI.NavMesh.CalculatePath(from, hit.position, UnityEngine.AI.NavMesh.AllAreas, path) &&
                    path.status == UnityEngine.AI.NavMeshPathStatus.PathComplete) return hit.position;
            }
            for (int i = 0; i < 30; i++)
            {
                Vector2 off = UnityEngine.Random.insideUnitCircle.normalized * (15f + (float)Rng.NextDouble() * 10f);
                var cand = from + new Vector3(off.x, 0f, off.y);
                if (!UnityEngine.AI.NavMesh.SamplePosition(cand, out var hit, 4f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                if (UnityEngine.AI.NavMesh.CalculatePath(from, hit.position, UnityEngine.AI.NavMesh.AllAreas, path) &&
                    path.status == UnityEngine.AI.NavMeshPathStatus.PathComplete) return hit.position;
            }
            return null;
        }

        /// <summary>
        /// Stood still for 2 minutes without seeing anyone (stuck on geometry, or SAIN holding a spot forever):
        /// move him a couple of metres to free floor that has a real path out, only if you're not close.
        /// </summary>
        private static void TryUnstick(BotOwner b)
        {
            try
            {
                var goal = b.Memory?.GoalEnemy;
                if (goal != null && goal.IsVisible) return;
                // only when he is TRYING to walk somewhere and can't - a bot holding a spot on purpose is left alone
                var mover = b.Mover;
                if (mover == null || !mover.HasPathAndNoComplete || mover.DistDestination < 2f)
                {
                    StillCount[b.ProfileId] = 0;
                    if (Logged.Add(b.ProfileId)) _log?.LogInfo($"[PowerRestore] Black Division ({b.Profile?.Nickname}) is holding a position (not stuck) at {b.Position:F1}.");
                    return;
                }
                Logged.Remove(b.ProfileId);
                var me = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
                if (me != null && (me.Position - b.Position).sqrMagnitude < 40f * 40f) return;   // you might see it
                var from = b.Position;
                var target = NearestTarget(Comfort.Common.Singleton<GameWorld>.Instance, from);
                var path = new UnityEngine.AI.NavMeshPath();
                for (int i = 0; i < 40; i++)
                {
                    float reach = i < 20 ? 1.5f + (float)Rng.NextDouble() * 2f : 3.5f + (float)Rng.NextDouble() * 5f;   // close first, then wider
                    Vector2 off = UnityEngine.Random.insideUnitCircle.normalized * reach;
                    var cand = from + new Vector3(off.x, 0f, off.y);
                    if (!UnityEngine.AI.NavMesh.SamplePosition(cand, out var hit, 1.5f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                    if (Mathf.Abs(hit.position.y - from.y) > 1.2f) continue;
                    if (target != null && (!UnityEngine.AI.NavMesh.CalculatePath(hit.position, target.Position, UnityEngine.AI.NavMesh.AllAreas, path) ||
                                           path.status != UnityEngine.AI.NavMeshPathStatus.PathComplete)) continue;
                    b.Mover?.Teleport(hit.position + Vector3.up * 0.1f);
                    StillCount[b.ProfileId] = 0;
                    _nextHint = Mathf.Min(_nextHint, Time.time + 2f);     // fresh hunt order right away
                    _log?.LogInfo($"[PowerRestore] Black Division ({b.Profile?.Nickname}) was stuck at {from:F1} - moved {(hit.position - from).magnitude:0.0} m to free floor.");
                    return;
                }
                _log?.LogInfo($"[PowerRestore] Black Division ({b.Profile?.Nickname}) seems stuck at {from:F1} - no free floor nearby.");
                StillCount[b.ProfileId] = 3;   // try again in a minute
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[PowerRestore] unstick failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Black Division runs the game's raider brain; its "Pmc" layer makes them pick a spot and hold it.
        /// In blackout raids we take that layer out of their brain (the way BigBrain/SAIN do it), so the hunt
        /// layer and SAIN decide instead. Only for this raid's Black Division - other maps are untouched.
        /// </summary>
        private static void StripCampLayer(BotOwner b)
        {
            try
            {
                if (b == null || b.IsDead || !StripCamp() || b.Brain?.BaseBrain == null) return;
                var strat = (AICoreStrategy<BotLogicDecision>)(object)b.Brain.BaseBrain;
                var layers = strat._layers;
                if (layers == null) return;
                int key = -1;
                foreach (var kv in layers)
                {
                    if (kv.Value != null && kv.Value.Name() == "Pmc") { key = kv.Key; break; }
                }
                if (BrainLogged.Add(b.ProfileId))
                {
                    var names = new List<string>();
                    foreach (var kv in layers) if (kv.Value != null) names.Add($"{kv.Value.Name()}:{kv.Value.Priority}");
                    _log?.LogInfo($"[PowerRestore] Black Division ({b.Profile?.Nickname}) brain {b.Brain.BaseBrain.ShortName()}: {string.Join(", ", names)}.");
                }
                if (key < 0) return;
                strat.DeactivateLayer(key);
                layers.Remove(key);
                _log?.LogInfo($"[PowerRestore] Black Division ({b.Profile?.Nickname}): raider 'hold your spot' layer removed.");
            }
            catch (Exception ex)
            {
                if (BrainLogged.Add("err" + b?.ProfileId)) _log?.LogWarning($"[PowerRestore] couldn't change {b?.Profile?.Nickname}'s brain: {ex.Message}");
            }
        }

        /// <summary>Closest living player or bot that isn't Black Division.</summary>
        private static bool IsStalker(Player p)
        {
            try { return p.IsAI && (int)p.Profile.Info.Settings.Role == 848470; }
            catch (Exception) { return false; }
        }

        private static Player NearestTarget(GameWorld gw, Vector3 from)
        {
            Player best = null;
            float bestSqr = float.MaxValue;
            foreach (var p in gw.AllAlivePlayersList)
            {
                if (p == null || BotIds.Contains(p.ProfileId)) continue;
                if (p.HealthController == null || !p.HealthController.IsAlive) continue;
                if (IsStalker(p)) continue;     // The Stalker (separate mod): they fight him if they meet, but don't hunt him down
                float d = (p.Position - from).sqrMagnitude;
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>
        /// Black Division vs everyone they get close to (50 m), both ways - no warnings, no "friendly" scavs.
        /// Not map-wide: an enemy they know nothing about makes the game's AI stand and wait.
        /// </summary>
        private static void MakeHostile(GameWorld gw)
        {
            foreach (var bd in Bots)
            {
                if (bd == null || bd.IsDead || bd.BotsGroup == null) continue;
                var bdPlayer = bd.GetPlayer;
                foreach (var p in gw.AllAlivePlayersList)
                {
                    if (p == null || BotIds.Contains(p.ProfileId)) continue;
                    if ((p.Position - bd.Position).sqrMagnitude > 50f * 50f) continue;
                    string key = bd.ProfileId + ">" + p.ProfileId;
                    if (!HostilePairs.Add(key)) continue;
                    try
                    {
                        bd.BotsGroup.AddEnemy(p, EBotEnemyCause.initial);
                        var other = p.AIData?.BotOwner;
                        if (p.IsAI && other != null && other.BotsGroup != null && bdPlayer != null)
                        {
                            other.BotsGroup.AddEnemy(bdPlayer, EBotEnemyCause.initial);
                        }
                    }
                    catch (Exception)
                    {
                        HostilePairs.Remove(key);   // group not ready yet - try again next scan
                    }
                }
            }
        }

        private static void Scan(GameWorld gw)
        {
            Bots.RemoveAll(b => b == null || b.IsDead);
            var list = gw.AllAlivePlayersList;
            if (list == null) return;
            // anyone in a Black Division group is Black Division too (guards join Wedge's group)
            foreach (var b in Bots.ToList())
            {
                var g = b?.BotsGroup;
                if (g == null) continue;
                bool ours = OurIds.Contains(b.ProfileId);
                for (int i = 0; i < g.MembersCount; i++)
                {
                    var m = g.Member(i);
                    if (m == null || m.IsDead || BotIds.Contains(m.ProfileId)) continue;
                    Bots.Add(m);
                    BotIds.Add(m.ProfileId);
                    if (ours) OurIds.Add(m.ProfileId);
                    FirstSeen[m.ProfileId] = Time.time;
                    _log?.LogInfo($"[PowerRestore] Black Division member found (group): {m.Profile?.Nickname} - {Bots.Count} so far.");
                }
            }
            foreach (var p in list)
            {
                if (p == null || !p.IsAI) continue;
                var owner = p.AIData?.BotOwner;
                if (owner == null || BotIds.Contains(p.ProfileId)) continue;
                string role = p.Profile?.Info?.Settings?.Role.ToString() ?? "";
                if (role.IndexOf("wedge", StringComparison.OrdinalIgnoreCase) < 0 &&
                    role.IndexOf("blackdiv", StringComparison.OrdinalIgnoreCase) < 0 &&
                    !WedgeSpawn.IsOurSpawn(p.Position)) continue;
                Bots.Add(owner);
                BotIds.Add(p.ProfileId);
                // ours: Wedge, his guards (blackDivIb is only used for his escort) and our second wave
                if (role.IndexOf("wedge", StringComparison.OrdinalIgnoreCase) >= 0 || role.Equals("blackDivIb", StringComparison.OrdinalIgnoreCase) ||
                    WedgeSpawn.IsOurSpawn(p.Position) || SecondWave.IsOurSpawn(p.Position) || GateSquad.IsOurSpawn(p.Position))
                    OurIds.Add(p.ProfileId);
                FirstSeen[p.ProfileId] = Time.time;
                _log?.LogInfo($"[PowerRestore] Black Division member found: {p.Profile?.Nickname} ({role}) - {Bots.Count} so far.");
                if (Bots.Count == 1)
                {
                    // first push: a few seconds to gear up, then they move out
                    _nextHint = Time.time + 8f + (float)Rng.NextDouble() * 7f;
                    _first = true;
                }
            }
        }

        public static void ResetRaid()
        {
            Bots.Clear();
            BotIds.Clear();
            HostilePairs.Clear();
            _huntAnnounced = false;
            LastPos.Clear();
            StillCount.Clear();
            FrozenSince.Clear();
            Logged.Clear();
            FirstSeen.Clear();
            OurIds.Clear();
            BrainLogged.Clear();
            HuntGoals.Clear();
            _nextStatus = 0f;
            _nextScan = _nextHint = 0f;
            _first = true;
        }
    }
}
