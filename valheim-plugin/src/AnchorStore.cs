using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ValCraft
{
    /// <summary>
    /// 0.13.5: per-Valheim-world horizontal mapping (MC x = Valheim x - ox, MC z = -(Valheim z - oz)), BepInEx/config/valcraft-anchors.txt
    /// ("uid&lt;TAB&gt;ox&lt;TAB&gt;oz"). A world not in the file gets (0, 0): the 0.13.0 mapping used by every session since 0.13.0
    /// (and by multiplayer), so builds made with 0.13.0-0.13.4 line up again. Edit the file or use "/valcraft offset" in MC to nudge.
    /// </summary>
    internal static class AnchorStore
    {
        static Dictionary<long, Vector2> _map;
        static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "valcraft-anchors.txt");

        static void Load()
        {
            if (_map != null) return;
            _map = new Dictionary<long, Vector2>();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var f = line.Split('\t'); if (f.Length < 3 || line.StartsWith("#")) continue;
                    _map[long.Parse(f[0], CultureInfo.InvariantCulture)] = new Vector2(float.Parse(f[1], CultureInfo.InvariantCulture), float.Parse(f[2], CultureInfo.InvariantCulture));
                }
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[sync] reading " + FilePath + ": " + e.Message); }
        }

        internal static Vector2 Get(long uid)
        {
            Load();
            if (_map.TryGetValue(uid, out var v)) { ValCraftPlugin.Log.LogInfo($"[sync] world {uid}: stored mapping offset ({v.x:F2}, {v.y:F2})"); return v; }
            v = Vector2.zero; Set(uid, v);
            ValCraftPlugin.Log.LogInfo($"[sync] world {uid}: no stored mapping yet -> (0, 0) (the 0.13.0+ mapping), saved");
            return v;
        }

        internal static void Set(long uid, Vector2 v)
        {
            Load(); _map[uid] = v;
            try
            {
                var lines = new List<string> { "# ValCraft: per Valheim world (uid) horizontal mapping offset: MC x = Valheim x - ox, MC z = -(Valheim z - oz)" };
                foreach (var kv in _map) lines.Add(kv.Key.ToString(CultureInfo.InvariantCulture) + "\t" + kv.Value.x.ToString("R", CultureInfo.InvariantCulture) + "\t" + kv.Value.y.ToString("R", CultureInfo.InvariantCulture));
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[sync] saving " + FilePath + ": " + e.Message); }
        }
    }
}
