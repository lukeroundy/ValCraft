using System;
using System.Reflection;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// Full-screen Minecraft overlay: a screen-space Canvas on the top sorting order with a uGUI RawImage showing the
    /// newest frame from shared memory. uGUI (UnityEngine.UI.dll) is reached by reflection so the plugin also builds
    /// without game DLLs. Frames are RGBA8 bottom-up, which is Texture2D's native row order (no flip needed).
    /// </summary>
    internal sealed unsafe class Overlay : IDisposable
    {
        internal static readonly Type RawImageType = Type.GetType("UnityEngine.UI.RawImage, UnityEngine.UI");
        static readonly Type GraphicRaycasterType = Type.GetType("UnityEngine.UI.GraphicRaycaster, UnityEngine.UI");
        readonly GameObject _root;
        readonly Canvas _canvas;
        readonly Component _rawImage;
        readonly PropertyInfo _texProp, _raycastProp;
        readonly Behaviour _raycaster;
        Texture2D _tex;
        long _lastSeq;
        int _uploads, _rejected;
        float _nextStats;

        public Overlay(GameObject host)
        {
            _root = new GameObject("ValCraftOverlay");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _canvas = _root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 32000;
            if (GraphicRaycasterType != null) { _raycaster = (Behaviour)_root.AddComponent(GraphicRaycasterType); _raycaster.enabled = false; }
            var imgGo = new GameObject("MinecraftFrame", typeof(RectTransform));
            imgGo.transform.SetParent(_root.transform, false);
            var rt = (RectTransform)imgGo.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            if (RawImageType == null) { ValCraftPlugin.Log.LogError("[overlay] UnityEngine.UI.RawImage not found; overlay disabled"); return; }
            _rawImage = imgGo.AddComponent(RawImageType);
            _texProp = RawImageType.GetProperty("texture");
            _raycastProp = RawImageType.GetProperty("raycastTarget");
            _raycastProp?.SetValue(_rawImage, false, null);
            // MC's exported color is premultiplied (drawn over transparent black with coverage alpha), so blend with
            // One/OneMinusSrcAlpha. Built-in premultiply shaders are only present if the game ships them (unverified).
            foreach (var name in new[] { "Legacy Shaders/Particles/Alpha Blended Premultiply", "Particles/Alpha Blended Premultiply", "Mobile/Particles/Alpha Blended Premultiply" })
            {
                var sh = Shader.Find(name);
                if (sh == null) continue;
                RawImageType.GetProperty("material")?.SetValue(_rawImage, new Material(sh), null);
                ValCraftPlugin.Log.LogInfo($"[overlay] premultiplied-alpha shader: {name}");
                break;
            }
            _root.SetActive(false);
        }

        public void Update(ValCraftLink link, bool visible, bool blockClicks)
        {
            if (_rawImage == null) return;
            if (_root.activeSelf != visible) _root.SetActive(visible);
            if (!visible) return;
            // While input goes to Minecraft, the overlay swallows uGUI clicks so Valheim's UI underneath stays inert.
            if (_raycaster != null && _raycaster.enabled != blockClicks) { _raycaster.enabled = blockClicks; _raycastProp?.SetValue(_rawImage, blockClicks, null); }

            if (!link.TryGetLatestFrame(_lastSeq, out var v)) return;
            if (_tex == null || _tex.width != v.Width || _tex.height != v.Height)
            {
                if (_tex != null) UnityEngine.Object.Destroy(_tex);
                _tex = new Texture2D(v.Width, v.Height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "ValCraftFrame" };
                _texProp.SetValue(_rawImage, _tex, null);
                ValCraftPlugin.Log.LogInfo($"[overlay] frame size {v.Width}x{v.Height} (screen {Screen.width}x{Screen.height})");
            }
            _tex.LoadRawTextureData((IntPtr)v.Data, v.Bytes);
            if (!link.FrameStillValid(v)) { _rejected++; return; } // writer lapped us mid-copy: don't upload a torn frame
            _tex.Apply(false, false);
            _lastSeq = v.Seq;
            _uploads++;
            if (Time.unscaledTime >= _nextStats)
            {
                if (_nextStats > 0) ValCraftPlugin.Log.LogDebug($"[overlay] {_uploads} frames uploaded, {_rejected} torn rejected in the last 10 s");
                _uploads = _rejected = 0; _nextStats = Time.unscaledTime + 10f;
            }
        }

        public void Dispose()
        {
            if (_tex != null) UnityEngine.Object.Destroy(_tex);
            if (_root != null) UnityEngine.Object.Destroy(_root);
        }
    }
}
