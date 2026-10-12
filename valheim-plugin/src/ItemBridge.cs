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
        static MethodInfo _znsGet, _getMaxDur, _removeAmt, _equip, _unequip;
        static FieldInfo _durF, _sharedF;
        static object Zns() => _znsGet != null ? _znsGet.Invoke(null, null) : _znsInst?.GetValue(null);
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
            _durF = AccessTools.Field(_itemData, "m_durability"); _sharedF = AccessTools.Field(_itemData, "m_shared");
            _getMaxDur = AccessTools.Method(_itemData, "GetMaxDurability", Type.EmptyTypes);
            _removeAmt = AccessTools.Method(_inventory, "RemoveItem", new[] { _itemData, typeof(int) });
            _equip = AccessTools.Method(_humanoid, "EquipItem", new[] { _itemData, typeof(bool) });
            _unequip = AccessTools.Method(_humanoid, "UnequipItem", new[] { _itemData, typeof(bool) });
            _objDbInst = AccessTools.Field(_objectDb, "m_instance");
            // 0.8.2: ZNetScene's singleton is s_instance (exposed as ZNetScene.instance), not m_instance. In 0.8.0 the lookup
            // failed, so converted drops fell back to Object.Destroy, leaving dead entries in ZNetScene.m_instances (the
            // RemoveObjects NRE spam after respawn); in 0.8.1 drop conversion was skipped entirely.
            var znsProp = AccessTools.Property(_znetScene, "instance");
            _znsGet = znsProp?.GetGetMethod(true);
            if (_znsGet == null) { var f = AccessTools.Field(_znetScene, "s_instance") ?? AccessTools.Field(_znetScene, "m_instance"); _znsInst = f; }
            var need = new (string, object)[] { ("Humanoid.GetInventory", _getInventory), ("Inventory.GetAllItems", _getAllItems), ("Inventory.RemoveItem(ItemData)", _removeItem),
                ("Inventory.AddItem(GameObject,int)", _addPrefab), ("Humanoid.TryUseItemOnInteractable", _tryUse), ("ObjectDB.GetItemPrefab", _getItemPrefab), ("ItemData.m_dropPrefab", _dropPrefab), ("ItemData.m_stack", _stack) };
            var missing = new List<string>(); foreach (var (n, o) in need) if (o == null) missing.Add(n);
            _ok = missing.Count == 0;
            ValCraftPlugin.Log.Log(_ok ? BepInEx.Logging.LogLevel.Info : BepInEx.Logging.LogLevel.Warning, _ok ? $"[items] Valheim API OK (craft UI={_invVisible != null && _getStation != null}, drop destroy={_sceneDestroy != null && (_znsGet != null || _znsInst != null)})" : $"[items] Valheim API missing: {string.Join(", ", missing)} -> item bridge off");
        }

        internal static void Apply(Harmony h)
        {
            var t = AccessTools.TypeByName("ItemDrop");
            var awake = t == null ? null : AccessTools.Method(t, "Awake");
            if (awake != null) h.Patch(awake, postfix: new HarmonyMethod(typeof(ItemBridge), nameof(DropAwakePost)));
            ValCraftPlugin.Log.LogInfo($"[items] ItemDrop.Awake hook {(awake != null ? "on" : "missing")}");
            var gui = AccessTools.TypeByName("InventoryGui");
            var craft = gui == null ? null : AccessTools.Method(gui, "DoCrafting");
            if (craft != null) h.Patch(craft, prefix: new HarmonyMethod(typeof(ItemBridge), nameof(CraftPre)), postfix: new HarmonyMethod(typeof(ItemBridge), nameof(CraftPost)));
            ValCraftPlugin.Log.LogInfo($"[items] InventoryGui.DoCrafting hook {(craft != null ? "on" : "missing")}");
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
            try
            {
                var text = File.ReadAllText(_path);
                var merged = ItemMap.Merge(text, out var added);
                if (!ReferenceEquals(merged, text)) { File.WriteAllText(_path, merged); text = merged; if (added.Count > 0) ValCraftPlugin.Log.LogInfo($"[items] added {added.Count} new default mapping(s) to {_path}: {string.Join(", ", added)}"); }
                mt = File.GetLastWriteTimeUtc(_path);
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[items] mapping merge failed: {e.Message}"); }
            _mtime = mt;
            try { int n = Map.Load(File.ReadAllText(_path)); ValCraftPlugin.Log.LogInfo($"[items] mapping loaded: {n} Valheim items <-> {Map.ToValheim.Count} Minecraft items ({_path})"); }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[items] mapping file error: {e.Message}"); }
        }

        internal static bool HeldIs(string id) => _heldCount > 0 && _heldId == id;
        internal static string HeldId => _heldId;
        static object Shared(object it) => _sharedF?.GetValue(it);
        static int MaxStack(object it) { var sh = Shared(it); var f = sh == null ? null : AccessTools.Field(sh.GetType(), "m_maxStackSize"); return f == null ? 1 : (int)f.GetValue(sh); }
        static ushort DurOf(object it)
        {
            try
            {
                var sh = Shared(it); var use = sh == null ? null : AccessTools.Field(sh.GetType(), "m_useDurability");
                if (use == null || !(bool)use.GetValue(sh) || _getMaxDur == null || _durF == null) return ItemMap.NoDur;
                float max = (float)_getMaxDur.Invoke(it, null);
                return max <= 0 ? ItemMap.NoDur : ItemMap.Dur((float)_durF.GetValue(it) / max);
            }
            catch { return ItemMap.NoDur; }
        }
        static void SetDur(object it, ushort dur)
        {
            if (dur == ItemMap.NoDur || _getMaxDur == null || _durF == null) return;
            try { _durF.SetValue(it, (float)_getMaxDur.Invoke(it, null) * dur / 10000f); } catch { }
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
                    if (nv == null || _isOwner == null || _sceneDestroy == null || Zns() == null || !(bool)_isOwner.Invoke(nv, null)) { _spawned.Remove(go); continue; }
                    var data = _itemDataF.GetValue(drop);
                    string prefab = data == null ? null : PrefabOf(data) ?? go.name.Replace("(Clone)", "");
                    string mc = Map.McFor(prefab);
                    _spawned.Remove(go);
                    if (mc == null) continue;
                    int n = (int)_stack.GetValue(data);
                    if (Give(mc, n, prefab, "drop", DurOf(data)))
                    {
                        // always through ZNetScene.Destroy (a plain Object.Destroy leaves a dead entry in ZNetScene.m_instances)
                        try { _sceneDestroy.Invoke(Zns(), new object[] { go }); } catch (Exception e) { Diag.Warn("dropdestroy", "[items] could not remove converted drop: " + e.GetBaseException().Message); }
                    }
                }
            }

            // sweep: mapped materials in the Valheim inventory -> MC (not during crafting)
            BuildUpdate();
            if (!_crafting && !_building && t >= _nextSweep && PendingUse.Count == 0)
            {
                _nextSweep = t + 0.5f;
                var inv = Inv();
                if (inv != null && _getAllItems.Invoke(inv, null) is IList items)
                    foreach (var it in new ArrayList(items))
                    {
                        if (_equipped != null && (bool)_equipped.GetValue(it)) continue;
                        if (ReferenceEquals(it, _virtualHammer)) continue;
                        string prefab = PrefabOf(it), mc = Map.McFor(prefab);
                        if (mc == null) continue;
                        if (Give(mc, (int)_stack.GetValue(it), prefab, "inventory", DurOf(it))) _removeItem.Invoke(inv, new[] { it });
                    }
            }
        }

        static bool Give(string mc, int n, string prefab, string why, ushort dur = ItemMap.NoDur)
        {
            if (n <= 0 || _link == null) return false;
            bool ok = _link.TrySend(ValCraftLink.EvGiveItem, ItemMap.Give(_nextReq++, n, mc, dur));
            ValCraftPlugin.Log.LogInfo($"[items] {(ok ? "gave" : "could not give (ring full)")} {n} x {prefab} -> {mc}{(dur != ItemMap.NoDur ? $" ({dur / 100f:F0}% durability)" : "")} ({why})");
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

        static bool _awaitSnap;
        static void OnSnapshot(Dictionary<string, int> inv)
        {
            _awaitSnap = false;
            if (!_crafting && !_building) return;
            foreach (var kv in inv)
            {
                var v = Map.ValheimFor(kv.Key);
                if (v == null || kv.Value <= 0 || !Stackable(v)) continue; // materials only; tools/armour stay in MC
                uint req = _nextReq++;
                if (_link.TrySend(ValCraftLink.EvConsumeReq, ItemMap.Consume(req, kv.Value, 1, kv.Key))) PendingDeposit[req] = v;
            }
        }

        static bool Stackable(string prefabName)
        {
            try
            {
                var db = _objDbInst?.GetValue(null);
                var prefab = db == null ? null : _getItemPrefab.Invoke(db, new object[] { prefabName }) as GameObject;
                var drop = prefab == null ? null : prefab.GetComponent(_itemDrop);
                return drop != null && MaxStack(_itemDataF.GetValue(drop)) > 1;
            }
            catch { return false; }
        }

        static void OnAck(byte[] p)
        {
            var (req, removed, dur, id) = ItemMap.Ack(p);
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
            SetDur(data, dur);
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

        // ---------------- 0.8.3: crafted items go straight to MC ----------------
        static Dictionary<string, int> _preCraft;
        static HashSet<object> _preItems;
        static void CraftPre(object player)
        {
            _preCraft = null; _preItems = null;
            try
            {
                if (!Enabled || !_ok || !PlayerSync.Following || PlayerSync.Player == null || !ReferenceEquals(player, PlayerSync.Player)) return;
                var inv = Inv(); if (inv == null) return;
                _preCraft = new Dictionary<string, int>(); _preItems = new HashSet<object>();
                foreach (var it in (IList)_getAllItems.Invoke(inv, null)) { var pf = PrefabOf(it); if (pf == null) continue; _preItems.Add(it); _preCraft[pf] = (_preCraft.TryGetValue(pf, out var c) ? c : 0) + (int)_stack.GetValue(it); }
            }
            catch (Exception e) { Diag.Warn("craftpre", "[items] craft snapshot failed: " + e.GetBaseException().Message); }
        }
        static void CraftPost()
        {
            if (_preCraft == null) return;
            try
            {
                var inv = Inv(); if (inv == null) return;
                var now = new Dictionary<string, List<object>>();
                foreach (var it in (IList)_getAllItems.Invoke(inv, null)) { var pf = PrefabOf(it); if (pf == null) continue; if (!now.TryGetValue(pf, out var l)) now[pf] = l = new List<object>(); l.Add(it); }
                foreach (var kv in now)
                {
                    string mc = Map.McFor(kv.Key); if (mc == null) continue;
                    int total = 0; foreach (var it in kv.Value) total += (int)_stack.GetValue(it);
                    int delta = total - (_preCraft.TryGetValue(kv.Key, out var b) ? b : 0);
                    if (delta <= 0) continue;
                    // new item objects first (tools/armour, fresh stacks), then the growth of existing stacks
                    var order = new List<object>(); foreach (var it in kv.Value) if (!_preItems.Contains(it)) order.Add(it); foreach (var it in kv.Value) if (_preItems.Contains(it)) order.Add(it);
                    foreach (var it in order)
                    {
                        if (delta <= 0) break;
                        int st = (int)_stack.GetValue(it), n = Math.Min(st, delta);
                        if (!Give(mc, n, kv.Key, "crafted", DurOf(it))) break;
                        if (n >= st) _removeItem.Invoke(inv, new[] { it }); else if (_removeAmt != null) _removeAmt.Invoke(inv, new object[] { it, n });
                        delta -= n;
                    }
                }
            }
            catch (Exception e) { Diag.Warn("craftpost", "[items] crafted item transfer failed: " + e.GetBaseException().Message); }
            _preCraft = null; _preItems = null;
        }

        // ---------------- 0.8.3: mace = Valheim hammer (build mode) ----------------
        internal const string MaceId = "minecraft:mace";
        static bool _building; static object _virtualHammer;
        static Type _hudT, _playerT; static MethodInfo _toggleSel, _hideSel, _selVisible, _tryPlace, _removePiece, _getSel; static FieldInfo _rot; static PropertyInfo _hudInst; static FieldInfo _hudInstF;
        static bool _buildInit;
        static void BuildInit()
        {
            _buildInit = true;
            _hudT = AccessTools.TypeByName("Hud"); _playerT = AccessTools.TypeByName("Player");
            _toggleSel = AccessTools.Method(_hudT, "TogglePieceSelection"); _hideSel = AccessTools.Method(_hudT, "HidePieceSelection"); _selVisible = AccessTools.Method(_hudT, "IsPieceSelectionVisible");
            _hudInst = AccessTools.Property(_hudT, "instance"); _hudInstF = AccessTools.Field(_hudT, "m_instance");
            _tryPlace = AccessTools.Method(_playerT, "TryPlacePiece"); _removePiece = AccessTools.Method(_playerT, "RemovePiece", Type.EmptyTypes); _getSel = AccessTools.Method(_playerT, "GetSelectedPiece", Type.EmptyTypes);
            _rot = AccessTools.Field(_playerT, "m_placeRotation");
            _selWinF = AccessTools.Field(_hudT, "m_pieceSelectionWindow");

            ValCraftPlugin.Log.LogInfo($"[build] Hud.TogglePieceSelection={_toggleSel != null} TryPlacePiece={_tryPlace != null} RemovePiece={_removePiece != null} EquipItem={_equip != null}");
        }
        static object Hud() => _hudInst != null ? _hudInst.GetValue(null) : _hudInstF?.GetValue(null);

        /// <summary>EvBuildMenu: MC right-click with the mace, not on an interactable.</summary>
        internal static void BuildMenu()
        {
            if (!Enabled || !_ok || !PlayerSync.Following || PlayerSync.Player == null) return;
            if (!_buildInit) BuildInit();
            if (!_building && !EnterBuild()) return;
            // 0.8.5: Valheim's own piece window can't be used (MC has OS focus and the window stayed inactive), so MC
            // shows its own picker: send the piece list once the material deposit has landed.
            _listWantedAt = Time.unscaledTime;
            ValCraftPlugin.Log.LogInfo("[build] piece list requested by Minecraft");
        }

        static float _listWantedAt = -1f;
        static readonly List<Component> _menuPieces = new List<Component>();
        static MethodInfo _updAvail, _haveReq, _setSel, _localize; static Type _reqModeT; static PropertyInfo _locInst;

        static string L(string s)
        {
            try
            {
                if (_localize == null) { var lt = AccessTools.TypeByName("Localization"); _localize = AccessTools.Method(lt, "Localize", new[] { typeof(string) }); _locInst = AccessTools.Property(lt, "instance"); }
                var inst = _locInst?.GetValue(null);
                return inst == null || _localize == null ? s : (string)_localize.Invoke(inst, new object[] { s });
            }
            catch { return s; }
        }

        static void SendPieceList()
        {
            _listWantedAt = -1f;
            var pl = PlayerSync.Player; if (pl == null || _link == null) return;
            try
            {
                _updAvail = _updAvail ?? AccessTools.Method(_playerT, "UpdateAvailablePiecesList");
                _reqModeT = _reqModeT ?? AccessTools.Inner(_playerT, "RequirementMode");
                _haveReq = _haveReq ?? AccessTools.Method(_playerT, "HaveRequirements", new[] { AccessTools.TypeByName("Piece"), _reqModeT });
                try { _updAvail?.Invoke(pl, null); } catch { }
                var table = AccessTools.Field(_playerT, "m_buildPieces")?.GetValue(pl);
                if (table == null) { ValCraftPlugin.Log.LogWarning("[build] player has no build piece table (hammer not equipped?)"); return; }
                _menuPieces.Clear();
                var entries = new List<BuildCodec.Entry>();
                var avail = AccessTools.Field(table.GetType(), "m_availablePieces")?.GetValue(table) as IList;
                var seen = new HashSet<Component>();
                if (avail != null) foreach (IList cat in avail) if (cat != null) foreach (Component pc in cat) if (pc != null && seen.Add(pc)) _menuPieces.Add(pc);
                if (_menuPieces.Count == 0 && AccessTools.Field(table.GetType(), "m_pieces")?.GetValue(table) is IList all)
                {
                    var known = _reqModeT == null ? null : Enum.ToObject(_reqModeT, 1);
                    foreach (GameObject go in all) { var pc = go == null ? null : go.GetComponent("Piece"); if (pc == null || !seen.Add(pc)) continue; try { if (_haveReq != null && !(bool)_haveReq.Invoke(pl, new[] { pc, known })) continue; } catch { } _menuPieces.Add(pc); }
                }
                var canMode = _reqModeT == null ? null : Enum.ToObject(_reqModeT, 0);
                for (int i = 0; i < _menuPieces.Count; i++)
                {
                    var pc = _menuPieces[i]; var t = pc.GetType();
                    string name = L((string)AccessTools.Field(t, "m_name").GetValue(pc));
                    int cat = Convert.ToInt32(AccessTools.Field(t, "m_category").GetValue(pc));
                    var cost = new System.Text.StringBuilder();
                    if (AccessTools.Field(t, "m_resources")?.GetValue(pc) is Array reqs)
                        foreach (var r in reqs)
                        {
                            var rt = r.GetType(); var drop = AccessTools.Field(rt, "m_resItem").GetValue(r) as Component; int amt = (int)AccessTools.Field(rt, "m_amount").GetValue(r);
                            if (drop == null || amt <= 0) continue;
                            string mc = Map.McFor(drop.gameObject.name);
                            if (cost.Length > 0) cost.Append(", ");
                            cost.Append(amt).Append(' ').Append(L((string)AccessTools.Field(_sharedF.FieldType, "m_name").GetValue(Shared(_itemDataF.GetValue(drop))))).Append(mc != null ? " (" + mc.Replace("minecraft:", "") + ")" : " (Valheim only)");
                        }
                    bool can = false; try { can = _haveReq != null && (bool)_haveReq.Invoke(pl, new[] { pc, canMode }); } catch { }
                    entries.Add(new BuildCodec.Entry { Index = (ushort)i, Category = (byte)Math.Max(0, Math.Min(255, cat)), CanBuild = can, Name = name, Cost = cost.ToString() });
                }
                int sent = 0; foreach (var m in BuildCodec.Encode(entries)) if (_link.TrySend(ValCraftLink.EvBuildPieces, m)) sent++;
                ValCraftPlugin.Log.LogInfo($"[build] sent {entries.Count} pieces to the Minecraft picker ({sent} messages)");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[build] piece list failed: " + e.GetBaseException().Message); }
        }

        /// <summary>EvBuildSelect {u16 index} from the MC picker.</summary>
        internal static void BuildSelect(byte[] p)
        {
            if (!_building || p == null || p.Length < 2 || PlayerSync.Player == null) return;
            int i = BitConverter.ToUInt16(p, 0);
            if (i >= _menuPieces.Count || _menuPieces[i] == null) { ValCraftPlugin.Log.LogWarning($"[build] picker index {i} out of range"); return; }
            try
            {
                _setSel = _setSel ?? AccessTools.Method(_playerT, "SetSelectedPiece", new[] { AccessTools.TypeByName("Piece") });
                bool ok = _setSel != null && (bool)_setSel.Invoke(PlayerSync.Player, new object[] { _menuPieces[i] });
                ValCraftPlugin.Log.LogInfo($"[build] selected {_menuPieces[i].name}: {(ok ? "ok (aim + left-click to place)" : "Valheim refused")}");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[build] select failed: " + e.GetBaseException().Message); }
        }

        // 0.8.4: in 0.8.3 the menu opened inside the HUD that ValCraft hides (HideValheimHud), so Valheim reported it
        // visible, input was handed to Valheim, and nothing was on screen: MC looked frozen. Now the HUD is shown while
        // the menu is up, input goes to Valheim only once the window is really visible, and Esc / right-click / timeout close it.
        static float _menuAskedAt = -1f, _menuShownAt = -1f;
        static FieldInfo _selWinF, _hudRootF;
        static GameObject SelWindow() { var hud = Hud(); return hud == null || _selWinF == null ? null : _selWinF.GetValue(hud) as GameObject; }

        /// <summary>The piece window is really on screen (active in hierarchy, HUD root included).</summary>
        internal static bool BuildMenuShown()
        {
            if (!_building) return false;
            try { var w = SelWindow(); return w != null && w.activeInHierarchy; } catch { return false; }
        }
        static bool MenuRequested() => _building && (_menuAskedAt >= 0 || _menuShownAt >= 0);

        static void ToggleMenu()
        {
            try
            {
                var hud = Hud();
                if (hud == null || _toggleSel == null) { ValCraftPlugin.Log.LogWarning("[build] Hud.instance or TogglePieceSelection missing: no build menu"); return; }
                bool wasOpen = _selVisible != null && (bool)_selVisible.Invoke(null, null);
                if (wasOpen) { CloseMenu("second right-click"); return; }
                PlayerSync.SetHud(true); // the window is under the HUD root
                _toggleSel.Invoke(hud, null);
                bool flag = _selVisible != null && (bool)_selVisible.Invoke(null, null);
                var w = SelWindow();
                _menuAskedAt = Time.unscaledTime; _menuShownAt = -1f;
                ValCraftPlugin.Log.LogInfo($"[build] TogglePieceSelection called: Valheim flag visible={flag}, window={(w == null ? "null" : w.name)} activeSelf={(w != null && w.activeSelf)} inHierarchy={(w != null && w.activeInHierarchy)}");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[build] piece menu: " + e.GetBaseException().Message); CloseMenu("error"); }
        }

        static void CloseMenu(string why)
        {
            try { if (_selVisible != null && (bool)_selVisible.Invoke(null, null)) _hideSel?.Invoke(null, null); } catch { }
            if (_menuAskedAt >= 0 || _menuShownAt >= 0) ValCraftPlugin.Log.LogInfo($"[build] piece menu closed ({why}); input back to Minecraft");
            _menuAskedAt = -1f; _menuShownAt = -1f;
        }

        static void MenuUpdate()
        {
            if (!MenuRequested()) return;
            float t = Time.unscaledTime;
            bool shown = BuildMenuShown();
            bool flag = false; try { flag = _selVisible != null && (bool)_selVisible.Invoke(null, null); } catch { }
            if (_menuShownAt < 0)
            {
                if (shown) { _menuShownAt = t; _menuAskedAt = -1f; ValCraftPlugin.Log.LogInfo("[build] piece menu visible: mouse/keyboard to Valheim (pick a piece; Esc or right-click closes)"); }
                else if (t - _menuAskedAt > 1.5f) { ValCraftPlugin.Log.LogWarning($"[build] piece menu did not become visible within 1.5 s (Valheim flag {flag}); giving input back to Minecraft"); CloseMenu("not visible"); }
                return;
            }
            var kb = BepInEx.UnityInput.Current;
            if (!flag || !shown) { CloseMenu("piece chosen / closed by Valheim"); return; }
            if (kb.GetKeyDown(KeyCode.Escape)) { CloseMenu("Esc"); return; }
            if (kb.GetMouseButtonDown(1)) { CloseMenu("right-click"); return; }
            if (t - _menuShownAt > 60f) { CloseMenu("60 s timeout"); return; }
        }

        static bool EnterBuild()
        {
            if (!HeldIs(MaceId)) return false;
            var inv = Inv(); if (inv == null || _equip == null) return false;
            object hammer = null;
            foreach (var it in (IList)_getAllItems.Invoke(inv, null)) if (PrefabOf(it) == "Hammer") { hammer = it; break; }
            if (hammer == null) { hammer = AddValheim("Hammer", 1); _virtualHammer = hammer; }
            if (hammer == null) { ValCraftPlugin.Log.LogWarning("[build] could not give the Valheim player a hammer"); return false; }
            try { _equip.Invoke(PlayerSync.Player, new object[] { hammer, false }); } catch (Exception e) { ValCraftPlugin.Log.LogWarning("[build] equip hammer: " + e.GetBaseException().Message); return false; }
            _building = true;
            _link?.TrySend(ValCraftLink.EvBuildState, new byte[] { 1 });
            _snapRequested = _awaitSnap = _link != null && _link.TrySend(ValCraftLink.EvInvReq, null); // building costs come from MC materials
            ValCraftPlugin.Log.LogInfo($"[build] build mode on (mace = {(_virtualHammer != null ? "virtual" : "real")} Valheim hammer); moving mapped Minecraft materials into the Valheim inventory");
            return true;
        }

        static void ExitBuild(string why)
        {
            if (!_building) return;
            CloseMenu("leaving build mode");
            _building = false;
            try
            {
                var inv = Inv();
                if (_virtualHammer != null && PlayerSync.Player != null)
                {
                    _unequip?.Invoke(PlayerSync.Player, new object[] { _virtualHammer, false });
                    if (inv != null) _removeItem.Invoke(inv, new[] { _virtualHammer });
                }
            }
            catch (Exception e) { Diag.Warn("buildexit", "[build] exit: " + e.GetBaseException().Message); }
            _virtualHammer = null;
            _link?.TrySend(ValCraftLink.EvBuildState, new byte[] { 0 });
            _nextSweep = 0;
            ValCraftPlugin.Log.LogInfo($"[build] build mode off ({why}); returning materials to Minecraft");
        }

        static void BuildUpdate()
        {
            if (!_building) return;
            if (!PlayerSync.Following || PlayerSync.Player == null) { _virtualHammer = null; ExitBuild("player gone"); return; }
            if (_listWantedAt >= 0 && ((PendingDeposit.Count == 0 && !_awaitSnap) || Time.unscaledTime - _listWantedAt > 1.5f)) SendPieceList();
            if (!HeldIs(MaceId)) ExitBuild("mace no longer held");
        }

        internal static void ResetBuild() { if (_building) { CloseMenu("disconnect"); _building = false; _virtualHammer = null; } }

        /// <summary>EvBuildAction: 1 place, 2 remove, 3 rotate.</summary>
        internal static void BuildAction(byte a)
        {
            if (!_building || PlayerSync.Player == null) return;
            var pl = PlayerSync.Player;
            try
            {
                if (a == 1)
                {
                    var piece = _getSel?.Invoke(pl, null);
                    if (piece == null) { ValCraftPlugin.Log.LogInfo("[build] no piece selected (right-click with the mace opens the menu)"); return; }
                    bool ok = (bool)_tryPlace.Invoke(pl, new[] { piece });
                    ValCraftPlugin.Log.LogInfo($"[build] place {(piece as Component)?.name}: {(ok ? "placed" : "not placed (invalid spot, no workbench or missing materials)")}");
                }
                else if (a == 2) { bool ok = (bool)_removePiece.Invoke(pl, null); ValCraftPlugin.Log.LogInfo($"[build] remove: {(ok ? "removed" : "nothing removable aimed at")}"); }
                else if (a == 3 && _rot != null) _rot.SetValue(pl, (int)_rot.GetValue(pl) + 1);
            }
            catch (Exception e) { Diag.Warn("buildact", "[build] action failed: " + e.GetBaseException().Message); }
        }
    }
}
