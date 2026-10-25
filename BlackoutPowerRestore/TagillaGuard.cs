using System;
using System.Collections.Generic;
using BepInEx.Logging;
using CommonAssets.Scripts.Game;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Tagilla comes through the sewage conduit right behind Killa and guards that exit: he stays near it,
    /// charges anyone who gets within ~20 m, chases up to ~40 m from the exit, then walks back to his post.
    /// While Killa lives he always guards. Once Killa is dead he keeps guarding only if the sewage conduit is
    /// the only way out; if another exit is open he leaves the post and hunts you.
    /// </summary>
    internal static class TagillaGuard
    {
        public const string Exit = "lab_Under_Storage_Collector";
        private static readonly string[] Gates = { "lab_Parking_Gate", "lab_Hangar_Gate" };

        public enum EMode { Guard, Hunt, Vanilla }

        // set by the plugin
        public static Func<bool> Enabled = () => true;
        public static Func<Vector3?> FallbackPost = () => null;
        public static Func<float> GuardRange = () => 20f, ChaseRange = () => 40f;
        public static Func<bool> Relentless = () => true;   // once he charges you, he chases you until one of you dies
        private static Player _chaseTarget;
        private static float _nextChaseHint;

        private static bool Chasing => _chaseTarget != null && _chaseTarget.HealthController != null && _chaseTarget.HealthController.IsAlive;
        public static Action<Player> OnArrive;
        public const float HoldRadius = 5f;

        private static ManualLogSource _log;
        private static BotOwner _tag;
        private static bool _seen, _ended, _returning;
        private static Vector3? _post;
        private static float _nextStatus;
        private static float _nextScan, _nextMode, _nextAlarm, _alarmUntil, _nextHint, _nextShout;
        private static readonly Dictionary<string, float> Announced = new Dictionary<string, float>();
        private static readonly System.Random Rng = new System.Random();

        public static EMode Mode { get; private set; } = EMode.Guard;
        public static Vector3? Post => _post;
        public static Vector3? Where { get; private set; }
        public static string State { get; private set; } = "not in the raid yet";

        public static void Init(ManualLogSource log) => _log = log;
        internal static ManualLogSource Log => _log;

        public static bool IsTagilla(Player p)
        {
            if (p == null || !p.IsAI) return false;
            var role = p.Profile?.Info?.Settings?.Role;
            return role == WildSpawnType.bossTagilla || role == WildSpawnType.bossTagillaAgro;
        }

        /// <summary>"Shadow of Tagilla" (the Labyrinth boss) or "Tagilla".</summary>
        public static string Name { get; private set; } = "Tagilla";

        private static bool IsOurs(BotOwner b) => b != null && _tag != null && b.ProfileId == _tag.ProfileId;

        /// <summary>He's after someone right now - the lights around him go mad.</summary>
        public static Vector3? Menace
        {
            get
            {
                try
                {
                    if (_tag == null || _tag.IsDead || _tag.GetPlayer == null) return null;
                    bool after = Time.time < _alarmUntil || Mode == EMode.Hunt || Chasing;
                    var e = _tag.Memory?.GoalEnemy;
                    if (!after && e != null && (e.IsVisible || Time.time - e.PersonalLastSeenTime < 6f)) after = true;
                    return after ? _tag.Position : (Vector3?)null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public static void Tick(GameWorld gw)
        {
            if (gw?.MainPlayer == null) return;
            if (_tag == null && !_ended && Time.time >= _nextScan)
            {
                _nextScan = Time.time + 1f;
                foreach (var p in gw.AllAlivePlayersList)
                {
                    if (!IsTagilla(p) || p.AIData?.BotOwner == null) continue;
                    Found(p);
                    break;
                }
            }
            if (_tag == null) return;
            if (_tag.IsDead || _tag.GetPlayer == null)
            {
                if (!_ended) _log?.LogInfo($"[PowerRestore] Tagilla is gone (last seen at {Where:F1}).");
                _ended = true;
                _tag = null;
                return;
            }
            Where = _tag.Position;
            PostLamp.Tick(Mode == EMode.Guard && (Time.time < _alarmUntil || Chasing));
            if (!_brainLogged) LogBrain();
            if (Time.time >= _nextPeace) KeepPeace(gw);
            if (Time.time >= _nextStatus)
            {
                _nextStatus = Time.time + 30f;
                string layer;
                try { layer = _tag.Brain?.ActiveLayerName() ?? "?"; } catch (Exception) { layer = "?"; }
                try { var why = _tag.Brain?.GetActiveNodeReason(); if (!string.IsNullOrEmpty(why)) layer += " (" + why + ")"; } catch (Exception) { }
                float fromPost = _post.HasValue ? (_tag.Position - _post.Value).magnitude : -1f;
                _log?.LogInfo($"[PowerRestore] {Name} status: {State}, {fromPost:0} m from the exit, layer {layer}.");
            }

            if (Time.time >= _nextMode)
            {
                _nextMode = Time.time + 2f;
                var mode = !Enabled() || !_post.HasValue ? EMode.Vanilla
                         : KillaStalk.Alive || !OtherExitOpen(gw) ? EMode.Guard : EMode.Hunt;
                if (mode != Mode)
                {
                    Mode = mode;
                    _nextHint = Time.time + 5f;
                    _log?.LogInfo(mode == EMode.Hunt
                        ? $"[PowerRestore] {Name} leaves the sewage exit - Killa is dead and there's another way out. He's hunting you."
                        : mode == EMode.Guard
                            ? $"[PowerRestore] Tagilla goes back to guarding the sewage exit ({(KillaStalk.Alive ? "Killa is alive" : "it's the only way out")})."
                            : "[PowerRestore] Tagilla: guard behaviour off.");
                }
            }
            if (_chaseTarget != null && !Chasing)
            {
                _log?.LogInfo($"[PowerRestore] {Name}: his target is dead - back to the sewage exit.");
                _chaseTarget = null;
            }
            if (Chasing && Time.time >= _nextChaseHint) ChaseOrder();
            State = Mode == EMode.Guard ? (Chasing ? "chasing " + (_chaseTarget.IsYourPlayer ? "you" : _chaseTarget.Profile?.Nickname) : Time.time < _alarmUntil ? "charging" : "guarding the sewage exit")
                  : Mode == EMode.Hunt ? "hunting you" : "alive";

            if (Mode == EMode.Guard && Time.time >= _nextAlarm) CheckIntruders(gw);
            if (Mode != EMode.Vanilla && Time.time >= _nextShout && Menace.HasValue)
            {
                _nextShout = Time.time + 5f + (float)Rng.NextDouble() * 3f;      // after you: you hear him the whole way
                Shout(EPhraseTrigger.OnFight);
            }
            if (Mode == EMode.Hunt && Time.time >= _nextHint) HuntOrder(gw);
        }

        private static bool _brainLogged, _pactLogged;
        private static float _nextPeace;

        /// <summary>
        /// Killa and Shadow of Tagilla are on the same side here (in the Labyrinth Shadow of Tagilla only gets along with
        /// "Vengeful" Killa, so the game makes them fight). Every second: no enemies of each other, allies instead.
        /// </summary>
        private static void KeepPeace(GameWorld gw)
        {
            _nextPeace = Time.time + 1f;
            try
            {
                var tp = _tag?.GetPlayer;
                if (tp == null) return;
                Player killa = null;
                foreach (var p in gw.AllAlivePlayersList) if (KillaStalk.IsKilla(p)) { killa = p; break; }
                var kb = killa?.AIData?.BotOwner;
                if (kb == null) return;
                bool fixedSome = false;
                if (_tag.BotsGroup != null)
                {
                    if (_tag.BotsGroup.IsEnemy(killa)) { _tag.BotsGroup.RemoveEnemy(killa); fixedSome = true; }
                    _tag.BotsGroup.AddAlly(killa);
                }
                if (kb.BotsGroup != null)
                {
                    if (kb.BotsGroup.IsEnemy(tp)) { kb.BotsGroup.RemoveEnemy(tp); fixedSome = true; }
                    kb.BotsGroup.AddAlly(tp);
                }
                if (!_pactLogged)
                {
                    _pactLogged = true;
                    _log?.LogInfo($"[PowerRestore] {KillaStalk.Name} and {Name} are allies.");
                }
                else if (fixedSome) _log?.LogInfo($"[PowerRestore] {KillaStalk.Name} and {Name} were about to fight each other - kept apart.");
            }
            catch (Exception ex)
            {
                if (!_pactLogged) { _pactLogged = true; _log?.LogWarning($"[PowerRestore] Killa/{Name} pact failed: {ex.Message}"); }
            }
        }

        /// <summary>Once: his brain's layers (to check the guard layer got in and where it sits).</summary>
        private static void LogBrain()
        {
            try
            {
                if (_tag.Brain?.BaseBrain == null) return;
                _brainLogged = true;
                var layers = ((AICoreStrategy<BotLogicDecision>)(object)_tag.Brain.BaseBrain)._layers;
                var names = new List<string>();
                if (layers != null) foreach (var kv in layers) if (kv.Value != null) names.Add($"{kv.Value.Name()}:{kv.Value.Priority}");
                _log?.LogInfo($"[PowerRestore] Tagilla brain {_tag.Brain.BaseBrain.ShortName()}: {string.Join(", ", names)}.");
            }
            catch (Exception ex)
            {
                _brainLogged = true;
                _log?.LogInfo($"[PowerRestore] Tagilla brain: couldn't read ({ex.Message}).");
            }
        }

        private static void Found(Player p)
        {
            _tag = p.AIData.BotOwner;
            if (_seen) return;
            _seen = true;
            Name = p.Profile?.Info?.Settings?.Role == WildSpawnType.bossTagillaAgro ? "Shadow of Tagilla" : "Tagilla";
            _post = FindPost(p.Position);
            Mode = EMode.Guard;
            State = "guarding the sewage exit";
            _log?.LogInfo($"[PowerRestore] {Name} is in the raid at {p.Position:F1} - guarding the sewage exit at {_post:F1}.");
            if (_post.HasValue) PostLamp.Spawn(_post.Value);
            try { OnArrive?.Invoke(p); } catch (Exception ex) { _log?.LogWarning($"[PowerRestore] Tagilla entrance failed: {ex.Message}"); }
            p.OnPlayerDead += (player, aggressor, damage, part) =>
            {
                _ended = true;
                Where = player != null ? player.Position : Where;
                string who = aggressor == null ? "unknown" :
                    aggressor.IsYourPlayer ? "you" : $"{aggressor.Profile?.Nickname} ({aggressor.Profile?.Info?.Settings?.Role})";
                State = "dead (killed by " + who + ")";
                PostLamp.Die();
                _log?.LogInfo($"[PowerRestore] {Name} was killed at {Where:F1} by {who}.");
            };
            p.OnPlayerDeadOrUnspawn += player =>
            {
                if (_ended) return;
                _ended = true;
                State = "removed from the raid (despawned, no body)";
                PostLamp.Remove();
                _log?.LogInfo($"[PowerRestore] Tagilla was removed from the raid (despawned, not killed) at {Where:F1}.");
                Where = null;
            };
        }

        /// <summary>The sewage conduit extract, on walkable floor. Falls back to where Killa came in.</summary>
        private static Vector3? FindPost(Vector3 near)
        {
            var spot = ExtractManager.PositionOf(Exit) ?? FallbackPost() ?? near;
            if (UnityEngine.AI.NavMesh.SamplePosition(spot, out var hit, 6f, UnityEngine.AI.NavMesh.AllAreas)) return hit.position;
            if (UnityEngine.AI.NavMesh.SamplePosition(near, out hit, 3f, UnityEngine.AI.NavMesh.AllAreas)) return hit.position;
            return near;
        }

        /// <summary>Any exit you could still use, apart from the sewage conduit.</summary>
        private static bool OtherExitOpen(GameWorld gw)
        {
            try
            {
                var points = ExfiltrationController.Instance?.ExfiltrationPoints;
                if (points == null) return false;
                foreach (var p in points)
                {
                    var name = p?.Settings?.Name;
                    if (name == null || name == Exit) continue;
                    var st = p.Status;
                    if (st == EExfiltrationStatus.NotPresent || st == EExfiltrationStatus.Hidden) continue;
                    if (st == EExfiltrationStatus.UncompleteRequirements && Array.IndexOf(Gates, name) >= 0) continue;   // gate not switched on
                    if (p.EligibleEntryPoints == null || p.EligibleEntryPoints.Length == 0) continue;                       // Blackout's lockdown
                    if (!p.InfiltrationMatch(gw.MainPlayer)) continue;
                    return true;
                }
            }
            catch (Exception)
            {
                // can't tell - keep guarding
            }
            return false;
        }

        /// <summary>Anyone (you, PMCs, raiders, Black Division) within guard range of the exit: he charges.</summary>
        private static void CheckIntruders(GameWorld gw)
        {
            _nextAlarm = Time.time + 0.5f;
            if (!_post.HasValue) return;
            float r = GuardRange(), warn = r + 12f;
            Player target = null;
            float best = warn * warn;
            foreach (var p in gw.AllAlivePlayersList)
            {
                if (!IsPrey(p)) continue;
                var d = p.Position - _post.Value;
                if (Mathf.Abs(d.y) > 4f) continue;          // another floor
                float sq = d.sqrMagnitude;
                if (sq < best) { best = sq; target = p; }
            }
            if (target == null) return;
            if (best > r * r)
            {
                // close, not close enough yet: he growls and taunts - you hear him before he comes
                if (Time.time >= _nextShout && Time.time >= _alarmUntil)
                {
                    _nextShout = Time.time + 6f + (float)Rng.NextDouble() * 4f;
                    Shout(Rng.Next(2) == 0 ? EPhraseTrigger.OnEnemyConversation : EPhraseTrigger.OnFight);
                    if (target.IsYourPlayer && !_warned)
                    {
                        _warned = true;
                        _log?.LogInfo($"[PowerRestore] {Name} hears you coming ({Mathf.Sqrt(best):0} m from the sewage exit) - warning shouts.");
                    }
                }
                return;
            }
            _nextAlarm = Time.time + 3f;
            if (Relentless() && !Chasing)
            {
                _chaseTarget = target;                            // no leash: he's after this one until one of them dies
                _nextChaseHint = Time.time + 4f;
                _log?.LogInfo($"[PowerRestore] {Name} won't stop until {(target.IsYourPlayer ? "you are" : target.Profile?.Nickname + " is")} dead.");
            }
            if (Time.time >= _alarmUntil) _charging = false;     // a new charge: first roar right away
            _alarmUntil = Time.time + 12f;
            _returning = false;
            try
            {
                var group = _tag.BotsGroup;
                group?.AddEnemy(target, EBotEnemyCause.initial);
                bool viaSain = SainBridge.SendToSearch(_tag, target.Position, target, 40f);
                if (!viaSain && group != null)
                {
                    if (group.Enemies.TryGetValue(target, out var info) && info != null)
                    {
                        info.EnemyLastPosition = target.Position;
                        info.IsLastPositionChecked = false;
                    }
                    group.AddPointToSearch(target.Position, 40f, _tag);
                }
                if (!_charging || Time.time >= _nextShout)
                {
                    _nextShout = Time.time + 4f + (float)Rng.NextDouble() * 2f;     // roaring all the way in
                    Shout(EPhraseTrigger.OnFight);
                }
                _charging = true;
                if (!Announced.TryGetValue(target.ProfileId, out var last) || Time.time - last > 30f)
                {
                    Announced[target.ProfileId] = Time.time;
                    string who = target.IsYourPlayer ? "you" : $"{target.Profile?.Nickname} ({target.Profile?.Info?.Settings?.Role})";
                    _log?.LogInfo($"[PowerRestore] {Name} charges {who} ({Mathf.Sqrt(best):0} m from the sewage exit){(viaSain ? " (SAIN)" : "")}.");
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[PowerRestore] Tagilla charge failed: {ex.Message}");
            }
        }

        private static bool _warned, _charging;

        /// <summary>Relentless chase: every 4 s he knows where his target is (a few metres off) and keeps coming.</summary>
        private static void ChaseOrder()
        {
            _nextChaseHint = Time.time + 4f;
            try
            {
                var t = _chaseTarget;
                var e = _tag.Memory?.GoalEnemy;
                if (e != null && e.IsVisible) return;                     // he sees him: the fight AI has it
                Vector2 off = UnityEngine.Random.insideUnitCircle * 4f;
                Vector3 guess = t.Position + new Vector3(off.x, 0f, off.y);
                if (UnityEngine.AI.NavMesh.SamplePosition(guess, out var hit, 6f, UnityEngine.AI.NavMesh.AllAreas)) guess = hit.position;
                var group = _tag.BotsGroup;
                group?.AddEnemy(t, EBotEnemyCause.initial);
                bool viaSain = SainBridge.SendToSearch(_tag, guess, t, 80f);
                if (!viaSain && group != null)
                {
                    if (group.Enemies.TryGetValue(t, out var info) && info != null)
                    {
                        info.EnemyLastPosition = guess;
                        info.IsLastPositionChecked = false;
                    }
                    group.AddPointToSearch(guess, 80f, _tag);
                }
            }
            catch (Exception)
            {
                // next time
            }
        }

        private static void Shout(EPhraseTrigger what)
        {
            try
            {
                var p = _tag?.GetPlayer;
                if (p != null && p.HealthController != null && p.HealthController.IsAlive)
                    p.Say(what, true, 0f, ETagStatus.Combat | ETagStatus.Aware, 100, true);
            }
            catch (Exception)
            {
                // no voice line - fine
            }
        }

        /// <summary>Hunt mode: every 30-60 s he gets a rough idea (~15 m) of where you are.</summary>
        private static void HuntOrder(GameWorld gw)
        {
            _nextHint = Time.time + 30f + (float)Rng.NextDouble() * 30f;
            try
            {
                var me = gw.MainPlayer;
                if (me?.HealthController == null || !me.HealthController.IsAlive) return;
                var goal = _tag.Memory?.GoalEnemy;
                if (goal != null && (goal.IsVisible || Time.time - goal.PersonalLastSeenTime < 20f)) return;   // busy
                Vector2 off = UnityEngine.Random.insideUnitCircle * 15f;
                Vector3 guess = me.Position + new Vector3(off.x, 0f, off.y);
                if (UnityEngine.AI.NavMesh.SamplePosition(guess, out var hit, 8f, UnityEngine.AI.NavMesh.AllAreas)) guess = hit.position;
                var group = _tag.BotsGroup;
                group?.AddEnemy(me, EBotEnemyCause.initial);
                bool viaSain = SainBridge.SendToSearch(_tag, guess, me, 60f);
                if (!viaSain && group != null)
                {
                    if (group.Enemies.TryGetValue(me, out var info) && info != null)
                    {
                        info.EnemyLastPosition = guess;
                        info.IsLastPositionChecked = false;
                    }
                    group.AddPointToSearch(guess, 60f, _tag);
                }
                _log?.LogInfo($"[PowerRestore] Tagilla is hunting you ({(guess - _tag.Position).magnitude:0} m away){(viaSain ? " (SAIN)" : "")}.");
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[PowerRestore] Tagilla hunt order failed: {ex.Message}");
            }
        }

        private static bool IsPrey(Player p)
        {
            if (p == null || IsTagilla(p) || KillaStalk.IsKilla(p) || p.HealthController == null || !p.HealthController.IsAlive) return false;
            if (!p.IsAI) return true;
            var role = p.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
            return role != WildSpawnType.assault && role != WildSpawnType.marksman && role != WildSpawnType.cursedAssault;
        }

        /// <summary>
        /// The guard layer runs when: he's too far from the exit (leash - he walks back all the way), or nobody is
        /// near the exit (he goes back to his post and keeps watch). It steps aside while he's fighting near the exit.
        /// </summary>
        public static bool WantsPost(BotOwner bot)
        {
            try
            {
                if (!IsOurs(bot) || bot.IsDead || Mode != EMode.Guard || !_post.HasValue) return false;
                if (Chasing) return false;                          // relentless: no leash until his target is dead
                float d = (bot.Position - _post.Value).magnitude;
                if (d > ChaseRange() && !_returning)
                {
                    _returning = true;                              // chased too far: back to the post, no turning round
                    _log?.LogInfo($"[PowerRestore] Tagilla chased {d:0} m from the sewage exit - walking back to his post.");
                }
                if (_returning)
                {
                    if (d > HoldRadius * 2f) return true;
                    _returning = false;
                }
                if (Time.time < _alarmUntil) return false;          // charging someone at the exit
                var e = bot.Memory?.GoalEnemy;
                if (e != null && (e.IsVisible || Time.time - e.PersonalLastSeenTime < 8f) &&
                    (e.CurrPosition - _post.Value).sqrMagnitude < (ChaseRange() + 5f) * (ChaseRange() + 5f)) return false;   // fighting near the exit
                if (bot.Memory != null && bot.Memory.IsUnderFire && d < ChaseRange()) return false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void ResetRaid()
        {
            _tag = null;
            _seen = _ended = _returning = _brainLogged = _warned = _charging = _pactLogged = false;
            _chaseTarget = null;
            _nextChaseHint = 0f;
            _nextPeace = 0f;
            Name = "Tagilla";
            PostLamp.Remove();
            _post = null;
            Where = null;
            Mode = EMode.Guard;
            State = "not in the raid yet";
            _nextStatus = _nextScan = _nextMode = _nextAlarm = _alarmUntil = _nextHint = _nextShout = 0f;
            Announced.Clear();
        }
    }

    internal sealed class TagillaGuardLayer : CustomLayer
    {
        public TagillaGuardLayer(BotOwner bot, int priority) : base(bot, priority) { }
        public override string GetName() => "BlackoutTagillaGuard";
        public override bool IsActive() => TagillaGuard.WantsPost(BotOwner);
        public override Action GetNextAction() => new Action(typeof(TagillaGuardLogic), "guarding the exit");
        public override bool IsCurrentActionEnding() => !TagillaGuard.WantsPost(BotOwner);
    }

    internal sealed class TagillaGuardLogic : CustomLogic
    {
        private float _nextPath, _nextLook;
        private bool _pathWarned;
        private Vector3 _look;
        private readonly System.Random _rng = new System.Random();

        public TagillaGuardLogic(BotOwner bot) : base(bot) { }

        public override void Start()
        {
            _nextPath = _nextLook = 0f;
            BotOwner.PatrollingData?.Pause();
        }

        public override void Stop()
        {
            BotOwner.PatrollingData?.Unpause();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            var post = TagillaGuard.Post;
            if (!post.HasValue) return;
            float d = (post.Value - BotOwner.Position).magnitude;
            BotOwner.SetPose(1f);
            if (d > TagillaGuard.HoldRadius)
            {
                BotOwner.Sprint(d > 15f);
                if (Time.time >= _nextPath)
                {
                    _nextPath = Time.time + 1.5f;
                    var st = BotOwner.Mover.GoToPoint(post.Value, true, 1.5f, false, false);
                    if (st != UnityEngine.AI.NavMeshPathStatus.PathComplete && !_pathWarned)
                    {
                        _pathWarned = true;
                        TagillaGuard.Log?.LogInfo($"[PowerRestore] Tagilla can't find a full path back to the exit ({st}) from {BotOwner.Position:F1}, {d:0} m away.");
                    }
                }
                BotOwner.Steering.LookToMovingDirection();
                return;
            }
            // at the post: stand and watch the ways in
            BotOwner.Sprint(false);
            BotOwner.StopMove();
            if (Time.time >= _nextLook)
            {
                _nextLook = Time.time + 3f + (float)_rng.NextDouble() * 4f;
                float a = (float)_rng.NextDouble() * Mathf.PI * 2f;
                _look = post.Value + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 8f + Vector3.up * 1.5f;
            }
            BotOwner.Steering.LookToPoint(_look);
        }
    }

    internal static class TagillaGuardSetup
    {
        public static bool Install(ManualLogSource log, int priority)
        {
            try
            {
                BrainManager.AddCustomLayer(typeof(TagillaGuardLayer), new List<string> { "Tagilla", "TagillaAgro" }, priority,
                    new List<WildSpawnType> { WildSpawnType.bossTagilla, WildSpawnType.bossTagillaAgro });
                log.LogInfo($"[PowerRestore] Tagilla guard layer registered (BigBrain, priority {priority}).");
                return true;
            }
            catch (Exception ex)
            {
                log.LogWarning($"[PowerRestore] Tagilla guard layer not registered ({ex.Message}) - he won't hold the sewage exit.");
                return false;
            }
        }
    }

    /// <summary>
    /// Lights near Tagilla while he's after someone: emergency lamps and red beacons stutter and drop out,
    /// worse the closer he is. Blackout only forces the lamps' brightness, so switching them is safe.
    /// </summary>
    internal static class DangerFlicker
    {
        public static Func<bool> Enabled = () => true;
        public static Func<Vector3?> Source = () => TagillaGuard.Menace;
        public const float Radius = 30f;
        private static readonly HashSet<Light> Touched = new HashSet<Light>();
        private static float _nextSound;
        private static Vector3? _src;

        /// <summary>1 = normal, 0 = dark this instant. Stateless: a hash of the spot and a 70 ms time slot.</summary>
        public static float Factor(Vector3 pos)
        {
            if (!_src.HasValue) return 1f;
            float d = (pos - _src.Value).magnitude;
            if (d > Radius) return 1f;
            float close = 1f - d / Radius;
            int slot = Mathf.FloorToInt(Time.time / 0.07f);
            int seed = Mathf.RoundToInt(pos.x * 7f) * 73856093 ^ Mathf.RoundToInt(pos.z * 7f) * 19349663 ^ slot * 83492791;
            float r = (seed & 0xFFFF) / 65535f;
            if (r < 0.2f + 0.45f * close) return 0f;
            return r < 0.85f ? 0.35f + 0.4f * r : 1f;
        }

        /// <summary>LateUpdate: after Blackout and the normal lamp flicker have had their say.</summary>
        public static void Apply(IList<Light> lamps, SoundBank sound)
        {
            _src = Enabled() ? Source() : null;
            if (!_src.HasValue)
            {
                Release();
                return;
            }
            if (lamps == null) return;
            Light near = null;
            foreach (var l in lamps)
            {
                if (l == null) continue;
                var p = l.transform.position;
                if ((p - _src.Value).sqrMagnitude > Radius * Radius)
                {
                    if (Touched.Remove(l)) l.enabled = true;
                    continue;
                }
                Touched.Add(l);
                l.enabled = Factor(p) > 0.5f;
                near = l;
            }
            if (near != null && sound != null && Time.time >= _nextSound)
            {
                _nextSound = Time.time + 1.5f + UnityEngine.Random.value * 2f;
                sound.Play3D("tube_flicker", near.transform.position, 0.5f, 2f, 20f);
            }
        }

        public static void Release()
        {
            _src = null;
            foreach (var l in Touched) if (l != null) l.enabled = true;
            Touched.Clear();
        }
    }

    /// <summary>
    /// One dying emergency lamp over the sewage exit while he guards it: a dim amber glow that twitches now and then,
    /// and strobes when he charges. You see it from the corridor and know something is down there.
    /// </summary>
    internal static class PostLamp
    {
        public static Func<bool> Enabled = () => true;
        private static GameObject _go;
        private static Light _light;
        private static float _dieAt = -1f;
        private static readonly System.Random Rng = new System.Random();

        public static void Spawn(Vector3 post)
        {
            Remove();
            if (!Enabled()) return;
            var top = Physics.Raycast(post + Vector3.up * 0.5f, Vector3.up, out var hit, 5f, ~0, QueryTriggerInteraction.Ignore)
                ? hit.point + Vector3.down * 0.15f : post + Vector3.up * 2.4f;
            _go = new GameObject("PowerRestore_tagilla_lamp");
            _go.transform.position = top;
            _light = _go.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = new Color(1f, 0.45f, 0.12f);
            _light.range = 10f;
            _light.intensity = 0f;
            _light.shadows = LightShadows.Soft;
            _dieAt = -1f;
        }

        public static void Tick(bool charging)
        {
            if (_light == null) return;
            _light.enabled = true;                    // Blackout's power-cut scans switch stray lights off - not this one
            float baseI = 0.9f;
            float f;
            if (charging) f = DangerFlicker.Factor(_go.transform.position);
            else
            {
                int slot = Mathf.FloorToInt(Time.time / 0.09f);
                int h = slot * 83492791 ^ 19349663;
                float r = (h & 0xFFFF) / 65535f;
                f = r < 0.06f ? 0f : r < 0.12f ? 0.4f : 1f;  // mostly steady, a twitch now and then
            }
            _light.intensity = baseI * f;
        }

        /// <summary>He's dead: the lamp sputters out over a couple of seconds.</summary>
        public static void Die()
        {
            if (_light == null) return;
            _dieAt = Time.time;
            Plugin?.StartCoroutine(Sputter());
        }

        public static MonoBehaviour Plugin;

        private static System.Collections.IEnumerator Sputter()
        {
            for (int i = 0; i < 8 && _light != null; i++)
            {
                _light.intensity = Rng.Next(2) == 0 ? 0f : 0.5f;
                yield return new WaitForSeconds(0.08f + (float)Rng.NextDouble() * 0.25f);
            }
            Remove();
        }

        public static void Remove()
        {
            if (_go != null) UnityEngine.Object.Destroy(_go);
            _go = null;
            _light = null;
        }
    }
}
