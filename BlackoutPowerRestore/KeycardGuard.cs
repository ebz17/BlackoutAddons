using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using EFT;
using EFT.Interactive;
using EFT.UI;
using HarmonyLib;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// While the power is out, keycard readers have no power: only Blackout's "Enter emergency code"
    /// may be offered. After power cycling, "Try keycard" options could show up next to it - this
    /// runs after Blackout's own hook and removes them.
    /// </summary>
    internal static class KeycardGuard
    {
        private const string CodeAction = "Enter emergency code";
        private static BlackoutBridge _bo;
        private static ManualLogSource _log;
        private static readonly HashSet<string> Logged = new HashSet<string>();

        /// <summary>Set by the plugin: true while only grid A has power.</summary>
        public static Func<bool> IsPartial = () => false;
        public static Func<bool> ForceDark = () => false;   // a power surge is not real power
        public static Func<bool> AllowCode = () => true;    // Blackout's emergency code (off = no power, nothing works)

        public static void Install(Harmony harmony, BlackoutBridge bo, ManualLogSource log)
        {
            _bo = bo;
            _log = log;
            var target = AccessTools.Method(typeof(InteractionContextHelper), "GetAvailableActions",
                new[] { typeof(GamePlayerOwner), typeof(KeycardDoor), typeof(bool) });
            if (target == null)
            {
                log.LogWarning("[PowerRestore] keycard menu method not found - keycard guard disabled.");
                return;
            }
            var postfix = new HarmonyMethod(typeof(KeycardGuard), nameof(Postfix)) { priority = Priority.Last };
            harmony.Patch(target, postfix: postfix);
        }

        public static void ResetRaid() => Logged.Clear();

        private static void Postfix(ref AvailableInteractionState __result, KeycardDoor door)
        {
            try
            {
                if (_bo == null || __result?.Actions == null || door == null) return;
                if (door.DoorState != EDoorState.Locked) return;

                // partial power: readers on the dark side are dead
                if (!_bo.BlackoutActive && IsPartial() && PowerGrid.IsGridB(door.transform.position))
                {
                    if (__result.Actions.Count > 0 && Logged.Add("nopower:" + door.Id))
                    {
                        _log?.LogInfo($"[PowerRestore] Partial power: keycard reader at '{door.Id}' has no power.");
                    }
                    __result = new AvailableInteractionState { Actions = new List<InteractionAction>(), Error = "No power" };
                    return;
                }

                if (!_bo.BlackoutActive && !ForceDark())
                {
                    // power is on, but the code keypad still has no business being there
                    if (!AllowCode() && __result.Actions.Any(a => a != null && a.Name == CodeAction))
                        __result = new AvailableInteractionState { Actions = __result.Actions.Where(a => a != null && a.Name != CodeAction).ToList() };
                    return;
                }

                var actions = __result.Actions;
                int before = actions.Count;
                var keep = actions.Where(a => a != null && a.Name == CodeAction && AllowCode()).ToList();
                if (keep.Count == before) return;   // already just the code option

                __result = new AvailableInteractionState
                {
                    Actions = keep,
                    Error = keep.Count == 0 ? "No power" : null,
                };

                string id = door.Id ?? "door";
                if (Logged.Add(id))
                {
                    _log?.LogInfo($"[PowerRestore] Power is out: hid {before - keep.Count} keycard option(s) at '{id}'.");
                }
            }
            catch (Exception)
            {
                // never break the interaction menu
            }
        }
    }
}
