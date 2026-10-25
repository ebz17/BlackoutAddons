using System.Collections.Generic;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Blackout zeroes Labs' ambient light (spherical harmonics shader globals, AmbientLight
    /// reflection intensity, RenderSettings) every frame and never puts it back mid-raid, because
    /// normally the map just unloads. We take a copy while the lights are still on and put it back
    /// when power is restored.
    /// </summary>
    internal sealed class AmbientSnapshot
    {
        private static readonly int[] GlobalIds =
        {
            Shader.PropertyToID("_SHAr"), Shader.PropertyToID("_SHAg"), Shader.PropertyToID("_SHAb"),
            Shader.PropertyToID("_SHBr"), Shader.PropertyToID("_SHBg"), Shader.PropertyToID("_SHBb"),
            Shader.PropertyToID("_SHC"), Shader.PropertyToID("_EFT_Ambient"),
        };

        private readonly Vector4[] _globals = new Vector4[GlobalIds.Length];
        private readonly Dictionary<AmbientLight, float> _reflection = new Dictionary<AmbientLight, float>();
        private float _rsAmbientIntensity;
        private Color _rsAmbientLight;
        private float _rsReflectionIntensity;

        public bool Taken { get; private set; }

        public void Take()
        {
            for (int i = 0; i < GlobalIds.Length; i++)
            {
                _globals[i] = Shader.GetGlobalVector(GlobalIds[i]);
            }

            _reflection.Clear();
            foreach (var amb in Object.FindObjectsOfType<AmbientLight>())
            {
                if (amb != null) _reflection[amb] = amb.ReflectionIntensity;
            }

            _rsAmbientIntensity = RenderSettings.ambientIntensity;
            _rsAmbientLight = RenderSettings.ambientLight;
            _rsReflectionIntensity = RenderSettings.reflectionIntensity;
            Taken = true;
        }

        /// <summary>Puts the saved ambient back. scale 0..1 lets it come up gradually.</summary>
        public void Apply(float scale = 1f)
        {
            if (!Taken) return;
            scale = Mathf.Clamp01(scale);

            for (int i = 0; i < GlobalIds.Length; i++)
            {
                Shader.SetGlobalVector(GlobalIds[i], _globals[i] * scale);
            }

            foreach (var kv in _reflection)
            {
                if (kv.Key != null) kv.Key.SetReflectionIntensity(kv.Value * scale);
            }

            RenderSettings.ambientIntensity = _rsAmbientIntensity * scale;
            RenderSettings.ambientLight = _rsAmbientLight * scale;
            RenderSettings.reflectionIntensity = _rsReflectionIntensity * scale;
        }

        public void Clear()
        {
            _reflection.Clear();
            Taken = false;
        }
    }
}
