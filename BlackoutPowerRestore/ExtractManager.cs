using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using CommonAssets.Scripts.Game;
using EFT.Interactive;
using EFT.UI;
using EFT.UI.BattleTimer;
using HarmonyLib;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Blackout locks every Labs extract except the two gates at raid start and never undoes it.
    /// We copy each extract's state just before Blackout locks it, so power can bring them back.
    /// </summary>
    internal static class ExtractManager
    {
        private const string Gate1 = "lab_Parking_Gate";
        private const string Gate2 = "lab_Hangar_Gate";

        private sealed class ExfilState
        {
            public ExfiltrationPoint Point;
            public string Name;
            public bool Restored;
            public EExfiltrationStatus Status;
            public string[] Eligible;
            public readonly List<KeyValuePair<Switch, bool>> Switches = new List<KeyValuePair<Switch, bool>>();
        }

        private static readonly List<ExfilState> Saved = new List<ExfilState>();
        // extracts opened for good (sewage conduit after Killa) - power cuts don't lock them again
        private static readonly HashSet<string> Permanent = new HashSet<string>();
        // the two gates, closed by us when the emergency batteries ran out (power brings them back)
        private static readonly List<ExfilState> ClosedGates = new List<ExfilState>();
        // the two gates as they were before Blackout's lockdown: with main power their own switches work again
        private static readonly List<ExfilState> SavedGates = new List<ExfilState>();
        /// <summary>Set by the plugin: Wedge's key was used on the gate switch (then emergency power decides about the gates).</summary>
        public static Func<bool> KeyUsed = () => false;
        private static ManualLogSource _log;

        public static bool HasSnapshot => Saved.Count > 0;
        public static bool Restored { get; private set; }

        /// <summary>
        /// Surprise: Blackout normally locks the extracts (and hides them from the list) at raid start.
        /// With this on, the raid looks completely normal until the power actually cuts.
        /// </summary>
        public static bool Surprise = true;
        private static bool _cutHappened;
        private static MethodInfo _lockdown, _hideRows;

        public static void Install(Harmony harmony, Type blackoutType, ManualLogSource log)
        {
            _log = log;
            _lockdown = AccessTools.Method(blackoutType, "ApplyExtractLockdown");
            _hideRows = AccessTools.Method(blackoutType, "HideDisabledExfilRows");
            var cut = AccessTools.Method(blackoutType, "ActivateBlackout");
            if (_lockdown == null)
            {
                log.LogWarning("[PowerRestore] Blackout ApplyExtractLockdown not found - extracts can't be restored with power.");
                return;
            }
            harmony.Patch(_lockdown, prefix: new HarmonyMethod(typeof(ExtractManager), nameof(LockdownPrefix)));
            if (_hideRows != null) harmony.Patch(_hideRows, prefix: new HarmonyMethod(typeof(ExtractManager), nameof(HideRowsPrefix)));
            if (cut != null) harmony.Patch(cut, postfix: new HarmonyMethod(typeof(ExtractManager), nameof(AfterPowerCut)));
            else log.LogWarning("[PowerRestore] Blackout ActivateBlackout not found - extracts lock at raid start (no surprise).");
        }

        public static void ResetRaid()
        {
            Saved.Clear();
            SavedGates.Clear();
            GatesDead = false;
            Permanent.Clear();
            ClosedGates.Clear();
            Restored = false;
            _cutHappened = false;
        }

        private static bool Deferred => Surprise && !_cutHappened && _hideRows != null;

        // Blackout's setup calls this at raid start: pretend it's done, keep the normal extracts for now.
        private static bool LockdownPrefix(ref bool __result)
        {
            if (Deferred)
            {
                __result = true;
                return false;
            }
            BeforeLockdown();
            return true;
        }

        private static bool HideRowsPrefix(ref bool __result)
        {
            if (Deferred)
            {
                __result = true;
                return false;
            }
            return true;
        }

        // The power just went out: NOW the normal exits lock and the list switches to the gates.
        private static void AfterPowerCut(object __instance)
        {
            if (_cutHappened) return;
            _cutHappened = true;
            if (!Surprise) return;
            try
            {
                bool locked = (bool)_lockdown.Invoke(__instance, null);
                bool hidden = _hideRows != null && (bool)_hideRows.Invoke(__instance, null);
                _log?.LogInfo($"[PowerRestore] Power cut: extract lockdown applied now (locked={locked}, rows hidden={hidden}).");
            }
            catch (Exception ex)
            {
                _log?.LogError($"[PowerRestore] delayed extract lockdown failed: {ex}");
            }
        }

        /// <summary>Partial power: only the extracts on the powered side come back.</summary>
        public static int RestoreWhere(Func<Vector3, bool> powered)
        {
            int n = 0;
            foreach (var s in Saved)
            {
                if (s.Point == null || s.Restored || !powered(s.Point.transform.position)) continue;
                s.Point.EligibleEntryPoints = (string[])s.Eligible.Clone();
                s.Point.Status = s.Status;
                foreach (var kv in s.Switches)
                {
                    if (kv.Key != null) kv.Key.Operatable = kv.Value;
                }
                s.Restored = true;
                n++;
            }
            // gates closed by flat emergency batteries: real power brings them back (on the powered side)
            foreach (var g in ClosedGates.ToArray())
            {
                if (g.Point == null || !powered(g.Point.transform.position)) continue;
                SetGate(g, open: true);
                ClosedGates.Remove(g);
                n++;
            }
            GateSwitchesOn(powered);
            int rows = ShowRestoredRows();
            if (n > 0) Restored = true;
            var open = Saved.FindAll(x => x.Restored).ConvertAll(x => x.Name);
            _log?.LogInfo($"[PowerRestore] Power restored {n} extract(s) - open now ({open.Count}): {string.Join(", ", open)} ({rows} rows shown).");
            return n;
        }

        // Runs right before Blackout's ApplyExtractLockdown (it retries until extracts exist).
        private static void BeforeLockdown()
        {
            try
            {
                if (Saved.Count > 0) return;
                var points = ExfiltrationController.Instance?.ExfiltrationPoints;
                if (points == null || points.Length == 0) return;

                foreach (var p in points)
                {
                    if (p == null || p.Settings == null) continue;
                    string name = p.Settings.Name;
                    if (name == Gate1 || name == Gate2)
                    {
                        var g = new ExfilState { Point = p, Name = name, Status = p.Status,
                            Eligible = p.EligibleEntryPoints != null ? (string[])p.EligibleEntryPoints.Clone() : Array.Empty<string>() };
                        for (var sw = p.Switch; sw != null; sw = sw.PreviousSwitch) g.Switches.Add(new KeyValuePair<Switch, bool>(sw, sw.Operatable));
                        SavedGates.Add(g);
                        continue;
                    }

                    var s = new ExfilState
                    {
                        Point = p,
                        Name = name,
                        Status = p.Status,
                        Eligible = p.EligibleEntryPoints != null ? (string[])p.EligibleEntryPoints.Clone() : Array.Empty<string>(),
                    };
                    for (var sw = p.Switch; sw != null; sw = sw.PreviousSwitch)
                    {
                        s.Switches.Add(new KeyValuePair<Switch, bool>(sw, sw.Operatable));
                    }
                    Saved.Add(s);
                }
                _log?.LogInfo($"[PowerRestore] Saved the state of {Saved.Count} extracts before Blackout's lockdown.");
            }
            catch (Exception ex)
            {
                _log?.LogError($"[PowerRestore] extract snapshot failed: {ex}");
            }
        }

        /// <summary>A sector lost power again: lock its (restored) extracts and hide their rows.</summary>
        public static int RelockWhere(Func<Vector3, bool> dark)
        {
            int n = 0;
            var names = new HashSet<string>();
            foreach (var s in Saved)
            {
                if (s.Point == null || !s.Restored || !dark(s.Point.transform.position)) continue;
                if (Permanent.Contains(s.Name)) continue;          // the broken-open sewage conduit stays open
                s.Point.Status = EExfiltrationStatus.NotPresent;
                s.Point.EligibleEntryPoints = Array.Empty<string>();
                for (var sw = s.Point.Switch; sw != null; sw = sw.PreviousSwitch) sw.Operatable = false;
                s.Restored = false;
                names.Add(s.Name);
                n++;
            }
            var panel = UnityEngine.Object.FindObjectOfType<ExtractionTimersPanel>();
            if (panel != null && names.Count > 0)
            {
                foreach (var f in typeof(ExtractionTimersPanel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (f.GetValue(panel) is Dictionary<string, ExitTimerPanel> rows)
                    {
                        foreach (var kv in rows)
                        {
                            if (names.Contains(kv.Key) && kv.Value != null) kv.Value.gameObject.SetActive(false);
                        }
                        break;
                    }
                }
            }
            Restored = Saved.Exists(x => x.Restored);
            GateSwitchesOff(dark);
            _log?.LogInfo($"[PowerRestore] Sector lost power: {n} extract(s) locked again ({string.Join(", ", names)}).");
            return n;
        }

        /// <summary>Power is back: normal Labs extracts work again (with their usual rules).</summary>
        public static int RestoreAll() => RestoreWhere(_ => true);

        /// <summary>Power cut again: let Blackout lock them back down.</summary>
        public static void Relock(BlackoutBridge bo, ManualLogSource log)
        {
            GateSwitchesOff(_ => true);
            if (!Restored)
            {
                if (GatesDead) CloseGates(log);
                return;
            }
            bo.Call("ApplyExtractLockdown", log);
            bo.Call("HideDisabledExfilRows", log);
            foreach (var s in Saved) s.Restored = false;
            Restored = false;
            foreach (var name in Permanent) Open(name, permanent: false);   // the conduit stays open
            if (GatesDead) CloseGates(log);                                // batteries still flat
        }

        /// <summary>Open one extract by name (e.g. the sewage conduit). permanent = power cuts won't lock it.</summary>
        public static bool Open(string name, bool permanent)
        {
            var s = Saved.Find(x => x.Name == name);
            if (s?.Point == null) return false;
            if (permanent) Permanent.Add(name);
            s.Point.EligibleEntryPoints = (string[])s.Eligible.Clone();
            s.Point.Status = s.Status == EExfiltrationStatus.NotPresent ? EExfiltrationStatus.RegularMode : s.Status;
            foreach (var kv in s.Switches)
            {
                if (kv.Key != null) kv.Key.Operatable = kv.Value;
            }
            s.Restored = true;
            Restored = true;
            ShowRestoredRows();
            _log?.LogInfo($"[PowerRestore] Extract '{name}' opened{(permanent ? " for good" : "")}.");
            return true;
        }

        /// <summary>A gate is open right now (opened the normal way on main power, or with the key).</summary>
        public static bool AnyGateOpen => SavedGates.Exists(g => IsOpen(g.Point));

        private static bool IsOpen(ExfiltrationPoint p) => p != null && p.Status == EExfiltrationStatus.RegularMode;

        /// <summary>
        /// Main power on this side: the gates' own switches work again - open them the normal way.
        /// (Wedge's key on the gate switch still works too.)
        /// </summary>
        private static void GateSwitchesOn(Func<Vector3, bool> powered)
        {
            var names = new List<string>();
            foreach (var g in SavedGates)
            {
                if (g.Point == null || !powered(g.Point.transform.position) || IsOpen(g.Point)) continue;
                if (ClosedGates.Exists(c => c.Point == g.Point)) continue;       // reopened by RestoreWhere itself
                foreach (var kv in g.Switches) if (kv.Key != null) kv.Key.Operatable = kv.Value;
                names.Add(g.Name);
            }
            if (names.Count > 0) _log?.LogInfo($"[PowerRestore] Main power: the gate switches work again ({string.Join(", ", names)}) - open them the normal way.");
        }

        /// <summary>
        /// Power cut on this side: closed gates' switches go dead again. A gate opened on main power (not with Wedge's key)
        /// closes - it comes back when the power does. With the key, emergency power keeps the gates (EmergencyGrid).
        /// </summary>
        private static void GateSwitchesOff(Func<Vector3, bool> dark)
        {
            foreach (var g in SavedGates)
            {
                if (g.Point == null || !dark(g.Point.transform.position)) continue;
                if (IsOpen(g.Point))
                {
                    if (KeyUsed() || ClosedGates.Exists(c => c.Point == g.Point)) continue;
                    var s = new ExfilState { Point = g.Point, Name = g.Name, Status = EExfiltrationStatus.RegularMode,
                        Eligible = g.Point.EligibleEntryPoints != null ? (string[])g.Point.EligibleEntryPoints.Clone() : Array.Empty<string>() };
                    ClosedGates.Add(s);
                    SetGate(s, open: false);
                    _log?.LogInfo($"[PowerRestore] Power cut: {g.Name} (opened on main power) closes until the power is back.");
                    continue;
                }
                if (g.Point.Switch != null) g.Point.Switch.Operatable = false;
            }
        }

        public static bool GatesDead { get; private set; }
        public static void MarkGatesDead() => GatesDead = true;

        /// <summary>Emergency batteries flat: both gates close (until real power comes back).</summary>
        public static void CloseGates(ManualLogSource log)
        {
            GatesDead = true;
            var points = ExfiltrationController.Instance?.ExfiltrationPoints;
            if (points == null) return;
            foreach (var p in points)
            {
                if (p?.Settings == null || (p.Settings.Name != Gate1 && p.Settings.Name != Gate2)) continue;
                if (ClosedGates.Exists(g => g.Point == p)) { SetGate(ClosedGates.Find(g => g.Point == p), open: false); continue; }
                var s = new ExfilState
                {
                    Point = p,
                    Name = p.Settings.Name,
                    Status = p.Status == EExfiltrationStatus.NotPresent ? EExfiltrationStatus.RegularMode : p.Status,
                    Eligible = p.EligibleEntryPoints != null ? (string[])p.EligibleEntryPoints.Clone() : Array.Empty<string>(),
                };
                ClosedGates.Add(s);
                SetGate(s, open: false);
            }
            log?.LogInfo($"[PowerRestore] Emergency power gone: {ClosedGates.Count} gate(s) closed.");
        }

        private static void SetGate(ExfilState g, bool open)
        {
            try
            {
                if (open)
                {
                    g.Point.EligibleEntryPoints = (string[])g.Eligible.Clone();
                    g.Point.ExternalSetStatus(g.Status);
                }
                else
                {
                    g.Point.ExternalSetStatus(EExfiltrationStatus.NotPresent);
                }
                if (g.Point.Switch != null) g.Point.Switch.Operatable = open;
                var panel = UnityEngine.Object.FindObjectOfType<ExtractionTimersPanel>();
                if (panel == null) return;
                foreach (var f in typeof(ExtractionTimersPanel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (f.GetValue(panel) is Dictionary<string, ExitTimerPanel> rows)
                    {
                        if (rows.TryGetValue(g.Name, out var row) && row != null) row.gameObject.SetActive(open);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[PowerRestore] gate '{g.Name}' {(open ? "open" : "close")} failed: {ex.Message}");
            }
        }

        private static int ShowRestoredRows()
        {
            var panel = UnityEngine.Object.FindObjectOfType<ExtractionTimersPanel>();
            if (panel == null) return 0;
            Dictionary<string, ExitTimerPanel> rows = null;
            foreach (var f in typeof(ExtractionTimersPanel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                rows = f.GetValue(panel) as Dictionary<string, ExitTimerPanel>;
                if (rows != null) break;
            }
            if (rows == null) return 0;
            int n = 0;
            foreach (var s in Saved)
            {
                if (s.Restored && s.Name != null && rows.TryGetValue(s.Name, out var row) && row != null)
                {
                    row.gameObject.SetActive(true);
                    n++;
                }
            }
            return n;
        }
    
        /// <summary>Where an extract is (by its internal name), e.g. the sewage conduit.</summary>
        public static Vector3? PositionOf(string name)
        {
            try
            {
                var points = ExfiltrationController.Instance?.ExfiltrationPoints;
                if (points == null) return null;
                foreach (var p in points)
                {
                    if (p?.Settings != null && p.Settings.Name == name) return p.transform.position;
                }
            }
            catch (Exception)
            {
                // not ready yet
            }
            return null;
        }
    }
}
