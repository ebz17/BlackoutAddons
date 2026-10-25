using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Loads the WAVs from the plugin's sounds folder and plays them in the world or on the HUD.
    /// While a PA announcement plays, every other sound (generator, siren...) is ducked so the voice is clear.
    /// </summary>
    internal sealed class SoundBank
    {
        private sealed class LiveSound
        {
            public AudioSource Src;
            public float Base;          // requested volume (before master volume)
            public float Fade = 1f;     // 0..1 fade in/out
            public float FadeTarget = 1f;
            public float FadeSpeed;     // per second, 0 = no fade running
            public bool DestroyAtZero;
            public bool Voice;

            // world sounds: walls/floors between you and the source muffle it
            public bool World;
            public AudioLowPassFilter Lpf;
            public float Occl;          // 0 = clear line, 1 = fully blocked (smoothed)
            public float OcclTarget;
            public float NextCheck;
        }

        private AudioListener _listener;
        private int _occluderMask;
        private readonly RaycastHit[] _hits = new RaycastHit[16];
        public bool Occlusion = true;

        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        // Real durations, read from the WAV files. clip.length reports 0 in-game for mod-made clips, so never trust it.
        private readonly Dictionary<string, float> _durations = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<LiveSound> _live = new List<LiveSound>();
        private readonly ManualLogSource _log;
        private readonly MonoBehaviour _host;
        private float _duck = 1f;
        private float _voiceUntil;

        // PA queue: one announcement at a time, a short pause between them, stale ones dropped
        private sealed class VoiceJob
        {
            public List<string> Names;
            public float Volume;
            public float QueuedAt;
        }
        private readonly Queue<VoiceJob> _voiceQueue = new Queue<VoiceJob>();
        private bool _voiceBusy;
        private float _voiceFreeAt;
        public float VoiceGap = 3f;       // pause between two announcements
        public float VoiceMaxWait = 10f;  // a line that would wait longer than this is out of date - dropped

        // the game's own audio channels (sounds outside them are silent / ignore the volume settings)
        private AudioMixerGroup _worldGroup;
        private AudioMixerGroup _ambientGroup;
        private bool _groupsResolved;

        public float Volume = 0.8f;
        public float VoiceVolume = 1f;
        public float DuckLevel = 0.35f;   // other sounds drop to 35% while the PA speaks

        public bool UsePaSpeakers = true;

        public SoundBank(MonoBehaviour host, ManualLogSource log)
        {
            _host = host;
            _log = log;
        }

        /// <summary>Finds the game's mixer channels: machinery for world sounds, indoor ambient effects for the rest.</summary>
        private void ResolveGroups()
        {
            if (_groupsResolved && _worldGroup != null && _ambientGroup != null) return;
            _groupsResolved = true;
            try
            {
                var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
                if (ba != null)
                {
                    _worldGroup = ba.EnvTechnicalSoundsGroup ?? ba.SimpleOccludedMixerGroup ?? ba.WorldMixer ?? ba.MasterMixerGroup;
                    _ambientGroup = ba.CommonAmbientInEffectsMixer ?? ba.CommonAmbientEffectsMixer ?? ba.NonSpatialBypassMixer ?? ba.MasterMixerGroup;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] could not read the game's audio channels: {ex.Message}");
            }
            if (_ambientGroup == null || _worldGroup == null)
            {
                // same fallback Blackout uses
                foreach (var g in Resources.FindObjectsOfTypeAll<AudioMixerGroup>())
                {
                    if (g != null && g.name == "CommonAmbInEffects")
                    {
                        _ambientGroup = _ambientGroup ?? g;
                        _worldGroup = _worldGroup ?? g;
                    }
                }
            }
            _log.LogInfo($"[PowerRestore] audio channels: world='{_worldGroup?.name ?? "none"}', ambient='{_ambientGroup?.name ?? "none"}'");
        }

        /// <summary>Loads every WAV with Unity's own decoder (fallback: our reader). Run as a coroutine.</summary>
        public IEnumerator LoadFolderAsync(string folder)
        {
            if (!Directory.Exists(folder))
            {
                _log.LogWarning($"[PowerRestore] sounds folder missing ({folder}) - restore will be silent.");
                yield break;
            }
            var report = new List<string>();
            foreach (var file in Directory.GetFiles(folder, "*.wav"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                float duration = WavLoader.Duration(file);
                AudioClip clip = null;
                string how = "unity";

                var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri, AudioType.WAV);
                if (req.downloadHandler is DownloadHandlerAudioClip dh)
                {
                    dh.streamAudio = false;
                    dh.compressed = false;
                }
                yield return req.SendWebRequest();
                try
                {
                    if (string.IsNullOrEmpty(req.error)) clip = DownloadHandlerAudioClip.GetContent(req);
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"[PowerRestore] unity decoder failed for {name}: {ex.Message}");
                }
                req.Dispose();

                if (clip == null || clip.samples <= 0)
                {
                    try
                    {
                        clip = WavLoader.Load(file);
                        how = "fallback";
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning($"[PowerRestore] could not load {name}: {ex.Message}");
                        continue;
                    }
                }

                clip.name = name;
                Protect(clip);
                _clips[name] = clip;
                _files[name] = file;
                _durations[name] = duration > 0f ? duration : SafeLength(clip);
                report.Add($"{name} {_durations[name]:0.0}s ({how}, clip says {clip.length:0.0}s/{clip.samples} samples)");
            }
            _log.LogInfo($"[PowerRestore] {_clips.Count} sound(s) loaded from {folder}: {string.Join("; ", report)}");
        }

        /// <summary>The game clears "unused" assets when a raid loads - our clips must survive that.</summary>
        private static void Protect(AudioClip clip)
        {
            if (clip != null) clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        }

        private static bool Usable(AudioClip clip)
        {
            try
            {
                return clip != null && clip.samples > 0 && clip.frequency > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Returns a playable clip, reloading it from disk if the game wiped it.</summary>
        private AudioClip GetClip(string name)
        {
            if (!_clips.TryGetValue(name, out var clip)) return null;
            if (Usable(clip)) return clip;
            if (!_files.TryGetValue(name, out var file)) return null;
            try
            {
                var fresh = WavLoader.Load(file);
                fresh.name = name;
                Protect(fresh);
                _clips[name] = fresh;
                _log.LogInfo($"[PowerRestore] sound '{name}' had been unloaded by the game - reloaded from disk ({fresh.samples} samples).");
                return Usable(fresh) ? fresh : null;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] could not reload '{name}': {ex.Message}");
                return null;
            }
        }

        /// <summary>Call at raid start: makes sure every sound is still there before it's needed.</summary>
        public void VerifyAll()
        {
            int reloaded = 0, ok = 0;
            foreach (var name in new List<string>(_clips.Keys))
            {
                bool wasUsable = Usable(_clips[name]);
                if (GetClip(name) != null)
                {
                    if (wasUsable) ok++;
                    else reloaded++;
                }
            }
            _log.LogInfo($"[PowerRestore] raid sound check: {ok} ok, {reloaded} reloaded, {_clips.Count - ok - reloaded} missing.");
        }

        private static float SafeLength(AudioClip c)
        {
            if (c == null) return 0f;
            if (c.length > 0.05f) return c.length;
            return c.frequency > 0 && c.samples > 0 ? (float)c.samples / c.frequency : 0f;
        }

        public void Add(string name, AudioClip clip, float duration)
        {
            if (clip == null) return;
            Protect(clip);
            _clips[name] = clip;
            _durations[name] = duration > 0.05f ? duration : SafeLength(clip);
        }

        public bool Has(string name) => _clips.ContainsKey(name);

        public IEnumerable<string> Names => new List<string>(_clips.Keys);

        public float Length(string name) => _durations.TryGetValue(name, out var d) ? d : 0f;

        /// <summary>Non-positional effect (distant breakers, siren heard through the building).</summary>
        public AudioSource Play2D(string name, float volume = 1f) => Play(name, null, volume, false, 0f, 0f, false);

        /// <summary>
        /// PA announcement: through Labs' own ceiling speakers (like the game's announcer) plus a quieter
        /// copy everywhere, so it's heard wherever you are. Everything else ducks while it plays.
        /// </summary>
        /// <summary>Queues one PA announcement.</summary>
        public void PlayVoice(string name, float volume = 1f) => PlayVoiceChain(new List<string> { name }, volume);

        /// <summary>Queues clips that must play back to back as one announcement (the game's composite lines).</summary>
        public void PlayVoiceChain(List<string> names, float volume = 1f)
        {
            if (names == null || names.Count == 0) return;
            var job = new VoiceJob { Names = names, Volume = volume, QueuedAt = Time.time };
            if (!_voiceBusy && _voiceQueue.Count == 0 && Time.time >= _voiceFreeAt) StartVoiceJob(job);
            else
            {
                _voiceQueue.Enqueue(job);
                _log.LogInfo($"[PowerRestore] PA busy - '{string.Join("+", names)}' queued.");
            }
        }

        private void StartVoiceJob(VoiceJob job)
        {
            _voiceBusy = true;
            _host.StartCoroutine(RunVoiceJob(job));
        }

        private IEnumerator RunVoiceJob(VoiceJob job)
        {
            try
            {
                foreach (var n in job.Names)
                {
                    PlayVoiceNow(n, job.Volume);
                    yield return new WaitForSeconds(Mathf.Max(0.2f, Length(n)) + 0.04f);
                }
            }
            finally
            {
                _voiceBusy = false;
                _voiceFreeAt = Time.time + VoiceGap;
            }
        }

        private void PumpVoiceQueue()
        {
            while (_voiceQueue.Count > 0 && !_voiceBusy && Time.time >= _voiceFreeAt)
            {
                var job = _voiceQueue.Dequeue();
                if (Time.time - job.QueuedAt > VoiceMaxWait)
                {
                    _log.LogInfo($"[PowerRestore] PA line '{string.Join("+", job.Names)}' dropped (waited too long, out of date).");
                    continue;
                }
                StartVoiceJob(job);
            }
        }

        private void PlayVoiceNow(string name, float volume = 1f)
        {
            var clip = GetClip(name);
            if (clip == null)
            {
                _log.LogWarning($"[PowerRestore] sound '{name}' not found in the sounds folder.");
                return;
            }
            float len = Length(name);
            _voiceUntil = Time.time + len;
            int speakers = 0;
            if (UsePaSpeakers)
            {
                foreach (var sp in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                {
                    if (sp != null && sp.gameObject.name.StartsWith("sound_disclamer"))
                    {
                        _host.StartCoroutine(ThroughSpeaker(sp, clip, len, volume));
                        speakers++;
                    }
                }
            }
            // everywhere copy: full level if there are no speakers, softer if the speakers carry it
            Play(name, null, volume * (speakers > 0 ? 0.55f : 1f), false, 0f, 0f, true);
            _log.LogInfo($"[PowerRestore] PA announcement '{name}' ({len:0.0}s) via {speakers} facility speakers.");
        }

        private IEnumerator ThroughSpeaker(AudioSource speaker, AudioClip clip, float length, float volume)
        {
            var oldClip = speaker.clip;
            float oldPitch = speaker.pitch, oldVolume = speaker.volume;
            bool oldLoop = speaker.loop;
            speaker.Stop();
            speaker.clip = clip;
            speaker.pitch = 1f;
            speaker.loop = false;
            speaker.volume = Mathf.Clamp01(Mathf.Max(oldVolume, 0.6f) * volume * VoiceVolume);
            speaker.Play();
            yield return new WaitForSeconds(length + 0.1f);
            if (speaker == null) yield break;
            if (speaker.clip == clip)
            {
                speaker.Stop();
                speaker.clip = oldClip;
                speaker.pitch = oldPitch;
                speaker.volume = oldVolume;
                speaker.loop = oldLoop;
            }
        }

        /// <summary>Positional (generator, switch).</summary>
        public AudioSource Play3D(string name, Vector3 pos, float volume = 1f, float minDist = 3f, float maxDist = 60f, bool loop = false) =>
            Play(name, pos, volume, loop, minDist, maxDist, false);

        private AudioSource Play(string name, Vector3? pos, float volume, bool loop, float minDist, float maxDist, bool voice)
        {
            var clip = GetClip(name);
            if (clip == null)
            {
                _log.LogWarning($"[PowerRestore] sound '{name}' not found in the sounds folder.");
                return null;
            }

            ResolveGroups();
            var go = new GameObject("PowerRestore_" + name);
            var src = go.AddComponent<AudioSource>();
            src.outputAudioMixerGroup = pos.HasValue ? _worldGroup : _ambientGroup;
            src.clip = clip;
            src.loop = loop;
            src.playOnAwake = false;
            src.dopplerLevel = 0f;
            src.priority = voice ? 0 : 64;          // voice must never be culled
            src.bypassReverbZones = voice;
            if (pos.HasValue)
            {
                go.transform.position = pos.Value;
                src.spatialBlend = 1f;
                src.spread = maxDist >= 40f ? 90f : 0f;     // big machines: sound comes from an area, not a point
                src.minDistance = minDist;
                src.maxDistance = maxDist;
                // natural falloff: full volume up close, drops fast, silent at maxDistance
                src.rolloffMode = AudioRolloffMode.Custom;
                float m = Mathf.Clamp01(minDist / maxDist);
                src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, new AnimationCurve(
                    new Keyframe(0f, 1f), new Keyframe(m, 1f), new Keyframe(Mathf.Lerp(m, 1f, 0.15f), 0.45f),
                    new Keyframe(Mathf.Lerp(m, 1f, 0.35f), 0.18f), new Keyframe(Mathf.Lerp(m, 1f, 0.65f), 0.05f),
                    new Keyframe(1f, 0f)));
            }
            else
            {
                src.spatialBlend = 0f;
            }

            var live = new LiveSound { Src = src, Base = volume, Voice = voice, World = pos.HasValue };
            if (live.World)
            {
                live.Lpf = go.AddComponent<AudioLowPassFilter>();
                live.Lpf.cutoffFrequency = 22000f;
                UpdateOcclusion(live, true);
            }
            _live.Add(live);
            Apply(live);
            src.Play();
            if (!loop) UnityEngine.Object.Destroy(go, Length(name) + 0.5f);
            _log.LogInfo($"[PowerRestore] play '{name}' {(pos.HasValue ? "3D" : "2D")}{(loop ? " loop" : "")} {Length(name):0.0}s " +
                         $"vol {src.volume:0.00} channel '{src.outputAudioMixerGroup?.name ?? "none"}' samples {clip.samples} playing={src.isPlaying}");
            return src;
        }

        /// <summary>Call every frame: fades, ducking under announcements, clean-up.</summary>
        public void Tick(float dt)
        {
            bool voicePlaying = false;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var l = _live[i];
                if (l.Src == null)
                {
                    _live.RemoveAt(i);
                    continue;
                }
                if (l.Voice && l.Src.isPlaying) voicePlaying = true;
            }
            if (Time.time < _voiceUntil) voicePlaying = true;
            PumpVoiceQueue();

            float duckTarget = voicePlaying ? DuckLevel : 1f;
            float duckSpeed = voicePlaying ? 3f : 0.8f;     // duck fast, come back gently
            _duck = Mathf.MoveTowards(_duck, duckTarget, duckSpeed * dt);

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var l = _live[i];
                if (l.FadeSpeed > 0f)
                {
                    l.Fade = Mathf.MoveTowards(l.Fade, l.FadeTarget, l.FadeSpeed * dt);
                    if (Mathf.Approximately(l.Fade, l.FadeTarget)) l.FadeSpeed = 0f;
                    if (l.DestroyAtZero && l.Fade <= 0f)
                    {
                        l.Src.Stop();
                        UnityEngine.Object.Destroy(l.Src.gameObject);
                        _live.RemoveAt(i);
                        continue;
                    }
                }
                if (l.World)
                {
                    if (Time.time >= l.NextCheck) UpdateOcclusion(l, false);
                    l.Occl = Mathf.MoveTowards(l.Occl, l.OcclTarget, 2.5f * dt);
                }
                Apply(l);
            }
        }

        private void Apply(LiveSound l)
        {
            float master = l.Voice ? VoiceVolume : Volume * _duck;
            float occl = 1f;
            if (l.World)
            {
                occl = Mathf.Lerp(1f, 0.16f, l.Occl);
                if (l.Lpf != null) l.Lpf.cutoffFrequency = Mathf.Lerp(22000f, 450f, Mathf.Pow(l.Occl, 0.6f));
            }
            l.Src.volume = Mathf.Clamp01(l.Base * l.Fade * master * occl);
        }

        /// <summary>Counts walls/floors between the listener and the sound (up to 3).</summary>
        private void UpdateOcclusion(LiveSound l, bool snap)
        {
            l.NextCheck = Time.time + 0.2f;
            if (!Occlusion || l.Src == null)
            {
                l.OcclTarget = 0f;
                if (snap) l.Occl = 0f;
                return;
            }
            if (_listener == null) _listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
            if (_listener == null) return;
            if (_occluderMask == 0)
            {
                _occluderMask = LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain");
                if (_occluderMask == 0) _occluderMask = Physics.DefaultRaycastLayers;
            }

            Vector3 from = _listener.transform.position;
            Vector3 to = l.Src.transform.position;
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            int walls = 0;
            if (dist > 0.6f)
            {
                int n = Physics.RaycastNonAlloc(from, dir / dist, _hits, dist - 0.3f, _occluderMask, QueryTriggerInteraction.Ignore);
                var seen = new HashSet<Collider>();
                for (int i = 0; i < n && walls < 3; i++)
                {
                    var c = _hits[i].collider;
                    // ignore whatever is right next to the source (a generator doesn't muffle its own sound)
                    if (c == null || _hits[i].distance < 0.4f || _hits[i].distance > dist - 4.5f || !seen.Add(c)) continue;
                    if (c.GetComponentInParent<EFT.Player>() != null) continue;   // bodies don't block sound much
                    walls++;
                }
            }
            l.OcclTarget = Mathf.Clamp01(walls * 0.42f);
            if (snap) l.Occl = l.OcclTarget;
        }

        private LiveSound Find(AudioSource src) => _live.Find(l => l.Src == src);

        public void FadeOut(AudioSource src, float seconds)
        {
            var l = Find(src);
            if (l == null) return;
            l.FadeTarget = 0f;
            l.FadeSpeed = 1f / Mathf.Max(0.05f, seconds);
            l.DestroyAtZero = true;
        }

        public void FadeIn(AudioSource src, float seconds)
        {
            var l = Find(src);
            if (l == null) return;
            l.Fade = 0f;
            l.FadeTarget = 1f;
            l.FadeSpeed = 1f / Mathf.Max(0.05f, seconds);
            Apply(l);
        }

        public void StopAll()
        {
            _voiceQueue.Clear();
            _voiceBusy = false;
            _voiceFreeAt = 0f;
            foreach (var l in _live)
            {
                if (l.Src != null) UnityEngine.Object.Destroy(l.Src.gameObject);
            }
            _live.Clear();
            _duck = 1f;
        }

        /// <summary>Plays a clip backwards (Blackout's power-down sound reversed = power surge).</summary>
        public static AudioClip Reverse(AudioClip clip, ManualLogSource log)
        {
            if (clip == null) return null;
            try
            {
                if (clip.loadState != AudioDataLoadState.Loaded) clip.LoadAudioData();
                var data = new float[clip.samples * clip.channels];
                if (!clip.GetData(data, 0)) return null;
                int ch = clip.channels;
                var rev = new float[data.Length];
                int frames = clip.samples;
                for (int f = 0; f < frames; f++)
                {
                    for (int c = 0; c < ch; c++)
                    {
                        rev[f * ch + c] = data[(frames - 1 - f) * ch + c];
                    }
                }
                var outClip = AudioClip.Create(clip.name + "_reversed", frames, ch, clip.frequency, false);
                outClip.SetData(rev, 0);
                return outClip;
            }
            catch (Exception ex)
            {
                log.LogInfo($"[PowerRestore] could not reverse '{clip.name}' ({ex.Message}) - no power surge sound.");
                return null;
            }
        }
    }
}
