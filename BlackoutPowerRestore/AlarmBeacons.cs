using System.Collections.Generic;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>Rotating red warning beacons in the corridors while the power comes back.</summary>
    internal sealed class AlarmBeacons
    {
        private readonly List<Transform> _heads = new List<Transform>();
        private readonly List<Light> _glows = new List<Light>();
        private readonly List<GameObject> _roots = new List<GameObject>();
        private float _fade;
        private bool _stopping;

        public bool Active => _roots.Count > 0;

        public void Spawn(IEnumerable<Vector3> positions)
        {
            Remove();
            int i = 0;
            foreach (var p in positions)
            {
                var root = new GameObject("PowerRestore_beacon");
                root.transform.position = p + Vector3.down * 0.25f;

                var head = new GameObject("head").transform;
                head.SetParent(root.transform, false);
                head.localRotation = Quaternion.Euler(18f, i * 77f, 0f);   // tilted down, staggered so they don't sweep in sync

                var spot = head.gameObject.AddComponent<Light>();
                spot.type = LightType.Spot;
                spot.color = new Color(1f, 0.06f, 0.02f);
                spot.spotAngle = 60f;
                spot.range = 18f;
                spot.intensity = 0f;
                spot.shadows = LightShadows.None;

                var glow = root.AddComponent<Light>();
                glow.type = LightType.Point;
                glow.color = new Color(1f, 0.08f, 0.03f);
                glow.range = 3f;
                glow.intensity = 0f;
                glow.shadows = LightShadows.None;

                _roots.Add(root);
                _heads.Add(head);
                _glows.Add(glow);
                i++;
            }
            _fade = 0f;
            _stopping = false;
        }

        public void Stop() => _stopping = true;

        public void Tick(float dt)
        {
            if (_roots.Count == 0) return;
            _fade = Mathf.Clamp01(_fade + (_stopping ? -dt / 1.2f : dt / 0.6f));
            if (_stopping && _fade <= 0f)
            {
                Remove();
                return;
            }
            for (int i = 0; i < _heads.Count; i++)
            {
                if (_heads[i] == null) continue;
                _heads[i].Rotate(Vector3.up, 330f * dt, Space.World);   // ~1 revolution per second
                var spot = _heads[i].GetComponent<Light>();
                if (spot != null)
                {
                    spot.enabled = true;                 // power-cut scans switch "stray" lights off - not ours
                    spot.intensity = 3.2f * _fade * DangerFlicker.Factor(_heads[i].position);   // Tagilla close: they stutter
                }
                if (_glows[i] != null)
                {
                    _glows[i].enabled = true;
                    _glows[i].intensity = (1.2f + 0.4f * Mathf.Sin(Time.time * 12f)) * _fade * DangerFlicker.Factor(_glows[i].transform.position);
                }
            }
        }

        public void Remove()
        {
            foreach (var r in _roots)
            {
                if (r != null) Object.Destroy(r);
            }
            _roots.Clear();
            _heads.Clear();
            _glows.Clear();
        }
    }
}
