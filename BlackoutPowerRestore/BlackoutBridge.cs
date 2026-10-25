using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Reflection access to the Blackout client plugin (com.vultify.blackout).
    /// We never change Blackout itself - we flip its own state and call its own
    /// clean-up routines (the same ones it runs at the end of every raid).
    /// </summary>
    internal sealed class BlackoutBridge
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private readonly FieldInfo _instanceField;
        private readonly FieldInfo _blackoutActive;
        private readonly FieldInfo _blackoutAt;
        private readonly FieldInfo _eventThisRaid;
        private readonly FieldInfo _clockStarted;
        private readonly FieldInfo _subtitleText;
        private readonly FieldInfo _subtitleUntil;

        private readonly MethodInfo _closeKeypad;
        private readonly MethodInfo _activateBlackout;
        private readonly MethodInfo[] _restoreSteps;

        private readonly Type _type;
        private readonly Dictionary<string, MethodInfo> _methodCache = new Dictionary<string, MethodInfo>();

        public bool Ok { get; private set; }

        private BlackoutBridge(Type t, ManualLogSource log)
        {
            _type = t;
            _instanceField = t.GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            _blackoutActive = t.GetField("_blackoutActive", Inst);
            _blackoutAt = t.GetField("_blackoutAt", Inst);
            _eventThisRaid = t.GetField("_eventThisRaid", Inst);
            _clockStarted = t.GetField("_clockStarted", Inst);
            _subtitleText = t.GetField("_subtitleText", Inst);
            _subtitleUntil = t.GetField("_subtitleUntil", Inst);
            _closeKeypad = t.GetMethod("CloseKeypad", Inst, null, Type.EmptyTypes, null);
            _activateBlackout = t.GetMethod("ActivateBlackout", Inst, null, Type.EmptyTypes, null);

            // Order matters: lights first, then materials, then ambient, then the orange emergency lamps.
            string[] steps = { "RestoreLightsOut", "RestoreEmissiveMaterials", "RestoreSpecular", "RestoreAmbient", "DestroyEmergencyLights" };
            _restoreSteps = new MethodInfo[steps.Length];
            for (int i = 0; i < steps.Length; i++)
            {
                _restoreSteps[i] = t.GetMethod(steps[i], Inst, null, Type.EmptyTypes, null);
            }

            Ok = true;
            Check(_instanceField, "Instance", log);
            Check(_blackoutActive, "_blackoutActive", log);
            Check(_blackoutAt, "_blackoutAt", log);
            Check(_eventThisRaid, "_eventThisRaid", log);
            for (int i = 0; i < steps.Length; i++)
            {
                Check(_restoreSteps[i], steps[i], log);
            }
            // Optional extras - nice to have, not required.
            if (_subtitleText == null || _subtitleUntil == null) log.LogWarning("Blackout subtitle fields not found - no on-screen announcement.");
            if (_closeKeypad == null) log.LogWarning("Blackout CloseKeypad not found - skipping.");
            if (_activateBlackout == null) log.LogWarning("Blackout ActivateBlackout not found - the switch can't cut power again.");

            void Check(MemberInfo m, string name, ManualLogSource l)
            {
                if (m != null) return;
                Ok = false;
                l.LogError($"Blackout member '{name}' not found - this Blackout version is not supported. Power restore disabled.");
            }
        }

        public static BlackoutBridge Create(ManualLogSource log)
        {
            if (!Chainloader.PluginInfos.TryGetValue("com.vultify.blackout", out PluginInfo info) || info.Instance == null)
            {
                log.LogError("Blackout (com.vultify.blackout) is not loaded. Power restore disabled.");
                return null;
            }
            var bridge = new BlackoutBridge(info.Instance.GetType(), log);
            log.LogInfo($"Hooked into Blackout {info.Metadata.Version} ({(bridge.Ok ? "ok" : "FAILED")}).");
            return bridge;
        }

        private object Instance => _instanceField.GetValue(null);

        public bool Ready => Ok && Instance != null;

        public bool EventThisRaid => Ready && (bool)_eventThisRaid.GetValue(Instance);

        public bool BlackoutActive => Ready && (bool)_blackoutActive.GetValue(Instance);

        /// <summary>The key was used on the gate switch: hangar and parking gates are open.</summary>
        public bool GatesActivated
        {
            get
            {
                try
                {
                    var bo = Ready ? Instance : null;
                    return bo != null && _type.GetField("_gatesActivated", Inst)?.GetValue(bo) is bool b && b;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>Blackout's countdown is running (it starts when the player first moves; the cut comes 15 s later).</summary>
        public bool ClockStarted => Ready && _clockStarted != null && (bool)_clockStarted.GetValue(Instance);

        /// <summary>When Blackout will cut the power (Time.time). Can be pushed back.</summary>
        public float BlackoutAt
        {
            get => Ready ? (float)_blackoutAt.GetValue(Instance) : float.MaxValue;
            set { if (Ready) _blackoutAt.SetValue(Instance, value); }
        }

        /// <summary>Turns the facility power back on for the rest of this raid.</summary>
        public void RestorePower(ManualLogSource log)
        {
            object bo = Instance;
            if (bo == null) return;

            // 1. Stop Blackout's per-frame darkness enforcement and its lamp-group override.
            _blackoutActive.SetValue(bo, false);
            // 2. Its clock would re-trigger the cut as soon as the flag drops - push it to "never".
            _blackoutAt.SetValue(bo, float.MaxValue);

            // 3. Emergency code keypad only exists while the power is out.
            Safe(() => _closeKeypad?.Invoke(bo, null), "CloseKeypad", log);

            // 4. Blackout's own restore routines (what it normally runs at raid end).
            foreach (var step in _restoreSteps)
            {
                Safe(() => step.Invoke(bo, null), step.Name, log);
            }
        }

        /// <summary>Cuts the power again using Blackout's own routine (lights out, emergency lamps, sound, announcement).</summary>
        public bool CutPower(ManualLogSource log)
        {
            object bo = Instance;
            if (bo == null || _activateBlackout == null || BlackoutActive) return false;
            // Blackout adds a fresh set of emergency lamps on every cut - clear the old set first or they pile up
            var destroy = _type.GetMethod("DestroyEmergencyLights", Inst, null, Type.EmptyTypes, null);
            Safe(() => destroy?.Invoke(bo, null), "DestroyEmergencyLights", log);
            bool ok = true;
            Safe(() => _activateBlackout.Invoke(bo, null), "ActivateBlackout", log, () => ok = false);
            return ok;
        }

        // ------------------------------------------------------------ staged restore support

        /// <summary>Stops Blackout's darkness enforcement without turning anything back on yet.</summary>
        public void StopEnforcement(ManualLogSource log)
        {
            object bo = Instance;
            if (bo == null) return;
            _blackoutActive.SetValue(bo, false);
            _blackoutAt.SetValue(bo, float.MaxValue);
            Safe(() => _closeKeypad?.Invoke(bo, null), "CloseKeypad", log);
        }

        /// <summary>
        /// Copies one of Blackout's private "things I switched off" lists and empties it,
        /// so we can switch those things back on ourselves, one wave at a time.
        /// </summary>
        public List<object> TakeList(string fieldName, ManualLogSource log)
        {
            var result = new List<object>();
            object bo = Instance;
            if (bo == null) return result;
            var f = _type.GetField(fieldName, Inst);
            if (!(f?.GetValue(bo) is IList list))
            {
                log.LogWarning($"[PowerRestore] Blackout list '{fieldName}' not found - skipped.");
                return result;
            }
            foreach (var item in list) result.Add(item);
            list.Clear();
            return result;
        }

        /// <summary>Reads one of Blackout's lists without changing it.</summary>
        public List<T> PeekList<T>(string fieldName) where T : class
        {
            var result = new List<T>();
            object bo = Instance;
            if (bo == null) return result;
            if (_type.GetField(fieldName, Inst)?.GetValue(bo) is IList list)
            {
                foreach (var item in list)
                {
                    if (item is T t) result.Add(t);
                }
            }
            return result;
        }

        /// <summary>A copy of one of Blackout's lists (Blackout keeps its own).</summary>
        public List<object> CopyList(string fieldName)
        {
            var result = new List<object>();
            object bo = Instance;
            if (bo == null) return result;
            if (_type.GetField(fieldName, Inst)?.GetValue(bo) is IList list)
            {
                foreach (var item in list) result.Add(item);
            }
            return result;
        }

        /// <summary>
        /// Cheap way back to darkness after a short surge: Blackout still has all its lists, so we just switch its
        /// enforcement back on (it re-zeroes every lamp each frame) instead of a full power cut, which rescans the
        /// whole map and causes a hitch. Emergency lamps, power-down sound, no repeated PA.
        /// </summary>
        /// <summary>Emergency batteries are flat: Blackout's battery lamps must not come back.</summary>
        public bool NoEmergencyLights;

        public void KillEmergencyLights(ManualLogSource log)
        {
            object bo = Instance;
            if (bo != null) Safe(() => Method("DestroyEmergencyLights")?.Invoke(bo, null), "DestroyEmergencyLights", log);
        }

        public void ReArm(ManualLogSource log, bool powerDownSound = true)
        {
            object bo = Instance;
            if (bo == null) return;
            _blackoutActive.SetValue(bo, true);
            _type.GetField("_announcementPlayed", Inst)?.SetValue(bo, true);
            Safe(() => Method("DestroyEmergencyLights")?.Invoke(bo, null), "DestroyEmergencyLights", log);
            if (!NoEmergencyLights) Safe(() => Method("SpawnEmergencyLights")?.Invoke(bo, null), "SpawnEmergencyLights", log);
            Safe(() => Method("EnforceBlackout")?.Invoke(bo, null), "EnforceBlackout", log);
            if (powerDownSound) Safe(() => Method("PlayPowerDownSound")?.Invoke(bo, null), "PlayPowerDownSound", log);
        }

        private MethodInfo Method(string name)
        {
            if (!_methodCache.TryGetValue(name, out var m))
            {
                m = _type.GetMethod(name, Inst, null, Type.EmptyTypes, null);
                _methodCache[name] = m;
            }
            return m;
        }

        /// <summary>Where Blackout puts its emergency lamps (corridors) - we hang warning beacons there.</summary>
        public List<UnityEngine.Vector3> EmergencyLampPositions()
        {
            var result = new List<UnityEngine.Vector3>();
            var f = _type.GetField("EmergencyLights", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (!(f?.GetValue(null) is Array arr)) return result;
            foreach (var e in arr)
            {
                var pf = e.GetType().GetField("Pos", Inst);
                if (pf?.GetValue(e) is UnityEngine.Vector3 v && !result.Exists(r => (r - v).sqrMagnitude < 1f))
                {
                    result.Add(v);
                }
            }
            return result;
        }

        public Type BlackoutType => _type;

        /// <summary>Calls one of Blackout's parameterless methods (RestoreEmissiveMaterials, ...).</summary>
        public void Call(string method, ManualLogSource log)
        {
            object bo = Instance;
            if (bo == null) return;
            if (!_methodCache.TryGetValue(method, out var mi))
            {
                mi = _type.GetMethod(method, Inst, null, Type.EmptyTypes, null);
                _methodCache[method] = mi;
            }
            if (mi == null)
            {
                log.LogWarning($"[PowerRestore] Blackout method '{method}' not found - skipped.");
                return;
            }
            Safe(() => mi.Invoke(bo, null), method, log);
        }

        /// <summary>Blackout's power-down sound (we play it backwards as the power surge).</summary>
        public UnityEngine.AudioClip PowerDownClip
        {
            get
            {
                object bo = Instance;
                return bo == null ? null : _type.GetField("_powerDownClip", Inst)?.GetValue(bo) as UnityEngine.AudioClip;
            }
        }

        public void ShowAnnouncement(string text, float seconds)
        {
            object bo = Instance;
            if (bo == null || _subtitleText == null || _subtitleUntil == null) return;
            _subtitleText.SetValue(bo, text);
            _subtitleUntil.SetValue(bo, UnityEngine.Time.time + seconds);
        }

        /// <summary>Blackout plays its "emergency power" PA 2 s after every cut - mark it as already played.</summary>
        public void SuppressCutAnnouncement()
        {
            object bo = Instance;
            if (bo == null) return;
            var f = _type.GetField("_announcementPlayed", Inst);
            f?.SetValue(bo, true);
        }

        /// <summary>Removes any caption Blackout is showing (including its own power-cut one).</summary>
        public void HideAnnouncement()
        {
            object bo = Instance;
            if (bo == null || _subtitleText == null) return;
            if (_subtitleText.GetValue(bo) != null) _subtitleText.SetValue(bo, null);
        }

        private static void Safe(Action a, string name, ManualLogSource log, Action onError = null)
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                log.LogError($"Blackout step {name} failed: {(ex as TargetInvocationException)?.InnerException ?? ex}");
                onError?.Invoke();
            }
        }
    }
}
