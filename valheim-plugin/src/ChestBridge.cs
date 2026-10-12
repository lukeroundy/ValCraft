using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// 0.10.0: Valheim chests in a Minecraft screen. MC right-click on a Valheim Container opens it (the Valheim UI can't take
    /// MC input): contents go to MC (EvChestContents) as mapped MC items (unmapped = placeholder), every change is re-sent.
    /// MC clicks send EvChestOp: take (Valheim removes, gives the MC item) / deposit (MC already removed it; Valheim adds the
    /// mapped item, or gives it back) / close. Valheim stays the owner of the chest contents.
    /// </summary>
    internal static partial class ItemBridge
    {
        static Component _chest; static object _chestInv; static uint _chestSerial; static int _chestHash; static float _chestNextPoll;
        static MethodInfo _cGetInv, _cCheckAccess, _cSave, _iW, _iH, _getPlayerId, _iChanged; static FieldInfo _gridPos, _cName; static Type _containerT;

        static void ChestInit()
        {
            if (_containerT != null) return;
            _containerT = AccessTools.TypeByName("Container");
            _cGetInv = AccessTools.Method(_containerT, "GetInventory", Type.EmptyTypes);
            _cCheckAccess = AccessTools.Method(_containerT, "CheckAccess", new[] { typeof(long) });
            _cSave = AccessTools.Method(_containerT, "Save", Type.EmptyTypes);
            _cName = AccessTools.Field(_containerT, "m_name");
            _iW = AccessTools.Method(_inventory, "GetWidth", Type.EmptyTypes); _iH = AccessTools.Method(_inventory, "GetHeight", Type.EmptyTypes);
            _iChanged = AccessTools.Method(_inventory, "Changed", Type.EmptyTypes);
            _gridPos = AccessTools.Field(_itemData, "m_gridPos");
            _getPlayerId = AccessTools.Method(AccessTools.TypeByName("Player"), "GetPlayerID", Type.EmptyTypes);
            ValCraftPlugin.Log.LogInfo($"[chest] Container.GetInventory={_cGetInv != null} CheckAccess={_cCheckAccess != null} Inventory size={_iW != null && _iH != null} gridPos={_gridPos != null}");
        }

        /// <summary>From InteractBridge: true if the target is a Valheim chest (opened in MC).</summary>
        internal static bool TryOpenChest(GameObject go)
        {
            if (!Enabled || !_ok || go == null || _link == null) return false;
            ChestInit();
            if (_containerT == null) return false;
            var c = go.GetComponentInParent(_containerT);
            if (c == null || _cGetInv == null) return false;
            try
            {
                if (_cCheckAccess != null && _getPlayerId != null && PlayerSync.Player != null && !(bool)_cCheckAccess.Invoke(c, new object[] { _getPlayerId.Invoke(PlayerSync.Player, null) }))
                { ValCraftPlugin.Log.LogInfo($"[chest] {go.name}: no access (private chest)"); return true; }
                var nv = c.GetComponent(AccessTools.TypeByName("ZNetView")); var claim = nv == null ? null : AccessTools.Method(nv.GetType(), "ClaimOwnership");
                try { claim?.Invoke(nv, null); } catch { }
                _chest = c; _chestInv = _cGetInv.Invoke(c, null); _chestSerial++; _chestHash = 0; _chestNextPoll = 0;
                ValCraftPlugin.Log.LogInfo($"[chest] opened {go.name} in Minecraft");
                SendChest(true);
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[chest] open failed: " + e.GetBaseException().Message); }
            return true;
        }

        internal static void ChestUpdate()
        {
            if (_chest == null) return;
            if (Time.unscaledTime < _chestNextPoll) return;
            _chestNextPoll = Time.unscaledTime + 0.25f;
            var pl = PlayerSync.Player;
            if (_chest == null || !_chest || pl == null || Vector3.Distance(_chest.transform.position, pl.transform.position) > InteractBridge.Range + 2.5f) { CloseChest("out of reach"); return; }
            SendChest(false);
        }

        static void CloseChest(string why)
        {
            if (_chest == null && _chestInv == null) return;
            _chest = null; _chestInv = null;
            foreach (var m in ChestCodec.Encode(_chestSerial, 0, 0, "", new List<ChestCodec.Entry>())) _link?.TrySend(ValCraftLink.EvChestContents, m);
            ValCraftPlugin.Log.LogInfo($"[chest] closed ({why})");
        }

        static void SendChest(bool force)
        {
            if (_chestInv == null) return;
            int w = (int)_iW.Invoke(_chestInv, null), h = (int)_iH.Invoke(_chestInv, null);
            var entries = new List<ChestCodec.Entry>(); int hash = w * 31 + h;
            foreach (var it in (IList)_getAllItems.Invoke(_chestInv, null))
            {
                int slot = SlotOf(it, w); if (slot < 0 || slot > 255) continue;
                string prefab = PrefabOf(it) ?? "?"; string mc = Map.McFor(prefab) ?? "";
                string name = prefab;
                try { name = L((string)AccessTools.Field(_sharedF.FieldType, "m_name").GetValue(Shared(it))); } catch { }
                var e = new ChestCodec.Entry { Slot = (byte)slot, Count = (ushort)Math.Min(65535, (int)_stack.GetValue(it)), Dur = DurOf(it), McId = mc, Name = name };
                entries.Add(e);
                hash = hash * 31 + slot; hash = hash * 31 + e.Count; hash = hash * 31 + e.Dur; hash = hash * 31 + prefab.GetHashCode();
            }
            if (!force && hash == _chestHash) return;
            string title = "Chest"; try { title = L((string)_cName.GetValue(_chest)); } catch { }
            bool ok = true;
            foreach (var m in ChestCodec.Encode(_chestSerial, w, h, title, entries)) ok &= _link.TrySend(ValCraftLink.EvChestContents, m);
            if (ok) _chestHash = hash;
        }

        internal static void ChestOp(byte[] p)
        {
            if (p == null || p.Length < 10) return;
            var (serial, op, slot, count, dur, id) = ChestCodec.Op(p);
            if (op == ChestCodec.OpDeposit && (_chestInv == null || serial != _chestSerial)) { GiveBack(id, count); return; } // MC already removed it
            if (_chestInv == null || serial != _chestSerial) return;
            try
            {
                if (op == ChestCodec.OpClose) { CloseChest("closed in Minecraft"); return; }
                int w = (int)_iW.Invoke(_chestInv, null);
                if (op == ChestCodec.OpTake)
                {
                    foreach (var it in new ArrayList((IList)_getAllItems.Invoke(_chestInv, null)))
                    {
                        if (SlotOf(it, w) != slot) continue;
                        string prefab = PrefabOf(it), mc = Map.McFor(prefab);
                        if (mc == null) { ValCraftPlugin.Log.LogInfo($"[chest] {prefab} has no Minecraft item (add it to valcraft-items.json): stays in the chest"); break; }
                        int have = (int)_stack.GetValue(it), n = count <= 0 ? have : Math.Min(count, have);
                        if (!Give(mc, n, prefab, "chest", DurOf(it))) break;
                        if (n >= have) _removeItem.Invoke(_chestInv, new[] { it }); else _removeAmt.Invoke(_chestInv, new[] { it, (object)n });
                        break;
                    }
                }
                else if (op == ChestCodec.OpDeposit)
                {
                    var v = Map.ValheimFor(id);
                    var db = _objDbInst?.GetValue(null);
                    var prefab = v == null || db == null ? null : _getItemPrefab.Invoke(db, new object[] { v }) as GameObject;
                    if (prefab == null || !(bool)_addPrefab.Invoke(_chestInv, new object[] { prefab, (int)count }))
                    { ValCraftPlugin.Log.LogInfo($"[chest] can't store {count} x {id} ({(v == null ? "no Valheim item mapped" : "chest full")})"); GiveBack(id, count); }
                    else
                    {
                        if (dur != ItemMap.NoDur && _getItem != null) { var d = _getItem.Invoke(_chestInv, new object[] { v, -1, true }); if (d != null) SetDur(d, dur); }
                        ValCraftPlugin.Log.LogInfo($"[chest] stored {count} x {id} as {v}");
                    }
                }
                try { _iChanged?.Invoke(_chestInv, null); } catch { }
                try { _cSave?.Invoke(_chest, null); } catch { }
                _chestNextPoll = 0;
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[chest] op failed: " + e.GetBaseException().Message); }
        }

        static FieldInfo _gx, _gy;
        static int SlotOf(object it, int w)
        {
            var gp = _gridPos.GetValue(it);
            if (_gx == null) { _gx = AccessTools.Field(gp.GetType(), "x"); _gy = AccessTools.Field(gp.GetType(), "y"); }
            return (int)_gy.GetValue(gp) * w + (int)_gx.GetValue(gp);
        }

        internal static void ChestReset() { _chest = null; _chestInv = null; }
    }
}
