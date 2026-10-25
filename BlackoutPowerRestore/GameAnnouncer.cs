using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// The Labs announcer's own lines, taken from the player's game at runtime (nothing is shipped with the mod).
    /// Scan: lists the clips that look like announcer lines in the log once per raid.
    /// Play: plays a clip by its game name through the facility PA speakers.
    /// </summary>
    internal static class GameAnnouncer
    {
        private static readonly string[] Hints =
            { "disclam", "announc", "speaker", "voice", "lift", "elevat", "sector", "secur", "alarm", "attention", "vnimanie", "pa_", "_pa" };

        private static bool _scanned;
        private static ManualLogSource _log;
        private static readonly Dictionary<string, AudioClip> Cache = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);

        public static void Init(ManualLogSource log) => _log = log;

        public static void ResetRaid()
        {
            _scanned = false;
            Cache.Clear();
        }

        /// <summary>Once per Labs raid: log what the facility speakers and the game have loaded.</summary>
        public static void Scan(IEnumerable<string> ownSounds)
        {
            if (_scanned) return;
            _scanned = true;
            try
            {
                var own = new HashSet<string>(ownSounds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                var lines = new List<string>();

                foreach (var src in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                {
                    if (src == null || !src.gameObject.name.StartsWith("sound_disclamer")) continue;
                    var c = src.clip;
                    lines.Add($"speaker '{src.gameObject.name}': clip '{(c != null ? c.name : "none")}' {(c != null ? c.length : 0f):0.0}s");
                }

                var clips = Resources.FindObjectsOfTypeAll<AudioClip>()
                    .Where(c => c != null && !string.IsNullOrEmpty(c.name) && !own.Contains(c.name))
                    .GroupBy(c => c.name).Select(g => g.First())
                    .ToList();
                var hits = clips.Where(c => Hints.Any(h => c.name.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(c => c.name).ToList();
                foreach (var c in hits) lines.Add($"clip '{c.name}' {c.length:0.0}s");

                _log?.LogInfo($"[PowerRestore] Announcer scan: {lines.Count} candidate(s) out of {clips.Count} loaded sounds:\n  " + string.Join("\n  ", lines));
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[PowerRestore] Announcer scan failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Queues one game line or a composite ("part1+part2+part3", like the game builds its own
        /// "security ... sector B ... 11" lines; "b|y" picks one at random) as a single PA announcement.
        /// </summary>
        public static void Speak(SoundBank sound, string spec)
        {
            if (string.IsNullOrWhiteSpace(spec) || sound == null) return;
            var names = new List<string>();
            foreach (var part in spec.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                var choices = part.Split('|');
                string name = choices[UnityEngine.Random.Range(0, choices.Length)].Trim();
                var key = Register(sound, name);
                if (key == null) return;
                names.Add(key);
            }
            sound.PlayVoiceChain(names);
        }

        /// <summary>Makes a game clip playable through the sound bank; returns its key or null.</summary>
        internal static string Register(SoundBank sound, string clipName)
        {
            if (!Cache.TryGetValue(clipName, out var clip) || clip == null)
            {
                clip = Resources.FindObjectsOfTypeAll<AudioClip>().FirstOrDefault(c => c != null && string.Equals(c.name, clipName, StringComparison.OrdinalIgnoreCase));
                if (clip == null)
                {
                    _log?.LogWarning($"[PowerRestore] game announcer clip '{clipName}' is not loaded in this raid.");
                    return null;
                }
                Cache[clipName] = clip;
            }
            string key = "game:" + clipName;
            sound.Add(key, clip, clip.length);
            return key;
        }

        /// <summary>Plays a game clip (by its name) at a spot in the world, with our own range. False if it isn't loaded.</summary>
        public static bool Play3D(SoundBank sound, string clipName, Vector3 pos, float volume, float minDist, float maxDist)
        {
            if (string.IsNullOrWhiteSpace(clipName) || sound == null) return false;
            var key = Register(sound, clipName.Trim());
            return key != null && sound.Play3D(key, pos, volume, minDist, maxDist) != null;
        }

        /// <summary>Plays a game clip (by its name) through the PA. False if it isn't loaded.</summary>
        public static bool Play(SoundBank sound, string clipName)
        {
            if (string.IsNullOrWhiteSpace(clipName) || sound == null) return false;
            clipName = clipName.Trim();
            if (!Cache.TryGetValue(clipName, out var clip) || clip == null)
            {
                clip = Resources.FindObjectsOfTypeAll<AudioClip>().FirstOrDefault(c => c != null && string.Equals(c.name, clipName, StringComparison.OrdinalIgnoreCase));
                if (clip == null)
                {
                    _log?.LogWarning($"[PowerRestore] game announcer clip '{clipName}' is not loaded in this raid.");
                    return false;
                }
                Cache[clipName] = clip;
            }
            string key = "game:" + clipName;
            sound.Add(key, clip, clip.length);
            sound.PlayVoice(key);
            return true;
        }
    }
}
