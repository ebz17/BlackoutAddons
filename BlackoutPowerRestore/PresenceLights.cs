using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// The lights around a "dark presence" (BlackoutApi.DarkPresence - The Stalker) die: every light in the facility
    /// within 5 m of him goes out, up to 14 m they flicker and die as he passes - main lights, emergency lamps, beacons.
    /// It shrinks a light's range to nothing, never its on/off state or brightness (those belong to Blackout's power cuts
    /// and our power restore), so he can't leave a light on in a blackout or off at full power. (Not the culling mask:
    /// deferred rendering only honours a few excluded layers.)
    /// Players' own lights (weapon lights, headlamps) are left alone.
    /// The facility's own tube and panel lights (TubeLight / AreaLight - the game's deferred lights, most of Labs at full power)
    /// are switched off as a component (Blackout and the power restore only ever touch their intensity) and back on after.
    /// </summary>
    internal static class PresenceLights
    {
        public static Func<bool> Enabled = () => true;
        public static Action<string> Log = _ => { };
        private static bool _logged;
        public const float Radius = 14f, Out = 5f;

        private static Light[] _all;
        private static float _nextScan, _nextNear, _nextSound;
        private static readonly List<Light> Near = new List<Light>();
        private static readonly Dictionary<Light, float> Touched = new Dictionary<Light, float>();  // light -> its own range
        private static readonly Dictionary<Light, bool> Owned = new Dictionary<Light, bool>();     // carried by a player?
        private static readonly HashSet<Light> Keep = new HashSet<Light>();

        private static BaseLight[] _allBase;
        private static readonly List<BaseLight> NearBase = new List<BaseLight>();
        private static readonly HashSet<BaseLight> TouchedBase = new HashSet<BaseLight>();     // we switched these off
        private static readonly HashSet<BaseLight> KeepBase = new HashSet<BaseLight>();

        /// <summary>1 = on, 0 = off this instant. Out within 5 m; further out a slow, dying flicker.</summary>
        private static float Factor(Vector3 pos, Vector3 src)
        {
            float d = (pos - src).magnitude;
            if (d > Radius) return 1f;
            if (d < Out) return 0f;
            float close = 1f - (d - Out) / (Radius - Out);
            int slot = Mathf.FloorToInt(Time.time / 0.16f);
            int seed = Mathf.RoundToInt(pos.x * 5f) * 49979687 ^ Mathf.RoundToInt(pos.z * 5f) * 32452843 ^ slot * 67867967;
            float r = (seed & 0xFFFF) / 65535f;
            return r < 0.25f + 0.6f * close ? 0f : 1f;
        }

        private static bool CarriedByPlayer(Light l)
        {
            if (Owned.TryGetValue(l, out var owned)) return owned;
            try { owned = l.GetComponentInParent<Player>() != null; }
            catch (Exception) { owned = true; }
            Owned[l] = owned;
            return owned;
        }

        /// <summary>LateUpdate, every frame.</summary>
        public static void Tick(SoundBank sound)
        {
            Vector3? src = null;
            try { src = Enabled() ? BlackoutApi.DarkPresence : null; } catch (Exception) { }
            if (!src.HasValue)
            {
                Release();
                return;
            }
            if (_all == null || Time.time >= _nextScan)
            {
                _nextScan = Time.time + 30f;                     // new lamps (emergency lights, beacons) come and go
                _all = UnityEngine.Object.FindObjectsOfType<Light>();
                try { _allBase = UnityEngine.Object.FindObjectsOfType<BaseLight>(); }
                catch (Exception) { _allBase = Array.Empty<BaseLight>(); }
                if (!_logged)
                {
                    _logged = true;
                    Log($"Dark presence: {_all.Length} lights and {_allBase.Length} tube/panel lights in reach of the effect.");
                }
            }
            if (Time.time >= _nextNear)
            {
                _nextNear = Time.time + 0.25f;
                Near.Clear();
                float r2 = Radius * Radius;
                foreach (var l in _all)
                {
                    if (l == null || l.type == LightType.Directional) continue;
                    if ((l.transform.position - src.Value).sqrMagnitude > r2) continue;
                    if (CarriedByPlayer(l)) continue;
                    Near.Add(l);
                }
                NearBase.Clear();
                foreach (var bl in _allBase)
                {
                    if (bl == null) continue;
                    if ((bl.transform.position - src.Value).sqrMagnitude > r2) continue;
                    NearBase.Add(bl);
                }
            }
            Keep.Clear();
            Vector3? flickering = null;
            foreach (var l in Near)
            {
                if (l == null) continue;
                var p = l.transform.position;
                bool off = Factor(p, src.Value) < 0.5f;
                if (off)
                {
                    if (!Touched.ContainsKey(l)) Touched[l] = l.range;
                    l.range = 0.01f;
                    Keep.Add(l);
                    if (l.enabled && l.intensity > 0f && (p - src.Value).sqrMagnitude > Out * Out) flickering = p;
                }
            }
            // everything he no longer kills gets its light back
            if (Touched.Count > Keep.Count)
            {
                var back = new List<Light>();
                foreach (var kv in Touched) if (!Keep.Contains(kv.Key)) back.Add(kv.Key);
                foreach (var l in back)
                {
                    if (l != null) l.range = Touched[l];
                    Touched.Remove(l);
                }
            }
            // the facility's tube / panel lights
            KeepBase.Clear();
            foreach (var bl in NearBase)
            {
                if (bl == null) continue;
                var p = bl.transform.position;
                if (Factor(p, src.Value) >= 0.5f) continue;
                if (bl.enabled)
                {
                    if (bl.m_Intensity > 0f && (p - src.Value).sqrMagnitude > Out * Out) flickering = p;
                    bl.enabled = false;
                    TouchedBase.Add(bl);
                }
                if (TouchedBase.Contains(bl)) KeepBase.Add(bl);
            }
            if (TouchedBase.Count > KeepBase.Count)
            {
                var back = new List<BaseLight>();
                foreach (var bl in TouchedBase) if (!KeepBase.Contains(bl)) back.Add(bl);
                foreach (var bl in back)
                {
                    if (bl != null) bl.enabled = true;
                    TouchedBase.Remove(bl);
                }
            }
            if (flickering != null && sound != null && Time.time >= _nextSound)
            {
                _nextSound = Time.time + 3f + UnityEngine.Random.value * 4f;
                sound.Play3D("tube_flicker", flickering.Value, 0.3f, 2f, 14f);
            }
        }

        public static void Release()
        {
            foreach (var bl in TouchedBase) if (bl != null) bl.enabled = true;
            TouchedBase.Clear();
            if (Touched.Count == 0) return;
            foreach (var kv in Touched) if (kv.Key != null) kv.Key.range = kv.Value;
            Touched.Clear();
        }

        public static void ResetRaid()
        {
            Release();
            _all = null;
            _allBase = null;
            _logged = false;
            Near.Clear();
            NearBase.Clear();
            Owned.Clear();
            _nextScan = _nextNear = 0f;
        }
    }
}
