using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// End-of-raid summary in the log: how the raid ended (died to whom / extracted where), what happened to the power,
    /// how much of Black Division went down, and Killa / Tagilla - plus a short timeline. It listens to this mod's own
    /// log lines for the events, so nothing else has to report to it.
    /// </summary>
    internal sealed class RaidReport : ILogListener
    {
        private static RaidReport _instance;
        private static ManualLogSource _log;

        private bool _active, _writing;
        private float _start;
        private readonly List<string> _timeline = new List<string>();
        private string _death;
        private float _deathAt = -1f;
        private string _lastExit;
        private float _lastExitAt = -1f;
        private int _starts, _falseStarts, _bdSeen, _bdAlive;
        private string _power = "never came back";
        private bool _burnt, _repaired, _emergency, _sectorFault, _blackout, _bdDown;
        private string _lights = "lights on";
        private Player _me;
        private float _nextPoll;
        private string _killaLine;

        public static void Install(ManualLogSource log)
        {
            if (_instance != null) return;
            _log = log;
            _instance = new RaidReport();
            BepInEx.Logging.Logger.Listeners.Add(_instance);
        }

        public static void Tick(GameWorld gw) => _instance?.DoTick(gw);
        public static void End() => _instance?.DoEnd();

        private string Clock(float t)
        {
            var s = Mathf.Max(0f, t - _start);
            return $"{(int)(s / 60):00}:{(int)(s % 60):00}";
        }

        private void DoTick(GameWorld gw)
        {
            var me = gw?.MainPlayer;
            if (me == null) return;
            if (!_active)
            {
                _active = true;
                BlackoutApi.NewRaid();
                _start = Time.time;
                _timeline.Clear();
                _death = _lastExit = null;
                _deathAt = _lastExitAt = -1f;
                _starts = _falseStarts = 0;
                _power = "never came back";
                _burnt = _repaired = _emergency = _sectorFault = _blackout = _bdDown = false;
                _killaLine = null;
                _lights = "lights on";
                _bdSeen = _bdAlive = 0;
                _me = me;
                me.OnPlayerDead += (player, aggressor, damage, part) =>
                {
                    if (_active && player == _me) OnMyDeath(player, aggressor, damage.DamageType.ToString(), part.ToString());
                };
            }
            if (Time.time < _nextPoll) return;
            _nextPoll = Time.time + 1f;
            _bdSeen = Mathf.Max(_bdSeen, BlackDivHunt.OurSeen);
            _killaLine = $"{KillaStalk.Name}: {KillaStalk.State} | {TagillaGuard.Name}: {TagillaGuard.State}";
            _bdAlive = BlackDivHunt.OurAlive;
            try
            {
                if (me.HealthController != null && me.HealthController.IsAlive)
                {
                    var points = ExfiltrationController.Instance?.ExfiltrationPoints;
                    if (points != null)
                    {
                        foreach (var p in points)
                        {
                            if (p?.Entered != null && p.Entered.Contains(me))
                            {
                                _lastExit = p.Settings?.Name ?? "an exit";
                                _lastExitAt = Time.time;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // the exit check is a nice-to-have
            }
        }

        private void OnMyDeath(Player player, IPlayer aggressor, string damageType, string part)
        {
            try
            {
                _deathAt = Time.time;
                string who = aggressor == null ? "unknown" : $"{aggressor.Profile?.Nickname} ({aggressor.Profile?.Info?.Settings?.Role})";
                if (aggressor != null && aggressor.ProfileId == player?.ProfileId) who = "yourself";
                _death = $"killed by {who}, {damageType} to the {part}{(_blackout ? ", " + _lights : "")}";
                Add($"YOU DIED ({who})");
            }
            catch (Exception)
            {
                _death = "died";
            }
        }

        private void Add(string what)
        {
            if (_timeline.Count < 60) _timeline.Add($"{Clock(Time.time)} {what}");
        }

        // ---- listening to our own log lines
        public void LogEvent(object sender, LogEventArgs e)
        {
            if (!_active || _writing || !(sender is ManualLogSource)) return;
            var m = e.Data as string;
            if (m == null || !m.Contains("[PowerRestore]")) return;
            try { Match(m); } catch (Exception) { }
        }

        private void Match(string m)
        {
            // first: "Killa is coming (Black Division wiped out)" also contains "Black Division wiped out"
            if (m.Contains("Killa is coming (")) { BlackoutApi.Killa(); return; }
            if (m.Contains("Black Division shut the generators down")) { BlackoutApi.LightsOut(); _blackout = true; _lights = "in the dark"; Add("lights out (Black Division)"); }
            else if (m.Contains("Start attempt #")) { _starts++; Add("generator start #" + _starts); }
            else if (m.Contains("Generator failed to start")) Add("  start failed");
            else if (m.Contains("GENERATOR CONTROL BURNT OUT")) { BlackoutApi.Burnt(); _burnt = true; _power = "burnt out"; Add("  CONTROL BURNT OUT"); }
            else if (m.Contains("Generator B TRIPPED")) Add("  generator B tripped");
            else if (m.Contains("PARTIAL POWER")) { BlackoutApi.SetPower(1); _power = "half (generator A only)"; _lights = "half the lights on"; Add("half power"); }
            else if (m.Contains("Breaker reset (")) Add("breaker reset");
            else if (m.Contains("Generator B restart: burnout")) Add("generator B restart");
            else if (m.Contains("Main power fully restored")) { BlackoutApi.SetPower(2); _power = "FULL POWER"; _lights = "lights on"; Add("FULL POWER"); }
            else if (m.Contains("Power cut again")) { BlackoutApi.SetPower(0); _power = "cut again"; _lights = "in the dark"; Add(m.Contains("the Stalker") ? "the Stalker cut the power" : "power cut again"); }
            else if (m.Contains("DISTRIBUTION FAULT")) { BlackoutApi.SetPower(1); _sectorFault = true; _lights = "half the lights on"; Add("sector B fault"); }
            else if (m.Contains("Distribution fault cleared by hand")) { BlackoutApi.SetPower(2); _lights = "lights on"; Add("sector B restored"); }
            else if (m.Contains("Generator control REPAIRED")) { _repaired = true; Add("control repaired"); }
            else if (m.Contains("Emergency power on")) { BlackoutApi.Emergency(); _emergency = true; Add("emergency power - gates open"); }
            else if (m.Contains("Emergency batteries flat")) Add("batteries flat - gates closed");
            else if (m.Contains("Gates opened with the key on main power")) { BlackoutApi.Emergency(); Add("gates opened with the key (main power)"); }
            else if (m.Contains("the gate switches work again")) Add("gate switches work again");
            else if (m.Contains("(opened on main power) closes")) Add("power cut - gate closed");
            else if (m.Contains("Second wave:") && m.Contains("arriving")) Add("second wave arrived");
            else if (m.Contains("Gate squad:") && m.Contains(" Black Division at ")) Add("gate squad at " + After(m, " Black Division at ").Split(' ')[1] + " gate");
            else if (m.Contains("Black Division wiped out") || m.Contains("Black Division out of the fight")) { if (!_bdDown) { _bdDown = true; Add("Black Division down"); } }
            else if (m.Contains("is in the raid at") && (m.Contains("Killa") || m.Contains("Tagilla"))) Add(Between(m, "] ", " is in the raid") + " arrived");
            else if (m.Contains(" was killed at ")) Add(Between(m, "] ", " was killed") + " killed by " + After(m, " by ").TrimEnd('.'));
            else if (m.Contains(" charges ") && m.Contains("from the sewage exit")) Add(Between(m, "] ", " charges") + " charged " + Between(m, " charges ", " ("));
            else if (m.Contains("False start (")) _falseStarts++;
        }

        private static string Between(string s, string a, string b)
        {
            int i = s.LastIndexOf(a, StringComparison.Ordinal);
            i = i < 0 ? 0 : i + a.Length;
            int j = s.IndexOf(b, i, StringComparison.Ordinal);
            return j > i ? s.Substring(i, j - i) : s.Substring(i);
        }

        private static string After(string s, string a)
        {
            int i = s.LastIndexOf(a, StringComparison.Ordinal);
            return i < 0 ? "?" : s.Substring(i + a.Length);
        }

        private void DoEnd()
        {
            if (!_active) return;
            _active = false;
            _me = null;
            string result;
            if (_death != null) result = $"died at {Clock(_deathAt)} - {_death}";
            else if (_lastExit != null && Time.time - _lastExitAt < 20f) result = $"EXTRACTED via {_lastExit}";
            else result = "survived / left the raid (no extract seen)";

            var sb = new StringBuilder();
            sb.AppendLine($"[PowerRestore] ===== RAID SUMMARY ({(_blackout ? "Labs blackout" : "Labs, no blackout this raid")}, {Clock(Time.time)} long) =====");
            sb.AppendLine($"  Result: {result}");
            sb.AppendLine($"  Power: {_power}; {_starts} start attempt(s){(_burnt ? ", control burnt out" : "")}{(_repaired ? ", repaired" : "")}" +
                          $"{(_sectorFault ? ", sector B fault" : "")}{(_emergency ? ", emergency power used" : "")}, {_falseStarts} false start(s)");
            if (_bdSeen > 0) sb.AppendLine($"  Black Division: {_bdSeen - _bdAlive} of {_bdSeen} killed (Wedge's team, second wave and gate squad)");
            sb.AppendLine("  " + (_killaLine ?? $"{KillaStalk.Name}: {KillaStalk.State} | {TagillaGuard.Name}: {TagillaGuard.State}"));
            sb.Append("  Timeline: ").Append(_timeline.Count == 0 ? "-" : string.Join(" | ", _timeline));
            _writing = true;
            try { _log.LogInfo(sb.ToString()); }
            finally { _writing = false; }
            _bdSeen = _bdAlive = 0;
        }

        public void Dispose() { }
    }
}
