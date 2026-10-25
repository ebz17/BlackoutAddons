using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using MultiFlare;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>One thing Blackout switched off (a lamp, a flare, a glowing panel...).</summary>
    internal abstract class PowerItem
    {
        public Vector3 Position;
        public int Stage;
        public bool IsOn;
        public bool GridB;         // powered by generator B
        public virtual bool CanFlicker => true;
        public abstract bool Alive { get; }
        public abstract void Set(bool on);
    }

    internal sealed class CullLightItem : PowerItem
    {
        private readonly CullingLightObject _c;
        public CullLightItem(CullingLightObject c) { _c = c; Position = c.transform.position; }
        public override bool Alive => _c != null;
        public override void Set(bool on)
        {
            _c.IntensityMultiplier = on ? 1f : 0f;
            if (_c._light != null)
            {
                _c._light.enabled = on;
                if (!on) _c._light.intensity = 0f;
            }
        }
    }

    internal sealed class CullAdvLightItem : PowerItem
    {
        private readonly CullingAdvancedLightObject _c;
        public CullAdvLightItem(CullingAdvancedLightObject c) { _c = c; Position = c.transform.position; }
        public override bool Alive => _c != null;
        public override void Set(bool on)
        {
            _c.IntensityMultiplier = on ? 1f : 0f;
            if (!on && _c._light != null) _c._light.m_Intensity = 0f;
        }
    }

    internal sealed class StrayLightItem : PowerItem
    {
        private readonly Light _l;
        private readonly float _intensity;
        private readonly bool _enabled;
        public StrayLightItem(Light l, float intensity, bool enabled)
        {
            _l = l; _intensity = intensity; _enabled = enabled; Position = l.transform.position;
        }
        public override bool Alive => _l != null;
        public override void Set(bool on)
        {
            if (on) { _l.enabled = _enabled; _l.intensity = _intensity; }
            else _l.enabled = false;
        }
    }

    internal sealed class StrayBaseLightItem : PowerItem
    {
        private readonly BaseLight _l;
        private readonly float _intensity;
        public StrayBaseLightItem(BaseLight l, float intensity) { _l = l; _intensity = intensity; Position = l.transform.position; }
        public override bool Alive => _l != null;
        public override void Set(bool on) => _l.m_Intensity = on ? _intensity : 0f;
    }

    internal sealed class VolumetricItem : PowerItem
    {
        private readonly Behaviour _v;
        private readonly bool _enabled;
        public VolumetricItem(Behaviour v, bool enabled) { _v = v; _enabled = enabled; Position = v.transform.position; }
        public override bool Alive => _v != null;
        public override void Set(bool on) => _v.enabled = on && _enabled;
    }

    internal sealed class FlareItem : PowerItem
    {
        private readonly FlareLight _f;
        public FlareItem(FlareLight f) { _f = f; Position = f.transform.position; }
        public override bool Alive => _f != null;
        public override void Set(bool on) => _f.SetAlpha(on ? 1f : 0f);
    }

    /// <summary>Glowing surfaces Blackout blacked out with a property block - can only be switched back on.</summary>
    internal sealed class EmissiveSlotItem : PowerItem
    {
        private readonly Renderer _r;
        private readonly int _slot;
        public EmissiveSlotItem(Renderer r, int slot) { _r = r; _slot = slot; Position = r.bounds.center; }
        public override bool CanFlicker => false;
        public override bool Alive => _r != null;
        public override void Set(bool on) { if (on) _r.SetPropertyBlock(null, _slot); }
    }

    /// <summary>Light-fill zone renderers ("Inverted_Shadow") - switched back on with their area.</summary>
    internal sealed class ZoneRendererItem : PowerItem
    {
        private readonly Renderer _r;
        public ZoneRendererItem(Renderer r) { _r = r; Position = r.bounds.center; }
        public override bool CanFlicker => false;
        public override bool Alive => _r != null;
        public override void Set(bool on) { if (on) _r.enabled = true; }
    }

    internal sealed class StagedSettings
    {
        public int Stages = 4;
        public float GeneratorStart = 6f;
        public float BetweenStages = 3f;
        public bool Flicker = true;
        public bool VoiceAnnouncements = true;
        public bool Subtitles = true;
        public string Language = "ru";      // voice files: vo_start_ru / vo_start_en ...
        public bool Siren = true;

        // where the generators stand (B optional: one generator if unset)
        public Vector3 GenA;
        public Vector3? GenB;
        public float SecondUnitDelay = 4.5f;

        // hooks into the rest of the mod
        public Action OnCatch;               // engine fires up (noise)
        public Action OnSirenStart;          // siren + beacons
        public Action OnSirenTick;           // every ~3 s while the siren wails (noise)
        public Action OnFullPower;           // extracts, beacons off

        // generator B trips at sync -> partial power (only grid A comes back)
        public bool BTrip;
        public bool PartialVoice = true;     // false: generator B is damaged, no "reset the breakers" PA
        public Action OnBTrip;
        public Action OnPartialPower;
    }

    /// <summary>
    /// Labs split into two power grids along the east-west axis. Grid A (fed by generator A) is the
    /// side with the generator room; grid B is the other side. Same split every raid.
    /// </summary>
    internal static class PowerGrid
    {
        public static bool Ready { get; private set; }
        private static float _splitX;
        private static bool _aIsLow;

        public static void Compute(IEnumerable<Vector3> positions, Vector3 generatorRoom)
        {
            var xs = positions.Select(p => p.x).OrderBy(x => x).ToList();
            if (xs.Count < 10) { Ready = false; return; }
            _splitX = xs[xs.Count / 2];
            _aIsLow = generatorRoom.x < _splitX;
            Ready = true;
        }

        public static bool IsGridB(Vector3 p) => Ready && ((p.x < _splitX) != _aIsLow);

        public static void Reset() => Ready = false;

        public static string Describe() => Ready ? $"split at x={_splitX:0.0}, grid A = {(_aIsLow ? "west" : "east")} side" : "not set";
    }

    /// <summary>
    /// Power restore in waves: generator spins up, then the lights come back area by area,
    /// starting next to the generator room and spreading through the facility, with flicker.
    /// </summary>
    internal sealed class StagedRestore
    {
        private readonly BlackoutBridge _bo;
        private readonly SoundBank _sound;
        private readonly AmbientSnapshot _ambient;
        private readonly ManualLogSource _log;
        private readonly List<PowerItem> _items = new List<PowerItem>();
        private readonly System.Random _rng = new System.Random();

        public bool Running { get; private set; }

        /// <summary>Generator B tripped: grid B is still dark, waiting for a restart.</summary>
        public bool IsPartial { get; private set; }

        /// <summary>0 = no flicker, 1 = default, 2 = a lot.</summary>
        public float FlickerAmount = 1f;

        private readonly List<PowerItem> _pendingB = new List<PowerItem>();
        private bool _gridBHeld;   // grid B has no power right now (tripped / distribution fault)

        public void ResetRaid()
        {
            IsPartial = false;
            _pendingB.Clear();
            _gridBHeld = false;
            _items.Clear();
            _nearby.Clear();
            PowerGrid.Reset();
        }

        public StagedRestore(BlackoutBridge bo, SoundBank sound, AmbientSnapshot ambient, ManualLogSource log)
        {
            _bo = bo;
            _sound = sound;
            _ambient = ambient;
            _log = log;
        }

        public IEnumerator Run(Vector3 origin, StagedSettings cfg, Action<AudioSource> generatorLoopStarted)
        {
            Running = true;
            try
            {
                // ---- take over from Blackout: grab everything it switched off, keep it dark for now
                _items.Clear();
                CollectItems();
                _bo.StopEnforcement(_log);
                _bo.Call("RestoreLightsOut", _log); // our lists were emptied, so this only restores the fog cameras
                foreach (var it in _items) SafeSet(it, false);

                AssignStages(origin, Math.Max(1, cfg.Stages));
                IsPartial = false;
                _pendingB.Clear();
                _gridBHeld = false;
                if (cfg.GenB.HasValue)
                {
                    PowerGrid.Compute(_items.Select(i => i.Position), origin);
                    foreach (var it in _items) it.GridB = PowerGrid.IsGridB(it.Position);
                }
                _log.LogInfo($"[PowerRestore] Staged restore: {_items.Count} light sources in {cfg.Stages} waves " +
                             $"(grid B: {_items.Count(i => i.GridB)}, {PowerGrid.Describe()}{(cfg.BTrip ? ", generator B will trip" : "")}).");

                // ---- t = 0: generator start (the start sound has its own solenoid clack)
                float t0 = Time.time;
                _sound.Play3D("generator_spinup", cfg.GenA, 1f, 6f, 45f);
                StartDelayed(1.6f, () => cfg.OnCatch?.Invoke());   // engine catches ~1.6 s into the start sound
                if (cfg.GenB.HasValue)
                {
                    var b = cfg.GenB.Value;
                    StartDelayed(cfg.SecondUnitDelay, () => _sound.Play3D("generator_spinup", b, 1f, 6f, 45f));
                }
                if (cfg.VoiceAnnouncements)
                {
                    StartDelayed(3.0f, () => _sound.PlayVoice("vo_start_" + cfg.Language));
                }

                // alarm siren from when the alternator kicks in until just before the "restored" announcement
                float sirenAt = Mathf.Max(0.5f, cfg.GeneratorStart - 0.3f);
                float sirenEnd = sirenAt + Mathf.Max(3f, _sound.Length("siren"));
                if (cfg.Siren)
                {
                    StartDelayed(sirenAt, () =>
                    {
                        _sound.Play2D("siren", 0.9f);
                        cfg.OnSirenStart?.Invoke();
                    });
                }
                if (cfg.Subtitles)
                {
                    StartDelayed(3.8f, () => _bo.ShowAnnouncement("Attention, personnel! Main generator start sequence initiated. Stand by for power.", 6.5f));
                }

                // generator running loop takes over from the spin-up
                float spinLen = _sound.Length("generator_spinup");
                float loopAt = spinLen > 1.5f ? spinLen - 1.2f : cfg.GeneratorStart;

                // power surge (Blackout's power-down sound, reversed) peaks as the first wave comes on
                var src = _bo.PowerDownClip;
                var surge = SoundBank.Reverse(src, _log);
                if (surge != null)
                {
                    float surgeLen = src.length > 0.05f ? src.length : (src.frequency > 0 ? (float)src.samples / src.frequency : 3f);
                    _sound.Add("surge", surge, surgeLen);
                    float surgeAt = Mathf.Max(0.5f, cfg.GeneratorStart - surgeLen);
                    StartDelayed(surgeAt, () => _sound.Play2D("surge", 0.55f));
                }

                bool loopStarted = false, loopBStarted = !cfg.GenB.HasValue, synced = !cfg.GenB.HasValue, tripped = false;
                AudioSource loopB = null;
                float loopBAt = cfg.SecondUnitDelay + loopAt;
                float syncAt = cfg.SecondUnitDelay + Mathf.Max(spinLen, 6f) + 0.6f;
                int nextStage = 0;
                float finalAt = cfg.GeneratorStart + cfg.Stages * cfg.BetweenStages + 1f;
                if (cfg.Siren) finalAt = Mathf.Max(finalAt, sirenEnd - 1.5f);   // let the siren wind down first
                if (cfg.GenB.HasValue) finalAt = Mathf.Max(finalAt, syncAt + 1.5f);
                float nextSirenTick = sirenAt;

                while (true)
                {
                    float t = Time.time - t0;

                    if (!loopStarted && t >= loopAt)
                    {
                        loopStarted = true;
                        var loop = _sound.Play3D("generator_loop", cfg.GenA, 1f, 6f, 45f, loop: true);
                        _sound.FadeIn(loop, 1.2f);
                        generatorLoopStarted?.Invoke(loop);
                    }

                    // second unit: runs a touch fast (slow beating against A) until its breaker syncs it
                    if (!loopBStarted && t >= loopBAt)
                    {
                        loopBStarted = true;
                        loopB = _sound.Play3D("generator_loop", cfg.GenB.Value, 1f, 6f, 45f, loop: true);
                        if (loopB != null)
                        {
                            loopB.pitch = 1.004f;
                            loopB.time = Mathf.Min(2.7f, Mathf.Max(0f, _sound.Length("generator_loop") - 0.1f));   // not in phase with A
                        }
                        _sound.FadeIn(loopB, 1.2f);
                        generatorLoopStarted?.Invoke(loopB);
                    }
                    if (!synced && !tripped && loopBStarted && t >= syncAt)
                    {
                        var b = cfg.GenB.Value;
                        if (cfg.BTrip)
                        {
                            // B trips its breaker instead of syncing: bang, arc, engine shuts down
                            tripped = true;
                            _sound.Play3D("breaker_trip", b, 1f, 2f, 35f);
                            StartDelayed(0.25f, () => _sound.Play3D("spark1", b, 0.9f, 1.5f, 20f));
                            if (loopB != null) _sound.FadeOut(loopB, 0.3f);
                            _sound.Play3D("generator_spindown", b, 1f, 6f, 45f);
                            cfg.OnBTrip?.Invoke();
                            _log.LogInfo("[PowerRestore] Generator B TRIPPED during synchronisation - grid B stays dark.");
                        }
                        else
                        {
                            synced = true;
                            _sound.Play3D("breaker_near", b, 0.9f, 2f, 25f);
                            if (loopB != null) Plugin.StartCoroutine(GlidePitch(loopB, 1f, 0.5f));
                            _log.LogInfo("[PowerRestore] Generator B synchronised.");
                            // grid B areas whose wave already passed come on now
                            foreach (var it in _items.Where(i => i.GridB && !i.IsOn && i.Stage < nextStage))
                            {
                                Plugin.StartCoroutine(TurnOn(it, (float)_rng.NextDouble() * 1.5f, cfg.Flicker && it.CanFlicker));
                            }
                        }
                    }

                    while (nextStage < cfg.Stages && t >= cfg.GeneratorStart + nextStage * cfg.BetweenStages)
                    {
                        StartStage(nextStage, cfg, synced);
                        nextStage++;
                    }

                    // keep not-yet-restored lights dark (the culling system likes to switch them back on)
                    foreach (var it in _items)
                    {
                        if (!it.IsOn && (it.Stage >= nextStage || (it.GridB && !synced))) SafeSet(it, false);
                    }

                    if (cfg.Siren && t >= nextSirenTick && t < sirenEnd)
                    {
                        nextSirenTick = t + 3f;
                        cfg.OnSirenTick?.Invoke();
                    }

                    // ambient light creeps up with each wave
                    if (nextStage > 0) _ambient.Apply(Mathf.Clamp01((float)nextStage / cfg.Stages) * 0.85f);

                    if (t >= finalAt) break;
                    yield return null;
                }

                if (tripped)
                {
                    FinishPartial(cfg);
                    yield break;
                }

                // ---- full power
                foreach (var it in _items)
                {
                    if (!it.IsOn) { SafeSet(it, true); it.IsOn = true; }
                }
                _bo.Call("RestoreEmissiveMaterials", _log);
                _bo.Call("RestoreSpecular", _log);
                _bo.Call("RestoreAmbient", _log);
                _ambient.Apply(1f);
                _bo.Call("DestroyEmergencyLights", _log);

                cfg.OnFullPower?.Invoke();
                if (cfg.VoiceAnnouncements) _sound.PlayVoice("vo_restored_" + cfg.Language);
                if (cfg.Subtitles)
                {
                    StartDelayed(1.2f, () => _bo.ShowAnnouncement("Attention, personnel! Main power restored. All facility systems are back in service.", 7f));
                }
                _log.LogInfo("[PowerRestore] Main power fully restored.");
            }
            finally
            {
                Running = false;
            }
        }

        /// <summary>Only grid A is up: grid B stays on emergency lighting until B is restarted.</summary>
        private void FinishPartial(StagedSettings cfg)
        {
            foreach (var it in _items.Where(i => !i.GridB && !i.IsOn))
            {
                SafeSet(it, true);
                it.IsOn = true;
            }
            _pendingB.Clear();
            _pendingB.AddRange(_items.Where(i => i.GridB && !i.IsOn));
            _gridBHeld = true;
            foreach (var it in _pendingB) SafeSet(it, false);

            _bo.Call("RestoreEmissiveMaterials", _log);
            _bo.Call("RestoreSpecular", _log);
            _bo.Call("RestoreAmbient", _log);
            _ambient.Apply(0.6f);

            // emergency lamps stay on only in the dark half
            int off = 0;
            foreach (var l in _bo.PeekList<Light>("_emergencyLights"))
            {
                if (l != null && !PowerGrid.IsGridB(l.transform.position))
                {
                    l.enabled = false;
                    off++;
                }
            }

            IsPartial = true;
            cfg.OnPartialPower?.Invoke();
            if (cfg.VoiceAnnouncements && cfg.PartialVoice) _sound.PlayVoice("vo_partial_" + cfg.Language);
            if (cfg.Subtitles && cfg.PartialVoice)
            {
                StartDelayed(1.2f, () => _bo.ShowAnnouncement(
                    "Attention, personnel! Generator B synchronisation failure. Sector B is out of service. Reset the breakers in the switchgear room.", 9f));
            }
            _log.LogInfo($"[PowerRestore] PARTIAL POWER: grid A on, {_pendingB.Count} grid B light sources dark, {off} emergency lamps off in grid A.");
        }

        private float _nextKeepDark;

        /// <summary>While on partial power: the culling system can relight lamps, so keep grid B dark.</summary>
        public void KeepGridBDark()
        {
            if (Time.time < _nextKeepDark) return;
            _nextKeepDark = Time.time + 0.25f;
            foreach (var it in _pendingB)
            {
                if (!it.IsOn) SafeSet(it, false);
            }
        }

        /// <summary>Breakers reset, generator B restarted: bring grid B up in two waves.</summary>
        public IEnumerator RunRestartB(StagedSettings cfg, Action<AudioSource> loopStarted)
        {
            if (!cfg.GenB.HasValue) yield break;
            Running = true;
            try
            {
                var b = cfg.GenB.Value;
                float t0 = Time.time;
                float spinLen = Mathf.Max(6f, _sound.Length("generator_spinup"));
                float loopAt = spinLen - 1.2f, syncAt = spinLen + 0.6f;
                _sound.Play3D("generator_spinup", b, 1f, 6f, 45f);
                StartDelayed(1.6f, () => cfg.OnCatch?.Invoke());
                if (cfg.Subtitles)
                {
                    StartDelayed(2.5f, () => _bo.ShowAnnouncement("Generator B restart initiated. Synchronising with the main bus.", 6f));
                }

                // two waves, nearest to generator B first
                var ordered = _pendingB.OrderBy(i => (i.Position - b).sqrMagnitude).ToList();
                _gridBHeld = false;
                for (int i = 0; i < ordered.Count; i++) ordered[i].Stage = i * 2 / Math.Max(1, ordered.Count);
                float wave0 = syncAt + 0.6f;
                float finalAt = wave0 + 2 * cfg.BetweenStages + 1f;

                AudioSource loopB = null;
                bool loopOn = false, synced = false;
                int nextWave = 0;
                while (true)
                {
                    float t = Time.time - t0;
                    if (!loopOn && t >= loopAt)
                    {
                        loopOn = true;
                        loopB = _sound.Play3D("generator_loop", b, 1f, 6f, 45f, loop: true);
                        if (loopB != null) loopB.pitch = 1.004f;
                        _sound.FadeIn(loopB, 1.2f);
                        loopStarted?.Invoke(loopB);
                    }
                    if (!synced && t >= syncAt)
                    {
                        synced = true;
                        _sound.Play3D("breaker_near", b, 0.9f, 2f, 25f);
                        if (loopB != null) Plugin.StartCoroutine(GlidePitch(loopB, 1f, 0.5f));
                        _log.LogInfo("[PowerRestore] Generator B synchronised (restart).");
                    }
                    while (nextWave < 2 && t >= wave0 + nextWave * cfg.BetweenStages)
                    {
                        _sound.Play2D(nextWave == 0 ? "breaker_far1" : "breaker_far2", 0.9f);
                        _sound.Play2D("tube_flicker", 0.5f);
                        foreach (var it in ordered.Where(i => i.Stage == nextWave))
                        {
                            Plugin.StartCoroutine(TurnOn(it, (float)_rng.NextDouble() * cfg.BetweenStages * 0.75f, cfg.Flicker && it.CanFlicker));
                        }
                        _log.LogInfo($"[PowerRestore] Grid B wave {nextWave + 1}/2 coming online.");
                        nextWave++;
                    }
                    foreach (var it in ordered)
                    {
                        if (!it.IsOn && it.Stage >= nextWave) SafeSet(it, false);
                    }
                    if (synced) _ambient.Apply(Mathf.Lerp(0.6f, 0.95f, nextWave / 2f));
                    if (t >= finalAt) break;
                    yield return null;
                }

                foreach (var it in ordered)
                {
                    if (!it.IsOn) { SafeSet(it, true); it.IsOn = true; }
                }
                _pendingB.Clear();
                _ambient.Apply(1f);
                _bo.Call("DestroyEmergencyLights", _log);
                IsPartial = false;

                cfg.OnFullPower?.Invoke();
                if (cfg.VoiceAnnouncements) _sound.PlayVoice("vo_restored_" + cfg.Language);
                if (cfg.Subtitles)
                {
                    StartDelayed(1.2f, () => _bo.ShowAnnouncement("Attention, personnel! Main power restored. All facility systems are back in service.", 7f));
                }
                _log.LogInfo("[PowerRestore] Main power fully restored (generator B back online).");
            }
            finally
            {
                Running = false;
            }
        }

        private void StartStage(int stage, StagedSettings cfg, bool gridBUp)
        {
            string breaker = stage == 0 ? "breaker_far1" : stage == 1 ? "breaker_far2" : "breaker_far3";
            _sound.Play2D(breaker, 1f - 0.12f * stage);
            _sound.Play2D("tube_flicker", 0.6f - 0.1f * Mathf.Min(stage, 3));

            foreach (var it in _items.Where(i => i.Stage == stage && (!i.GridB || gridBUp)))
            {
                float delay = (float)_rng.NextDouble() * cfg.BetweenStages * 0.75f;
                Plugin.StartCoroutine(TurnOn(it, delay, cfg.Flicker && it.CanFlicker));
            }
            _log.LogInfo($"[PowerRestore] Wave {stage + 1}/{cfg.Stages} coming online.");
            // each new load makes the supply sag: everything already lit dips a couple of times
            if (cfg.Flicker && FlickerAmount > 0f && stage > 0) Plugin.StartCoroutine(SagLater(0.4f + (float)_rng.NextDouble() * 0.8f, null));
        }

        private IEnumerator SagLater(float delay, Func<PowerItem, bool> which)
        {
            yield return new WaitForSeconds(delay);
            Dip(which, 1 + _rng.Next(1 + Mathf.RoundToInt(FlickerAmount * 1.5f)));
        }

        /// <summary>A voltage dip: the lit lights (optionally only some) blink off together a few times.</summary>
        public void Dip(Func<PowerItem, bool> which, int blinks)
        {
            if (FlickerAmount <= 0f || _items.Count == 0) return;
            var lit = _items.Where(i => i.IsOn && i.CanFlicker && (which == null || which(i))).ToList();
            if (lit.Count > 0) Plugin.StartCoroutine(DipRoutine(lit, Math.Max(1, blinks)));
        }

        private IEnumerator DipRoutine(List<PowerItem> lit, int blinks)
        {
            for (int b = 0; b < blinks; b++)
            {
                foreach (var it in lit) if (it.IsOn) SafeSet(it, false);
                yield return new WaitForSeconds(0.05f + (float)_rng.NextDouble() * 0.09f);
                if (_bo.BlackoutActive) yield break;
                foreach (var it in lit) if (it.IsOn) SafeSet(it, true);
                yield return new WaitForSeconds(0.08f + (float)_rng.NextDouble() * 0.25f);
            }
        }

        private IEnumerator TurnOn(PowerItem it, float delay, bool flicker)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            float amt = Mathf.Max(0f, FlickerAmount);
            if (flicker && amt > 0f)
            {
                double roll = _rng.NextDouble();
                if (roll < 0.10 * amt)
                {
                    // late starter: stays dark a while, then struggles on
                    yield return new WaitForSeconds(3f + (float)_rng.NextDouble() * 5f);
                }
                if (roll < 0.35 * amt)
                {
                    // struggling tube: rapid stutter for a couple of seconds, a pause, then the normal start
                    float until = Time.time + 1.2f + (float)_rng.NextDouble() * 2f;
                    while (Time.time < until)
                    {
                        SafeSet(it, true);
                        yield return new WaitForSeconds(0.02f + (float)_rng.NextDouble() * 0.05f);
                        SafeSet(it, false);
                        yield return new WaitForSeconds(0.03f + (float)_rng.NextDouble() * 0.12f);
                    }
                    yield return new WaitForSeconds(0.3f + (float)_rng.NextDouble() * 1.2f);
                }
                int flashes = _rng.Next(3, 4 + Mathf.RoundToInt(4 * amt));
                for (int i = 0; i < flashes; i++)
                {
                    SafeSet(it, true);
                    yield return new WaitForSeconds(0.03f + (float)_rng.NextDouble() * 0.1f);
                    SafeSet(it, false);
                    yield return new WaitForSeconds(0.05f + (float)_rng.NextDouble() * 0.22f);
                }
            }
            if (_bo.BlackoutActive || (it.GridB && _gridBHeld)) yield break;   // power went away meanwhile
            SafeSet(it, true);
            it.IsOn = true;
        }

        // ------------------------------------------------------------ false start (burnt-out control)

        /// <summary>
        /// The generators start by themselves, the lights surge back flickering for a few seconds, then it all dies
        /// again. Nothing else comes back: extracts and card readers stay dead.
        /// </summary>
        // timings baked into the fs_* sounds (seconds from the start of the file)
        // fs_body: crank + catch + 31 s of rough running; fs_end_*: the last 2.4 s of running + the ending
        private const float FsCatch = 1.5f, FsLightsOn = 1.8f, FsHuntHz = 0.55f, FsEndPre = 2.4f;
        private static readonly float[] FsMisfires =                                  // after the catch (baked into fs_body)
            { 1.1f, 4.19f, 7.28f, 9.91f, 12.16f, 14.05f, 16.46f, 18.92f, 20.79f, 22.67f, 26.06f, 28.91f };
        private static readonly float[] FsCoughs = { 1.2f, 1.9f };                     // into fs_end_stall

        /// <summary>
        /// The generators try on their own: rough start, the lights struggle up and follow the engine (pulsing with
        /// its hunting, flickering on every misfire), then it stalls or trips the breaker and everything dies again.
        /// Extracts and card readers never come back.
        /// </summary>
        public IEnumerator FalseStart(Vector3 genA, Vector3? genB, bool stall, float lightsSeconds = 20f)
        {
            lightsSeconds = Mathf.Clamp(lightsSeconds, 4f, 30f);
            float fsDie = FsCatch + lightsSeconds;         // lights die (from the start of fs_body)
            float fsCut = fsDie - FsEndPre;                // the ending clip takes over here
            AudioSource body = null;
            Running = true;
            try
            {
                // borrow Blackout's lamps without taking them away: it keeps its lists, so going dark again is cheap
                _items.Clear();
                CollectItems(take: false);
                _bo.StopEnforcement(_log);
                foreach (var it in _items) SafeSet(it, false);
                _log.LogInfo($"[PowerRestore] False start ({(stall ? "stall" : "overload trip")}, lights ~{lightsSeconds:0} s): generators trying on their own ({_items.Count} light sources).");

                string v = stall ? "stall" : "trip";
                float t0 = Time.time;
                body = _sound.Play3D("fs_body", genA, 1f, 6f, 45f);
                bool ended = false;
                // no 2D facility layer: the generator sound plus Blackout's own power-down sound at the end
                yield return new WaitForSeconds(FsLightsOn);

                // power arrives: everything struggles on (the battery lamps switch off while it lasts)
                _bo.Call("DestroyEmergencyLights", _log);
                _ambient.Apply(0.45f);
                foreach (var it in _items)
                {
                    if (it.CanFlicker) Plugin.StartCoroutine(TurnOn(it, (float)_rng.NextDouble() * 0.5f, true));
                }

                var flick = _items.Where(i => i.CanFlicker).ToList();
                int nextMisfire = 0, nextCough = 0;
                float nextTrough = t0 + FsCatch + 2.5f;
                while (Time.time - t0 < fsDie && flick.Count > 0)
                {
                    float el = Time.time - t0;
                    // the engine is about to give up: cut over to the ending clip
                    if (!ended && el >= fsCut)
                    {
                        ended = true;
                        _sound.Play3D("fs_end_" + v, genA, 1f, 6f, 45f);
                        if (body != null) _sound.FadeOut(body, 0.3f);
                        body = null;
                    }
                    // misfire: the whole facility blinks with the engine
                    if (nextMisfire < FsMisfires.Length && el >= FsCatch + FsMisfires[nextMisfire])
                    {
                        nextMisfire++;
                        if (el < fsCut) Dip(null, 2 + _rng.Next(2));
                    }
                    // stall: coughs as it dies
                    if (stall && nextCough < FsCoughs.Length && el >= fsCut + FsCoughs[nextCough])
                    {
                        nextCough++;
                        Dip(null, 1 + _rng.Next(2));
                    }
                    // hunting governor: at every low point of the revs a share of the tubes sag out for a moment
                    if (Time.time >= nextTrough)
                    {
                        nextTrough += 1f / FsHuntHz;
                        int n = Math.Max(1, flick.Count / 6);
                        for (int i = 0; i < n; i++)
                        {
                            var it = flick[_rng.Next(flick.Count)];
                            if (it.IsOn) Plugin.StartCoroutine(Blink(it));
                        }
                    }
                    yield return null;
                }

                // the end: a stall stutters out, a breaker trip kills it at once
                foreach (var it in _items)
                {
                    if (!it.CanFlicker) continue;
                    it.IsOn = false;
                    if (stall) Plugin.StartCoroutine(DropOut(it));
                    else SafeSet(it, false);
                }
                yield return new WaitForSeconds(stall ? 0.45f : 0.05f);
            }
            finally
            {
                if (body != null) _sound.FadeOut(body, 0.5f);      // ended early (raid over etc.)
                // back to darkness: our lamps off, Blackout's enforcement back on (no full power cut = no hitch)
                foreach (var it in _items)
                {
                    it.IsOn = false;
                    SafeSet(it, false);
                }
                _items.Clear();
                _ambient.Apply(0f);
                _bo.ReArm(_log, powerDownSound: true);    // Blackout's original lights-out sound
                Running = false;
            }
        }

        private IEnumerator Blink(PowerItem it)
        {
            SafeSet(it, false);
            yield return new WaitForSeconds(0.04f + (float)_rng.NextDouble() * 0.2f);
            if (it.IsOn && !_bo.BlackoutActive) SafeSet(it, true);
        }

        // ------------------------------------------------------------ flicker while powered

        private readonly List<PowerItem> _nearby = new List<PowerItem>();
        private readonly HashSet<PowerItem> _flickering = new HashSet<PowerItem>();
        private float _nextNearbyScan, _nextAmbientFlicker;

        /// <summary>Old tubes near the player stutter now and then even with the power on.</summary>
        public void AmbientFlickerTick(Vector3 player)
        {
            if (Running || FlickerAmount <= 0f || _items.Count == 0 || _bo.BlackoutActive) return;
            if (Time.time >= _nextNearbyScan)
            {
                _nextNearbyScan = Time.time + 2f;
                _nearby.Clear();
                foreach (var it in _items)
                {
                    if (it.IsOn && it.CanFlicker && (it.Position - player).sqrMagnitude < 35f * 35f) _nearby.Add(it);
                }
            }
            if (_nearby.Count == 0 || Time.time < _nextAmbientFlicker) return;
            _nextAmbientFlicker = Time.time + (0.6f + (float)_rng.NextDouble() * 3f) / FlickerAmount;
            var pick = _nearby[_rng.Next(_nearby.Count)];
            if (!pick.IsOn || _flickering.Contains(pick)) return;
            Plugin.StartCoroutine(Stutter(pick));
        }

        private IEnumerator Stutter(PowerItem it)
        {
            _flickering.Add(it);
            try
            {
                if (_rng.NextDouble() < 0.12)
                {
                    // tube drops out for a few seconds, then struggles back
                    SafeSet(it, false);
                    yield return new WaitForSeconds(1f + (float)_rng.NextDouble() * 3f);
                }
                int n = _rng.Next(2, 7);
                for (int i = 0; i < n; i++)
                {
                    if (_bo.BlackoutActive || !it.IsOn) yield break;
                    SafeSet(it, false);
                    yield return new WaitForSeconds(0.02f + (float)_rng.NextDouble() * 0.07f);
                    if (_bo.BlackoutActive || !it.IsOn) yield break;
                    SafeSet(it, true);
                    yield return new WaitForSeconds(0.03f + (float)_rng.NextDouble() * 0.15f);
                }
                if (!_bo.BlackoutActive && it.IsOn) SafeSet(it, true);
            }
            finally
            {
                _flickering.Remove(it);
            }
        }

        // ------------------------------------------------------------ distribution fault (grid B on/off)

        /// <summary>Switch the whole grid B on or off (breaker fault / auto-reclose), with flicker.</summary>
        public void SetGridB(bool on)
        {
            if (!PowerGrid.Ready) return;
            var gridB = _items.Where(i => i.GridB).ToList();
            if (on)
            {
                _pendingB.Clear();
                _gridBHeld = false;
                foreach (var it in gridB)
                {
                    Plugin.StartCoroutine(TurnOn(it, (float)_rng.NextDouble() * 0.8f, it.CanFlicker));
                }
            }
            else
            {
                _pendingB.Clear();
                _pendingB.AddRange(gridB);
                _gridBHeld = true;
                foreach (var it in gridB)
                {
                    it.IsOn = false;
                    Plugin.StartCoroutine(DropOut(it));
                }
            }
        }

        private IEnumerator DropOut(PowerItem it)
        {
            // brief stutter as the supply collapses
            if (it.CanFlicker && _rng.NextDouble() < 0.5)
            {
                SafeSet(it, false);
                yield return new WaitForSeconds(0.04f + (float)_rng.NextDouble() * 0.06f);
                SafeSet(it, true);
                yield return new WaitForSeconds(0.03f + (float)_rng.NextDouble() * 0.1f);
            }
            SafeSet(it, false);
        }

        public void ClearPendingB()
        {
            _pendingB.Clear();
            _gridBHeld = false;
        }

        private static IEnumerator GlidePitch(AudioSource src, float to, float seconds)
        {
            float from = src != null ? src.pitch : to, t = 0f;
            while (src != null && t < seconds)
            {
                t += Time.deltaTime;
                src.pitch = Mathf.Lerp(from, to, t / seconds);
                yield return null;
            }
            if (src != null) src.pitch = to;
        }

        private void StartDelayed(float seconds, Action a) => Plugin.StartCoroutine(Delayed(seconds, a));

        private static IEnumerator Delayed(float seconds, Action a)
        {
            yield return new WaitForSeconds(seconds);
            a();
        }

        internal static MonoBehaviour Plugin;

        private void SafeSet(PowerItem it, bool on)
        {
            try
            {
                if (it.Alive) it.Set(on);
            }
            catch (Exception)
            {
                // object destroyed or culled away mid-sequence - ignore
            }
        }

        /// <summary>Closest areas to the generator room come back first.</summary>
        private void AssignStages(Vector3 origin, int stages)
        {
            var ordered = _items.OrderBy(i => (i.Position - origin).sqrMagnitude).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].Stage = Math.Min(stages - 1, i * stages / Math.Max(1, ordered.Count));
            }
        }

        private void CollectItems(bool take = true)
        {
            Func<string, ManualLogSource, List<object>> TakeList = take
                ? (Func<string, ManualLogSource, List<object>>)((n, l) => _bo.TakeList(n, l))
                : (n, l) => _bo.CopyList(n);
            foreach (var o in TakeList("_dimmedCullLights", _log))
                if (o is CullingLightObject c && c != null) _items.Add(new CullLightItem(c));
            foreach (var o in TakeList("_pendingCullLights", _log))
                if (o is CullingLightObject c && c != null) _items.Add(new CullLightItem(c));
            foreach (var o in TakeList("_dimmedCullAdvLights", _log))
                if (o is CullingAdvancedLightObject c && c != null) _items.Add(new CullAdvLightItem(c));
            foreach (var o in TakeList("_pendingCullAdvLights", _log))
                if (o is CullingAdvancedLightObject c && c != null) _items.Add(new CullAdvLightItem(c));

            foreach (var o in TakeList("_strayLights", _log))
            {
                var l = Field<Light>(o, "Light");
                if (l != null) _items.Add(new StrayLightItem(l, Field<float>(o, "Intensity"), Field<bool>(o, "Enabled")));
            }
            foreach (var o in TakeList("_strayBaseLights", _log))
            {
                var l = Field<BaseLight>(o, "Light");
                if (l != null) _items.Add(new StrayBaseLightItem(l, Field<float>(o, "Intensity")));
            }
            foreach (var o in TakeList("_dimmedVolumetrics", _log))
            {
                var v = Field<Behaviour>(o, "Volumetric");
                if (v != null) _items.Add(new VolumetricItem(v, Field<bool>(o, "Enabled")));
            }
            foreach (var o in TakeList("_dimmedFlares", _log))
                if (o is FlareLight f && f != null) _items.Add(new FlareItem(f));
            foreach (var o in TakeList("_blockedEmissives", _log))
            {
                var r = Field<Renderer>(o, "Renderer");
                if (r != null) _items.Add(new EmissiveSlotItem(r, Field<int>(o, "Slot")));
            }
            foreach (var o in TakeList("_disabledZoneRenderers", _log))
                if (o is Renderer r && r != null) _items.Add(new ZoneRendererItem(r));
        }

        private static readonly Dictionary<(Type, string), FieldInfo> FieldCache = new Dictionary<(Type, string), FieldInfo>();

        private static T Field<T>(object boxed, string name)
        {
            if (boxed == null) return default;
            var key = (boxed.GetType(), name);
            if (!FieldCache.TryGetValue(key, out var fi))
            {
                fi = boxed.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldCache[key] = fi;
            }
            var v = fi?.GetValue(boxed);
            return v is T t ? t : default;
        }
    }
}
