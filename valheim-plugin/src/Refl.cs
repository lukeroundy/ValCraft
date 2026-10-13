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
