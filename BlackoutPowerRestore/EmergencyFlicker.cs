using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Battery emergency lamps in an old facility: every so often one stutters, buzzes off for a moment
    /// or browns out. Blackout only forces their brightness, so toggling them on/off is safe.
    /// </summary>
    internal sealed class EmergencyFlicker
    {
        private readonly MonoBehaviour _host;
        private readonly System.Random _rng = new System.Random();
        private float _next;
        private bool _busy;
        public float Amount = 1f;

        public EmergencyFlicker(MonoBehaviour host) => _host = host;

        public void Tick(IList<Light> lamps)
        {
            if (_busy || lamps == null || lamps.Count == 0 || Time.time < _next) return;
            _next = Time.time + (1.2f + (float)_rng.NextDouble() * 3.5f) / Mathf.Max(0.2f, Amount);

            var pick = lamps[_rng.Next(lamps.Count)];
            if (pick == null) return;
            // lamps at the same spot (Blackout uses a spot + a point per lamp) flicker together
            var group = lamps.Where(l => l != null && (l.transform.position - pick.transform.position).sqrMagnitude < 0.5f).ToList();
            _host.StartCoroutine(Flicker(group));
        }

        private IEnumerator Flicker(List<Light> group)
        {
            _busy = true;
            int pattern = _rng.Next(3);
            if (pattern == 0)
            {
                // quick stutter
                int n = _rng.Next(3, 7);
                for (int i = 0; i < n; i++)
                {
                    Set(group, false);
                    yield return new WaitForSeconds(0.03f + (float)_rng.NextDouble() * 0.07f);
                    Set(group, true);
                    yield return new WaitForSeconds(0.04f + (float)_rng.NextDouble() * 0.12f);
                }
            }
            else if (pattern == 1)
            {
                // drops out for a moment, struggles back
                Set(group, false);
                yield return new WaitForSeconds(0.6f + (float)_rng.NextDouble() * 1.6f);
                for (int i = 0; i < 3; i++)
                {
                    Set(group, true);
                    yield return new WaitForSeconds(0.05f);
                    Set(group, false);
                    yield return new WaitForSeconds(0.08f + (float)_rng.NextDouble() * 0.15f);
                }
            }
            else
            {
                // single blink
                Set(group, false);
                yield return new WaitForSeconds(0.08f);
            }
            Set(group, true);
            _busy = false;
        }

        public void StopAll(IList<Light> lamps)
        {
            _busy = false;
            if (lamps == null) return;
            foreach (var l in lamps)
            {
                if (l != null) l.enabled = true;
            }
        }

        private static void Set(List<Light> group, bool on)
        {
            foreach (var l in group)
            {
                if (l != null) l.enabled = on;
            }
        }
    }
}
