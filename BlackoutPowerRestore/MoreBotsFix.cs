using System;
using System.Linq;
using BepInEx.Logging;
using EFT;
using HarmonyLib;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Compatibility fix for MoreBotsAPI 2.1.x: HuntManager.OnBotCreated reads
    /// bot.SpawnProfileData.SpawnParams.Id_spawn without a null check. Bots spawned by QuestingBots
    /// have no spawn params, so it throws - and QuestingBots then fails to activate those PMCs
    /// ("Could not finish activation of ..."). We skip the call for bots without spawn params;
    /// they're never hunt bots anyway.
    /// </summary>
    internal static class MoreBotsFix
    {
        private static int _skipped;
        private static ManualLogSource _log;

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("MoreBotsAPI.Components.HuntManager", false))
                    .FirstOrDefault(t => t != null);
                if (type == null) return;
                var m = AccessTools.Method(type, "OnBotCreated", new[] { typeof(BotOwner) });
                if (m == null) return;
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(MoreBotsFix), nameof(Prefix)) { priority = Priority.First });
                log.LogInfo("[PowerRestore] MoreBotsAPI hunt fix installed (bots spawned without spawn params, e.g. QuestingBots PMCs).");
            }
            catch (Exception ex)
            {
                log.LogWarning($"[PowerRestore] MoreBotsAPI fix not installed: {ex.Message}");
            }
        }

        private static bool Prefix(BotOwner bot)
        {
            try
            {
                if (bot?.SpawnProfileData?.SpawnParams?.Id_spawn != null) return true;
            }
            catch (Exception)
            {
                // fall through: skip
            }
            if (_skipped++ < 3) _log?.LogInfo($"[PowerRestore] MoreBotsAPI: skipped hunt check for {bot?.Profile?.Nickname} (no spawn params) - avoids its NullReference.");
            return false;
        }
    }
}
