using System;
using System.Linq;
using BepInEx.Logging;
using EFT.Interactive;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Wedge's key on the gate switch opens both gates (and the parking door). In the dark they run on the emergency
    /// batteries, which only last a few minutes (random 3-4 by default): then the gates close and even the emergency
    /// lamps die. While main power is on, the gates run on it - no time limit; the batteries only start (or carry on)
    /// when the power goes again. Only real power brings flat-battery gates back.
    /// </summary>
    internal sealed class EmergencyGrid
    {
        private const string ParkingDoor = "door_Laboratory_parking_00000";

        private readonly BlackoutBridge _bo;
        private readonly SoundBank _sound;
        private readonly ManualLogSource _log;
        private readonly System.Random _rng = new System.Random();

        public Func<bool> Enabled = () => true;
        public Func<float> MinMinutes = () => 3f, MaxMinutes = () => 4f;
        public Func<string> Language = () => "ru";
        public Func<bool> Voice = () => true;
        public Action<string, float> Show = (t, s) => { };

        /// <summary>The batteries are running the gates right now (on-screen countdown).</summary>
        public bool Active => _started && !Depleted && !_onMain && _batteryStarted;
        public bool Depleted { get; private set; }
        public float Remaining => Active ? Mathf.Max(0f, _left) : 0f;

        private float _left = -1f;
        private bool _onMain, _batteryStarted;
        private readonly AlarmBeacons _beacons = new AlarmBeacons();
        public Func<bool> Beacons = () => true;
        public Func<System.Collections.Generic.List<Vector3>> BeaconSpots = () => new System.Collections.Generic.List<Vector3>();   // placed with Ctrl+F3
        private bool _warned, _started;

        public EmergencyGrid(BlackoutBridge bo, SoundBank sound, ManualLogSource log)
        {
            _bo = bo;
            _sound = sound;
            _log = log;
        }

        public void Tick()
        {
            _beacons.Tick(Time.deltaTime);
            // real main power only: a false start (the lights coming on for a few seconds by themselves) is not power
            bool dark = BlackoutApi.Power == 0;
            if (_beacons.Active && !dark) _beacons.Stop();                  // real power: the alarm stops
            if (!_started)
            {
                if (!_bo.GatesActivated || !Enabled()) return;
                _started = true;
                float min = Mathf.Min(MinMinutes(), MaxMinutes()), max = Mathf.Max(MinMinutes(), MaxMinutes());
                _left = (min + (float)_rng.NextDouble() * (max - min)) * 60f;
                UnlockParkingDoor();
                if (dark) StartBattery(first: true);
                else
                {
                    _onMain = true;
                    Show("Gates open - on main power, no time limit while the power holds.", 6f);
                    _log.LogInfo($"[PowerRestore] Gates opened with the key on main power: open with no time limit; emergency batteries ({_left / 60f:0.0} min) only if the power goes.");
                }
                return;
            }
            if (Depleted)
            {
                // flat batteries: Blackout's lamps must stay dead (a later power cut would bring them back)
                if (dark && _bo.PeekList<Light>("_emergencyLights").Any(l => l != null)) _bo.KillEmergencyLights(_log);
                return;
            }
            if (!dark)
            {
                if (!_onMain)
                {
                    _onMain = true;
                    Show("Main power back - the gates run on it, no time limit.", 5f);
                    _log.LogInfo($"[PowerRestore] Main power back: the gates run on it (batteries paused, {_left:0} s left in them).");
                }
                return;
            }
            if (_onMain)
            {
                _onMain = false;
                StartBattery(first: false);
            }
            _left -= Time.deltaTime;
            if (!_warned && _left <= 60f)
            {
                _warned = true;
                if (Voice()) _sound.PlayVoice("vo_emergency_low_" + Language());
                Show("Emergency batteries: 1 minute left", 5f);
            }
            if (_left <= 0f) Deplete();
        }

        /// <summary>The gates go over to the emergency batteries (key used in the dark, or the power went with the gates open).</summary>
        private void StartBattery(bool first)
        {
            if (!_batteryStarted)
            {
                _batteryStarted = true;
                if (Beacons()) SpawnBeacons();
            }
            else if (Beacons() && !_beacons.Active)
            {
                _beacons.Remove();
                SpawnBeacons();
            }
            if (Voice()) _sound.PlayVoice("vo_emergency_on_" + Language());
            Show(first ? "Emergency power ON - gates open. The batteries won't last long."
                       : $"Power lost - the gates are on emergency batteries: {_left / 60f:0.0} min.", 6f);
            _log.LogInfo(first ? $"[PowerRestore] Emergency power on (gate switch): gates open for {_left / 60f:0.0} min."
                               : $"[PowerRestore] Emergency power on (power lost with the gates open): {_left / 60f:0.0} min left in the batteries.");
        }

        private void Deplete()
        {
            Depleted = true;
            _bo.NoEmergencyLights = true;
            _beacons.Stop();
            if (!_bo.BlackoutActive)
            {
                // main power is on right now: the gates run on it. They close if the power goes again.
                ExtractManager.MarkGatesDead();
                _log.LogInfo("[PowerRestore] Emergency batteries flat - main power is on, the gates stay open until the next power cut.");
                return;
            }
            ExtractManager.CloseGates(_log);
            if (_bo.BlackoutActive) _bo.KillEmergencyLights(_log);
            if (Voice()) _sound.PlayVoice("vo_emergency_out_" + Language());
            Show("Emergency power depleted - the gates are closed", 6f);
            _log.LogInfo("[PowerRestore] Emergency batteries flat: gates closed, emergency lamps out.");
        }

        /// <summary>Red rotating beacons on the ceiling of the main rooms (where Blackout's emergency lamps are).</summary>
        private void SpawnBeacons()
        {
            var spots = new System.Collections.Generic.List<Vector3>();
            var placed = BeaconSpots() ?? new System.Collections.Generic.List<Vector3>();
            var where = new System.Collections.Generic.List<Vector3>();
            foreach (var p in placed) where.Add(p + Vector3.up * 1.2f);      // stored where you stood: start at chest height
            if (placed.Count == 0)
            {
                // nothing placed yet: Blackout's emergency lamp spots, but not three in one corridor
                foreach (var p in _bo.EmergencyLampPositions())
                    if (!where.Exists(w => (w - p).sqrMagnitude < 12f * 12f)) where.Add(p);
            }
            // and where it matters most: the two gates and the gate switch room
            foreach (var g in new[] { "lab_Hangar_Gate", "lab_Parking_Gate" })
            {
                var gp = ExtractManager.PositionOf(g);
                if (gp.HasValue) where.Add(gp.Value + Vector3.up * 1.5f);
            }
            where.Add(new Vector3(-130.9f, 2.2f, -336.4f));
            foreach (var p in where)
            {
                // up to the ceiling (max 12 m - the atrium is tall), just below it
                var top = Physics.Raycast(p + Vector3.up * 0.2f, Vector3.up, out var hit, 12f, ~0, QueryTriggerInteraction.Ignore)
                    ? hit.point + Vector3.down * 0.05f : p + Vector3.up * 0.6f;
                if (!spots.Exists(s => (s - top).sqrMagnitude < 4f)) spots.Add(top);
            }
            if (spots.Count == 0) return;
            _beacons.Spawn(spots);
            _log.LogInfo($"[PowerRestore] Emergency power: {spots.Count} red beacons on: {string.Join(" ", spots.ConvertAll(v => $"({v.x:0},{v.y:0},{v.z:0})"))}.");
        }

        /// <summary>A spot was added/cleared while the alarm is on: show the new set right away.</summary>
        public void RefreshBeacons()
        {
            if (!Active || !Beacons()) return;
            _beacons.Remove();
            SpawnBeacons();
        }

        /// <summary>Emergency power also feeds the parking door's lock (Blackout used a code for this).</summary>
        private void UnlockParkingDoor()
        {
            try
            {
                var door = UnityEngine.Object.FindObjectsOfType<KeycardDoor>().FirstOrDefault(d => d != null && d.Id == ParkingDoor);
                if (door != null && door.DoorState == EDoorState.Locked)
                {
                    door.Unlock();
                    _log.LogInfo($"[PowerRestore] Emergency power: '{ParkingDoor}' unlocked.");
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] parking door unlock failed: {ex.Message}");
            }
        }

        public void ResetRaid()
        {
            _left = -1f;
            _beacons.Remove();
            _warned = _started = _onMain = _batteryStarted = false;
            Depleted = false;
            _bo.NoEmergencyLights = false;
        }
    }
}
