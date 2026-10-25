using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Later in the raid, with full power, the switchgear breaker for sector B can trip on its own.
    /// Sector B goes dark (its exits lock, its card readers die) and the protection keeps trying to
    /// reclose: the lights come back for a few seconds, then trip again - over and over, until the
    /// breaker cabinet is reset by hand.
    /// </summary>
    internal sealed class DistributionFault
    {
        private readonly StagedRestore _staged;
        private readonly SoundBank _sound;
        private readonly BlackoutBridge _bo;
        private readonly ManualLogSource _log;
        private readonly System.Random _rng = new System.Random();
        private readonly List<Light> _emergency = new List<Light>();

        private float _tripAt = -1f;
        private float _nextReclose, _reTripAt;
        private bool _pulseOn;
        private Vector3 _cabinet;

        public bool Active { get; private set; }
        public IList<Light> EmergencyLamps => _emergency;

        // set by the plugin
        public string Language = "ru";
        public bool Voice = true, Subtitles = true;
        public Func<bool> SpawnCabinetFault;   // returns false if no cabinet could be placed
        public Func<Vector3?> CabinetPosition;  // where the fault actually appeared

        public DistributionFault(StagedRestore staged, SoundBank sound, BlackoutBridge bo, ManualLogSource log)
        {
            _staged = staged;
            _sound = sound;
            _bo = bo;
            _log = log;
        }

        public bool Scheduled => _tripAt > 0f;

        public void Schedule(float minMinutes, float maxMinutes)
        {
            float delay = Mathf.Lerp(minMinutes, maxMinutes, (float)_rng.NextDouble()) * 60f;
            _tripAt = Time.time + delay;
            _log.LogInfo($"[PowerRestore] A distribution fault will hit sector B in {delay / 60f:0.0} min.");
        }

        public void Tick(bool fullPowerIdle, Vector3 cabinet)
        {
            if (!Active)
            {
                if (_tripAt > 0f && fullPowerIdle && Time.time >= _tripAt && PowerGrid.Ready)
                {
                    _tripAt = -1f;
                    Trip(cabinet);
                }
                return;
            }

            if (!_pulseOn && Time.time >= _nextReclose)
            {
                // automatic reclose attempt: clunk, sector B flickers back on...
                _pulseOn = true;
                _sound.Play2D("breaker_far1", 0.8f);
                _staged.SetGridB(true);
                _staged.Dip(i => !i.GridB, 1 + _rng.Next(2));     // the whole facility (basement too) sags as B is re-energised
                _reTripAt = Time.time + 2f + (float)_rng.NextDouble() * 4f;
                _log.LogInfo("[PowerRestore] Auto-reclose attempt - sector B energised.");
            }
            else if (_pulseOn && Time.time >= _reTripAt)
            {
                // ...and trips again
                _pulseOn = false;
                _sound.Play3D("breaker_trip", _cabinet, 0.75f, 2f, 60f);
                _sound.Play2D("breaker_far3", 0.6f);
                _staged.SetGridB(false);
                _staged.Dip(i => !i.GridB, 2 + _rng.Next(2));
                _nextReclose = Time.time + 20f + (float)_rng.NextDouble() * 20f;
                _log.LogInfo("[PowerRestore] Reclose failed - sector B tripped again.");
            }
        }

        private void Trip(Vector3 cabinet)
        {
            _cabinet = cabinet;
            if (SpawnCabinetFault != null && !SpawnCabinetFault())
            {
                _log.LogWarning("[PowerRestore] No breaker cabinet available - distribution fault skipped.");
                return;
            }
            cabinet = CabinetPosition?.Invoke() ?? cabinet;   // the cabinet that was picked, not always #1
            _cabinet = cabinet;
            Active = true;
            _pulseOn = false;
            _sound.Play3D("breaker_trip", cabinet, 1f, 2f, 60f);
            _sound.Play2D("breaker_far2", 0.7f);
            _staged.SetGridB(false);
            _staged.Dip(i => !i.GridB, 3);
            ExtractManager.RelockWhere(p => PowerGrid.IsGridB(p));
            SpawnEmergencyLamps();
            if (Voice) _sound.PlayVoice("vo_reclose_fault_" + Language);
            if (Subtitles) _bo.ShowAnnouncement("Attention, personnel! Fault in sector B. Automatic reclosing in progress.", 8f);
            _nextReclose = Time.time + 12f + (float)_rng.NextDouble() * 8f;
            _log.LogInfo("[PowerRestore] DISTRIBUTION FAULT: sector B tripped, auto-reclose cycling until the breaker is reset.");
        }

        /// <summary>Breaker reset by hand: sector B comes back for good.</summary>
        public void Resolve(bool restoreExtracts)
        {
            if (!Active) return;
            Active = false;
            _pulseOn = false;
            _sound.Play2D("breaker_far1", 0.9f);
            _staged.SetGridB(true);
            RemoveEmergencyLamps();
            if (restoreExtracts) ExtractManager.RestoreWhere(p => PowerGrid.IsGridB(p));
            if (Voice) _sound.PlayVoice("vo_distribution_ok_" + Language);
            if (Subtitles) _bo.ShowAnnouncement("Attention, personnel! Power to sector B restored.", 7f);
            _log.LogInfo("[PowerRestore] Distribution fault cleared by hand - sector B restored.");
        }

        /// <summary>Power cut / raid end: forget everything.</summary>
        public void Cancel()
        {
            _tripAt = -1f;
            Active = false;
            _pulseOn = false;
            RemoveEmergencyLamps();
            _staged.ClearPendingB();
        }

        // Blackout's emergency lamps are gone at full power - put a few back in the dark sector.
        private void SpawnEmergencyLamps()
        {
            RemoveEmergencyLamps();
            foreach (var p in _bo.EmergencyLampPositions())
            {
                if (!PowerGrid.IsGridB(p)) continue;
                var go = new GameObject("PowerRestore_emergency");
                go.transform.position = p + Vector3.down * 0.3f;
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = new Color(0.74f, 0.48f, 0f);
                l.range = 9f;
                l.intensity = 1.1f;
                l.shadows = LightShadows.None;
                _emergency.Add(l);
            }
        }

        private void RemoveEmergencyLamps()
        {
            foreach (var l in _emergency)
            {
                if (l != null) UnityEngine.Object.Destroy(l.gameObject);
            }
            _emergency.Clear();
        }
    }
}
