using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Read-only state of the Labs blackout for other mods (e.g. The Stalker). Read it by reflection, so neither mod
    /// needs the other: Type.GetType("BlackoutPowerRestore.BlackoutApi, BlackoutPowerRestore").
    /// Times are Unity Time.time; -1 = hasn't happened this raid.
    /// </summary>
    public static class BlackoutApi
    {
        public const int ApiVersion = 3;
        /// <summary>Changes every raid (lets a reader notice a new raid).</summary>
        public static int RaidNumber { get; private set; }
        /// <summary>The lights went out this raid (Black Division cut the power).</summary>
        public static bool BlackoutRaid { get; private set; }
        /// <summary>-1 = power never cut (yet), 0 = dark, 1 = half the facility (generator A / sector B fault), 2 = full power.</summary>
        public static int Power { get; private set; } = -1;
        /// <summary>When the power last went from on (or half) to dark.</summary>
        public static float DarkSince { get; private set; } = -1f;
        public static float LightsOutAt { get; private set; } = -1f;
        public static float BurntAt { get; private set; } = -1f;
        public static float FullPowerAt { get; private set; } = -1f;
        public static float EmergencyAt { get; private set; } = -1f;
        public static float KillaAt { get; private set; } = -1f;

        /// <summary>(API 3) Where the generator switch is (null until it's spawned this raid).</summary>
        public static Vector3? SwitchPosition { get; internal set; }
        /// <summary>(API 3) The power is on (full or half) and the switch can shut it down right now.</summary>
        public static bool CanCut { get; internal set; }

        private static string _cutBy;

        /// <summary>
        /// (API 3) Shut the generators down from the switch, like a player would (The Stalker does this).
        /// False = not possible right now (power already off, switch burnt, a start-up still running).
        /// </summary>
        public static bool RequestCut(string who)
        {
            if (!CanCut || _cutBy != null) return false;
            _cutBy = string.IsNullOrEmpty(who) ? "someone" : who;
            return true;
        }

        internal static string TakeCutRequest()
        {
            var who = _cutBy;
            _cutBy = null;
            return who;
        }

        private static Vector3 _presence;
        private static float _presenceAt = -100f;

        /// <summary>
        /// (API 2) Something dark is moving here - another mod sets it every frame (The Stalker sets his position).
        /// The emergency lamps around it flicker and die. It counts as gone half a second after the last set.
        /// </summary>
        public static Vector3? DarkPresence
        {
            get => Time.time - _presenceAt < 0.5f ? _presence : (Vector3?)null;
            set
            {
                if (value.HasValue)
                {
                    _presence = value.Value;
                    _presenceAt = Time.time;
                }
                else
                {
                    _presenceAt = -100f;
                }
            }
        }

        internal static void NewRaid()
        {
            _presenceAt = -100f;
            SwitchPosition = null;
            CanCut = false;
            _cutBy = null;
            RaidNumber++;
            BlackoutRaid = false;
            Power = -1;
            DarkSince = LightsOutAt = BurntAt = FullPowerAt = EmergencyAt = KillaAt = -1f;
        }

        internal static void LightsOut()
        {
            BlackoutRaid = true;
            LightsOutAt = Time.time;
            SetPower(0);
        }

        internal static void Burnt()
        {
            BurntAt = Time.time;
            SetPower(0);
        }

        internal static void SetPower(int p)
        {
            if (p == 0 && Power != 0) DarkSince = Time.time;
            if (p == 2) FullPowerAt = Time.time;
            Power = p;
        }

        internal static void Emergency() => EmergencyAt = Time.time;

        internal static void Killa()
        {
            if (KillaAt < 0f) KillaAt = Time.time;
        }
    }
}
