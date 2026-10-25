using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;
using UnityEngine.AI;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// A burnt generator control can be repaired with a Toolset, a Spark plug and 2 Bundles of wires.
    /// Blackout raids: the toolset and the wires lie next to the computers in the room with the most PCs;
    /// the spark plug is in the pockets of one of Wedge's guards (they took it when they cut the power).
    /// </summary>
    internal static class RepairKit
    {
        public const string Toolset = "590c2e1186f77425357b6124";
        public const string SparkPlug = "590a3c0a86f774385a33c450";
        public const string Wires = "5c06779c86f77426e00dd782";
        private const string PcBlock = "59139c2186f77411564f8e42";
        private static readonly Vector3 FallbackRoom = new Vector3(-130.9f, 1.4f, -336.4f);   // the gate switch room

        private static ManualLogSource _log;
        private static readonly System.Random Rng = new System.Random();
        private static bool _placed, _placing, _plugGiven, _plugTrying;
        private static float _placeAfter = -1f;
        private static readonly List<LootItem> Placed = new List<LootItem>();
        private static string _plugCarrier;        // profile id of the guard with the plug
        private static Vector3 _lastCarrierPos;
        private static bool _plugDropped;

        public static string Needed => "Toolset, Spark plug, 2x Bundle of wires";
        public static Func<List<Vector3>> PartSpots = () => new List<Vector3>();     // placed with Ctrl+F2 (where you stood)
        public static void Init(ManualLogSource log) => _log = log;

        /// <summary>Call every frame in blackout raids.</summary>
        public static void Tick(GameWorld gw)
        {
            if (gw == null) return;
            if (!_placed && !_placing)
            {
                if (_placeAfter < 0f) _placeAfter = Time.time + 20f;     // let the level and its containers settle
                else if (Time.time >= _placeAfter) PlaceRoomParts(gw);
            }
            if (_placed && !_plugGiven && !_plugTrying) GivePlugToAGuard(gw);     // one bundle load at a time
            WatchCarrier(gw);
        }

        // ------------------------------------------------------------- toolset + wires by the computers

        private static async void PlaceRoomParts(GameWorld gw)
        {
            _placing = true;
            try
            {
                var mine = PartSpots() ?? new List<Vector3>();
                if (mine.Count > 0)
                {
                    // your spots: each part on a different one (random each raid) while there are enough
                    var order = mine.OrderBy(_ => Rng.Next()).ToList();
                    var tpls = new[] { Toolset, Wires, Wires };
                    for (int i = 0; i < tpls.Length; i++)
                    {
                        var at = order[i % order.Count];
                        var off = UnityEngine.Random.insideUnitCircle * 0.4f;
                        var loot = await Spawn(gw, tpls[i], at + new Vector3(off.x, 0f, off.y));
                        if (loot != null)
                        {
                            Placed.Add(loot);
                            _log.LogInfo($"[PowerRestore] Repair parts: {Name(tpls[i])} placed at your spot {at:F1}.");
                        }
                    }
                    return;
                }
                var pcs = UnityEngine.Object.FindObjectsOfType<LootableContainer>()
                    .Where(c => c != null && c.Template == PcBlock).Select(c => c.transform.position).ToList();
                // the "computer room": the PC with the most other PCs around it (same floor, 14 m)
                List<Vector3> room = null;
                foreach (var p in pcs)
                {
                    var near = pcs.Where(q => Mathf.Abs(q.y - p.y) < 2f && (q - p).sqrMagnitude < 14f * 14f).ToList();
                    if (room == null || near.Count > room.Count) room = near;
                }
                var anchors = room != null && room.Count >= 3 ? room : new List<Vector3> { FallbackRoom };
                _log.LogInfo(room != null && room.Count >= 3
                    ? $"[PowerRestore] Repair parts: computer room found ({room.Count} PCs around {Center(room):F1})."
                    : $"[PowerRestore] Repair parts: no computer room found ({pcs.Count} PCs) - using the gate switch room.");

                foreach (var tpl in new[] { Toolset, Wires, Wires })
                {
                    var spot = FloorSpotNear(anchors[Rng.Next(anchors.Count)]);
                    if (!spot.HasValue) spot = FloorSpotNear(Center(anchors));
                    if (!spot.HasValue) continue;
                    var loot = await Spawn(gw, tpl, spot.Value);
                    if (loot != null)
                    {
                        Placed.Add(loot);
                        _log.LogInfo($"[PowerRestore] Repair parts: {Name(tpl)} placed at {spot.Value:F1}.");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] Repair parts could not be placed: {ex.Message}");
            }
            finally
            {
                _placed = true;
                _placing = false;
            }
        }

        private static Vector3 Center(List<Vector3> pts) => pts.Aggregate(Vector3.zero, (a, b) => a + b) / Math.Max(1, pts.Count);

        /// <summary>Open floor ~1 m from the PC (not inside the desk).</summary>
        private static Vector3? FloorSpotNear(Vector3 p)
        {
            for (int i = 0; i < 16; i++)
            {
                var off = UnityEngine.Random.insideUnitCircle.normalized * (0.7f + (float)Rng.NextDouble() * 0.8f);
                if (NavMesh.SamplePosition(p + new Vector3(off.x, 0f, off.y), out var hit, 1.2f, NavMesh.AllAreas) &&
                    Mathf.Abs(hit.position.y - p.y) < 1.6f)
                    return hit.position + Vector3.up * 0.03f;
            }
            return NavMesh.SamplePosition(p, out var h2, 3f, NavMesh.AllAreas) ? h2.position + Vector3.up * 0.03f : (Vector3?)null;
        }

        private static async Task<Item> Make(string tpl)
        {
            var item = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), tpl, null);
            if (item == null) return null;
            // the item's model may not be loaded in this raid yet (never wait more than 15 s for it)
            var load = Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid,
                ObjectsFactory.AssemblyType.Local, item.Template.AllResources.ToArray(), Diz.Jobs.JobYieldPriority.General, null, default);
            if (await Task.WhenAny(load, Task.Delay(15000)) != load)
                _log.LogWarning($"[PowerRestore] Repair parts: model for {Name(tpl)} still loading after 15 s - going ahead.");
            return item;
        }

        private static async Task<LootItem> Spawn(GameWorld gw, string tpl, Vector3 pos)
        {
            var item = await Make(tpl);
            if (item == null || gw == null) return null;
            // find the real floor (the navmesh can sit a little above or below it), then let it drop onto it
            var from = pos + Vector3.up * 1.0f;
            if (Physics.Raycast(from, Vector3.down, out var hit, 2.5f, ~0, QueryTriggerInteraction.Ignore)) pos = hit.point;
            var rot = Quaternion.Euler(0f, (float)Rng.NextDouble() * 360f, 0f);
            return gw.ThrowItem(item, gw.MainPlayer, pos + Vector3.up * 0.3f, rot, Vector3.zero, Vector3.zero, true);
        }

        // ------------------------------------------------------------- spark plug on a guard

        private static async void GivePlugToAGuard(GameWorld gw)
        {
            // one of Wedge's guards (first team), once they are all in
            var guards = gw.AllAlivePlayersList.Where(p => p != null && p.IsAI &&
                (p.Profile?.Info?.Settings?.Role.ToString() ?? "").Equals("blackDivIb", StringComparison.OrdinalIgnoreCase)).ToList();
            if (guards.Count == 0) return;
            if (_firstGuardAt < 0f) _firstGuardAt = Time.time;
            // wait until the whole team is in (or 30 s after the first guard showed up)
            if (guards.Count < WedgeSpawn.Expected - 1 && Time.time - _firstGuardAt < 30f) return;
            _plugTrying = true;
            _plugGiven = true;                 // one attempt only - whatever happens below, never again this raid
            var guard = guards[Rng.Next(guards.Count)];
            _log.LogInfo($"[PowerRestore] Repair parts: giving the spark plug to {guard.Profile?.Nickname}...");
            Item item = null;
            try
            {
                item = await Make(SparkPlug);
                var ctrl = guard.InventoryController;
                // the game's FindGridToPickUp asks for the item's current parent - a brand-new item has none, so look ourselves
                ItemAddress addr = null;
                if (item != null && ctrl != null)
                {
                    foreach (var grid in ctrl.Inventory.Equipment.GetPrioritizedGridsForLoot(item))
                    {
                        addr = grid?.FindLocationForItem(item);
                        if (addr != null) break;
                    }
                }
                if (addr == null) throw new Exception("no free space");
                ctrl.AddAndRaiseEvents(item, addr);              // same as the game does for a new item
                if (item.CurrentAddress == null) throw new Exception("not in his inventory after adding");
                _plugGiven = true;
                _plugCarrier = guard.ProfileId;
                _log.LogInfo($"[PowerRestore] Repair parts: the spark plug is in {guard.Profile?.Nickname}'s pockets.");
            }
            catch (Exception ex) when (item?.CurrentAddress != null)      // (Parent throws when there is none)
            {
                _plugGiven = true;
                _plugCarrier = guard.ProfileId;
                _log.LogInfo($"[PowerRestore] Repair parts: the spark plug is in {guard.Profile?.Nickname}'s pockets ({ex.Message}).");
            }
            catch (Exception ex)
            {
                // couldn't put it in his inventory: he drops it when he dies instead
                _plugGiven = true;
                _plugCarrier = guard.ProfileId;
                _plugDropped = false;
                _dropOnDeath = true;
                _log.LogInfo($"[PowerRestore] Repair parts: spark plug not added to {guard.Profile?.Nickname} ({ex.Message}) - he drops it when he dies.");
            }
            finally
            {
                _plugTrying = false;
            }
        }

        private static bool _dropOnDeath;
        private static float _firstGuardAt = -1f;

        private static void WatchCarrier(GameWorld gw)
        {
            if (!_dropOnDeath || _plugDropped || _plugCarrier == null) return;
            var p = gw.AllAlivePlayersList.FirstOrDefault(x => x != null && x.ProfileId == _plugCarrier);
            if (p != null && p.HealthController != null && p.HealthController.IsAlive)
            {
                _lastCarrierPos = p.Position;
                return;
            }
            _plugDropped = true;
            DropPlug(gw, _lastCarrierPos);
        }

        private static async void DropPlug(GameWorld gw, Vector3 at)
        {
            var spot = FloorSpotNear(at) ?? at;
            var loot = await Spawn(gw, SparkPlug, spot);
            if (loot != null) Placed.Add(loot);
            _log.LogInfo($"[PowerRestore] Repair parts: the spark plug fell out next to the body at {spot:F1}.");
        }

        // ------------------------------------------------------------- the player's inventory

        public static bool HasParts(Player p, out string missing)
        {
            var items = Items(p);
            int tools = items.Count(i => Tpl(i) == Toolset), plugs = items.Count(i => Tpl(i) == SparkPlug), wires = items.Count(i => Tpl(i) == Wires);
            var miss = new List<string>();
            if (tools < 1) miss.Add("Toolset");
            if (plugs < 1) miss.Add("Spark plug");
            if (wires < 2) miss.Add(wires == 1 ? "1 more Bundle of wires" : "2x Bundle of wires");
            missing = string.Join(", ", miss);
            return miss.Count == 0;
        }

        /// <summary>The spark plug and both wires are used up; the toolset is kept.</summary>
        public static bool UseParts(Player p)
        {
            try
            {
                var items = Items(p);
                var use = items.Where(i => Tpl(i) == SparkPlug).Take(1).Concat(items.Where(i => Tpl(i) == Wires).Take(2)).ToList();
                if (use.Count < 3) return false;
                foreach (var it in use)
                {
                    var r = ItemManipulator.Remove(it, p.InventoryController, false);
                    if (r.Succeeded)
                    {
                        r.Value.RaiseEvents(p.InventoryController, CommandStatus.Begin);    // inventory screen updates
                        r.Value.RaiseEvents(p.InventoryController, CommandStatus.Succeed);
                    }
                    else _log.LogWarning($"[PowerRestore] could not use {it.ShortName}: {r.Error}");
                }
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[PowerRestore] using the repair parts failed: {ex.Message}");
                return false;
            }
        }

        private static List<Item> Items(Player p)
        {
            try
            {
                return p?.InventoryController?.Inventory?.AllRealPlayerItems?.ToList() ?? new List<Item>();
            }
            catch (Exception)
            {
                return new List<Item>();
            }
        }

        private static string Tpl(Item i) => i?.TemplateId.ToString();

        private static string Name(string tpl) => tpl == Toolset ? "Toolset" : tpl == Wires ? "Bundle of wires" : "Spark plug";

        /// <summary>For the Ctrl+F5 test display: the nearest part still lying around.</summary>
        public static (string name, Vector3 pos)? NearestPart(Vector3 from)
        {
            LootItem best = null;
            foreach (var l in Placed)
            {
                if (l == null || l.Item == null || !l.gameObject.activeInHierarchy) continue;
                if (best == null || (l.transform.position - from).sqrMagnitude < (best.transform.position - from).sqrMagnitude) best = l;
            }
            return best == null ? ((string, Vector3)?)null : (Name(Tpl(best.Item)), best.transform.position);
        }

        public static string CarrierName(GameWorld gw)
        {
            if (_plugCarrier == null) return null;
            var p = gw?.AllAlivePlayersList.FirstOrDefault(x => x != null && x.ProfileId == _plugCarrier);
            return p != null ? p.Profile?.Nickname : "(dead)";
        }

        public static void ResetRaid()
        {
            _placed = _placing = _plugGiven = _plugTrying = _plugDropped = _dropOnDeath = false;
            _placeAfter = -1f;
            _firstGuardAt = -1f;
            Placed.Clear();
            _plugCarrier = null;
        }
    }
}
