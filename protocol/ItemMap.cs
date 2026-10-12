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
  ""CookedDeerMeat"": [""minecraft:cooked_mutton""],
  ""ArrowWood"": [""minecraft:arrow""],
  ""ArrowFire"": [""minecraft:arrow""],
  ""ArrowFlint"": [""minecraft:arrow""],
  ""ArrowBronze"": [""minecraft:arrow""],
  ""ArrowIron"": [""minecraft:arrow""],
  ""ArrowSilver"": [""minecraft:arrow""],
  ""ArrowObsidian"": [""minecraft:arrow""],
  ""ArrowPoison"": [""minecraft:arrow""],
  ""ArrowFrost"": [""minecraft:arrow""],
  ""ArrowNeedle"": [""minecraft:arrow""],
  ""ArrowCarapace"": [""minecraft:arrow""],
  ""ArrowCharred"": [""minecraft:arrow""],
  ""Bow"": [""minecraft:bow""],
  ""BowFineWood"": [""minecraft:bow""],
  ""BowHuntsman"": [""minecraft:bow""],
  ""BowDraugrFang"": [""minecraft:bow""],
  ""BowSpineSnap"": [""minecraft:bow""],
  ""CrossbowArbalest"": [""minecraft:crossbow""],
  ""AxeStone"": [""minecraft:wooden_axe""],
  ""AxeFlint"": [""minecraft:stone_axe""],
  ""AxeBronze"": [""minecraft:iron_axe""],
  ""AxeIron"": [""minecraft:diamond_axe""],
  ""AxeBlackMetal"": [""minecraft:netherite_axe""],
  ""AxeJotunBane"": [""minecraft:netherite_axe""],
  ""PickaxeAntler"": [""minecraft:stone_pickaxe""],
  ""PickaxeBronze"": [""minecraft:iron_pickaxe""],
  ""PickaxeIron"": [""minecraft:diamond_pickaxe""],
  ""PickaxeBlackMetal"": [""minecraft:netherite_pickaxe""],
  ""Club"": [""minecraft:wooden_sword""],
  ""KnifeFlint"": [""minecraft:stone_sword""],
  ""KnifeCopper"": [""minecraft:stone_sword""],
  ""SwordBronze"": [""minecraft:iron_sword""],
  ""MaceBronze"": [""minecraft:iron_sword""],
  ""SwordIron"": [""minecraft:diamond_sword""],
  ""MaceIron"": [""minecraft:diamond_sword""],
  ""SwordSilver"": [""minecraft:diamond_sword""],
  ""MaceSilver"": [""minecraft:diamond_sword""],
  ""SwordBlackmetal"": [""minecraft:netherite_sword""],
  ""Torch"": [""minecraft:torch""],
  ""Hoe"": [""minecraft:stone_hoe""],
  ""Cultivator"": [""minecraft:iron_hoe""],
  ""Hammer"": [""minecraft:mace""],
  ""SpearFlint"": [""minecraft:trident""],
  ""SpearBronze"": [""minecraft:trident""],
  ""SpearElderbark"": [""minecraft:trident""],
  ""SpearWolfFang"": [""minecraft:trident""],
  ""ShieldWood"": [""minecraft:shield""],
  ""ShieldWoodTower"": [""minecraft:shield""],
  ""ShieldBronzeBuckler"": [""minecraft:shield""],
  ""ShieldBanded"": [""minecraft:shield""],
  ""ShieldIronSquare"": [""minecraft:shield""],
  ""ShieldIronTower"": [""minecraft:shield""],
  ""ShieldSilver"": [""minecraft:shield""],
  ""ShieldBlackmetal"": [""minecraft:shield""],
  ""ArmorRagsChest"": [""minecraft:leather_chestplate""],
  ""ArmorRagsLegs"": [""minecraft:leather_leggings""],
  ""ArmorLeatherChest"": [""minecraft:leather_chestplate""],
  ""ArmorLeatherLegs"": [""minecraft:leather_leggings""],
  ""HelmetLeather"": [""minecraft:leather_helmet""],
  ""ArmorTrollLeatherChest"": [""minecraft:leather_chestplate""],
  ""ArmorTrollLeatherLegs"": [""minecraft:leather_leggings""],
  ""HelmetTrollLeather"": [""minecraft:leather_helmet""],
  ""ArmorBronzeChest"": [""minecraft:chainmail_chestplate""],
  ""ArmorBronzeLegs"": [""minecraft:chainmail_leggings""],
  ""HelmetBronze"": [""minecraft:chainmail_helmet""],
  ""ArmorIronChest"": [""minecraft:iron_chestplate""],
  ""ArmorIronLegs"": [""minecraft:iron_leggings""],
  ""HelmetIron"": [""minecraft:iron_helmet""],
  ""ArmorWolfChest"": [""minecraft:diamond_chestplate""],
  ""ArmorWolfLegs"": [""minecraft:diamond_leggings""],
  ""HelmetDrake"": [""minecraft:diamond_helmet""],
  ""ArmorPaddedCuirass"": [""minecraft:netherite_chestplate""],
  ""ArmorPaddedGreaves"": [""minecraft:netherite_leggings""],
  ""HelmetPadded"": [""minecraft:netherite_helmet""],
  ""CapeDeerHide"": [""minecraft:leather_boots""],
  ""CapeTrollHide"": [""minecraft:chainmail_boots""],
  ""CapeLinen"": [""minecraft:iron_boots""],
  ""CapeWolf"": [""minecraft:diamond_boots""],
  ""CapeLox"": [""minecraft:netherite_boots""]
}";

        /// <summary>
        /// 0.8.3: add default entries missing from a user file without touching user edits. "_known" lists every default
        /// key already offered, so entries the user deleted on purpose are not re-added.
        /// </summary>
        public static string Merge(string userJson, out List<string> added)
        {
            added = new List<string>();
            var user = new HashSet<string>(); var known = new HashSet<string>();
            foreach (Match m in Entry.Matches(userJson))
            {
                user.Add(m.Groups[1].Value);
                if (m.Groups[1].Value == "_known") foreach (Match k in Str.Matches(m.Groups[2].Value)) known.Add(k.Groups[1].Value);
            }
            var defs = new List<(string key, string body)>();
            foreach (Match m in Entry.Matches(DefaultJson)) if (!m.Groups[1].Value.StartsWith("_")) defs.Add((m.Groups[1].Value, m.Groups[2].Value.Trim()));
            var sb = new StringBuilder();
            foreach (var (k, body) in defs) if (!user.Contains(k) && !known.Contains(k)) { added.Add(k); sb.Append(",\n  \"").Append(k).Append("\": [").Append(body).Append(']'); }
            bool knownComplete = true; foreach (var (k, _) in defs) if (!known.Contains(k)) { knownComplete = false; break; }
            if (added.Count == 0 && knownComplete) return userJson;
            string text = Regex.Replace(userJson, ",?\\s*\"_known\"\\s*:\\s*\\[[^\\]]*\\]", "");
            int end = text.LastIndexOf('}');
            if (end < 0) { text = "{"; end = 1; text += "}"; }
            string head = text.Substring(0, end).TrimEnd();
            bool empty = head.EndsWith("{");
            var all = new StringBuilder(); foreach (var (k, _) in defs) { if (all.Length > 0) all.Append(", "); all.Append('"').Append(k).Append('"'); }
            string add = sb.ToString() + ",\n  \"_known\": [" + all + "]";
            if (empty) add = add.Substring(1);
            return head + add + "\n" + text.Substring(end);
        }

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
        /// <summary>Durability on the wire: u16 0..10000 = fraction remaining, 0xFFFF = none.</summary>
        public const ushort NoDur = 0xFFFF;
        public static ushort Dur(float frac) => (ushort)Math.Round(Math.Max(0f, Math.Min(1f, frac)) * 10000f);
        public static byte[] Give(uint req, int count, string mcId, ushort dur = NoDur)
        {
            var n = Encoding.UTF8.GetBytes(mcId); var b = new byte[8 + n.Length];
            BitConverter.GetBytes(req).CopyTo(b, 0); BitConverter.GetBytes((ushort)Math.Min(65535, count)).CopyTo(b, 4); BitConverter.GetBytes(dur).CopyTo(b, 6); n.CopyTo(b, 8); return b;
        }
        public static byte[] Consume(uint req, int count, byte mode, string mcId)
        {
            var n = Encoding.UTF8.GetBytes(mcId ?? ""); var b = new byte[7 + n.Length];
            BitConverter.GetBytes(req).CopyTo(b, 0); BitConverter.GetBytes((ushort)Math.Min(65535, count)).CopyTo(b, 4); b[6] = mode; n.CopyTo(b, 7); return b;
        }
        public static (uint req, int removed, ushort dur, string id) Ack(byte[] p) => (BitConverter.ToUInt32(p, 0), BitConverter.ToUInt16(p, 4), BitConverter.ToUInt16(p, 6), Encoding.UTF8.GetString(p, 8, p.Length - 8));
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
