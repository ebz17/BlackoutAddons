using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>The generator control switch shorted out: a bang, then buzzing and sparks for the rest of the raid.</summary>
    internal sealed class BurntSwitch
    {
        private readonly SoundBank _sound;
        private readonly System.Random _rng = new System.Random();
        private GameObject _root;
        private Light _flash;
        private AudioSource _buzz;
        private Vector3 _pos;
        private float _nextSpark, _flashUntil;

        public bool Active => _root != null;

        public BurntSwitch(SoundBank sound) => _sound = sound;

        public void Burn(Vector3 pos)
        {
            Clear();
            _pos = pos;
            _root = new GameObject("blackout_burnt_switch");
            _root.transform.position = pos;
            _flash = _root.AddComponent<Light>();
            _flash.type = LightType.Point;
            _flash.range = 4f;
            _flash.shadows = LightShadows.None;
            _flash.color = new Color(0.85f, 0.9f, 1f);
            _flash.intensity = 0f;

            // the short: bang, a shower of sparks, then it keeps buzzing
            _sound.Play3D("breaker_trip", pos, 1f, 3f, 60f);
            _sound.Play3D("spark1", pos, 1f, 1.5f, 25f);
            _sound.Play3D("spark2", pos + Vector3.up * 0.1f, 1f, 1.5f, 25f);
            _flashUntil = Time.time + 0.35f;
            _nextSpark = Time.time + 0.6f;
            _buzz = _sound.Play3D("fault_buzz", pos, BuzzVolume, 1f, 12f, loop: true);
        }

        private const float BuzzVolume = 0.3f, BuzzRepairVolume = 0.1f;

        /// <summary>While you work on it the buzz drops right down (you're standing next to it).</summary>
        public void Quiet(bool on)
        {
            if (_buzz != null) _buzz.volume = on ? BuzzRepairVolume : BuzzVolume;
        }

        /// <summary>Using the dead switch: just a zap.</summary>
        public void Zap()
        {
            if (!Active) return;
            _sound.Play3D(_rng.Next(2) == 0 ? "spark1" : "spark2", _pos, 1f, 1.5f, 20f);
            _flashUntil = Time.time + 0.12f;
        }

        public void Tick()
        {
            if (!Active || _flash == null) return;
            if (Time.time >= _nextSpark)
            {
                _nextSpark = Time.time + 2f + (float)_rng.NextDouble() * 5f;
                _sound.Play3D(_rng.Next(2) == 0 ? "spark1" : "spark2", _pos, 0.85f, 1.5f, 18f);
                _flashUntil = Time.time + 0.05f + (float)_rng.NextDouble() * 0.1f;
            }
            _flash.intensity = Time.time < _flashUntil ? 3.5f + (float)_rng.NextDouble() * 2f : 0f;
        }

        public void Clear()
        {
            if (_buzz != null) _sound.FadeOut(_buzz, 0.1f);
            _buzz = null;
            if (_root != null) Object.Destroy(_root);
            _root = null;
            _flash = null;
        }
    }
}
