using System;
using System.Collections.Generic;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Black Division runs the game's raider brain: pick a spot, hold it until someone shows up. In blackout raids
    /// that makes them campers, and our hunt orders only "tell" them where people are. This BigBrain layer takes over
    /// when a soldier has nobody in sight: he walks (or runs, if it's far) to the last hunt order's spot. As soon as he
    /// sees or hears someone, the combat AI (SAIN) is above us and takes over again.
    /// </summary>
    internal sealed class HuntLayer : CustomLayer
    {
        public const string Name = "BlackoutHunt";
        public HuntLayer(BotOwner bot, int priority) : base(bot, priority) { }

        public override string GetName() => Name;

        public override bool IsActive() => HuntGoals.Wants(BotOwner, out _);

        public override Action GetNextAction() => new Action(typeof(HuntLogic), "hunting");

        public override bool IsCurrentActionEnding() => !HuntGoals.Wants(BotOwner, out _);
    }

    internal sealed class HuntLogic : CustomLogic
    {
        private float _nextPath;
        public HuntLogic(BotOwner bot) : base(bot) { }

        public override void Start()
        {
            _nextPath = 0f;
            BotOwner.PatrollingData?.Pause();
        }

        public override void Stop()
        {
            BotOwner.PatrollingData?.Unpause();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            if (!HuntGoals.ForcedWants(BotOwner, out var goal) && !HuntGoals.Wants(BotOwner, out goal)) return;
            float dist = (goal - BotOwner.Position).magnitude;
            BotOwner.SetPose(1f);
            BotOwner.Sprint(dist > 35f || HuntGoals.ForcedWants(BotOwner, out _));   // far (or breaking free): run, close: walk (quieter)
            if (Time.time >= _nextPath)
            {
                _nextPath = Time.time + 2f;
                var status = BotOwner.Mover.GoToPoint(goal, true, 2f, false, false);
                if (status == UnityEngine.AI.NavMeshPathStatus.PathInvalid) HuntGoals.Drop(BotOwner, "no path");
            }
            BotOwner.Steering.LookToMovingDirection();
        }
    }

    /// <summary>
    /// A soldier frozen in SAIN's "Avoid Threat" (a grenade danger that never ends): for a few seconds this layer sits
    /// above SAIN's and walks him out of it. Afterwards SAIN decides again - with a real grenade it reacts normally.
    /// </summary>
    internal sealed class UnfreezeLayer : CustomLayer
    {
        public UnfreezeLayer(BotOwner bot, int priority) : base(bot, priority) { }
        public override string GetName() => "BlackoutUnfreeze";
        public override bool IsActive() => HuntGoals.ForcedWants(BotOwner, out _);
        public override Action GetNextAction() => new Action(typeof(HuntLogic), "breaking free");
        public override bool IsCurrentActionEnding() => !HuntGoals.ForcedWants(BotOwner, out _);
    }

    /// <summary>Where each Black Division group was sent (by BlackDivHunt), and when that's worth walking to.</summary>
    internal static class HuntGoals
    {
        public static Func<bool> Enabled = () => false;      // blackout raid, lights out, "Black Division hunts" on
        private static readonly Dictionary<string, Vector3> Goals = new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, float> Set = new Dictionary<string, float>();

        public static void SetGoal(BotOwner bot, Vector3 goal)
        {
            if (bot == null) return;
            Goals[bot.ProfileId] = goal;
            Set[bot.ProfileId] = Time.time;
        }

        public static void Drop(BotOwner bot, string why)
        {
            if (bot != null)
            {
                Goals.Remove(bot.ProfileId);
                Forced.Remove(bot.ProfileId);
            }
        }

        private static readonly Dictionary<string, KeyValuePair<Vector3, float>> Forced = new Dictionary<string, KeyValuePair<Vector3, float>>();
        public static bool Installed;

        /// <summary>Walk him to this spot for a few seconds, whatever SAIN's Avoid Threat says.</summary>
        public static void Force(BotOwner bot, Vector3 goal, float seconds)
        {
            if (bot != null) Forced[bot.ProfileId] = new KeyValuePair<Vector3, float>(goal, Time.time + seconds);
        }

        public static bool ForcedWants(BotOwner bot, out Vector3 goal)
        {
            goal = default;
            try
            {
                if (bot == null || bot.IsDead || !Forced.TryGetValue(bot.ProfileId, out var f)) return false;
                goal = f.Key;
                if (Time.time > f.Value || (goal - bot.Position).sqrMagnitude < 4f * 4f) { Forced.Remove(bot.ProfileId); return false; }
                var enemy = bot.Memory?.GoalEnemy;
                if (enemy != null && enemy.IsVisible) { Forced.Remove(bot.ProfileId); return false; }   // he sees someone: fight
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool Wants(BotOwner bot, out Vector3 goal)
        {
            goal = default;
            try
            {
                if (bot == null || bot.IsDead || !Enabled() || !Goals.TryGetValue(bot.ProfileId, out goal)) return false;
                if ((goal - bot.Position).sqrMagnitude < 6f * 6f) { Goals.Remove(bot.ProfileId); return false; }   // there - let the AI search
                if (Time.time - Set[bot.ProfileId] > 90f) { Goals.Remove(bot.ProfileId); return false; }          // stale order
                var enemy = bot.Memory?.GoalEnemy;
                if (enemy != null && (enemy.IsVisible || Time.time - enemy.PersonalLastSeenTime < 12f)) return false;   // fighting
                if (bot.Memory != null && bot.Memory.IsUnderFire) return false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Clear()
        {
            Goals.Clear();
            Set.Clear();
            Forced.Clear();
        }
    }

    /// <summary>Registers the layer for Black Division only (needs BigBrain - Black Division itself requires it).</summary>
    internal static class HuntLayerSetup
    {
        public static bool Install(BepInEx.Logging.ManualLogSource log, int priority)
        {
            try
            {
                var roles = new List<WildSpawnType>();
                foreach (int id in new[] { 848420, 848421, 848422, 848423, 848424, 848426 }) roles.Add((WildSpawnType)id);
                var brains = new List<string> { "PMC", "ExUsec", "PmcUsec", "PmcBear", "Assault", "BossKilla", "BossKnight", "FollowerBully" };
                BrainManager.AddCustomLayer(typeof(HuntLayer), brains, priority, roles);
                log.LogInfo($"[PowerRestore] Black Division hunt layer registered (BigBrain, priority {priority}).");
                // above SAIN's Avoid Threat (80) and AvoidDanger (80), below Flashed (85)
                BrainManager.AddCustomLayer(typeof(UnfreezeLayer), brains, 82, roles);
                HuntGoals.Installed = true;
                return true;
            }
            catch (Exception ex)
            {
                log.LogWarning($"[PowerRestore] BigBrain hunt layer not registered ({ex.Message}) - Black Division only gets hunt orders.");
                return false;
            }
        }
    }
}
