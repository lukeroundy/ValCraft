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
    /// 0.9.0 world hazards: Valheim fire (Aoe fire damage, EffectArea Burning, lava) around the MC player's mapped
    /// position -> MC fire damage + ignite; Valheim water/tar surface grid -> MC (vanilla swimming, air and drowning).
    /// </summary>
    internal static class Hazards
    {
        internal static bool Fire = true, Water = true;
        static float _nextFire, _nextWater;
        static bool _init;
        static Type _aoeT, _effT, _charT;
        static FieldInfo _aoeDmg, _effType; static MethodInfo _inLava, _liquid, _hmFind, _hmIsLava, _hmHeight;
        static readonly Collider[] _hits = new Collider[512];
        static MethodInfo _inArea, _isBurning; static Type _fpT; static object[] _fires = new object[0]; static float _nextFireScan, _nextNearLog;
        static bool _wasFire;

        static void Init()
        {
            _init = true;
            _aoeT = Refl.T("Aoe"); _effT = Refl.T("EffectArea"); _charT = Refl.T("Character");
            _aoeDmg = AccessTools.Field(_aoeT, "m_damage"); _effType = AccessTools.Field(_effT, "m_type");
            _inLava = AccessTools.Method(_charT, "InLava", Type.EmptyTypes);
            var hmT = Refl.T("Heightmap"); const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            _inArea = _effT?.GetMethod("IsPointInsideArea", BindingFlags.Public | BindingFlags.Static);
            _fpT = Refl.T("Fireplace"); _isBurning = _fpT?.GetMethod("IsBurning", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            _hmFind = hmT?.GetMethod("FindHeightmap", BF, null, new[] { typeof(Vector3) }, null);
            _hmIsLava = hmT?.GetMethod("IsLava", BF, null, new[] { typeof(Vector3), typeof(float) }, null);
            _hmHeight = hmT?.GetMethod("GetHeight", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Vector3), typeof(float).MakeByRefType() }, null);
            var fl = Refl.T("Floating"); var lt = Refl.T("LiquidType");
            _liquid = fl == null || lt == null ? null : AccessTools.Method(fl, "GetLiquidLevel", new[] { typeof(Vector3), typeof(float), lt });
            _liquidType = lt;
            ValCraftPlugin.Log.LogInfo($"[hazard] Aoe={_aoeT != null} EffectArea={_effT != null} InLava={_inLava != null} Heightmap.IsLava(pos,r)={_hmIsLava != null} Floating.GetLiquidLevel={_liquid != null} (fire={Fire}, water={Water})");
        }
        static Type _liquidType;

        internal static void Update(ValCraftLink link)
        {
            if (!PlayerSync.Following || PlayerSync.Player == null || link == null) return;
            if (!_init) Init();
            float t = Time.unscaledTime;
            try { if (Fire && t >= _nextFire) { _nextFire = t + 0.5f; FireCheck(link); } } catch (Exception e) { Diag.Warn("fire", "[hazard] fire check: " + e.GetBaseException().Message); }
            try { if (Water && t >= _nextWater) { _nextWater = t + 0.2f; WaterGrid(link); } } catch (Exception e) { Diag.Warn("water", "[hazard] water grid: " + e.GetBaseException().Message); }
        }

        static float FireDamage(Component c)
        {
            if (_aoeT != null && _aoeT.IsInstanceOfType(c) && ((Behaviour)c).isActiveAndEnabled)
            {
                var d = _aoeDmg.GetValue(c); var f = AccessTools.Field(d.GetType(), "m_fire");
                if (f != null && (float)f.GetValue(d) > 0) return (float)f.GetValue(d);
            }
            if (_effT != null && _effT.IsInstanceOfType(c) && ((Behaviour)c).isActiveAndEnabled && (Convert.ToInt32(_effType.GetValue(c)) & 8) != 0) return 5f; // Burning
            return 0f;
        }

        static MethodInfo _findBiome;
        static bool IsAshlands(Vector3 p)
        {
            if (_findBiome == null) _findBiome = Refl.T("Heightmap")?.GetMethod("FindBiome", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector3) }, null);
            try { return _findBiome != null && Convert.ToInt32(_findBiome.Invoke(null, new object[] { p })) == 32; } catch { return false; }
        }

        static void FireCheck(ValCraftLink link)
        {
            var pl = PlayerSync.Player; var p = pl.transform.position;
            float best = 0; string src = null;
            int n = Physics.OverlapCapsuleNonAlloc(p + Vector3.up * 0.3f, p + Vector3.up * 1.5f, 0.35f, _hits, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var col = _hits[i]; if (col == null || col.transform.IsChildOf(pl.transform)) continue;
                foreach (var c in col.GetComponentsInParent<Component>())
                {
                    if (c == null || !(c is Behaviour)) continue;
                    float d = FireDamage(c);
                    if (d > best) { best = d; src = c.gameObject.name; }
                }
            }
            // 0.9.3: the overlap above found nothing in practice (the hidden player sits among hundreds of block/terrain colliders);
            // also ask Valheim's own burning-area registry and check burning fireplaces by distance.
            if (best <= 0 && _inArea != null)
            {
                foreach (var probe in new[] { p + Vector3.up * 0.25f, p + Vector3.up * 1f })
                {
                    try
                    {
                        var ea = _inArea.Invoke(null, new object[] { probe, Enum.ToObject(_inArea.GetParameters()[1].ParameterType, 8), 0f }) as Component; // 0.9.4: Burning only (type Fire = FireWarmth comfort areas -> false burns)
                        if (ea != null && !ea.transform.IsChildOf(pl.transform)) { float d = FireDamage(ea); if (d <= 0) continue; if (d > best) { best = d; src = ea.gameObject.name + " (area)"; } break; }
                    }
                    catch { }
                }
            }
            float nearest = float.MaxValue; string nearName = null;
            if (_fpT != null)
            {
                if (Time.unscaledTime >= _nextFireScan) { _nextFireScan = Time.unscaledTime + 3f; _fires = UnityEngine.Object.FindObjectsOfType(_fpT); }
                foreach (var o in _fires)
                {
                    var fc = o as Component; if (fc == null) continue;
                    bool burning = false; try { burning = _isBurning != null && (bool)_isBurning.Invoke(fc, null); } catch { }
                    if (!burning) continue;
                    var fpos = fc.transform.position; var dh = new Vector2(fpos.x - p.x, fpos.z - p.z).magnitude; float dy = p.y - fpos.y;
                    if (dh < nearest) { nearest = dh; nearName = fc.gameObject.name; }
                    if (dh < 0.8f && dy > -0.5f && dy < 1.2f && best < 5f) { best = 5f; src = fc.gameObject.name + " (fireplace)"; }
                }
            }
            if (nearest < 4f && Time.unscaledTime >= _nextNearLog) { _nextNearLog = Time.unscaledTime + 1f; ValCraftPlugin.Log.LogInfo($"[hazard] near burning {nearName}: {nearest:F2} m horizontal (player {p}), fire={best > 0}"); }
            bool lava = false; try { lava = _inLava != null && (bool)_inLava.Invoke(pl, null); } catch { }
            // 0.9.2: the hidden player is kinematic (no ground contact), so also ask the terrain: Ashlands lava under the feet
            if (!lava && _hmFind != null && _hmIsLava != null)
            {
                try
                {
                    var fp = pl.transform.position; var hm = _hmFind.Invoke(null, new object[] { fp });
                    if (hm != null && IsAshlands(fp) && (bool)_hmIsLava.Invoke(hm, new object[] { fp, 0.6f }))
                    {
                        var args = new object[] { fp, 0f };
                        bool near = _hmHeight == null || !(bool)_hmHeight.Invoke(null, args) || fp.y - (float)args[1] < 0.3f;
                        lava = near;
                    }
                }
                catch { }
            }
            if (lava) { best = Math.Max(best, 20f); src = "lava"; }
            bool fire = best > 0;
            if (fire != _wasFire) { _wasFire = fire; ValCraftPlugin.Log.LogInfo(fire ? $"[hazard] MC player in Valheim fire ({src})" : "[hazard] out of fire"); }
            if (!fire || Time.unscaledTime - HealthLink.LastBurnForward < 1f) return; // Valheim's own burn hit already reached MC
            float mc = lava ? 4f : 1f; // MC lava / fire-block damage per hit
            link.TrySend(ValCraftLink.EvDamage, HealthCodec.Damage(mc, 5, 0, 0, 0));
        }

        // 0.9.1: 18 m tiles (9×9 cells, 2 m) covering ±36 m (boats, mobs, items). The player's tile every 0.2 s, the rest
        // round-robin (~1 s for all 25 tiles). Tiles without any liquid are sent once (as "none") then skipped.
        const int Step = MobCodec.TileStep, N = MobCodec.TileCells, TS = MobCodec.TileSize, R = 2;
        static readonly float[] _surf = new float[N * N]; static readonly bool[] _tar = new bool[N * N];
        static bool _wasWet; static int _rr;
        static readonly Dictionary<long, bool> _dryTiles = new Dictionary<long, bool>();
        static void WaterGrid(ValCraftLink link)
        {
            if (_liquid == null || !PlayerSync.Anchored) return;
            var a = PlayerSync.Anchor; var p = PlayerSync.Player.transform.position;
            float mx = p.x - a.x, mz = -(p.z - a.z);
            int ptx = Mathf.FloorToInt(mx / TS), ptz = Mathf.FloorToInt(mz / TS);
            WaterTile(link, ptx, ptz, true);
            int side = 2 * R + 1;
            for (int k = 0; k < 5; k++) { int idx = _rr++ % (side * side); int tx = ptx - R + idx % side, tz = ptz - R + idx / side; if (tx != ptx || tz != ptz) WaterTile(link, tx, tz, false); }
            if (_dryTiles.Count > 4000) _dryTiles.Clear();
        }

        static void WaterTile(ValCraftLink link, int tx, int tz, bool playerTile)
        {
            var a = PlayerSync.Anchor; var p = PlayerSync.Player.transform.position;
            float x0 = tx * TS + 1, z0 = tz * TS + 1;
            object water = Enum.ToObject(_liquidType, 0), tar = Enum.ToObject(_liquidType, 1);
            bool any = false;
            for (int j = 0; j < N; j++) for (int i = 0; i < N; i++)
            {
                float cx = x0 + i * Step, cz = z0 + j * Step;
                var u = new Vector3(a.x + cx, p.y, a.z - cz);
                float w = (float)_liquid.Invoke(null, new object[] { u, 1f, water });
                float tr = (float)_liquid.Invoke(null, new object[] { u, 1f, tar });
                int k = j * N + i;
                bool isTar = tr > -1000f && tr >= w;
                float s = isTar ? tr : w;
                _surf[k] = s > -1000f ? s - a.y : float.NaN; _tar[k] = isTar;
                if (s > -1000f) any = true;
            }
            if (playerTile)
            {
                float mx = p.x - a.x, mz = -(p.z - a.z);
                int ci = Mathf.Clamp(Mathf.RoundToInt((mx - x0) / Step), 0, N - 1), cj = Mathf.Clamp(Mathf.RoundToInt((mz - z0) / Step), 0, N - 1), c = cj * N + ci;
                bool wet = !float.IsNaN(_surf[c]) && _surf[c] > p.y - a.y;
                if (wet != _wasWet) { _wasWet = wet; ValCraftPlugin.Log.LogInfo(wet ? $"[hazard] MC player below Valheim {(_tar[c] ? "tar" : "water")} surface (MC Y {_surf[c]:F1})" : "[hazard] out of Valheim water"); }
            }
            long key = ((long)tx << 32) ^ (uint)tz;
            if (!any) { if (_dryTiles.ContainsKey(key)) return; _dryTiles[key] = true; }
            else _dryTiles.Remove(key);
            link.TrySend(ValCraftLink.EvWater, MobCodec.Water(x0, z0, Step, N, N, _surf, _tar));
        }
    }

    /// <summary>
    /// 0.9.0 cross-game mobs. MC mobs near the player get invisible Valheim proxies (a disabled-AI creature prefab,
    /// non-persistent ZDO) so Valheim monsters see and attack them; hits on a proxy go to the MC mob. Valheim hostiles
    /// near the player are streamed to MC, where invisible proxy entities let MC hostiles target them; MC hits come back
    /// as Character.Damage with the attacker's Valheim proxy as attacker (so the Valheim monster fights back).
    /// </summary>
    /// <summary>0.9.1: Valheim time of day drives MC (EvDayTime every second).</summary>
    internal static class DayLink
    {
        internal static bool Enabled = true;
        static float _next; static PropertyInfo _inst; static MethodInfo _frac; static bool _init;
        internal static void Update(ValCraftLink link)
        {
            if (!Enabled || link == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            if (!_init) { _init = true; var t = Refl.T("EnvMan"); _inst = AccessTools.Property(t, "instance"); _frac = AccessTools.Method(t, "GetDayFraction", Type.EmptyTypes); ValCraftPlugin.Log.LogInfo($"[time] EnvMan.GetDayFraction={_frac != null}"); }
            try
            {
                var env = _inst?.GetValue(null); if (env == null || _frac == null) return;
                float f = (float)_frac.Invoke(env, null);
                link.TrySend(ValCraftLink.EvDayTime, BitConverter.GetBytes(f));
            }
            catch (Exception e) { Diag.Warn("time", "[time] " + e.GetBaseException().Message); }
        }
    }

    /// <summary>0.9.4: tell MC which Valheim world is loaded (i64 uid, utf8 name) every 2 s, so it opens the matching MC world.</summary>
    internal static class WorldInfo
    {
        static float _next; static FieldInfo _world, _uid, _name; static bool _loggedWait; static long _sentUid = long.MinValue;
        internal static void Update(ValCraftLink link)
        {
            if (link == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            if (_world == null) { _world = AccessTools.Field(Refl.T("ZNet"), "m_world"); var wt = Refl.T("World"); _uid = AccessTools.Field(wt, "m_uid"); _name = AccessTools.Field(wt, "m_name"); }
            try
            {
                var w = _world?.GetValue(null); if (w == null || !PlayerSync.HasLocalPlayer) { if (!_loggedWait) { _loggedWait = true; ValCraftPlugin.Log.LogInfo($"[world] no Valheim world yet (ZNet.m_world {(w == null ? "null" : "set")}, local player {PlayerSync.HasLocalPlayer})"); } return; } // 0.9.5: not Player (only set while following = chicken-and-egg)
                long uid = (long)_uid.GetValue(w); var name = (string)_name.GetValue(w) ?? "";
                var nb = System.Text.Encoding.UTF8.GetBytes(name); int nl = Math.Min(nb.Length, 200);
                var p = new byte[8 + 1 + nl]; BitConverter.GetBytes(uid).CopyTo(p, 0); p[8] = (byte)nl; Array.Copy(nb, 0, p, 9, nl);
                if (link.TrySend(ValCraftLink.EvWorldInfo, p) && uid != _sentUid) { _sentUid = uid; _loggedWait = false; ValCraftPlugin.Log.LogInfo($"[world] sending Valheim world identity '{name}' (uid {uid})"); }
            }
            catch (Exception e) { Diag.Warn("worldinfo", "[world] " + e.GetBaseException().Message); }
        }
    }

    internal static class MobBridge
    {
        internal static bool Enabled = true; internal static int MaxProxies = 24; internal static string ProxyPrefab = "Greyling";
        internal static string HostileFaction = "Players";
        class Proxy { public GameObject Go; public Component Ch; public Vector3 Target; public float Seen; public byte Kind; public float W, H, Max; public string Name; }
        static readonly Dictionary<uint, Proxy> _proxies = new Dictionary<uint, Proxy>();
        static readonly Dictionary<object, uint> _proxyToMc = new Dictionary<object, uint>();
        static readonly Dictionary<uint, Component> _vmobs = new Dictionary<uint, Component>();
        static readonly List<MobCodec.Mob> _incoming = new List<MobCodec.Mob>();
        static bool _init, _ok; static float _nextList;
        static Type _charT, _znvT, _zdoT, _hitT, _baseAIT, _factionT, _playerT;
        static MethodInfo _setMax, _hoverName, _getAll, _getHealth, _getMax, _setHealth, _isDead, _isTamed, _damage, _getZdo, _setAttacker, _findInst, _sceneDestroy, _getPrefab;
        static FieldInfo _faction, _name, _attackerF; static PropertyInfo _persistent, _znsInst;
        static ValCraftLink _link;

        static void Init()
        {
            _init = true;
            _charT = Refl.T("Character"); _znvT = Refl.T("ZNetView"); _zdoT = Refl.T("ZDO"); _hitT = Refl.T("HitData");
            _baseAIT = Refl.T("BaseAI"); _playerT = Refl.T("Player");
            _factionT = AccessTools.Inner(_charT, "Faction");
            _getAll = AccessTools.Method(_charT, "GetAllCharacters"); _getHealth = AccessTools.Method(_charT, "GetHealth"); _getMax = AccessTools.Method(_charT, "GetMaxHealth", Type.EmptyTypes);
            _setHealth = AccessTools.Method(_charT, "SetHealth"); _isDead = AccessTools.Method(_charT, "IsDead"); _isTamed = AccessTools.Method(_charT, "IsTamed", Type.EmptyTypes);
            _damage = AccessTools.Method(_charT, "Damage", new[] { _hitT }); _getZdo = AccessTools.Method(_znvT, "GetZDO");
            _setAttacker = AccessTools.Method(_hitT, "SetAttacker"); _attackerF = AccessTools.Field(_hitT, "m_attacker");
            var zs = Refl.T("ZNetScene"); _znsInst = AccessTools.Property(zs, "instance");
            _findInst = AccessTools.Method(zs, "FindInstance", new[] { Refl.T("ZDOID") }); _sceneDestroy = AccessTools.Method(zs, "Destroy", new[] { typeof(GameObject) });
            _getPrefab = AccessTools.Method(zs, "GetPrefab", new[] { typeof(string) });
            _setMax = AccessTools.Method(_charT, "SetMaxHealth", new[] { typeof(float) }); _hoverName = AccessTools.Method(_charT, "GetHoverName", Type.EmptyTypes);
            _faction = AccessTools.Field(_charT, "m_faction"); _name = AccessTools.Field(_charT, "m_name");
            _persistent = AccessTools.Property(_zdoT, "Persistent");
            _ok = _getAll != null && _damage != null && _getPrefab != null && _sceneDestroy != null && _faction != null;
            ValCraftPlugin.Log.LogInfo($"[mobs] cross-game combat API {(_ok ? "OK" : "incomplete -> off")} (proxy prefab {ProxyPrefab}, hostile MC mobs = faction {HostileFaction}, max {MaxProxies})");
        }

        internal static bool IsProxy(object c) => c != null && _proxyToMc.ContainsKey(c);
        static object Zns() => _znsInst?.GetValue(null);
        static Vector3 ToUnity(float x, float y, float z) { var a = PlayerSync.Anchor; return new Vector3(a.x + x, a.y + y, a.z - z); }
        static Vector3 ToMc(Vector3 u) { var a = PlayerSync.Anchor; return new Vector3(u.x - a.x, u.y - a.y, -(u.z - a.z)); }

        /// <summary>EvMobList from MC (snapshot runs first..last).</summary>
        internal static void Receive(byte[] p)
        {
            if (!Enabled) return;
            if (p[0] != 0) _incoming.Clear();
            _incoming.AddRange(MobCodec.DecodeList(p));
            if (p[1] == 0) return;
            if (!_init) Init();
            if (!_ok || !PlayerSync.Anchored || PlayerSync.Player == null) return;
            float t = Time.unscaledTime;
            var keep = new HashSet<uint>();
            foreach (var m in _incoming)
            {
                // 0.13.0: every MC client reports the same MC mobs; only the nearest Valheim player spawns the (networked) stand-in
                if (!NetTunnel.Owns(ToUnity(m.X, m.Y, m.Z), _proxies.ContainsKey(m.Id) ? 4f : 0f)) continue;
                keep.Add(m.Id);
                if (!_proxies.TryGetValue(m.Id, out var px))
                {
                    if (_proxies.Count >= MaxProxies) continue;
                    px = Create(m); if (px == null) continue;
                    _proxies[m.Id] = px; _proxyToMc[px.Ch] = m.Id;
                }
                px.Target = ToUnity(m.X, m.Y, m.Z); px.Seen = t;
                try
                {
                    // 0.9.1: name + health bar of the MC mob (custom name tag or type name; MC hp / max hp)
                    string nm = string.IsNullOrEmpty(m.Name) ? (m.Kind == MobCodec.Hostile ? "Minecraft monster" : "Minecraft animal") : m.Name;
                    if (nm != px.Name) { px.Name = nm; _name?.SetValue(px.Ch, nm); }
                    float max = Math.Max(1f, m.MaxHp > 0 ? m.MaxHp : 20f);
                    if (Math.Abs(max - px.Max) > 0.01f) { px.Max = max; _setMax?.Invoke(px.Ch, new object[] { max }); }
                    _setHealth?.Invoke(px.Ch, new object[] { Math.Max(0.5f, max * Math.Max(0.01f, m.HpFrac)) });
                }
                catch { }
            }
            foreach (var id in new List<uint>(_proxies.Keys)) if (!keep.Contains(id)) Remove(id, "left range / died");
        }

        static Proxy Create(MobCodec.Mob m)
        {
            try
            {
                var prefab = _getPrefab.Invoke(Zns(), new object[] { ProxyPrefab }) as GameObject;
                if (prefab == null) { Diag.Warn("noprefab", $"[mobs] proxy prefab '{ProxyPrefab}' not found"); return null; }
                var pos = ToUnity(m.X, m.Y, m.Z);
                var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
                var nv = go.GetComponent(_znvT); var zdo = nv == null ? null : _getZdo.Invoke(nv, null);
                if (zdo != null && _persistent?.GetSetMethod(true) != null) _persistent.SetValue(zdo, false); // never saved with the world
                foreach (var b in go.GetComponentsInChildren<Behaviour>(true))
                {
                    var tn = b.GetType().Name;
                    if ((_baseAIT != null && _baseAIT.IsInstanceOfType(b)) || b is Animator || tn == "CharacterDrop" || tn == "Tameable" || tn == "FootStep" || tn == "LevelEffects" || tn == "Procreation" || tn == "Growup" || tn == "MonsterAI" || tn == "AnimalAI")
                        b.enabled = false;
                }
                foreach (var cd in go.GetComponentsInChildren<Component>(true)) if (cd != null && cd.GetType().Name == "CharacterDrop") UnityEngine.Object.Destroy(cd);
                foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                foreach (var a in go.GetComponentsInChildren<AudioSource>(true)) a.mute = true;
                // 0.9.2: not kinematic (Character code sets velocity every FixedUpdate -> 20k Unity warnings); frozen dynamic body, moved by position
                var rb = go.GetComponent<Rigidbody>(); if (rb != null) { rb.isKinematic = false; rb.useGravity = false; rb.constraints = RigidbodyConstraints.FreezeAll; rb.interpolation = RigidbodyInterpolation.None; }
                var cap = go.GetComponent<CapsuleCollider>();
                if (cap != null && m.W > 0 && m.H > 0) { cap.radius = Math.Max(0.2f, m.W / 2); cap.height = Math.Max(cap.radius * 2, m.H); cap.center = new Vector3(0, cap.height / 2, 0); }
                var ch = go.GetComponent(_charT);
                if (ch == null) { _sceneDestroy.Invoke(Zns(), new object[] { go }); return null; }
                string fac = m.Kind == MobCodec.Hostile ? HostileFaction : "AnimalsVeg";
                try { _faction.SetValue(ch, Enum.Parse(_factionT, fac)); } catch { _faction.SetValue(ch, Enum.ToObject(_factionT, 0)); }

                Diag.Info("proxy+", $"[mobs] proxy for MC mob {m.Id} ({(m.Kind == MobCodec.Hostile ? "hostile" : "passive")}, {m.W:F1}x{m.H:F1} m, faction {fac})");
                return new Proxy { Go = go, Ch = ch, Kind = m.Kind, W = m.W, H = m.H, Target = pos };
            }
            catch (Exception e) { Diag.Warn("proxyerr", "[mobs] proxy create failed: " + e.GetBaseException().Message); return null; }
        }

        static void Remove(uint id, string why)
        {
            if (!_proxies.TryGetValue(id, out var px)) return;
            _proxies.Remove(id); if (px.Ch != null) _proxyToMc.Remove(px.Ch);
            try { if (px.Go != null) _sceneDestroy.Invoke(Zns(), new object[] { px.Go }); } catch { }
        }

        internal static void Clear(string why)
        {
            if (_proxies.Count > 0) ValCraftPlugin.Log.LogInfo($"[mobs] removing {_proxies.Count} proxies ({why})");
            foreach (var id in new List<uint>(_proxies.Keys)) Remove(id, why);
            _proxyToMc.Clear(); _vmobs.Clear(); _incoming.Clear();
        }

        internal static void Update(ValCraftLink link)
        {
            _link = link;
            if (!Enabled) return;
            if (!PlayerSync.Following || PlayerSync.Player == null) { if (_proxies.Count > 0) Clear("not following"); return; }
            if (!_init) Init();
            if (!_ok) return;
            // keep proxies on their MC mobs
            foreach (var px in _proxies.Values)
            {
                if (px.Go == null) continue;
                var rb = px.Go.GetComponent<Rigidbody>();
                if (rb != null) rb.position = px.Target;
                px.Go.transform.position = px.Target;
            }
            float t = Time.unscaledTime;
            if (t < _nextList) return;
            _nextList = t + 0.2f;
            var list = new List<MobCodec.Mob>(); var prevSent = new HashSet<uint>(_vmobs.Keys); _vmobs.Clear();
            var me = PlayerSync.Player.transform.position;
            if (_getAll.Invoke(null, null) is IList all)
                foreach (Component c in all)
                {
                    if (c == null || IsProxy(c) || (_playerT != null && _playerT.IsInstanceOfType(c))) continue;
                    if (list.Count >= MaxProxies) break;
                    if ((c.transform.position - me).sqrMagnitude > 32f * 32f) continue;
                    if (!NetTunnel.Owns(c.transform.position, prevSent.Contains((uint)c.GetInstanceID()) ? 4f : 0f)) continue; // 0.13.0: one Valheim player mirrors each monster
                    try
                    {
                        if ((bool)_isDead.Invoke(c, null) || (_isTamed != null && (bool)_isTamed.Invoke(c, null))) continue;
                        string f = _faction.GetValue(c).ToString();
                        if (f == "Players" || f == "AnimalsVeg" || f == "PlayerSpawned" || f == "TrainingDummy" || f == "Dverger") continue;
                        uint vid = (uint)c.GetInstanceID();
                        _vmobs[vid] = c;
                        var cap = c.GetComponent<CapsuleCollider>();
                        float hp = (float)_getHealth.Invoke(c, null), max = Math.Max(1f, (float)_getMax.Invoke(c, null));
                        var mc = ToMc(c.transform.position);
                        string hn = null; try { hn = _hoverName?.Invoke(c, null) as string; } catch { }
                        list.Add(new MobCodec.Mob { Id = vid, Kind = MobCodec.Hostile, X = mc.x, Y = mc.y, Z = mc.z, W = cap != null ? cap.radius * 2 : 0.8f, H = cap != null ? cap.height : 1.8f, HpFrac = hp / max, MaxHp = max, Name = hn ?? c.gameObject.name.Replace("(Clone)", "") });
                    }
                    catch { }
                }
            foreach (var m in MobCodec.EncodeList(list)) link.TrySend(ValCraftLink.EvVMobList, m);
        }

        /// <summary>HealthLink's ApplyDamage prefix: a Valheim hit on a proxy goes to the MC mob.</summary>
        internal static void ForwardProxyHit(object proxy, float valheimDamage, Vector3 dir, object hit)
        {
            if (_link == null || !_proxyToMc.TryGetValue(proxy, out var mcId)) return;
            uint attacker = 0;
            try
            {
                var zid = _attackerF?.GetValue(hit);
                var go = zid == null || _findInst == null ? null : _findInst.Invoke(Zns(), new[] { zid }) as GameObject;
                var ch = go == null ? null : go.GetComponent(_charT);
                if (ch != null && !IsProxy(ch) && !(_playerT != null && _playerT.IsInstanceOfType(ch))) attacker = (uint)ch.GetInstanceID();
            }
            catch { }
            float mc = valheimDamage / Math.Max(0.1f, ValheimHit.CombatScale);
            _link.TrySend(ValCraftLink.EvMobHurt, MobCodec.MobHurt(mcId, mc, dir.x, -dir.z, attacker));
            Diag.Info("proxyhit", $"[mobs] Valheim hit on MC mob {mcId}: {valheimDamage:F1} -> MC {mc:F1} (attacker {(attacker == 0 ? "player/none" : attacker.ToString())})");
        }

        /// <summary>EvVMobHurt: an MC mob (or proxy-attacking MC entity) hit a Valheim monster.</summary>
        internal static void VMobHurt(byte[] p)
        {
            if (!Enabled || !_ok || p.Length < 12) return;
            var (vid, dmg, attacker) = MobCodec.VMobHurt(p);
            if (!_vmobs.TryGetValue(vid, out var c) || c == null) return;
            try
            {
                var hit = Activator.CreateInstance(_hitT);
                var dtF = AccessTools.Field(_hitT, "m_damage"); object dt = dtF.GetValue(hit);
                bool byPlayer = attacker == uint.MaxValue; // 0.11.0: MC player projectile
                AccessTools.Field(dt.GetType(), byPlayer ? "m_pierce" : "m_blunt")?.SetValue(dt, dmg * ValheimHit.CombatScale); dtF.SetValue(hit, dt);
                AccessTools.Field(_hitT, "m_point")?.SetValue(hit, c.transform.position + Vector3.up);
                Proxy src = null; if (attacker != 0) _proxies.TryGetValue(attacker, out src);
                if (src != null) AccessTools.Field(_hitT, "m_dir")?.SetValue(hit, (c.transform.position - src.Go.transform.position).normalized);
                if (src != null && _setAttacker != null) _setAttacker.Invoke(hit, new object[] { src.Ch });
                if (byPlayer && PlayerSync.Player != null && _setAttacker != null) { _setAttacker.Invoke(hit, new object[] { PlayerSync.Player }); AccessTools.Field(_hitT, "m_dir")?.SetValue(hit, (c.transform.position - PlayerSync.Player.transform.position).normalized); }
                AccessTools.Field(_hitT, "m_pushForce")?.SetValue(hit, 10f);
                _damage.Invoke(c, new[] { hit });
                Diag.Info("vmobhit", $"[mobs] MC mob {attacker} hit Valheim {c.gameObject.name}: MC {dmg:F1} -> {dmg * ValheimHit.CombatScale:F1}");
            }
            catch (Exception e) { Diag.Warn("vmobhurt", "[mobs] damage failed: " + e.GetBaseException().Message); }
        }
    }
}
