using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// 0.7.0: draws the MC blocks that aren't mirrored Valheim terrain (player builds, placed blocks). MC streams
    /// 16^3 sections (BlockSync.java); <see cref="BlockStore"/> keeps them and builds face-culled meshes; here each
    /// section becomes a GameObject (MeshRenderer + optional MeshCollider) under one root at the MC->Valheim anchor.
    /// Textures: PNG atlas MC writes next to the shm file. Valheim only mirrors; MC's world saves the blocks.
    /// </summary>
    internal static class BlockRender
    {
        internal static bool Enabled = true, Collision = true;
        internal static int Radius = 64, RebuildsPerFrame = 4;
        internal static Transform Root => _root;
        static Transform _root;
        static readonly BlockStore Store = new BlockStore();
        static readonly BlockStore.MeshData Md = new BlockStore.MeshData();
        static readonly Dictionary<(int, int, int), GameObject> Objects = new Dictionary<(int, int, int), GameObject>();
        static Material _mat, _matCutout, _matTrans, _matEmit, _matShiny; static Material[] _mats;
        internal static bool Lights = true, Reflections = true; internal static int MaxLights = 32;
        static readonly Dictionary<(int, int, int), List<(float x, float y, float z, int level, int rgb, int count)>> SectionLights = new Dictionary<(int, int, int), List<(float, float, float, int, int, int)>>();
        static readonly List<Light> LightPool = new List<Light>();
        static float _nextLightUpdate, _nextProbe; static ReflectionProbe _probe; static bool _reflectiveCube;
        static Texture2D _atlas;
        static int _loadedRevision = -1, _layer = -1, _messages, _built;
        static float _nextAtlasTry, _nextStat;
        static bool _sentEnabled, _configSent;
        static int _sentRadius, _sentMaxEnt, _sentMaxMobs;

        /// <summary>Called with every M2V event; true if it was a block message.</summary>
        internal static int MaxEntities = 64, MaxMobs = 24; internal static string OpaqueShader = "";
        static readonly EntityStore Ents = new EntityStore();
        static readonly Dictionary<uint, Mesh> EntMeshes = new Dictionary<uint, Mesh>();
        static readonly Dictionary<uint, GameObject> EntObjs = new Dictionary<uint, GameObject>();
        static Texture2D _frames; static bool _copyOk;
        static readonly Dictionary<uint, float> GroundOffset = new Dictionary<uint, float>();

        internal static bool Handle(LinkEvent ev)
        {
            if (Ents.Handle(ev.Type, ev.Payload)) { _messages++; return true; }
            if (!Store.Handle(ev.Type, ev.Payload)) return false;
            _messages++;
            if (Store.ResetRequested) { Store.ResetRequested = false; ValCraftPlugin.Log.LogInfo("[blocks] Minecraft reset block stream (new link)"); }
            return true;
        }

        internal static void OnConnected() { _configSent = false; }
        internal static void Clear()
        {
            Store.Handle(ValCraftLink.EvBlocksReset, new byte[0]);
            Store.ResetRequested = false;
            foreach (var go in Objects.Values) if (go != null) DestroyGo(go);
            Objects.Clear(); Store.Dirty.Clear(); SectionLights.Clear();
            foreach (var l in LightPool) if (l != null) l.enabled = false;
            Ents.Handle(ValCraftLink.EvBlocksReset, new byte[0]);
            foreach (var go in EntObjs.Values) if (go != null) UnityEngine.Object.Destroy(go);
            foreach (var m in EntMeshes.Values) if (m != null) UnityEngine.Object.Destroy(m);
            EntObjs.Clear(); EntMeshes.Clear();
        }

        internal static void Update(ValCraftLink link, bool connected)
        {
            if (connected && (!_configSent || _sentEnabled != Enabled || _sentRadius != Radius || _sentMaxEnt != MaxEntities || _sentMaxMobs != MaxMobs))
            {
                if (link.TrySend(ValCraftLink.EvBlockSyncConfig, Payload.U32(Enabled ? 1u : 0u, (uint)Radius, (uint)MaxEntities, (uint)MaxMobs)))
                {
                    _configSent = true; _sentEnabled = Enabled; _sentRadius = Radius; _sentMaxEnt = MaxEntities; _sentMaxMobs = MaxMobs;
                    ValCraftPlugin.Log.LogInfo($"[blocks] sent config to Minecraft: render={(Enabled ? "on" : "off")}, radius {Radius}");
                }
            }
            if (!Enabled) { if (Objects.Count > 0) Clear(); return; }
            bool show = connected && PlayerSync.Anchored;
            if (_root == null)
            {
                var go = new GameObject("ValCraftBlocks");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _root = go.transform;
                _layer = LayerMask.NameToLayer("piece");
                if (_layer < 0) _layer = 0;
            }
            if (_root.gameObject.activeSelf != show) _root.gameObject.SetActive(show);
            if (!show) return;
            _root.position = PlayerSync.Anchor;
            _root.rotation = Quaternion.identity;
            EnsureMaterial();
            LoadAtlasIfChanged();

            int budget = RebuildsPerFrame;
            var done = new List<(int, int, int)>();
            foreach (var k in Store.Dirty)
            {
                if (budget-- <= 0) break;
                Rebuild(k);
                done.Add(k);
            }
            foreach (var k in done) Store.Dirty.Remove(k);
            UpdateLights();
            UpdateProbe();
            Animate();
            UpdateEntities();

            float t = Time.unscaledTime;
            if (t >= _nextStat && (_messages > 0 || Objects.Count > 0))
            {
                _nextStat = t + 30f;
                ValCraftPlugin.Log.LogInfo($"[blocks] {Store.Sections.Count} sections, {Objects.Count} meshes, {Store.Palette.Count} block states, {_messages} messages, {_built} rebuilds, atlas rev {_loadedRevision}, {Store.Dirty.Count} pending");
            }
        }

        /// <summary>0.9.2: drop zero-area / NaN collider triangles (PhysX "cleaning the mesh failed"); empty if nothing solid is left.</summary>
        static readonly List<int> _cleanTris = new List<int>();
        static List<int> CleanTris(Vector3[] v, List<int> tris)
        {
            _cleanTris.Clear();
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (a >= v.Length || b >= v.Length || c >= v.Length) continue;
                var cr = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
                float area2 = cr.sqrMagnitude;
                if (float.IsNaN(area2) || area2 < 1e-8f) continue;
                _cleanTris.Add(a); _cleanTris.Add(b); _cleanTris.Add(c);
            }
            if (_cleanTris.Count < 3) _cleanTris.Clear();
            return _cleanTris;
        }

        static void Rebuild((int, int, int) k)
        {
            Store.Build(k, Md);
            Objects.TryGetValue(k, out var go);
            if (Md.Lights.Count > 0) SectionLights[k] = new List<(float, float, float, int, int, int)>(Md.Lights); else SectionLights.Remove(k);
            if (Md.TriCount == 0) { if (go != null) DestroyGo(go); Objects.Remove(k); return; }
            if (go == null)
            {
                go = new GameObject($"mc_section_{k.Item1}_{k.Item2}_{k.Item3}");
                go.layer = _layer;
                go.transform.SetParent(_root, false);
                go.transform.localPosition = new Vector3(k.Item1 * 16, k.Item2 * 16, -k.Item3 * 16);
                go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = _mats;
                mr.shadowCastingMode = ShadowCastingMode.On;
                mr.receiveShadows = true;
                Objects[k] = go;
            }
            var mf = go.GetComponent<MeshFilter>();
            var mesh = mf.sharedMesh;
            if (mesh == null) { mesh = new Mesh { name = go.name, indexFormat = IndexFormat.UInt32 }; mf.sharedMesh = mesh; }
            mesh.Clear();
            int nv = Md.Pos.Count / 3;
            var v = new Vector3[nv]; var n = new Vector3[nv]; var uv = new Vector2[nv];
            for (int i = 0; i < nv; i++)
            {
                v[i] = new Vector3(Md.Pos[i * 3], Md.Pos[i * 3 + 1], Md.Pos[i * 3 + 2]);
                n[i] = new Vector3(Md.Nrm[i * 3], Md.Nrm[i * 3 + 1], Md.Nrm[i * 3 + 2]);
                uv[i] = new Vector2(Md.Uv[i * 2], Md.Uv[i * 2 + 1]);
            }
            mesh.vertices = v; mesh.normals = n; mesh.uv = uv;
            var white = new Color32[nv]; for (int i = 0; i < nv; i++) white[i] = new Color32(255, 255, 255, 255);
            mesh.colors32 = white;
            mesh.subMeshCount = 5;
            for (int sm = 0; sm < 5; sm++) mesh.SetTriangles(Md.Sub[sm], sm, false);
            mesh.RecalculateBounds();
            var mc = go.GetComponent<MeshCollider>();
            var colTris = Collision && Md.ColTris.Count > 0 ? CleanTris(v, Md.ColTris) : null;
            if (colTris != null && colTris.Count > 0)
            {
                var cm = mc != null ? mc.sharedMesh : null;
                if (cm == null || cm == mesh) cm = new Mesh { name = go.name + "_col", indexFormat = IndexFormat.UInt32 };
                cm.Clear(); cm.vertices = v; cm.SetTriangles(colTris, 0); cm.RecalculateBounds();
                if (mc == null) mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = null; mc.sharedMesh = cm;
            }
            else if (mc != null) { if (mc.sharedMesh != null) UnityEngine.Object.Destroy(mc.sharedMesh); UnityEngine.Object.Destroy(mc); }
            _built++;
        }

        static void DestroyGo(GameObject go)
        {
            var mf = go.GetComponent<MeshFilter>(); if (mf != null && mf.sharedMesh != null) UnityEngine.Object.Destroy(mf.sharedMesh);
            var mc = go.GetComponent<MeshCollider>(); if (mc != null && mc.sharedMesh != null) UnityEngine.Object.Destroy(mc.sharedMesh);
            UnityEngine.Object.Destroy(go);
        }

        static readonly string[] OpaqueNames = { "Standard", "Legacy Shaders/Diffuse", "Custom/Piece", "Unlit/Texture" };
        static readonly string[] CutoutNames = { "Legacy Shaders/Transparent/Cutout/Diffuse", "Standard", "Unlit/Transparent Cutout", "Legacy Shaders/Transparent/Cutout/VertexLit", "Nature/Tree Soft Occlusion Leaves" };
        static readonly string[] TransNames = { "Legacy Shaders/Transparent/Diffuse", "Sprites/Default", "Standard", "Unlit/Transparent", "Legacy Shaders/Transparent/VertexLit", "Sprites/Default", "Legacy Shaders/Particles/Alpha Blended", "UI/Default" };
        static readonly string[] EmitNames = { "Standard", "Legacy Shaders/Self-Illumin/Diffuse", "Legacy Shaders/Self-Illumin/VertexLit", "Unlit/Texture", "Particles/Standard Unlit" };
        static readonly string[] ShinyNames = { "Standard", "Legacy Shaders/Reflective/Specular", "Legacy Shaders/Reflective/Diffuse", "Legacy Shaders/Specular", "Legacy Shaders/Bumped Specular" };
        static Shader[] _loaded;

        static Shader FindShader(string[] names, out string picked)
        {
            // priority order across both Shader.Find and already-loaded shaders (0.7.3 log: "Standard" is only in the loaded list)
            if (_loaded == null) _loaded = Resources.FindObjectsOfTypeAll<Shader>();
            foreach (var n in names)
            {
                var sh = Shader.Find(n);
                if (sh == null) foreach (var l in _loaded) if (l != null && l.name == n) { sh = l; break; }
                if (sh != null) { picked = n; return sh; }
            }
            picked = null; return null;
        }

        static Material Make(Shader sh, string name, int mode)
        {
            var m = new Material(sh) { name = "ValCraftBlocks_" + mode };
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
            if (name == "Standard" && mode == 1) { m.SetFloat("_Mode", 1f); m.EnableKeyword("_ALPHATEST_ON"); m.SetOverrideTag("RenderType", "TransparentCutout"); m.renderQueue = (int)RenderQueue.AlphaTest; }
            if (name == "Standard" && mode == 2)
            {
                m.SetFloat("_Mode", 2f); m.SetOverrideTag("RenderType", "Transparent");
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha); m.SetInt("_ZWrite", 0);
                m.DisableKeyword("_ALPHATEST_ON"); m.EnableKeyword("_ALPHABLEND_ON"); m.renderQueue = (int)RenderQueue.Transparent;
            }
            if (mode == 3)
            {
                if (m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", Color.white * 1.2f); m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive; }
                if (m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", _atlas);
                if (m.HasProperty("_Illum")) m.SetTexture("_Illum", _atlas);
            }
            if (mode == 4 || (mode == 2 && Reflections))
            {
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", mode == 4 ? 0.8f : 0.92f);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", mode == 4 ? 0.8f : 0.92f);
                if (mode == 4 && m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.35f);
                if (m.HasProperty("_Shininess")) m.SetFloat("_Shininess", 0.6f);
                if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", new Color(0.6f, 0.6f, 0.6f, 1f));
                if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", new Color(0.35f, 0.35f, 0.35f, 0.5f));
            }
            if (mode == 2 && m.renderQueue < (int)RenderQueue.Transparent) m.renderQueue = (int)RenderQueue.Transparent;
            if (mode == 1 && m.renderQueue < (int)RenderQueue.AlphaTest) m.renderQueue = (int)RenderQueue.AlphaTest;
            return m;
        }

        static void EnsureMaterial()
        {
            if (_mat != null) return;
            var op = FindShader(string.IsNullOrEmpty(OpaqueShader) ? OpaqueNames : new[] { OpaqueShader }, out var opN); if (op == null) op = FindShader(OpaqueNames, out opN);
            if (op == null) { ValCraftPlugin.Log.LogWarning("[blocks] no opaque shader found; blocks will be invisible"); op = Shader.Find("Hidden/InternalErrorShader"); opN = "error"; }
            var cu = FindShader(CutoutNames, out var cuN);
            var tr = FindShader(TransNames, out var trN);
            if (cu == null) { cu = tr; cuN = trN; } // no alpha-test shader: blended is the next best thing for plants/glass
            if (cu == null) { cu = op; cuN = opN; }
            if (tr == null) { tr = cu; trN = cuN; }
            var em = FindShader(EmitNames, out var emN); if (em == null) { em = op; emN = opN; }
            string shN = null; var shs = Reflections ? FindShader(ShinyNames, out shN) : null; if (shs == null) { shs = op; shN = opN; }
            _reflectiveCube = shN.Contains("Reflective");
            _mat = Make(op, opN, 0); _matCutout = Make(cu, cuN, 1); _matTrans = Make(tr, trN, 2); _matEmit = Make(em, emN, 3); _matShiny = Make(shs, shN, 4);
            _mats = new[] { _mat, _matCutout, _matTrans, _matEmit, _matShiny };
            ValCraftPlugin.Log.LogInfo($"[blocks] shaders: opaque={opN}, cutout={cuN}, translucent={trN}, emissive={emN}, shiny={shN} (reflections {(Reflections ? "on" : "off")}, lights {(Lights ? "on, max " + MaxLights : "off")})");
            if (_loaded == null) _loaded = Resources.FindObjectsOfTypeAll<Shader>();
            var names = new List<string>(); foreach (var sh in _loaded) if (sh != null && !sh.name.StartsWith("Hidden/")) names.Add(sh.name);
            names.Sort();
            ValCraftPlugin.Log.LogInfo($"[blocks] loaded shaders ({names.Count}): {string.Join(", ", names)}");
            if (_atlas != null) foreach (var m in _mats) m.mainTexture = _atlas;
        }

        /// <summary>Pick the MaxLights strongest/nearest block-light clusters around the camera; pooled point lights, no shadows.</summary>
        static void UpdateLights()
        {
            float t = Time.unscaledTime;
            if (t < _nextLightUpdate) return;
            _nextLightUpdate = t + 0.5f;
            var cam = Camera.main != null ? Camera.main.transform.position : _root.position;
            var cand = new List<(float score, Vector3 pos, float range, Color col, float intensity)>();
            if (Lights)
                foreach (var kv in SectionLights)
                {
                    var origin = _root.position + new Vector3(kv.Key.Item1 * 16, kv.Key.Item2 * 16, -kv.Key.Item3 * 16);
                    foreach (var l in kv.Value)
                    {
                        var p = origin + new Vector3(l.x, l.y, l.z);
                        float range = l.level * 0.8f + Mathf.Min(6f, Mathf.Sqrt(l.count));
                        float d = Vector3.Distance(cam, p);
                        if (d > Radius + range) continue;
                        var c = new Color(((l.rgb >> 16) & 255) / 255f, ((l.rgb >> 8) & 255) / 255f, (l.rgb & 255) / 255f);
                        if (l.rgb == 0) c = new Color(1f, 0.7f, 0.4f);
                        cand.Add((d / (l.level + 1f), p, range, c, 0.6f + 0.06f * l.level + Mathf.Min(0.8f, l.count * 0.05f)));
                    }
                }
            cand.Sort((a, b) => a.score.CompareTo(b.score));
            int n = Mathf.Min(cand.Count, MaxLights);
            while (LightPool.Count < n)
            {
                var go = new GameObject("mc_light");
                go.transform.SetParent(_root, false);
                var li = go.AddComponent<Light>();
                li.type = LightType.Point; li.shadows = LightShadows.None; li.renderMode = LightRenderMode.Auto;
                LightPool.Add(li);
            }
            for (int i = 0; i < LightPool.Count; i++)
            {
                var li = LightPool[i];
                if (i >= n) { li.enabled = false; continue; }
                li.transform.position = cand[i].pos; li.range = cand[i].range; li.color = cand[i].col; li.intensity = cand[i].intensity; li.enabled = true;
            }
        }

        /// <summary>One low-res realtime reflection probe at the player, re-rendered every 3 s (only if there is something shiny/translucent).</summary>
        static void UpdateProbe()
        {
            if (!Reflections) { if (_probe != null) _probe.enabled = false; return; }
            float t = Time.unscaledTime;
            if (t < _nextProbe) return;
            _nextProbe = t + 3f;
            if (Objects.Count == 0) return;
            if (_probe == null)
            {
                var go = new GameObject("mc_reflection_probe");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _probe = go.AddComponent<ReflectionProbe>();
                _probe.mode = ReflectionProbeMode.Realtime;
                _probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
                _probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
                _probe.resolution = 64; _probe.size = new Vector3(Radius * 2f, 128f, Radius * 2f);
                _probe.importance = 2; _probe.boxProjection = false; _probe.hdr = false; _probe.farClipPlane = 200f;
                ValCraftPlugin.Log.LogInfo("[blocks] reflection probe created (64 px, realtime via script, every 3 s)");
            }
            _probe.enabled = true;
            var cam = Camera.main != null ? Camera.main.transform.position : _root.position;
            _probe.transform.position = cam + Vector3.up * 0.5f;
            _probe.RenderProbe();
            if (_reflectiveCube && _probe.realtimeTexture != null)
                foreach (var m in _mats) if (m.HasProperty("_Cube")) m.SetTexture("_Cube", _probe.realtimeTexture);
        }

        /// <summary>Animated tiles: GPU-copy the current frame tile (from an untouched copy of the atlas) into the display tile.</summary>
        static void Animate()
        {
            if (_atlas == null || _frames == null || !_copyOk || Store.Anims.Count == 0) return;
            long tick = (long)(Time.time * 20f);
            int ts = Store.AtlasTileSize, per = Store.AtlasPerRow, h = _atlas.height;
            foreach (var a in Store.Anims.Values)
            {
                int f = a.FrameAt(tick);
                if (f == a.Current) continue;
                a.Current = f;
                int src = a.Frames[f], dst = a.Display;
                try
                {
                    Graphics.CopyTexture(_frames, 0, 0, (src % per) * ts, h - (src / per + 1) * ts, ts, ts, _atlas, 0, 0, (dst % per) * ts, h - (dst / per + 1) * ts);
                }
                catch (Exception e) { _copyOk = false; ValCraftPlugin.Log.LogWarning($"[blocks] texture animation disabled: {e.Message}"); return; }
            }
        }

        static void UpdateEntities()
        {
            foreach (var id in Ents.NewMeshes)
            {
                if (!Ents.Meshes.TryGetValue(id, out var quads)) continue;
                Md.Clear();
                float g = Store.AtlasPerRow, eps = 0.5f / Math.Max(1, Store.AtlasTileSize);
                foreach (var q in quads) BlockStore.ModelQuad(Md, Md.Sub[1], q, 0, 0, 0, g, eps);
                int nv = Md.Pos.Count / 3;
                var v = new Vector3[nv]; var n = new Vector3[nv]; var uv = new Vector2[nv]; var col = new Color32[nv];
                for (int i = 0; i < nv; i++) { v[i] = new Vector3(Md.Pos[i * 3], Md.Pos[i * 3 + 1], Md.Pos[i * 3 + 2]); n[i] = new Vector3(Md.Nrm[i * 3], Md.Nrm[i * 3 + 1], Md.Nrm[i * 3 + 2]); uv[i] = new Vector2(Md.Uv[i * 2], Md.Uv[i * 2 + 1]); col[i] = new Color32(255, 255, 255, 255); }
                // reuse the Mesh object (mobs re-send their mesh ~10x/s)
                if (!EntMeshes.TryGetValue(id, out var mesh) || mesh == null) { mesh = new Mesh { name = "mc_entity_" + id, indexFormat = IndexFormat.UInt32 }; mesh.MarkDynamic(); }
                mesh.Clear();
                mesh.vertices = v; mesh.normals = n; mesh.uv = uv; mesh.colors32 = col; mesh.SetTriangles(Md.Sub[1], 0); mesh.RecalculateBounds();
                EntMeshes[id] = mesh;
                foreach (var go in EntObjs.Values) if (go != null && go.name.EndsWith("#" + id)) go.GetComponent<MeshFilter>().sharedMesh = mesh;
            }
            Ents.NewMeshes.Clear();
            var seen = new HashSet<uint>();
            int count = 0;
            float t = Time.time;
            foreach (var e in Ents.Current.Values)
            {
                if (count++ >= MaxEntities + MaxMobs + 96) break;
                seen.Add(e.Id);
                if (!EntObjs.TryGetValue(e.Id, out var go) || go == null)
                {
                    go = new GameObject($"mc_entity {e.Id}#{e.Mesh}");
                    go.transform.SetParent(_root, false);
                    go.AddComponent<MeshFilter>();
                    var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = _matCutout;
                    go.transform.localPosition = new Vector3(e.X, e.Y, -e.Z);
                    EntObjs[e.Id] = go;
                }
                if (!go.name.EndsWith("#" + e.Mesh)) go.name = $"mc_entity {e.Id}#{e.Mesh}";
                var mf = go.GetComponent<MeshFilter>();
                EntMeshes.TryGetValue(e.Mesh, out var m);
                if (mf.sharedMesh != m) mf.sharedMesh = m;
                var target = new Vector3(e.X, e.Y, -e.Z);
                // 0.7.5: re-seat on Valheim's smooth ground (flag 8 = MC groundY valid; 4 = standing on it). offset = valheimGround - mcGround.
                GroundOffset.TryGetValue(e.Id, out var off);
                if ((e.Flags & 8) != 0)
                {
                    float vg = TerrainExport.HeightAtMc(e.X, e.Z, _root.position);
                    if (!float.IsNaN(vg))
                    {
                        float want = vg - (e.GroundY + _root.position.y);
                        if (Mathf.Abs(want) < 3f) off = (e.Flags & 4) != 0 ? Mathf.Lerp(off, want, Mathf.Clamp01(Time.deltaTime * 10f)) : off;
                    }
                }
                else off = Mathf.Lerp(off, 0f, Mathf.Clamp01(Time.deltaTime * 4f)); // on a player block / unknown: no correction
                GroundOffset[e.Id] = off;
                target.y += off;
                if ((e.Flags & 1) != 0) target.y += Mathf.Sin(t * 2f + e.Id) * 0.1f + 0.1f;
                // 0.10.1: the MC player (third person) is drawn in-world at the Valheim player's own position (no 10 Hz lag)
                if ((e.Flags & 16) != 0 && PlayerSync.Player != null)
                {
                    go.transform.position = PlayerSync.Player.transform.position;
                    go.transform.localRotation = Quaternion.identity;
                    continue;
                }
                var lp = go.transform.localPosition;
                go.transform.localPosition = (lp - target).sqrMagnitude > 16f ? target : Vector3.Lerp(lp, target, Mathf.Clamp01(Time.deltaTime * 12f));
                go.transform.localRotation = (e.Flags & 1) != 0 ? Quaternion.Euler(0, (t * 60f + e.Id * 37) % 360f, 0) : Quaternion.identity;
            }
            if (EntObjs.Count > seen.Count)
            {
                var gone = new List<uint>();
                foreach (var id in EntObjs.Keys) if (!seen.Contains(id)) gone.Add(id);
                foreach (var id in gone)
                {
                    var go = EntObjs[id];
                    if (go != null)
                    {
                        // per-mob dynamic mesh (id bit 30): free it with the entity
                        var mf = go.GetComponent<MeshFilter>(); var mm = mf != null ? mf.sharedMesh : null;
                        foreach (var kv in EntMeshes) if (kv.Value == mm && (kv.Key & 0x40000000u) != 0) { UnityEngine.Object.Destroy(mm); EntMeshes.Remove(kv.Key); Ents.Meshes.Remove(kv.Key); break; }
                        UnityEngine.Object.Destroy(go);
                    }
                    EntObjs.Remove(id); GroundOffset.Remove(id);
                }
            }
        }

        static void LoadAtlasIfChanged()
        {
            if (Store.AtlasRevision == _loadedRevision || string.IsNullOrEmpty(Store.AtlasPath)) return;
            float t = Time.unscaledTime;
            if (t < _nextAtlasTry) return;
            _nextAtlasTry = t + 0.5f;
            try
            {
                var bytes = File.ReadAllBytes(Store.AtlasPath);
                if (_atlas == null) _atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "ValCraftAtlas" };
                if (!ImageConversion.LoadImage(_atlas, bytes, false)) throw new Exception("LoadImage failed");
                if (_frames == null) _frames = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "ValCraftAtlasFrames" };
                _copyOk = ImageConversion.LoadImage(_frames, bytes, false) && SystemInfo.copyTextureSupport != CopyTextureSupport.None && _frames.format == _atlas.format;
                foreach (var a in Store.Anims.Values) a.Current = -1;
                _atlas.filterMode = FilterMode.Point; _atlas.wrapMode = TextureWrapMode.Clamp; _atlas.anisoLevel = 0;
                foreach (var m in _mats) { m.mainTexture = _atlas; if (m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", _atlas); if (m.HasProperty("_Illum")) m.SetTexture("_Illum", _atlas); }
                _loadedRevision = Store.AtlasRevision;
                ValCraftPlugin.Log.LogInfo($"[blocks] atlas rev {_loadedRevision} loaded ({_atlas.width}x{_atlas.height}, {Store.AtlasTiles} tiles) from {Store.AtlasPath}");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[blocks] atlas load failed (retrying): {e.Message}"); }
        }
    }
}
