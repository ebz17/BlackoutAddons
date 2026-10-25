using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using EFT.Interactive;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Tripped breakers in the switchgear room: blinking amber light, electrical buzz and sparks.
    /// Each must be reset by hand before generator B can be restarted.
    /// </summary>
    internal sealed class FaultPanels
    {
        private sealed class Fault
        {
            public GameObject Root;
            public Switch Sw;
            public Light Lamp;
            public AudioSource Buzz;
            public Vector3 Pos;
            public bool Reset;
            public float NextSpark;
            public float FlashUntil;
            public float LampOffAt;
        }

        private const string Tip = "Reset tripped breaker";
        private readonly List<Fault> _faults = new List<Fault>();
        private readonly SoundBank _sound;
        private readonly ManualLogSource _log;
        private readonly System.Random _rng = new System.Random();

        public Action OnAllReset;

        public FaultPanels(SoundBank sound, ManualLogSource log)
        {
            _sound = sound;
            _log = log;
        }

        public int Remaining => _faults.Count(f => !f.Reset);
        public Vector3? FirstPosition => _faults.Count > 0 ? _faults[0].Pos : (Vector3?)null;
        public bool Active => _faults.Count > 0;

        public void Spawn(IList<Vector3> spots, int count, Switch donor)
        {
            Clear();
            if (spots == null || spots.Count == 0 || donor == null) return;
            var picks = spots.OrderBy(_ => _rng.Next()).Take(Mathf.Clamp(count, 1, spots.Count)).ToList();
            foreach (var p in picks) Create(p, donor);
            _log.LogInfo($"[PowerRestore] {picks.Count} breaker fault(s) in the switchgear room: {string.Join(" / ", picks.Select(v => $"({v.x:0.0}, {v.y:0.0}, {v.z:0.0})"))}");
        }

        private void Create(Vector3 pos, Switch donor)
        {
            var f = new Fault { Pos = pos, NextSpark = Time.time + 1f + (float)_rng.NextDouble() * 3f };
            f.Root = UnityEngine.Object.Instantiate(donor.gameObject);
            f.Root.name = "blackout_breaker_fault";
            f.Root.transform.SetPositionAndRotation(pos, Quaternion.identity);
            // the cabinet itself is the visual: hide the copied console mesh, keep its collider
            foreach (var r in f.Root.GetComponentsInChildren<Renderer>(true)) r.enabled = false;

            f.Sw = f.Root.GetComponent<Switch>();
            f.Sw.Id = "blackout_breaker_fault_" + _faults.Count;
            f.Sw.ExfiltrationPoint = null;
            f.Sw.Door = null;
            f.Sw.NextSwitches = Array.Empty<Switch.SwitchAndOperation>();
            f.Sw.PreviousSwitch = null;
            f.Sw.AutoTurnOff = false;
            f.Sw.DoorState = EDoorState.Shut;
            f.Sw.Operatable = true;
            f.Sw.ExtractionZoneTip = string.Empty;
            f.Sw.ContextMenuTip = Tip;
            f.Sw.OnDoorStateChanged += (obj, prev, next) =>
            {
                if (next == EDoorState.Open) ResetFault(f);
            };
            f.Root.SetActive(true);

            var lampGo = new GameObject("fault_lamp");
            lampGo.transform.SetParent(f.Root.transform, false);
            lampGo.transform.localPosition = new Vector3(0f, 0.25f, 0f);
            f.Lamp = lampGo.AddComponent<Light>();
            f.Lamp.type = LightType.Point;
            f.Lamp.range = 3.5f;
            f.Lamp.shadows = LightShadows.None;
            f.Lamp.color = new Color(1f, 0.72f, 0.05f);

            f.Buzz = _sound.Play3D("fault_buzz", pos, 0.2f, 1f, 10f, loop: true);   // quieter hum: you hear it up close
            _faults.Add(f);
        }

        private void ResetFault(Fault f)
        {
            if (f.Reset) return;
            f.Reset = true;
            if (f.Sw != null) f.Sw.Operatable = false;
            _sound.FadeOut(f.Buzz, 0.25f);
            _sound.Play3D("breaker_near", f.Pos, 1f, 2f, 20f);
            if (f.Lamp != null)
            {
                f.Lamp.color = new Color(0.2f, 1f, 0.3f);
                f.Lamp.intensity = 0.8f;
            }
            f.LampOffAt = Time.time + 25f;
            _log.LogInfo($"[PowerRestore] Breaker reset ({Remaining} fault(s) left).");
            if (Remaining == 0) OnAllReset?.Invoke();
        }

        public void Tick()
        {
            foreach (var f in _faults)
            {
                if (f.Lamp == null) continue;
                if (f.Reset)
                {
                    if (f.LampOffAt > 0f && Time.time >= f.LampOffAt)
                    {
                        UnityEngine.Object.Destroy(f.Lamp.gameObject);
                        f.Lamp = null;
                    }
                    continue;
                }

                // sparks: crackle + a white flash
                if (Time.time >= f.NextSpark)
                {
                    f.NextSpark = Time.time + 3f + (float)_rng.NextDouble() * 6f;
                    _sound.Play3D(_rng.Next(2) == 0 ? "spark1" : "spark2", f.Pos, 0.9f, 1.5f, 18f);
                    f.FlashUntil = Time.time + 0.05f + (float)_rng.NextDouble() * 0.08f;
                }
                if (Time.time < f.FlashUntil)
                {
                    f.Lamp.color = new Color(0.85f, 0.9f, 1f);
                    f.Lamp.intensity = 4f;
                }
                else
                {
                    // fault indicator: amber blink, ~2 Hz
                    f.Lamp.color = new Color(1f, 0.72f, 0.05f);
                    f.Lamp.intensity = Mathf.Repeat(Time.time * 2f, 1f) < 0.5f ? 1.4f : 0.05f;
                }
            }
        }

        public void Clear()
        {
            foreach (var f in _faults)
            {
                if (f.Buzz != null) _sound.FadeOut(f.Buzz, 0.1f);
                if (f.Root != null) UnityEngine.Object.Destroy(f.Root);
            }
            _faults.Clear();
        }
    }
}
