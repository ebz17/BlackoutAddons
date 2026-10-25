using System;
using BepInEx.Logging;
using EFT;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Blackout Killa (the surprise final boss from the sewage conduit) doesn't wander: every so often he
    /// gets a rough idea (~20 m) of where someone is and goes to check - every other time it's you, the rest
    /// of the time whoever is closest (PMCs, Black Division, raiders). Hostile to all of them, both ways.
    /// Once he finds someone the game's own AI (or SAIN) takes over the fight.
    /// </summary>
    internal static class KillaStalk
    {
        public static Func<bool> Enabled = () => true;
        public static Action<Player> OnArrive;      // the hatch bang + his screams
        private static int _orders;
        private static float _nextHostile;
        private static readonly System.Collections.Generic.HashSet<string> HostileTo = new System.Collections.Generic.HashSet<string>();
        public static float MinInterval = 45f, MaxInterval = 90f, Accuracy = 20f;

        private static BotOwner _killa;
        private static bool _seen, _ended, _brainLogged;
        /// <summary>Where Killa is (alive) or where his body is (killed). Null = not in yet / despawned.</summary>
        public static Vector3? Where { get; private set; }
        public static string State { get; private set; } = "not in the raid yet";
        private static float _nextScan, _nextHint;
        private static readonly System.Random Rng = new System.Random();
        private static ManualLogSource _log;

        public static void Init(ManualLogSource log) => _log = log;

        /// <summary>Killa is in the raid and alive.</summary>
        public static bool Alive => _seen && !_ended && _killa != null && !_killa.IsDead;

        public static bool IsKilla(Player p)
        {
            if (p == null || !p.IsAI) return false;
            var role = p.Profile?.Info?.Settings?.Role;
            return role == WildSpawnType.bossKilla || role == WildSpawnType.bossKillaAgro;
        }

        /// <summary>"Vengeful Killa" (Shadow of Tagilla's partner) or "Killa".</summary>
        public static string Name { get; private set; } = "Killa";

        public static void Tick(GameWorld gw)
        {
            if (gw?.MainPlayer == null) return;
            if (_killa == null && Time.time >= _nextScan)
            {
                _nextScan = Time.time + 1f;
                foreach (var p in gw.AllAlivePlayersList)
                {
                    if (!IsKilla(p) || p.AIData?.BotOwner == null) continue;
                    _killa = p.AIData.BotOwner;
                    if (!_seen)
                    {
                        _seen = true;
                        State = "alive";
                        Name = p.Profile?.Info?.Settings?.Role == WildSpawnType.bossKillaAgro ? "Vengeful Killa" : "Killa";
                        _log?.LogInfo($"[PowerRestore] {Name} is in the raid at {p.Position:F1}.");
                        try { OnArrive?.Invoke(p); } catch (Exception ex) { _log?.LogWarning($"[PowerRestore] Killa entrance failed: {ex.Message}"); }
                        p.OnPlayerDead += (player, aggressor, damage, part) =>
                        {
                            _ended = true;
                            Where = player != null ? player.Position : Where;
                            string who = aggressor == null ? "unknown" :
                                aggressor.IsYourPlayer ? "you" : $"{aggressor.Profile?.Nickname} ({aggressor.Profile?.Info?.Settings?.Role})";
                            State = "dead (killed by " + who + ")";
                            _log?.LogInfo($"[PowerRestore] {Name} was killed at {Where:F1} by {who}.");
                        };
                        p.OnPlayerDeadOrUnspawn += player =>
                        {
                            if (_ended) return;
                            _ended = true;
                            State = "removed from the raid (despawned, no body)";
                            _log?.LogInfo($"[PowerRestore] Killa was removed from the raid (despawned, not killed) at {Where:F1}.");
                            Where = null;
                        };
                    }
                    _nextHint = Time.time + 20f + (float)Rng.NextDouble() * 20f;   // a moment to get his bearings
                    break;
                }
            }
            if (_killa == null) return;
            if (!_ended) Where = _killa.GetPlayer != null ? _killa.GetPlayer.Position : Where;
            if (!_brainLogged && _killa.Brain?.BaseBrain != null)
            {
                _brainLogged = true;
                try
                {
                    var layers = ((AICoreStrategy<BotLogicDecision>)(object)_killa.Brain.BaseBrain)._layers;
                    var names = new System.Collections.Generic.List<string>();
                    if (layers != null) foreach (var kv in layers) if (kv.Value != null) names.Add($"{kv.Value.Name()}:{kv.Value.Priority}");
                    _log?.LogInfo($"[PowerRestore] {Name} brain {_killa.Brain.BaseBrain.ShortName()}: {string.Join(", ", names)}.");
                }
                catch (Exception) { }
            }
            if (_killa.IsDead || _killa.GetPlayer == null)
            {
                if (!_ended) _log?.LogInfo($"[PowerRestore] Killa is gone (last seen at {Where:F1}).");
                _killa = null;
                _nextScan = float.MaxValue;   // once per raid
                return;
            }
            if (Time.time >= _nextHostile)
            {
                _nextHostile = Time.time + 4f;
                MakeHostile(gw);
            }
            if (!Enabled() || Time.time < _nextHint) return;
            _nextHint = Time.time + Mathf.Lerp(MinInterval, MaxInterval, (float)Rng.NextDouble());
            try
            {
                var goal = _killa.Memory?.GoalEnemy;
                if (goal != null && (goal.IsVisible || Time.time - goal.PersonalLastSeenTime < 20f)) return;   // busy fighting

                // every other order: you; otherwise whoever is closest (could still be you)
                Player me = gw.MainPlayer;
                bool meAlive = me?.HealthController != null && me.HealthController.IsAlive;
                Player target = (_orders++ % 2 == 0 && meAlive) ? me : Nearest(gw, _killa.Position) ?? (meAlive ? me : null);
                if (target == null) return;

                Vector2 off = UnityEngine.Random.insideUnitCircle * Accuracy;
                Vector3 guess = target.Position + new Vector3(off.x, 0f, off.y);
                if (UnityEngine.AI.NavMesh.SamplePosition(guess, out var hit, 8f, UnityEngine.AI.NavMesh.AllAreas)) guess = hit.position;

                var group = _killa.BotsGroup;
                group?.AddEnemy(target, EBotEnemyCause.initial);
                bool viaSain = SainBridge.SendToSearch(_killa, guess, target, 60f);   // a quiet "noise": he comes to check
                if (!viaSain && group != null)
                {
                    if (group.Enemies.TryGetValue(target, out var info) && info != null)
                    {
                        info.EnemyLastPosition = guess;
                        info.IsLastPositionChecked = false;
                    }
                    group.AddPointToSearch(guess, 60f, _killa);
                }
                string who = target.IsYourPlayer ? "you" : $"{target.Profile?.Nickname} ({target.Profile?.Info?.Settings?.Role})";
                _log?.LogInfo($"[PowerRestore] Killa is hunting {who} ({(guess - _killa.Position).magnitude:0} m away){(viaSain ? " (SAIN)" : "")}.");
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[PowerRestore] Killa stalk order failed: {ex.Message}");
            }
        }

        /// <summary>Closest living PMC / Black Division / raider / player that isn't Killa or a scav.</summary>
        private static Player Nearest(GameWorld gw, Vector3 from)
        {
            Player best = null;
            float bestSqr = float.MaxValue;
            foreach (var p in gw.AllAlivePlayersList)
            {
                if (!IsPrey(p)) continue;
                float d = (p.Position - from).sqrMagnitude;
                if (d < bestSqr) { bestSqr = d; best = p; }
            }
            return best;
        }

        private static bool IsPrey(Player p)
        {
            if (p == null || IsKilla(p) || TagillaGuard.IsTagilla(p) || p.HealthController == null || !p.HealthController.IsAlive) return false;
            if (!p.IsAI) return true;
            var role = p.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
            return role != WildSpawnType.assault && role != WildSpawnType.marksman && role != WildSpawnType.cursedAssault;   // not scavs
        }

        /// <summary>Killa vs PMCs, Black Division and raiders within 60 m: enemies both ways.</summary>
        private static void MakeHostile(GameWorld gw)
        {
            var kp = _killa?.GetPlayer;
            if (kp == null || _killa.BotsGroup == null) return;
            foreach (var p in gw.AllAlivePlayersList)
            {
                if (!IsPrey(p) || (p.Position - kp.Position).sqrMagnitude > 60f * 60f) continue;
                if (!HostileTo.Add(p.ProfileId)) continue;
                try
                {
                    _killa.BotsGroup.AddEnemy(p, EBotEnemyCause.initial);
                    var other = p.AIData?.BotOwner;
                    if (p.IsAI && other?.BotsGroup != null) other.BotsGroup.AddEnemy(kp, EBotEnemyCause.initial);
                }
                catch (Exception)
                {
                    HostileTo.Remove(p.ProfileId);
                }
            }
        }

        public static void ResetRaid()
        {
            _orders = 0;
            _nextHostile = 0f;
            HostileTo.Clear();
            _killa = null;
            _seen = _ended = _brainLogged = false;
            Where = null;
            State = "not in the raid yet";
            Name = "Killa";
            _nextScan = _nextHint = 0f;
        }
    }
}
