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
        static Material _mat;
        static Texture2D _atlas;
        static int _loadedRevision = -1, _layer = -1, _messages, _built;
        static float _nextAtlasTry, _nextStat;
        static bool _sentEnabled, _configSent;
        static int _sentRadius;

        /// <summary>Called with every M2V event; true if it was a block message.</summary>
        internal static bool Handle(LinkEvent ev)
        {
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
            Objects.Clear(); Store.Dirty.Clear();
        }

        internal static void Update(ValCraftLink link, bool connected)
        {
            if (connected && (!_configSent || _sentEnabled != Enabled || _sentRadius != Radius))
            {
                if (link.TrySend(ValCraftLink.EvBlockSyncConfig, Payload.U32(Enabled ? 1u : 0u, (uint)Radius)))
                {
                    _configSent = true; _sentEnabled = Enabled; _sentRadius = Radius;
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

            float t = Time.unscaledTime;
            if (t >= _nextStat && (_messages > 0 || Objects.Count > 0))
            {
                _nextStat = t + 30f;
                ValCraftPlugin.Log.LogInfo($"[blocks] {Store.Sections.Count} sections, {Objects.Count} meshes, {Store.Palette.Count} block states, {_messages} messages, {_built} rebuilds, atlas rev {_loadedRevision}, {Store.Dirty.Count} pending");
            }
        }

        static void Rebuild((int, int, int) k)
        {
            Store.Build(k, Md);
            Objects.TryGetValue(k, out var go);
            if (Md.Tris.Count == 0) { if (go != null) DestroyGo(go); Objects.Remove(k); return; }
            if (go == null)
            {
                go = new GameObject($"mc_section_{k.Item1}_{k.Item2}_{k.Item3}");
                go.layer = _layer;
                go.transform.SetParent(_root, false);
                go.transform.localPosition = new Vector3(k.Item1 * 16, k.Item2 * 16, -k.Item3 * 16);
                go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _mat;
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
            mesh.SetTriangles(Md.Tris, 0);
            mesh.RecalculateBounds();
            var mc = go.GetComponent<MeshCollider>();
            if (Collision && Md.ColTris.Count > 0)
            {
                var cm = mc != null ? mc.sharedMesh : null;
                if (cm == null || cm == mesh) cm = new Mesh { name = go.name + "_col", indexFormat = IndexFormat.UInt32 };
                cm.Clear(); cm.vertices = v; cm.SetTriangles(Md.ColTris, 0); cm.RecalculateBounds();
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

        static readonly string[] ShaderNames = { "Standard", "Legacy Shaders/Transparent/Cutout/Diffuse", "Legacy Shaders/Diffuse", "Custom/Piece", "Unlit/Transparent Cutout", "Unlit/Texture", "Sprites/Default" };

        static void EnsureMaterial()
        {
            if (_mat != null) return;
            Shader sh = null; string name = null;
            foreach (var s in ShaderNames) { sh = Shader.Find(s); if (sh != null) { name = s; break; } }
            if (sh == null)
            {
                // Valheim strips unused shaders from Shader.Find; take one from a loaded object.
                foreach (var s in Resources.FindObjectsOfTypeAll<Shader>())
                    foreach (var want in ShaderNames) if (s != null && s.name == want) { sh = s; name = s.name; break; }
            }
            if (sh == null) { ValCraftPlugin.Log.LogWarning("[blocks] no usable shader found (tried " + string.Join(", ", ShaderNames) + "); blocks will be invisible"); sh = Shader.Find("Hidden/InternalErrorShader"); name = "error"; }
            _mat = new Material(sh) { name = "ValCraftBlocks" };
            if (_mat.HasProperty("_Color")) _mat.SetColor("_Color", Color.white);
            if (_mat.HasProperty("_Glossiness")) _mat.SetFloat("_Glossiness", 0f);
            if (_mat.HasProperty("_Metallic")) _mat.SetFloat("_Metallic", 0f);
            if (_mat.HasProperty("_Cutoff")) _mat.SetFloat("_Cutoff", 0.5f);
            if (name == "Standard")
            {
                _mat.SetFloat("_Mode", 1f); _mat.EnableKeyword("_ALPHATEST_ON");
                _mat.SetOverrideTag("RenderType", "TransparentCutout"); _mat.renderQueue = (int)RenderQueue.AlphaTest;
            }
            ValCraftPlugin.Log.LogInfo($"[blocks] material shader: {name}");
            if (_atlas != null) _mat.mainTexture = _atlas;
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
                _atlas.filterMode = FilterMode.Point; _atlas.wrapMode = TextureWrapMode.Clamp; _atlas.anisoLevel = 0;
                _mat.mainTexture = _atlas;
                _loadedRevision = Store.AtlasRevision;
                ValCraftPlugin.Log.LogInfo($"[blocks] atlas rev {_loadedRevision} loaded ({_atlas.width}x{_atlas.height}, {Store.AtlasTiles} tiles) from {Store.AtlasPath}");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[blocks] atlas load failed (retrying): {e.Message}"); }
        }
    }
}
