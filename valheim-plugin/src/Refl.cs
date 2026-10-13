using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ValCraft
{
    /// <summary>0.12.4: cached type lookup. AccessTools.TypeByName scans every loaded assembly when Type.GetType misses
    /// (tens of ms in Valheim); it ran on every interact (station check), chest open and the 2 s weather poll -> hitches.</summary>
    internal static class Refl
    {
        static readonly Dictionary<string, Type> Cache = new Dictionary<string, Type>(StringComparer.Ordinal);
        /// <summary>0.13.0: resolve the usual names on a background thread at startup, so even the first interact doesn't pay the
        /// assembly scan (0.12.4 log: 211 ms for the first station check).</summary>
        internal static void Prewarm()
        {
            var names = ("Aoe BaseAI Character ClutterSystem Container EffectArea EnvMan Fireplace Floating Game GameCamera Heightmap HitData Hoverable Hud Humanoid " +
                "IDestructible ISocket Interactable Inventory InventoryGui ItemDrop LiquidType Localization Minimap ObjectDB Piece PieceTable Player PrivateArea RuneStone " +
                "TerrainComp TerrainOp TextViewer TreeBase Vegvisir World WorldGenerator ZDO ZDOID ZInput ZNet ZNetPeer ZNetScene ZNetView ZPackage ZRoutedRpc ZoneSystem " +
                "Smelter CookingStation Fermenter ItemStand Turret ShieldGenerator Incinerator Beehive Windmill BossStone Door Sign Bed Chair").Split(' ');
            new System.Threading.Thread(() => { foreach (var n in names) try { T(n); } catch { } }) { IsBackground = true, Name = "ValCraft type cache" }.Start();
        }

        internal static Type T(string name)
        {
            if (name == null) return null;
            lock (Cache)
            {
                if (Cache.TryGetValue(name, out var t)) return t;
                t = AccessTools.TypeByName(name);
                Cache[name] = t;
                return t;
            }
        }
    }
}
