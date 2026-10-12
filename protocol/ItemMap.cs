using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ValCraft.Protocol
{
    /// <summary>
    /// 0.8.0 Valheim prefab &lt;-&gt; Minecraft item table. File format (valcraft-items.json):
    /// { "ValheimPrefab": ["minecraft:give_this", "minecraft:also_accepted", ...], ... }.
    /// Valheim -> MC gives the first listed item; MC -> Valheim accepts any listed item. Pure C# (unit-tested).
    /// </summary>
    public sealed class ItemMap
    {
        public readonly Dictionary<string, List<string>> ToMc = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> ToValheim = new Dictionary<string, string>(StringComparer.Ordinal);

        public const string DefaultJson = @"{
  ""_comment"": [""Valheim prefab name -> Minecraft items. First item is what Valheim gives you; any listed item is accepted by Valheim stations. Edit and save: reloaded automatically.""],
  ""Wood"": [""minecraft:oak_log"", ""minecraft:jungle_log"", ""minecraft:acacia_log"", ""minecraft:mangrove_log"", ""minecraft:cherry_log"", ""minecraft:oak_planks"", ""minecraft:spruce_planks"", ""minecraft:birch_planks"", ""minecraft:jungle_planks"", ""minecraft:acacia_planks"", ""minecraft:dark_oak_planks"", ""minecraft:mangrove_planks"", ""minecraft:cherry_planks""],
  ""FineWood"": [""minecraft:birch_log""],
  ""RoundLog"": [""minecraft:spruce_log""],
  ""ElderBark"": [""minecraft:dark_oak_log""],
  ""Stone"": [""minecraft:cobblestone"", ""minecraft:stone""],
  ""Flint"": [""minecraft:flint""],
  ""Resin"": [""minecraft:honeycomb""],
  ""Coal"": [""minecraft:charcoal"", ""minecraft:coal""],
  ""CopperOre"": [""minecraft:raw_copper""],
  ""Copper"": [""minecraft:copper_ingot""],
  ""TinOre"": [""minecraft:raw_gold""],
  ""Tin"": [""minecraft:gold_ingot""],
  ""Bronze"": [""minecraft:brick""],
  ""IronScrap"": [""minecraft:raw_iron""],
  ""Iron"": [""minecraft:iron_ingot""],
  ""Obsidian"": [""minecraft:obsidian""],
  ""Crystal"": [""minecraft:amethyst_shard""],
  ""Coins"": [""minecraft:gold_nugget""],
  ""Raspberry"": [""minecraft:sweet_berries""],
  ""Blueberries"": [""minecraft:glow_berries""],
  ""Mushroom"": [""minecraft:red_mushroom""],
  ""MushroomYellow"": [""minecraft:brown_mushroom""],
  ""Carrot"": [""minecraft:carrot""],
  ""Turnip"": [""minecraft:beetroot""],
  ""Barley"": [""minecraft:wheat""],
  ""Flax"": [""minecraft:string""],
  ""Dandelion"": [""minecraft:dandelion""],
  ""Honey"": [""minecraft:honey_bottle""],
  ""LeatherScraps"": [""minecraft:leather""],
  ""DeerHide"": [""minecraft:rabbit_hide""],
  ""BoneFragments"": [""minecraft:bone""],
  ""Feathers"": [""minecraft:feather""],
  ""RawMeat"": [""minecraft:beef""],
  ""DeerMeat"": [""minecraft:mutton""],
  ""CookedMeat"": [""minecraft:cooked_beef""],
  ""CookedDeerMeat"": [""minecraft:cooked_mutton""]
}";

        static readonly Regex Entry = new Regex("\"([^\"]+)\"\\s*:\\s*\\[([^\\]]*)\\]", RegexOptions.Singleline);
        static readonly Regex Str = new Regex("\"([^\"]*)\"");

        /// <summary>Parse; returns the number of Valheim entries. Keys starting with '_' are comments.</summary>
        public int Load(string json)
        {
            ToMc.Clear(); ToValheim.Clear();
            foreach (Match m in Entry.Matches(json))
            {
                string key = m.Groups[1].Value;
                if (key.StartsWith("_")) continue;
                var list = new List<string>();
                foreach (Match s in Str.Matches(m.Groups[2].Value)) { var id = s.Groups[1].Value.Trim(); if (id.Length == 0) continue; if (id.IndexOf(':') < 0) id = "minecraft:" + id; list.Add(id); }
                if (list.Count == 0) continue;
                ToMc[key] = list;
                foreach (var id in list) if (!ToValheim.ContainsKey(id)) ToValheim[id] = key;
            }
            return ToMc.Count;
        }

        public string McFor(string valheimPrefab) => valheimPrefab != null && ToMc.TryGetValue(valheimPrefab, out var l) ? l[0] : null;
        public string ValheimFor(string mcId) => mcId != null && ToValheim.TryGetValue(mcId, out var v) ? v : null;

        // ---- payloads ----
        public static byte[] Give(uint req, int count, string mcId)
        {
            var n = Encoding.UTF8.GetBytes(mcId); var b = new byte[6 + n.Length];
            BitConverter.GetBytes(req).CopyTo(b, 0); BitConverter.GetBytes((ushort)Math.Min(65535, count)).CopyTo(b, 4); n.CopyTo(b, 6); return b;
        }
        public static byte[] Consume(uint req, int count, byte mode, string mcId)
        {
            var n = Encoding.UTF8.GetBytes(mcId ?? ""); var b = new byte[7 + n.Length];
            BitConverter.GetBytes(req).CopyTo(b, 0); BitConverter.GetBytes((ushort)Math.Min(65535, count)).CopyTo(b, 4); b[6] = mode; n.CopyTo(b, 7); return b;
        }
        public static (uint req, int removed, string id) Ack(byte[] p) => (BitConverter.ToUInt32(p, 0), BitConverter.ToUInt16(p, 4), Encoding.UTF8.GetString(p, 6, p.Length - 6));
        public static (int count, string id) Held(byte[] p) => (BitConverter.ToUInt16(p, 0), Encoding.UTF8.GetString(p, 2, p.Length - 2));

        /// <summary>Accumulates EvInvSnapshot messages; returns true when the last one arrived (Result holds totals).</summary>
        public sealed class Snapshot
        {
            public Dictionary<string, int> Building, Result;
            public bool Add(byte[] p)
            {
                if (p[0] != 0 || Building == null) Building = new Dictionary<string, int>();
                int n = p[2], o = 3;
                for (int i = 0; i < n && o + 3 <= p.Length; i++)
                {
                    int c = BitConverter.ToUInt16(p, o), len = p[o + 2];
                    if (o + 3 + len > p.Length) break;
                    Building[Encoding.UTF8.GetString(p, o + 3, len)] = c; o += 3 + len;
                }
                if (p[1] != 0) { Result = Building; Building = null; return true; }
                return false;
            }
        }
    }
}
