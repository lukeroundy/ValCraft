using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// 0.8.0 Phase C item bridge (Valheim side; all Valheim access by reflection).
    ///  - Valheim -> MC: (a) every 0.5 s mapped materials in the hidden Valheim player's inventory (auto-pickup, pickables,
    ///    picked-up station output) are moved to the MC inventory; (b) ItemDrops spawned within 20 m of the player within
    ///    4 s of an MC hit/interaction (tree/log/rock/ore/creature drops) are converted directly.
    ///  - MC -> Valheim stations: MC right-click on a Valheim station while holding a mapped item -> MC consumes 1 (server
    ///    authoritative, acked) -> the Valheim item is added to the hidden player's inventory and used on the station with
    ///    Valheim's own Humanoid.TryUseItemOnInteractable (fuel, ore, cooking, fermenter, item stands...). Anything the station
    ///    didn't take goes back via (a).
    ///  - Crafting: when a Valheim crafting UI opens, all mapped MC items are moved into the Valheim inventory (MC snapshot ->
    ///    consume by id -> add) so Valheim's recipes see them; on close, (a) returns the leftovers. Crafted tools/armour (not in
    ///    the table) stay in the Valheim inventory.
    /// Mapping: BepInEx/config/valcraft-items.json (written with defaults, reloaded when it changes).
    /// </summary>
    internal static class ItemBridge
    {
        internal static bool Enabled = true;
        static readonly ItemMap Map = new ItemMap();
        static string _path; static DateTime _mtime; static float _nextFileCheck, _nextSweep;
        static uint _nextReq = 1;
        static string _heldId = ""; static int _heldCount;
        static readonly Dictionary<uint, (GameObject target, string valheim)> PendingUse = new Dictionary<uint, (GameObject, string)>();
        static readonly Dictionary<uint, string> PendingDeposit = new Dictionary<uint, string>();
        static readonly ItemMap.Snapshot Snap = new ItemMap.Snapshot();
        static bool _crafting, _snapRequested;
        static float _lastAction = -100f;
        static readonly List<GameObject> _spawned = new List<GameObject>();
        static ValCraftLink _link;

        static Type _humanoid, _itemDrop, _itemData, _inventory, _objectDb, _invGui, _znetScene, _znv;
        static MethodInfo _getInventory, _getAllItems, _removeItem, _addPrefab, _tryUse, _getItemPrefab, _invVisible, _getStation, _sceneDestroy, _isOwner, _getItem;
        static FieldInfo _dropPrefab, _stack, _equipped, _itemDataF, _objDbInst, _znsInst;
        static bool _init, _ok;

        static void Init()
        {
            _init = true;
            _humanoid = AccessTools.TypeByName("Humanoid"); _itemDrop = AccessTools.TypeByName("ItemDrop"); _inventory = AccessTools.TypeByName("Inventory");
            _itemData = _itemDrop == null ? null : AccessTools.Inner(_itemDrop, "ItemData");
            _objectDb = AccessTools.TypeByName("ObjectDB"); _invGui = AccessTools.TypeByName("InventoryGui"); _znetScene = AccessTools.TypeByName("ZNetScene"); _znv = AccessTools.TypeByName("ZNetView");
            var playerT = AccessTools.TypeByName("Player");
            _getInventory = AccessTools.Method(_humanoid, "GetInventory");
            _getAllItems = AccessTools.Method(_inventory, "GetAllItems", Type.EmptyTypes);
            _removeItem = AccessTools.Method(_inventory, "RemoveItem", new[] { _itemData });
            _addPrefab = AccessTools.Method(_inventory, "AddItem", new[] { typeof(GameObject), typeof(int) });
            _getItem = AccessTools.Method(_inventory, "GetItem", new[] { typeof(string), typeof(int), typeof(bool) });
            _tryUse = AccessTools.Method(_humanoid, "TryUseItemOnInteractable", new[] { _itemData, typeof(GameObject), typeof(bool) });
            _getItemPrefab = AccessTools.Method(_objectDb, "GetItemPrefab", new[] { typeof(string) });
            _invVisible = AccessTools.Method(_invGui, "IsVisible");
            _getStation = AccessTools.Method(playerT, "GetCurrentCraftingStation");
            _sceneDestroy = AccessTools.Method(_znetScene, "Destroy", new[] { typeof(GameObject) });
            _isOwner = AccessTools.Method(_znv, "IsOwner");
            _dropPrefab = AccessTools.Field(_itemData, "m_dropPrefab"); _stack = AccessTools.Field(_itemData, "m_stack"); _equipped = AccessTools.Field(_itemData, "m_equipped");
            _itemDataF = AccessTools.Field(_itemDrop, "m_itemData");
            _objDbInst = AccessTools.Field(_objectDb, "m_instance"); _znsInst = AccessTools.Field(_znetScene, "m_instance");
            var need = new (string, object)[] { ("Humanoid.GetInventory", _getInventory), ("Inventory.GetAllItems", _getAllItems), ("Inventory.RemoveItem(ItemData)", _removeItem),
                ("Inventory.AddItem(GameObject,int)", _addPrefab), ("Humanoid.TryUseItemOnInteractable", _tryUse), ("ObjectDB.GetItemPrefab", _getItemPrefab), ("ItemData.m_dropPrefab", _dropPrefab), ("ItemData.m_stack", _stack) };
            var missing = new List<string>(); foreach (var (n, o) in need) if (o == null) missing.Add(n);
            _ok = missing.Count == 0;
            ValCraftPlugin.Log.Log(_ok ? BepInEx.Logging.LogLevel.Info : BepInEx.Logging.LogLevel.Warning, _ok ? $"[items] Valheim API OK (craft UI={_invVisible != null && _getStation != null}, drop destroy={_sceneDestroy != null})" : $"[items] Valheim API missing: {string.Join(", ", missing)} -> item bridge off");
        }

        internal static void Apply(Harmony h)
        {
            var t = AccessTools.TypeByName("ItemDrop");
            var awake = t == null ? null : AccessTools.Method(t, "Awake");
            if (awake != null) h.Patch(awake, postfix: new HarmonyMethod(typeof(ItemBridge), nameof(DropAwakePost)));
            ValCraftPlugin.Log.LogInfo($"[items] ItemDrop.Awake hook {(awake != null ? "on" : "missing")}");
        }

        static void DropAwakePost(object __instance)
        {
            if (!Enabled || !PlayerSync.Following || Time.time - _lastAction > 4f) return;
            if (__instance is Component c && PlayerSync.Player != null && Vector3.Distance(c.transform.position, PlayerSync.Player.transform.position) < 20f) _spawned.Add(c.gameObject);
        }

        /// <summary>MC hit / interaction happened (drops in the next seconds belong to the MC player).</summary>
        internal static void NoteAction() { _lastAction = Time.time; }

        static void LoadMap()
        {
            if (_path == null)
            {
                _path = Path.Combine(Paths.ConfigPath, "valcraft-items.json");
                if (!File.Exists(_path)) { File.WriteAllText(_path, ItemMap.DefaultJson); ValCraftPlugin.Log.LogInfo($"[items] wrote default mapping {_path}"); }
            }
            var mt = File.GetLastWriteTimeUtc(_path);
            if (mt == _mtime) return;
            _mtime = mt;
            try { int n = Map.Load(File.ReadAllText(_path)); ValCraftPlugin.Log.LogInfo($"[items] mapping loaded: {n} Valheim items <-> {Map.ToValheim.Count} Minecraft items ({_path})"); }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[items] mapping file error: {e.Message}"); }
        }

        static object Inv() => PlayerSync.Player == null ? null : _getInventory.Invoke(PlayerSync.Player, null);
        static string PrefabOf(object itemData) => (_dropPrefab.GetValue(itemData) as GameObject)?.name;

        internal static bool Handle(LinkEvent ev)
        {
            switch (ev.Type)
            {
                case ValCraftLink.EvHeldItem: { var (c, id) = ItemMap.Held(ev.Payload); _heldCount = c; _heldId = id; return true; }
                case ValCraftLink.EvConsumeAck: OnAck(ev.Payload); return true;
                case ValCraftLink.EvInvSnapshot: if (Snap.Add(ev.Payload)) OnSnapshot(Snap.Result); return true;
            }
            return false;
        }

        internal static void Update(ValCraftLink link, bool connected)
        {
            _link = link;
            if (!Enabled || !connected) return;
            if (!_init) Init();
            if (!_ok) return;
            float t = Time.unscaledTime;
            if (t >= _nextFileCheck) { _nextFileCheck = t + 2f; try { LoadMap(); } catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[items] {e.Message}"); } }
            if (!PlayerSync.Following || PlayerSync.Player == null) { _spawned.Clear(); return; }

            // crafting session
            bool craftUi = false;
            try { craftUi = _invVisible != null && (bool)_invVisible.Invoke(null, null) && _getStation != null && _getStation.Invoke(PlayerSync.Player, null) != null; } catch { }
            if (craftUi && !_crafting) { _crafting = true; _snapRequested = link.TrySend(ValCraftLink.EvInvReq, null); ValCraftPlugin.Log.LogInfo("[items] crafting station opened: moving mapped Minecraft items into the Valheim inventory"); }
            if (!craftUi && _crafting) { _crafting = false; ValCraftPlugin.Log.LogInfo("[items] crafting closed: returning mapped leftovers to Minecraft"); }

            // drops from MC actions
            if (_spawned.Count > 0)
            {
                foreach (var go in _spawned.ToArray())
                {
                    if (go == null) { _spawned.Remove(go); continue; }
                    var drop = go.GetComponent(_itemDrop);
                    var nv = go.GetComponent(_znv);
                    if (nv == null || _isOwner == null || _sceneDestroy == null || _znsInst?.GetValue(null) == null || !(bool)_isOwner.Invoke(nv, null)) { _spawned.Remove(go); continue; }
                    var data = _itemDataF.GetValue(drop);
                    string prefab = data == null ? null : PrefabOf(data) ?? go.name.Replace("(Clone)", "");
                    string mc = Map.McFor(prefab);
                    _spawned.Remove(go);
                    if (mc == null) continue;
                    int n = (int)_stack.GetValue(data);
                    if (Give(mc, n, prefab, "drop"))
                    {
                        // always through ZNetScene.Destroy (a plain Object.Destroy leaves a dead entry in ZNetScene.m_instances)
                        try { _sceneDestroy.Invoke(_znsInst.GetValue(null), new object[] { go }); } catch (Exception e) { Diag.Warn("dropdestroy", "[items] could not remove converted drop: " + e.GetBaseException().Message); }
                    }
                }
            }

            // sweep: mapped materials in the Valheim inventory -> MC (not during crafting)
            if (!_crafting && t >= _nextSweep && PendingUse.Count == 0)
            {
                _nextSweep = t + 0.5f;
                var inv = Inv();
                if (inv != null && _getAllItems.Invoke(inv, null) is IList items)
                    foreach (var it in new ArrayList(items))
                    {
                        if (_equipped != null && (bool)_equipped.GetValue(it)) continue;
                        string prefab = PrefabOf(it), mc = Map.McFor(prefab);
                        if (mc == null) continue;
                        if (Give(mc, (int)_stack.GetValue(it), prefab, "inventory")) _removeItem.Invoke(inv, new[] { it });
                    }
            }
        }

        static bool Give(string mc, int n, string prefab, string why)
        {
            if (n <= 0 || _link == null) return false;
            bool ok = _link.TrySend(ValCraftLink.EvGiveItem, ItemMap.Give(_nextReq++, n, mc));
            ValCraftPlugin.Log.LogInfo($"[items] {(ok ? "gave" : "could not give (ring full)")} {n} x {prefab} -> {mc} ({why})");
            return ok;
        }

        static readonly string[] StationTypes = { "Fireplace", "Smelter", "CookingStation", "Fermenter", "ItemStand", "Turret", "ShieldGenerator", "Incinerator", "Beehive", "Windmill" };

        /// <summary>MC right-click on a Valheim target. Returns true if handled as "use held MC item on a station".</summary>
        internal static bool TryUseHeld(GameObject target)
        {
            NoteAction();
            if (!Enabled || !_ok || target == null || string.IsNullOrEmpty(_heldId) || _heldCount <= 0) return false;
            string valheim = Map.ValheimFor(_heldId);
            if (valheim == null) return false;
            bool station = false;
            foreach (var n in StationTypes) { var ty = AccessTools.TypeByName(n); if (ty != null && target.GetComponentInParent(ty) != null) { station = true; break; } }
            if (!station) return false;
            uint req = _nextReq++;
            if (!_link.TrySend(ValCraftLink.EvConsumeReq, ItemMap.Consume(req, 1, 0, _heldId))) return false;
            PendingUse[req] = (target, valheim);
            ValCraftPlugin.Log.LogInfo($"[items] use {_heldId} (= {valheim}) on {target.name}: asking Minecraft to consume 1");
            return true;
        }

        static void OnSnapshot(Dictionary<string, int> inv)
        {
            if (!_crafting) return;
            foreach (var kv in inv)
            {
                var v = Map.ValheimFor(kv.Key);
                if (v == null || kv.Value <= 0) continue;
                uint req = _nextReq++;
                if (_link.TrySend(ValCraftLink.EvConsumeReq, ItemMap.Consume(req, kv.Value, 1, kv.Key))) PendingDeposit[req] = v;
            }
        }

        static void OnAck(byte[] p)
        {
            var (req, removed, id) = ItemMap.Ack(p);
            if (PendingDeposit.TryGetValue(req, out var dv))
            {
                PendingDeposit.Remove(req);
                if (removed > 0 && AddValheim(dv, removed) == null) GiveBack(id, removed);
                else if (removed > 0) ValCraftPlugin.Log.LogInfo($"[items] moved {removed} x {id} -> {dv} into the Valheim inventory for crafting");
                return;
            }
            if (!PendingUse.TryGetValue(req, out var pu)) return;
            PendingUse.Remove(req);
            if (removed <= 0) { ValCraftPlugin.Log.LogInfo($"[items] Minecraft had no {id} to use"); return; }
            var data = AddValheim(pu.valheim, removed);
            if (data == null) { GiveBack(id, removed); return; }
            try
            {
                _tryUse.Invoke(PlayerSync.Player, new object[] { data, pu.target, false });
                ValCraftPlugin.Log.LogInfo($"[items] used {pu.valheim} on {pu.target?.name} (leftovers return to Minecraft)");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[items] use on {pu.target?.name} failed: {e.InnerException?.Message ?? e.Message}"); }
            _nextSweep = 0; // return whatever the station didn't take
        }

        static void GiveBack(string id, int n) { _link?.TrySend(ValCraftLink.EvGiveItem, ItemMap.Give(_nextReq++, n, id)); ValCraftPlugin.Log.LogWarning($"[items] returned {n} x {id} to Minecraft (could not add the Valheim item)"); }

        static object AddValheim(string prefabName, int n)
        {
            try
            {
                var db = _objDbInst?.GetValue(null);
                var prefab = db == null ? null : _getItemPrefab.Invoke(db, new object[] { prefabName }) as GameObject;
                if (prefab == null) { ValCraftPlugin.Log.LogWarning($"[items] no Valheim item prefab '{prefabName}' (check valcraft-items.json)"); return null; }
                var inv = Inv();
                if (inv == null || !(bool)_addPrefab.Invoke(inv, new object[] { prefab, n })) { ValCraftPlugin.Log.LogWarning($"[items] Valheim inventory full for {prefabName}"); return null; }
                if (_getItem != null) { var d = _getItem.Invoke(inv, new object[] { prefabName, -1, true }); if (d != null) return d; }
                foreach (var it in (IList)_getAllItems.Invoke(inv, null)) if (PrefabOf(it) == prefabName) return it;
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[items] add {prefabName}: {e.InnerException?.Message ?? e.Message}"); }
            return null;
        }
    }
}
