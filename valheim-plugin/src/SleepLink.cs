using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// 0.13.2 sleep + spawn points across both games (PROTOCOL.md v22).
    ///  - MC bed: EvSleep 1 {x,y,z} -> if Valheim can sleep (night, no enemies) the Valheim player is marked in bed (ZDO inBed),
    ///    so Game.UpdateSleeping skips to morning (Valheim's normal sleep: fade, Rested on waking). The MC bed becomes the Valheim
    ///    custom spawn point (respawn there is accepted without a Valheim bed: Game.FindSpawnPoint prefix).
    ///  - Valheim bed (interacted from MC): Player.InBed() -> EvSleep 1 to MC (MC sleeps where it stands); out of bed -> EvSleep 0.
    ///  - Valheim custom spawn point changes (a Valheim bed was used) -> EvSpawn -> MC respawn position (forced).
    /// </summary>
    internal static class SleepLink
    {
        static bool _init, _sent, _mcBed, _wasSleeping, _seenSpawn;
        static Vector3 _lastSpawn; static float _nextSpawnPoll;
        static MethodInfo _inBed, _isSleeping, _attachStop, _canSleep, _isSensed, _getZdo, _zdoSet, _getProfile, _setSpawn, _getSpawn, _haveSpawn, _haveLogout, _worldName, _setRef, _areaReady;
        static FieldInfo _nview, _respawnAfterDeath;
        static Func<object> _game, _znet, _zscene;
        static readonly Dictionary<string, Vector3> _mcSpawns = new Dictionary<string, Vector3>();
        static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "valcraft-mcbeds.txt");

        internal static void Apply(Harmony h)
        {
            Init();
            var fsp = AccessTools.Method(Refl.T("Game"), "FindSpawnPoint");
            if (fsp != null) { try { h.Patch(fsp, prefix: new HarmonyMethod(typeof(SleepLink), nameof(FindSpawnPre))); } catch (Exception e) { ValCraftPlugin.Log.LogWarning("[sleep] FindSpawnPoint hook: " + e.Message); } }
            ValCraftPlugin.Log.LogInfo($"[sleep] InBed={_inBed != null} IsSleeping={_isSleeping != null} EnvMan.CanSleep={_canSleep != null} ZDO.Set={_zdoSet != null} SetCustomSpawnPoint={_setSpawn != null} FindSpawnPoint={fsp != null} ({_mcSpawns.Count} MC bed spawn(s) known)");
        }

        static void Init()
        {
            if (_init) return; _init = true;
            var pt = Refl.T("Player"); var ct = Refl.T("Character"); var gt = Refl.T("Game"); var et = Refl.T("EnvMan"); var zt = Refl.T("ZDO");
            var prof = Refl.T("PlayerProfile"); var znt = Refl.T("ZNet"); var zst = Refl.T("ZNetScene"); var nvt = Refl.T("ZNetView");
            _inBed = AccessTools.Method(pt, "InBed", Type.EmptyTypes); _isSleeping = AccessTools.Method(pt, "IsSleeping", Type.EmptyTypes);
            _attachStop = AccessTools.Method(pt, "AttachStop", Type.EmptyTypes); _isSensed = AccessTools.Method(pt, "IsSensed", Type.EmptyTypes);
            _canSleep = AccessTools.Method(et, "CanSleep", Type.EmptyTypes);
            _nview = AccessTools.Field(ct, "m_nview"); _getZdo = AccessTools.Method(nvt, "GetZDO", Type.EmptyTypes);
            _zdoSet = AccessTools.Method(zt, "Set", new[] { typeof(string), typeof(bool) });
            _getProfile = AccessTools.Method(gt, "GetPlayerProfile", Type.EmptyTypes); _respawnAfterDeath = AccessTools.Field(gt, "m_respawnAfterDeath");
            _setSpawn = AccessTools.Method(prof, "SetCustomSpawnPoint", new[] { typeof(Vector3) }); _getSpawn = AccessTools.Method(prof, "GetCustomSpawnPoint", Type.EmptyTypes);
            _haveSpawn = AccessTools.Method(prof, "HaveCustomSpawnPoint", Type.EmptyTypes); _haveLogout = AccessTools.Method(prof, "HaveLogoutPoint", Type.EmptyTypes);
            _worldName = AccessTools.Method(znt, "GetWorldName", Type.EmptyTypes); _setRef = AccessTools.Method(znt, "SetReferencePosition", new[] { typeof(Vector3) });
            _areaReady = AccessTools.Method(zst, "IsAreaReady", new[] { typeof(Vector3) });
            if (gt != null) _game = PlayerSync.FindSingleton(gt); if (znt != null) _znet = PlayerSync.FindSingleton(znt); if (zst != null) _zscene = PlayerSync.FindSingleton(zst);
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        var f = line.Split('\t'); if (f.Length < 4) continue;
                        _mcSpawns[f[0]] = new Vector3(float.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture), float.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture));
                    }
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[sleep] reading " + FilePath + ": " + e.Message); }
        }

        static string World() { try { var z = _znet?.Invoke(); return z == null || _worldName == null ? null : _worldName.Invoke(_worldName.IsStatic ? null : z, null) as string; } catch { return null; } }
        static object Profile() { try { var g = _game?.Invoke(); return g == null ? null : _getProfile?.Invoke(g, null); } catch { return null; } }
        static bool Call(MethodInfo m, object o) { try { return m != null && o != null && (bool)m.Invoke(o, null); } catch { return false; } }

        static void SetInBed(Component pl, bool v)
        {
            try { var nv = _nview?.GetValue(pl); var zdo = nv == null ? null : _getZdo?.Invoke(nv, null); if (zdo != null) _zdoSet?.Invoke(zdo, new object[] { "inBed", v }); }
            catch (Exception e) { Diag.Warn("sleepzdo", "[sleep] inBed: " + e.GetBaseException().Message); }
        }

        static bool IsOurSpawn(Vector3 p) { var w = World(); return w != null && _mcSpawns.TryGetValue(w, out var s) && (s - p).sqrMagnitude < 0.25f; }

        static void SetMcSpawn(Vector3 u)
        {
            var prof = Profile(); var w = World();
            if (prof == null || _setSpawn == null) return;
            try { _setSpawn.Invoke(prof, new object[] { u }); } catch (Exception e) { Diag.Warn("spawnset", "[sleep] SetCustomSpawnPoint: " + e.GetBaseException().Message); return; }
            _lastSpawn = u; _seenSpawn = true;
            if (w != null)
            {
                _mcSpawns[w] = u;
                try { var sb = new StringBuilder(); foreach (var kv in _mcSpawns) sb.Append(kv.Key.Replace('\t', ' ')).Append('\t').Append(kv.Value.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\t').Append(kv.Value.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\t').Append(kv.Value.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n'); File.WriteAllText(FilePath, sb.ToString()); }
                catch (Exception e) { ValCraftPlugin.Log.LogWarning("[sleep] saving MC bed spawn: " + e.Message); }
            }
            ValCraftPlugin.Log.LogInfo($"[sleep] MC bed -> Valheim spawn point {u} (world {w})");
        }

        static void Refuse(ValCraftLink link, string why)
        {
            ValCraftPlugin.Log.LogInfo("[sleep] Valheim can't sleep now: " + why);
            var b = Encoding.UTF8.GetBytes(why); var p = new byte[1 + b.Length]; p[0] = 2; Buffer.BlockCopy(b, 0, p, 1, b.Length);
            link?.TrySend(ValCraftLink.EvSleep, p);
        }

        /// <summary>EvSleep from MC: u8 state (1 asleep in an MC bed + f32 x,y,z, 0 awake).</summary>
        internal static void Receive(ValCraftLink link, byte[] p)
        {
            if (p == null || p.Length == 0) return;
            Init();
            var pl = PlayerSync.Player;
            if (p[0] == 1)
            {
                if (p.Length >= 13 && PlayerSync.Anchored)
                {
                    var a = PlayerSync.Anchor;
                    SetMcSpawn(new Vector3(a.x + BitConverter.ToSingle(p, 1), a.y + BitConverter.ToSingle(p, 5), a.z - BitConverter.ToSingle(p, 9)));
                }
                if (pl == null) return;
                bool night = true; try { night = _canSleep == null || (bool)_canSleep.Invoke(null, null); } catch { }
                if (!night) { Refuse(link, "you can only sleep at night"); return; }
                if (Call(_isSensed, pl)) { Refuse(link, "you can't sleep with enemies nearby"); return; }
                SetInBed(pl, true); _mcBed = true;
                ValCraftPlugin.Log.LogInfo("[sleep] MC bed: Valheim player marked in bed (Valheim skips the night when everyone sleeps)");
            }
            else if (p[0] == 0)
            {
                if (_mcBed) { _mcBed = false; if (pl != null) SetInBed(pl, false); ValCraftPlugin.Log.LogInfo("[sleep] MC woke: Valheim player out of bed"); }
                else if (pl != null && Call(_inBed, pl) && _attachStop != null) { try { _attachStop.Invoke(pl, null); ValCraftPlugin.Log.LogInfo("[sleep] MC woke: Valheim player leaves the bed"); } catch { } }
            }
        }

        internal static void Update(ValCraftLink link)
        {
            if (!_init) Init();
            var pl = PlayerSync.Player;
            if (pl == null || !PlayerSync.Following) { _mcBed = false; _sent = false; _wasSleeping = false; return; }
            bool inBed = Call(_inBed, pl);
            if (inBed != _sent) { _sent = inBed; link.TrySend(ValCraftLink.EvSleep, new[] { (byte)(inBed ? 1 : 0) }); ValCraftPlugin.Log.LogInfo(inBed ? "[sleep] Valheim player in bed -> MC sleeps" : "[sleep] Valheim player out of bed -> MC wakes"); }
            bool sl = Call(_isSleeping, pl);
            if (_wasSleeping && !sl && _mcBed) { _mcBed = false; SetInBed(pl, false); ValCraftPlugin.Log.LogInfo("[sleep] Valheim morning: out of the (MC) bed"); }
            _wasSleeping = sl;
            if (Time.unscaledTime < _nextSpawnPoll || !PlayerSync.Anchored) return;
            _nextSpawnPoll = Time.unscaledTime + 1f;
            var prof = Profile(); if (prof == null || !Call(_haveSpawn, prof)) return;
            Vector3 sp; try { sp = (Vector3)_getSpawn.Invoke(prof, null); } catch { return; }
            if (_seenSpawn && (sp - _lastSpawn).sqrMagnitude < 0.01f) return;
            _seenSpawn = true; _lastSpawn = sp;
            if (IsOurSpawn(sp)) return; // set from an MC bed: MC has it already
            var a = PlayerSync.Anchor;
            float mx = sp.x - a.x, my = sp.y - a.y, mz = -(sp.z - a.z);
            var b = new byte[12]; Buffer.BlockCopy(BitConverter.GetBytes(mx), 0, b, 0, 4); Buffer.BlockCopy(BitConverter.GetBytes(my), 0, b, 4, 4); Buffer.BlockCopy(BitConverter.GetBytes(mz), 0, b, 8, 4);
            link.TrySend(ValCraftLink.EvSpawn, b);
            ValCraftPlugin.Log.LogInfo($"[sleep] Valheim spawn point {sp} -> MC respawn position {mx:F1} {my:F1} {mz:F1}");
        }

        internal static void Reset() { _sent = false; _mcBed = false; _wasSleeping = false; _seenSpawn = false; }

        /// <summary>Respawn at an MC-bed spawn point: Valheim would look for a Valheim bed there, find none and drop the spawn point.</summary>
        static bool FindSpawnPre(object __instance, ref Vector3 point, ref bool usedLogoutPoint, ref bool __result)
        {
            try
            {
                var prof = _getProfile?.Invoke(__instance, null);
                if (prof == null || !Call(_haveSpawn, prof)) return true;
                bool afterDeath = _respawnAfterDeath != null ? (bool)_respawnAfterDeath.GetValue(__instance) : !Call(_haveLogout, prof);
                if (!afterDeath) return true;
                var sp = (Vector3)_getSpawn.Invoke(prof, null);
                if (!IsOurSpawn(sp)) return true;
                var zn = _znet?.Invoke(); if (zn != null) _setRef?.Invoke(zn, new object[] { sp });
                var zs = _zscene?.Invoke();
                if (zs != null && _areaReady != null && !(bool)_areaReady.Invoke(zs, new object[] { sp })) { __result = false; return false; }
                point = sp + Vector3.up * 0.1f; usedLogoutPoint = false; __result = true;
                ValCraftPlugin.Log.LogInfo($"[sleep] respawning at the MC bed spawn point {sp}");
                return false;
            }
            catch (Exception e) { Diag.Warn("findspawn", "[sleep] FindSpawnPoint: " + e.GetBaseException().Message); return true; }
        }
    }
}
