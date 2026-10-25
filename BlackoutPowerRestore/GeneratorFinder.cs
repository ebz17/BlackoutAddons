using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Finds the two big generator models near the generator room switch, so each engine's sound
    /// comes from its own machine without manual placement.
    /// </summary>
    internal static class GeneratorFinder
    {
        private static readonly string[] Keywords = { "generator", "diesel", "dizel", "genset", "dgu", "engine" };

        public static List<Vector3> Find(Vector3 near, ManualLogSource log)
        {
            var named = new List<(Vector3 c, float vol, string name)>();
            var big = new List<(Vector3 c, float vol, string name)>();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled) continue;
                var b = r.bounds;
                if ((b.center - near).sqrMagnitude > 40f * 40f || Mathf.Abs(b.center.y - near.y) > 5f) continue;
                float horiz = Mathf.Max(b.size.x, b.size.z);
                if (horiz < 3f || horiz > 14f || b.size.y < 1.2f || b.size.y > 5f) continue;
                string name = PathName(r.transform);
                float vol = b.size.x * b.size.y * b.size.z;
                big.Add((b.center, vol, name));
                string lower = name.ToLowerInvariant();
                if (Keywords.Any(k => lower.Contains(k))) named.Add((b.center, vol, name));
            }

            var picks = new List<(Vector3 c, float vol, string name)>();
            foreach (var cand in named.OrderByDescending(x => x.vol))
            {
                if (picks.All(p => (p.c - cand.c).sqrMagnitude > 3f * 3f)) picks.Add(cand);
                if (picks.Count == 2) break;
            }

            if (picks.Count > 0)
            {
                log.LogInfo($"[PowerRestore] Auto-found generator(s): {string.Join(" | ", picks.Select(p => $"{p.name} at ({p.c.x:0.0}, {p.c.y:0.0}, {p.c.z:0.0})"))}");
            }
            else
            {
                // tell us what's there so the default can be set later
                log.LogInfo("[PowerRestore] No generator models recognised near the switch. Large objects nearby: " +
                            string.Join(" | ", big.OrderByDescending(x => x.vol).Take(8).Select(x => $"{x.name} ({x.c.x:0.0}, {x.c.y:0.0}, {x.c.z:0.0})")));
            }
            return picks.OrderBy(p => (p.c - near).sqrMagnitude).Select(p => p.c).ToList();
        }

        private static string PathName(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; i < 3 && t != null; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
