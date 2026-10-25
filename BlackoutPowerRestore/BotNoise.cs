using System;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Makes bots "hear" the generator and siren, using the same game event gunshots and grenades use
    /// (SAIN listens to it too). Bots treat it like a loud noise at that spot and may come to investigate.
    /// </summary>
    internal static class BotNoise
    {
        public static float RangeMultiplier = 1f;
        public static bool Enabled = true;

        /// <param name="range">roughly the hearing distance in metres (a gunshot is ~280)</param>
        public static void Emit(Vector3 position, float range)
        {
            if (!Enabled || range <= 0f) return;
            try
            {
                if (!Singleton<GlobalEventDispatcher>.Instantiated) return;
                var player = Singleton<GameWorld>.Instance?.MainPlayer;
                if (player == null) return;
                Singleton<GlobalEventDispatcher>.Instance.PlaySound(player, position, range * RangeMultiplier, AISoundType.gun);
            }
            catch (Exception)
            {
                // never break the sequence because of AI hearing
            }
        }
    }
}
