using System;
using HarmonyLib;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>0.11.0: a chopped-down Valheim tree (TreeBase.SpawnLog) near the player drops an MC sapling with a small chance.</summary>
    internal static class Saplings
    {
        internal static float Chance = 0.05f;
        static readonly System.Random Rng = new System.Random();

        internal static void Apply(Harmony h)
        {
            var t = Refl.T("TreeBase");
            var m = t == null ? null : AccessTools.Method(t, "SpawnLog");
            if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(Saplings), nameof(SpawnLogPost)));
            ValCraftPlugin.Log.LogInfo($"[sapling] TreeBase.SpawnLog hook {(m != null ? "on" : "missing")} (chance {Chance:P0})");
        }

        internal static string SaplingFor(string treeName)
        {
            string n = (treeName ?? "").ToLowerInvariant();
            if (n.Contains("swamp")) return "minecraft:dark_oak_sapling";
            if (n.Contains("birch") || n.Contains("beech")) return "minecraft:birch_sapling";
            if (n.Contains("pine") || n.Contains("fir")) return "minecraft:spruce_sapling";
            return "minecraft:oak_sapling";
        }

        static void SpawnLogPost(Component __instance)
        {
            try
            {
                if (Chance <= 0 || __instance == null || PlayerSync.Player == null) return;
                if (Vector3.Distance(__instance.transform.position, PlayerSync.Player.transform.position) > 40f) return;
                if (Rng.NextDouble() >= Chance) return;
                var id = SaplingFor(__instance.gameObject.name);
                ItemBridge.GiveMc(id, 1);
                ValCraftPlugin.Log.LogInfo($"[sapling] {__instance.gameObject.name} chopped down: +1 {id}");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogDebug("[sapling] " + e.Message); }
        }
    }
}
