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
    internal static partial class ItemBridge
    {
        internal static bool Enabled = true;
        /// 0.15.2 [Items] PreferMinecraftItems: crafted / upgraded Valheim items go to the MC inventory as the ValCraft item
        /// (quality + crafter kept); MC gear is lent to the Valheim inventory while a station is open so it can be upgraded.
        internal static bool PreferMc = true;
        static readonly Dictionary<uint, string> PendingGear = new Dictionary<uint, string>();
        static readonly HashSet<object> _keepValheim = new HashSet<object>();
        /// 0.15.5: which MC ids (and how many) were lent into each Valheim prefab for crafting/building; returns use them first
        /// (40 planks lent as Wood come back as planks, not logs)
        static readonly Dictionary<string, List<KeyValuePair<string, int>>> _lent = new Dictionary<string, List<KeyValuePair<string, int>>>();
        static void Lend(string prefab, string mcId, int n)
        {
            if (!_lent.TryGetValue(prefab, out var l)) _lent[prefab] = l = new List<KeyValuePair<string, int>>();
            l.Add(new KeyValuePair<string, int>(mcId, n));
        }

        static FieldInfo _qualityF, _crafterF;
        static readonly ItemMap Map = new ItemMap();
        static string _path; static DateTime _mtime; static float _nextFileCheck, _nextSweep;
        static uint _nextReq = 1;
        static string _heldId = ""; static int _heldCount;
        static readonly Dictionary<uint, (GameObject target, string valheim)> PendingUse = new Dictionary<uint, (GameObject, string)>();
        static readonly Dictionary<uint, string> PendingDeposit = new Dictionary<uint, string>();
        static readonly ItemMap.Snapshot Snap = new ItemMap.Snapshot();
        static bool _crafting, _snapRequested; static int _sweepsSinceSession;
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
            _humanoid = Refl.T("Humanoid"); _itemDrop = Refl.T("ItemDrop"); _inventory = Refl.T("Inventory");
            _itemData = _itemDrop == null ? null : AccessTools.Inner(_itemDrop, "ItemData");
            _objectDb = Refl.T("ObjectDB"); _invGui = Refl.T("InventoryGui"); _znetScene = Refl.T("ZNetScene"); _znv = Refl.T("ZNetView");
            var playerT = Refl.T("Player");
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
            _qualityF = AccessTools.Field(_itemData, "m_quality"); _crafterF = AccessTools.Field(_itemData, "m_crafterName");
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
            var t = Refl.T("ItemDrop");
            var awake = t == null ? null : AccessTools.Method(t, "Awake");
            if (awake != null) h.Patch(awake, postfix: new HarmonyMethod(typeof(ItemBridge), nameof(DropAwakePost)));
            ValCraftPlugin.Log.LogInfo($"[items] ItemDrop.Awake hook {(awake != null ? "on" : "missing")}");
            var gui = Refl.T("InventoryGui");
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
            try { int n = Map.Load(File.ReadAllText(_path)); AddCookables(Map); ValCraftPlugin.Log.LogInfo($"[items] mapping loaded: {n} Valheim items <-> {Map.ToValheim.Count} Minecraft items ({_path})"); }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[items] mapping file error: {e.Message}"); }
        }

        // ---------------- 0.15.5: MC raw meat / fish on Valheim cooking stations ----------------
        /// MC raw id -> (Valheim raw prefab, MC cooked id). The Valheim output (CookedMeat / FishCooked) comes back as the
        /// cooked version of what was put on (raw chicken -> cooked chicken), not the table's first item.
        static Dictionary<string, (string raw, string cooked)> Cookables => ItemMap.Cookables;
        static Dictionary<string, string> CookedOf => ItemMap.CookedOf;
        static readonly Dictionary<string, Queue<string>> _cooking = new Dictionary<string, Queue<string>>();
        static void AddCookables(ItemMap map) => map.AddCookables();
        static void NoteCooking(string mcRawId, int n)
        {
            if (!Cookables.TryGetValue(mcRawId, out var c) || !CookedOf.TryGetValue(c.raw, out var cooked)) return;
            if (!_cooking.TryGetValue(cooked, out var q)) _cooking[cooked] = q = new Queue<string>();
            for (int i = 0; i < n && q.Count < 64; i++) q.Enqueue(c.cooked);
        }
        /// MC id for a Valheim cooked prefab coming back: the cooked version of what went on the station, else the table's.
        static string CookedMc(string prefab, string def)
        {
            return _cooking.TryGetValue(prefab, out var q) && q.Count > 0 ? q.Dequeue() : def;
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
        static int QualityOf(object it) { try { return _qualityF == null ? 1 : Math.Max(1, (int)_qualityF.GetValue(it)); } catch { return 1; } }
        static string CrafterOf(object it) { try { return _crafterF?.GetValue(it) as string ?? ""; } catch { return ""; } }
        static bool IsGear(object it) => MaxStack(it) <= 1;
        static string McForItem(object it, string prefab) => IsGear(it) ? Map.CraftedMcFor(prefab) : Map.McFor(prefab);
        /// Gear (single-stack) goes over EvGiveItemQ with quality + crafter; materials over the plain EvGiveItem.
        static bool GiveItem(object it, string mc, int n, string prefab, string why)
        {
            if (!IsGear(it)) return Give(mc, n, prefab, why, DurOf(it));
            if (n <= 0 || _link == null) return false;
            var q = new ItemMap.Q { Req = _nextReq++, Count = n, Dur = DurOf(it), Quality = QualityOf(it), Crafter = CrafterOf(it), Id = mc };
            bool ok = _link.TrySend(ValCraftLink.EvGiveItemQ, ItemMap.EncodeQ(q));
            ValCraftPlugin.Log.LogInfo($"[items] {(ok ? "gave" : "could not give (ring full)")} {n} x {prefab} -> {mc} (quality {q.Quality}{(q.Crafter.Length > 0 ? ", crafted by " + q.Crafter : "")}) ({why})");
            return ok;
        }
        static object Inv() => PlayerSync.Player == null ? null : _getInventory.Invoke(PlayerSync.Player, null);
        static string PrefabOf(object itemData) => (_dropPrefab.GetValue(itemData) as GameObject)?.name;

        internal static bool Handle(LinkEvent ev)
        {
            switch (ev.Type)
            {
                case ValCraftLink.EvHeldItem: { var (c, id) = ItemMap.Held(ev.Payload); _heldCount = c; _heldId = id; return true; }
                case ValCraftLink.EvConsumeAck: OnAck(ev.Payload); return true;
                case ValCraftLink.EvConsumeAckQ: OnAckQ(ev.Payload); return true;
                case ValCraftLink.EvChestOp: ChestOp(ev.Payload); return true;
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

            ChestUpdate();
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
                    if (n == 1) mc = CookedMc(prefab, mc); // 0.15.5: cooked MC meat comes back as itself
                    if (Give(mc, n, prefab, "drop", DurOf(data)))
                    {
                        // always through ZNetScene.Destroy (a plain Object.Destroy leaves a dead entry in ZNetScene.m_instances)
                        try { _sceneDestroy.Invoke(Zns(), new object[] { go }); } catch (Exception e) { Diag.Warn("dropdestroy", "[items] could not remove converted drop: " + e.GetBaseException().Message); }
                    }
                }
            }

            // sweep: mapped materials in the Valheim inventory -> MC (not during crafting)
            BuildUpdate();
            LedgerTick();
            if (!_crafting && !_building && t >= _nextSweep && PendingUse.Count == 0)
            {
                _nextSweep = t + 0.5f;
                _sweepsSinceSession++;
                var inv = Inv();
                if (inv != null && _getAllItems.Invoke(inv, null) is IList items)
                    foreach (var it in new ArrayList(items))
                    {
                        if (_equipped != null && (bool)_equipped.GetValue(it)) continue;
                        if (ReferenceEquals(it, _virtualHammer)) continue;
                        if (_keepValheim.Contains(it)) continue;
                        if (!PreferMc && IsGear(it)) continue; // 0.15.2: with PreferMinecraftItems off, Valheim gear stays in Valheim
                        string prefab = PrefabOf(it), mc = McForItem(it, prefab);
                        if (mc == null) continue;
                        if (!IsGear(it) && _cooking.TryGetValue(prefab, out var cq) && cq.Count > 0)
                        {
                            // 0.15.5: cooked on a station from MC meat -> the matching MC cooked item
                            int st = (int)_stack.GetValue(it), given = 0;
                            var by = new Dictionary<string, int>();
                            for (int i = 0; i < st; i++) { var id = CookedMc(prefab, mc); by[id] = (by.TryGetValue(id, out var c0) ? c0 : 0) + 1; }
                            foreach (var kv in by) if (Give(kv.Key, kv.Value, prefab, "cooked")) given += kv.Value;
                            if (given >= st) _removeItem.Invoke(inv, new[] { it }); else if (given > 0 && _removeAmt != null) _removeAmt.Invoke(inv, new object[] { it, given });
                            continue;
                        }
                        if (!IsGear(it) && _lent.ContainsKey(prefab))
                        {
                            // 0.15.5: lent materials go back as the exact MC items (planks stay planks)
                            int st = (int)_stack.GetValue(it), given = 0;
                            foreach (var part in ItemMap.ReturnParts(_lent, prefab, st, mc)) { if (!Give(part.Key, part.Value, prefab, "returned", DurOf(it))) break; given += part.Value; }
                            if (given >= st) _removeItem.Invoke(inv, new[] { it }); else if (given > 0 && _removeAmt != null) _removeAmt.Invoke(inv, new object[] { it, given });
                            continue;
                        }
                        if (GiveItem(it, mc, (int)_stack.GetValue(it), prefab, "inventory")) _removeItem.Invoke(inv, new[] { it });
                    }
            }
        }

        /// called at the end of Update: forget the ledger 2 sweeps after the station / build session (whatever was used up is gone)
        static void LedgerTick() { if (_crafting || _building) _sweepsSinceSession = 0; else if (_sweepsSinceSession > 2 && _lent.Count > 0) _lent.Clear(); }

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
            if (_building || IsHammerId(_heldId)) return false; // 0.15.5: never feed the build hammer to a station (it was eaten by cooking stations)
            string valheim = Map.ValheimFor(_heldId);
            if (valheim == null) return false;
            bool station = false;
            foreach (var n in StationTypes) { var ty = Refl.T(n); if (ty != null && target.GetComponentInParent(ty) != null) { station = true; break; } }
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
                if (v == null || kv.Value <= 0) continue;
                if (!Stackable(v))
                {
                    // 0.15.2: lend MC gear (main inventory) to Valheim while a station is open so Valheim can upgrade it;
                    // only ids that come back as themselves (no lossy conversion), at most 8 per id
                    if (!PreferMc || !_crafting || Map.CraftedMcFor(v) != kv.Key) continue;
                    for (int i = 0; i < Math.Min(8, kv.Value); i++) { uint rq = _nextReq++; if (_link.TrySend(ValCraftLink.EvConsumeReq, ItemMap.Consume(rq, 1, ItemMap.ConsumeGear, kv.Key))) PendingGear[rq] = v; }
                    continue;
                }
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

        static void OnAckQ(byte[] p)
        {
            var q = ItemMap.DecodeQ(p);
            if (!PendingGear.TryGetValue(q.Req, out var v)) return;
            PendingGear.Remove(q.Req);
            if (q.Count <= 0) return;
            var data = AddValheim(v, 1);
            if (data == null)
            {
                _link?.TrySend(ValCraftLink.EvGiveItemQ, ItemMap.EncodeQ(new ItemMap.Q { Req = _nextReq++, Count = 1, Dur = q.Dur, Quality = q.Quality, Crafter = q.Crafter, Id = q.Id }));
                ValCraftPlugin.Log.LogWarning($"[items] returned {q.Id} to Minecraft (could not add the Valheim item)"); return;
            }
            try { _qualityF?.SetValue(data, q.Quality); if (!string.IsNullOrEmpty(q.Crafter)) _crafterF?.SetValue(data, q.Crafter); } catch { }
            SetDur(data, q.Dur);
            ValCraftPlugin.Log.LogInfo($"[items] lent {q.Id} (quality {q.Quality}) -> {v} to the Valheim inventory for upgrading");
        }

        static void OnAck(byte[] p)
        {
            var (req, removed, dur, id) = ItemMap.Ack(p);
            if (PendingGear.Remove(req)) return; // mode-2 request answered by an older/no-player path: nothing was removed
            if (PendingDeposit.TryGetValue(req, out var dv))
            {
                PendingDeposit.Remove(req);
                if (removed > 0 && AddValheim(dv, removed) == null) GiveBack(id, removed);
                else if (removed > 0) { Lend(dv, id, removed); ValCraftPlugin.Log.LogInfo($"[items] moved {removed} x {id} -> {dv} into the Valheim inventory for crafting"); }
                return;
            }
            if (!PendingUse.TryGetValue(req, out var pu)) return;
            PendingUse.Remove(req);
            if (removed <= 0) { ValCraftPlugin.Log.LogInfo($"[items] Minecraft had no {id} to use"); return; }
            var data = AddValheim(pu.valheim, removed);
            if (data == null) { GiveBack(id, removed); return; }
            if (pu.target != null && Refl.T("CookingStation") is Type _cst && pu.target.GetComponentInParent(_cst) != null) NoteCooking(id, removed);
            SetDur(data, dur);
            try
            {
                _tryUse.Invoke(PlayerSync.Player, new object[] { data, pu.target, false });
                ValCraftPlugin.Log.LogInfo($"[items] used {pu.valheim} on {pu.target?.name} (leftovers return to Minecraft)");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[items] use on {pu.target?.name} failed: {e.InnerException?.Message ?? e.Message}"); }
            _nextSweep = 0; // return whatever the station didn't take
        }

        internal static void GiveMc(string id, int n) { _link?.TrySend(ValCraftLink.EvGiveItem, ItemMap.Give(_nextReq++, n, id)); }
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
        static Dictionary<object, int> _preQuality;
        static void CraftPre(object player)
        {
            _preCraft = null; _preItems = null; _preQuality = null;
            try
            {
                if (!Enabled || !_ok || !PlayerSync.Following || PlayerSync.Player == null || !ReferenceEquals(player, PlayerSync.Player)) return;
                var inv = Inv(); if (inv == null) return;
                _preCraft = new Dictionary<string, int>(); _preItems = new HashSet<object>(); _preQuality = new Dictionary<object, int>();
                foreach (var it in (IList)_getAllItems.Invoke(inv, null)) { var pf = PrefabOf(it); if (pf == null) continue; _preItems.Add(it); _preQuality[it] = QualityOf(it); _preCraft[pf] = (_preCraft.TryGetValue(pf, out var c) ? c : 0) + (int)_stack.GetValue(it); }
            }
            catch (Exception e) { Diag.Warn("craftpre", "[items] craft snapshot failed: " + e.GetBaseException().Message); }
        }
        static void CraftPost()
        {
            if (_preCraft == null) return;
            try
            {
                var inv = Inv(); if (inv == null) return;
                if (!PreferMc)
                {
                    // 0.15.2: PreferMinecraftItems off -> the crafted item stays a Valheim item (the sweep leaves it alone)
                    foreach (var it in (IList)_getAllItems.Invoke(inv, null)) if (!_preItems.Contains(it)) _keepValheim.Add(it);
                    _preCraft = null; _preItems = null; _preQuality = null; return;
                }
                // 0.15.2: upgrades (same ItemData, higher quality) go back to MC right away as the upgraded MC item
                foreach (var it in new ArrayList((IList)_getAllItems.Invoke(inv, null)))
                {
                    if (!_preQuality.TryGetValue(it, out var q0) || QualityOf(it) <= q0) continue;
                    if (_equipped != null && (bool)_equipped.GetValue(it)) continue;
                    string pf = PrefabOf(it), mc = pf == null ? null : McForItem(it, pf);
                    if (mc == null) continue;
                    if (GiveItem(it, mc, (int)_stack.GetValue(it), pf, $"upgraded {q0}->{QualityOf(it)}")) _removeItem.Invoke(inv, new[] { it });
                }
                var now = new Dictionary<string, List<object>>();
                foreach (var it in (IList)_getAllItems.Invoke(inv, null)) { var pf = PrefabOf(it); if (pf == null) continue; if (!now.TryGetValue(pf, out var l)) now[pf] = l = new List<object>(); l.Add(it); }
                foreach (var kv in now)
                {
                    string mc = Map.CraftedMcFor(kv.Key); if (mc == null) continue; // 0.15.2: the ValCraft item
                    int total = 0; foreach (var it in kv.Value) total += (int)_stack.GetValue(it);
                    int delta = total - (_preCraft.TryGetValue(kv.Key, out var b) ? b : 0);
                    if (delta <= 0) continue;
                    // new item objects first (tools/armour, fresh stacks), then the growth of existing stacks
                    var order = new List<object>(); foreach (var it in kv.Value) if (!_preItems.Contains(it)) order.Add(it); foreach (var it in kv.Value) if (_preItems.Contains(it)) order.Add(it);
                    foreach (var it in order)
                    {
                        if (delta <= 0) break;
                        int st = (int)_stack.GetValue(it), n = Math.Min(st, delta);
                        if (!GiveItem(it, mc, n, kv.Key, "crafted")) break; // not sent -> stays a Valheim item (fallback)
                        if (n >= st) _removeItem.Invoke(inv, new[] { it }); else if (_removeAmt != null) _removeAmt.Invoke(inv, new object[] { it, n });
                        delta -= n;
                    }
                }
            }
            catch (Exception e) { Diag.Warn("craftpost", "[items] crafted item transfer failed: " + e.GetBaseException().Message); }
            _preCraft = null; _preItems = null; _preQuality = null;
        }

        // ---------------- 0.8.3: mace = Valheim hammer (build mode) ----------------
        internal const string MaceId = "minecraft:mace", HammerId = "valcraft:valheim_hammer";
        /// 0.15.5: the ValCraft build hammer item is a build hammer too
        internal static bool HeldHammer() => HeldIs(MaceId) || HeldIs(HammerId);
        internal static bool IsHammerId(string id) => id == MaceId || id == HammerId;
        static bool _building; static object _virtualHammer, _hammerUsed;
        static Type _hudT, _playerT; static MethodInfo _toggleSel, _hideSel, _selVisible, _tryPlace, _removePiece, _getSel; static FieldInfo _rot; static PropertyInfo _hudInst; static FieldInfo _hudInstF;
        static bool _buildInit;
        static void BuildInit()
        {
            _buildInit = true;
            _hudT = Refl.T("Hud"); _playerT = Refl.T("Player");
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
                if (_localize == null) { var lt = Refl.T("Localization"); _localize = AccessTools.Method(lt, "Localize", new[] { typeof(string) }); _locInst = AccessTools.Property(lt, "instance"); }
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
                _haveReq = _haveReq ?? AccessTools.Method(_playerT, "HaveRequirements", new[] { Refl.T("Piece"), _reqModeT });
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
                // 0.10.0: the hammer's Repair entry first in the picker (select it, then left-click a damaged piece)
                try
                {
                    Component rep = null;
                    foreach (var pc in _menuPieces) if (IsRepair(pc)) { rep = pc; break; }
                    if (rep == null && AccessTools.Field(table.GetType(), "m_pieces")?.GetValue(table) is IList allP)
                        foreach (GameObject go in allP) { var pc = go == null ? null : go.GetComponent("Piece"); if (pc != null && IsRepair(pc)) { rep = pc; break; } }
                    if (rep != null) { _menuPieces.Remove(rep); _menuPieces.Insert(0, rep); }
                    else ValCraftPlugin.Log.LogInfo("[build] no Repair entry in this piece table");
                }
                catch { }
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
                    bool can = false; try { can = IsRepair(pc) || _haveReq != null && (bool)_haveReq.Invoke(pl, new[] { pc, canMode }); } catch { }
                    if (IsRepair(pc)) { name = "Repair (" + name + ")"; cost.Clear(); cost.Append("left-click a damaged piece"); }
                    entries.Add(new BuildCodec.Entry { Index = (ushort)i, Category = (byte)Math.Max(0, Math.Min(255, cat)), CanBuild = can, Name = name, Cost = cost.ToString() });
                }
                int sent = 0; foreach (var m in BuildCodec.Encode(entries)) if (_link.TrySend(ValCraftLink.EvBuildPieces, m)) sent++;
                ValCraftPlugin.Log.LogInfo($"[build] sent {entries.Count} pieces to the Minecraft picker ({sent} messages)");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[build] piece list failed: " + e.GetBaseException().Message); }
        }

        static bool IsRepair(Component pc) { try { return pc != null && AccessTools.Field(pc.GetType(), "m_repairPiece")?.GetValue(pc) is bool b && b; } catch { return false; } }
        static MethodInfo _repair, _rightItem;

        /// <summary>EvBuildSelect {u16 index} from the MC picker.</summary>
        internal static void BuildSelect(byte[] p)
        {
            if (!_building || p == null || p.Length < 2 || PlayerSync.Player == null) return;
            int i = BitConverter.ToUInt16(p, 0);
            if (i >= _menuPieces.Count || _menuPieces[i] == null) { ValCraftPlugin.Log.LogWarning($"[build] picker index {i} out of range"); return; }
            try
            {
                _setSel = _setSel ?? AccessTools.Method(_playerT, "SetSelectedPiece", new[] { Refl.T("Piece") });
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
        /// <summary>0.12.1: the HUD must stay visible while the build menu is requested/open (it lives under the HUD root).</summary>
        internal static bool MenuWanted() => MenuRequested();
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
            if (!HeldHammer()) return false;
            var inv = Inv(); if (inv == null || _equip == null) return false;
            object hammer = null;
            foreach (var it in (IList)_getAllItems.Invoke(inv, null)) if (PrefabOf(it) == "Hammer") { hammer = it; break; }
            if (hammer == null) { hammer = AddValheim("Hammer", 1); _virtualHammer = hammer; }
            if (hammer == null) { ValCraftPlugin.Log.LogWarning("[build] could not give the Valheim player a hammer"); return false; }
            _hammerUsed = hammer;
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
            // 0.8.6: 0.8.5 only unequipped a *virtual* hammer; with a real one (log: "real Valheim hammer") Valheim stayed in
            // place mode and the ghost stayed. Now: unequip whatever hammer we equipped, leave place mode, drop the ghost.
            var pl = PlayerSync.Player;
            try
            {
                var inv = Inv();
                if (pl != null && _hammerUsed != null) _unequip?.Invoke(pl, new object[] { _hammerUsed, false });
                if (_virtualHammer != null && inv != null) _removeItem.Invoke(inv, new[] { _virtualHammer });
            }
            catch (Exception e) { Diag.Warn("buildexit", "[build] exit unequip: " + e.GetBaseException().Message); }
            try
            {
                if (pl != null)
                {
                    var spm = AccessTools.Method(_playerT, "SetPlaceMode", new[] { Refl.T("PieceTable") });
                    spm?.Invoke(pl, new object[] { null });
                    foreach (var fn in new[] { "m_placementGhost", "m_placementMarkerInstance" })
                    {
                        var f = AccessTools.Field(_playerT, fn);
                        if (f?.GetValue(pl) is GameObject g && g != null) { UnityEngine.Object.Destroy(g); f.SetValue(pl, null); }
                    }
                    ValCraftPlugin.Log.LogInfo($"[build] place mode cleared (SetPlaceMode {(spm != null ? "ok" : "missing")}), ghost removed, hammer unequipped");
                }
            }
            catch (Exception e) { Diag.Warn("buildexit2", "[build] exit place mode: " + e.GetBaseException().Message); }
            _hammerUsed = null;
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
            if (!HeldHammer()) ExitBuild("hammer no longer held");
        }

        internal static void ResetBuild() { if (_building) { CloseMenu("disconnect"); _building = false; _virtualHammer = null; } }

        static FieldInfo _manualSnap, _ghost;
        /// <summary>0.13.2: Left Alt in MC -> next snap point of the placement ghost (Player.m_manualSnapPoint 0..n-1, then -1 = automatic).</summary>
        static void CycleSnap(Component pl)
        {
            _manualSnap = _manualSnap ?? AccessTools.Field(_playerT, "m_manualSnapPoint");
            _ghost = _ghost ?? AccessTools.Field(_playerT, "m_placementGhost");
            if (_manualSnap == null) { Diag.Warn("snap", "[build] Player.m_manualSnapPoint not found: snap cycling unavailable"); return; }
            var g = _ghost?.GetValue(pl) as GameObject;
            int n = 0;
            if (g != null) foreach (Transform c in g.transform) { try { if (c.CompareTag("snappoint")) n++; } catch { } }
            int cur = (int)_manualSnap.GetValue(pl), next = cur + 1;
            if (n > 0 && next >= n) next = -1;
            _manualSnap.SetValue(pl, next);
            ValCraftPlugin.Log.LogInfo($"[build] snap point {(next < 0 ? "automatic" : (next + 1) + "/" + n)} ({g?.name})");
        }

        /// <summary>EvBuildAction: 1 place, 2 remove, 3 rotate, 4 cycle snap point.</summary>
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
                    if (IsRepair(piece as Component))
                    {
                        _repair = _repair ?? AccessTools.Method(_playerT, "Repair");
                        _rightItem = _rightItem ?? AccessTools.Method(_playerT, "GetRightItem", Type.EmptyTypes);
                        var tool = _rightItem?.Invoke(pl, null) ?? _virtualHammer;
                        if (_repair == null || tool == null) { ValCraftPlugin.Log.LogWarning($"[build] repair unavailable (Player.Repair={_repair != null}, tool={tool != null})"); return; }
                        _repair.Invoke(pl, new[] { tool, piece });
                        ValCraftPlugin.Log.LogInfo("[build] repair on the aimed piece (Valheim shows the result)");
                        return;
                    }
                    bool ok = (bool)_tryPlace.Invoke(pl, new[] { piece });
                    ValCraftPlugin.Log.LogInfo($"[build] place {(piece as Component)?.name}: {(ok ? "placed" : "not placed (invalid spot, no workbench or missing materials)")}");
                }
                else if (a == 2) { bool ok = (bool)_removePiece.Invoke(pl, null); ValCraftPlugin.Log.LogInfo($"[build] remove: {(ok ? "removed" : "nothing removable aimed at")}"); }
                else if (a == 3 && _rot != null) _rot.SetValue(pl, (int)_rot.GetValue(pl) + 1);
                else if (a == 5 && _rot != null) _rot.SetValue(pl, (int)_rot.GetValue(pl) - 1); // 0.15.5: mouse wheel back
                else if (a == 4) CycleSnap(pl);
            }
            catch (Exception e) { Diag.Warn("buildact", "[build] action failed: " + e.GetBaseException().Message); }
        }
    }
}
