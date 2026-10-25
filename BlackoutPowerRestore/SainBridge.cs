using System;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using EFT;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Optional link to SAIN (no hard dependency, all by reflection). SAIN replaces the game's own
    /// bot search/hearing, so orders sent the vanilla way are often ignored. Through SAIN's squad
    /// search the bot "hears" the player near a spot, marks them as an enemy and goes hunting.
    /// </summary>
    internal static class SainBridge
    {
        private static bool _tried;
        private static MethodInfo _getSain;          // SAINEnableClass.GetSAIN(string, out BotComponent)
        private static PropertyInfo _squad;          // BotComponent.Squad
        private static PropertyInfo _squadInfo;      // BotSquadContainer.SquadInfo
        private static MethodInfo _addPoint;         // Squad.AddPointToSearch(Vector3, float, BotComponent, AISoundType, IPlayer, ESearchPointType)
        private static object _hearing;              // ESearchPointType.Hearing
        private static ManualLogSource _log;

        public static bool Available { get; private set; }

        public static void Init(ManualLogSource log)
        {
            if (_tried) return;
            _tried = true;
            _log = log;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SAIN");
                if (asm == null) return;
                var enable = asm.GetType("SAIN.SAINEnableClass");
                var botComp = asm.GetType("SAIN.Components.BotComponent");
                var squadCls = asm.GetType("SAIN.BotController.Classes.Squad");
                _getSain = enable?.GetMethod("GetSAIN", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), botComp?.MakeByRefType() }, null);
                _squad = botComp?.GetProperty("Squad", BindingFlags.Public | BindingFlags.Instance);
                _squadInfo = _squad?.PropertyType.GetProperty("SquadInfo", BindingFlags.Public | BindingFlags.Instance);
                var pointType = squadCls?.GetNestedType("ESearchPointType");
                _addPoint = squadCls?.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m =>
                    m.Name == "AddPointToSearch" && m.GetParameters().Length == 6 && m.GetParameters()[0].ParameterType == typeof(Vector3));
                if (pointType != null) _hearing = Enum.Parse(pointType, "Hearing");
                Available = _getSain != null && _squad != null && _squadInfo != null && _addPoint != null && _hearing != null;
                log.LogInfo($"[PowerRestore] SAIN {(Available ? "found - Black Division hunts through SAIN's squad search" : "found but its search API changed - using the game's own")}.");
            }
            catch (Exception ex)
            {
                Available = false;
                log.LogWarning($"[PowerRestore] SAIN link failed: {ex.Message}");
            }
        }

        /// <summary>Is SAIN driving this bot? (custom bots can be left to the game's own AI)</summary>
        public static bool Controls(BotOwner bot)
        {
            if (!Available || bot == null) return false;
            try
            {
                var args = new object[] { bot.ProfileId, null };
                return (bool)_getSain.Invoke(null, args) && args[1] != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// SAIN's "dog fight" (Avoid Threat layer) can stick forever: it starts when someone shoots the bot from up
        /// close, and only ends once that enemy has been SEEN and lost for 8 s - shot from the dark by someone he never
        /// saw, he stands there for the rest of the raid. Clearing the target lets SAIN decide again.
        /// </summary>
        public static bool ClearDogFight(BotOwner bot, out string why)
        {
            why = "SAIN not found";
            if (!Available || bot == null) return false;
            try
            {
                var args = new object[] { bot.ProfileId, null };
                if (!(bool)_getSain.Invoke(null, args) || args[1] == null) { why = "not a SAIN bot"; return false; }
                var decision = args[1].GetType().GetProperty("Decision", BindingFlags.Public | BindingFlags.Instance)?.GetValue(args[1]);
                var dog = decision?.GetType().GetProperty("DogFightDecision", BindingFlags.Public | BindingFlags.Instance)?.GetValue(decision);
                var field = dog?.GetType().GetField("_lastDogFightTarget", BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null) { why = "SAIN's dog fight code changed"; return false; }
                if (field.GetValue(dog) == null) { why = "no dog fight target (grenade?)"; return false; }
                field.SetValue(dog, null);
                why = "dog fight target cleared";
                return true;
            }
            catch (Exception ex)
            {
                why = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        /// <summary>The bot "heard" the player around this spot: SAIN makes them an enemy and sends the squad to check.</summary>
        public static bool SendToSearch(BotOwner bot, Vector3 spot, IPlayer player, float power)
        {
            if (!Available || bot == null || player == null) return false;
            try
            {
                var args = new object[] { bot.ProfileId, null };
                if (!(bool)_getSain.Invoke(null, args) || args[1] == null) return false;
                var comp = args[1];
                var squad = _squadInfo.GetValue(_squad.GetValue(comp));
                if (squad == null) return false;
                _addPoint.Invoke(squad, new object[] { spot, power, comp, AISoundType.step, player, _hearing });
                return true;
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[PowerRestore] SAIN search order failed: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }
    }
}
