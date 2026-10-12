using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// Reads Unity input (through BepInEx's UnityInput, which works with both the legacy Input Manager and the Input
    /// System) and sends it to Minecraft as GLFW-style events. Mouse deltas come from the legacy "Mouse X/Y" axes or,
    /// if the legacy manager is disabled, from InputSystem.Mouse.current.delta via reflection.
    /// </summary>
    internal sealed class InputForwarder
    {
        // GLFW modifier bits
        const int ModShift = 1, ModCtrl = 2, ModAlt = 4, ModSuper = 8;
        static readonly List<KeyValuePair<KeyCode, int>> Keys = BuildKeyTable();
        readonly ValCraftPlugin _plugin;
        readonly HashSet<KeyCode> _down = new HashSet<KeyCode>();
        readonly bool[] _buttons = new bool[3];
        readonly byte[] _buf = new byte[16];
        Vector3 _lastMousePos;
        bool _haveLastPos;

        static bool? _legacy;
        static PropertyInfo _mouseCurrent, _mouseDelta;
        static MethodInfo _readValue;

        public InputForwarder(ValCraftPlugin plugin) { _plugin = plugin; }

        public static bool LegacyInputWorks()
        {
            if (_legacy.HasValue) return _legacy.Value;
            try { Input.GetAxisRaw("Mouse X"); _legacy = true; } catch { _legacy = false; }
            return _legacy.Value;
        }

        public static bool InputSystemPresent()
        {
            var t = Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem");
            if (t == null) return false;
            _mouseCurrent = t.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            _mouseDelta = AccessDelta(t);
            return _mouseCurrent != null && _mouseDelta != null;
        }

        static PropertyInfo AccessDelta(Type mouse)
        {
            for (var t = mouse; t != null; t = t.BaseType) { var p = t.GetProperty("delta", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly); if (p != null) return p; }
            return null;
        }

        Vector2 MouseDelta()
        {
            if (LegacyInputWorks())
                return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f; // legacy axes ≈ pixels * 0.1 (Unity default sensitivity; unverified for Valheim)
            if (_mouseCurrent == null && !InputSystemPresent()) return Vector2.zero;
            var mouse = _mouseCurrent.GetValue(null, null);
            if (mouse == null) return Vector2.zero;
            var ctl = _mouseDelta.GetValue(mouse, null);
            if (_readValue == null) _readValue = ctl.GetType().GetMethod("ReadValue", Type.EmptyTypes);
            return _readValue != null ? (Vector2)_readValue.Invoke(ctl, null) : Vector2.zero;
        }

        public void Reset() { _down.Clear(); for (int i = 0; i < _buttons.Length; i++) _buttons[i] = false; _haveLastPos = false; }

        /// <summary>Called every Valheim frame while F7 forwarding is on.</summary>
        public void Forward(bool mcScreenOpen)
        {
            var link = _plugin.Link;
            var inp = UnityInput.Current;
            int mods = 0;
            if (inp.GetKey(KeyCode.LeftShift) || inp.GetKey(KeyCode.RightShift)) mods |= ModShift;
            if (inp.GetKey(KeyCode.LeftControl) || inp.GetKey(KeyCode.RightControl)) mods |= ModCtrl;
            if (inp.GetKey(KeyCode.LeftAlt) || inp.GetKey(KeyCode.RightAlt)) mods |= ModAlt;
            if (inp.GetKey(KeyCode.LeftWindows) || inp.GetKey(KeyCode.RightWindows)) mods |= ModSuper;

            // keys (edge-detected; MC handles its own repeat for movement, chat repeat is a known gap)
            foreach (var kv in Keys)
            {
                bool now = inp.GetKey(kv.Key);
                bool was = _down.Contains(kv.Key);
                if (now == was) continue;
                if (now) _down.Add(kv.Key); else _down.Remove(kv.Key);
                Send(ValCraftLink.EvKey, kv.Value, 0, now ? 1 : 0, mods);
            }

            // typed characters (chat, signs, search boxes)
            if (LegacyInputWorks())
            {
                string s = Input.inputString;
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c < 32 || c == 127) continue; // backspace/enter/tab are sent as keys
                    int cp = char.IsHighSurrogate(c) && i + 1 < s.Length ? char.ConvertToUtf32(c, s[++i]) : c;
                    Send(ValCraftLink.EvChar, cp, mods, 0, 0, 8);
                }
            }

            // mouse
            Vector3 pos = inp.mousePosition;
            Vector2 d = MouseDelta() * _plugin.CfgMouseScale.Value;
            if (!LegacyInputWorks() && _mouseCurrent == null && _haveLastPos) d = (Vector2)(pos - _lastMousePos); // last resort
            _lastMousePos = pos; _haveLastPos = true;
            float nx = Screen.width > 0 ? Mathf.Clamp01(pos.x / Screen.width) : 0.5f;
            float ny = Screen.height > 0 ? Mathf.Clamp01(1f - pos.y / Screen.height) : 0.5f;
            if (d.x != 0 || d.y != 0 || mcScreenOpen)
                SendF(ValCraftLink.EvMouseMove, d.x, -d.y, nx, ny); // MC: +y is down
            for (int b = 0; b < 3; b++)
            {
                bool now = inp.GetMouseButton(b);
                if (now == _buttons[b]) continue;
                _buttons[b] = now;
                Send(ValCraftLink.EvMouseButton, b, now ? 1 : 0, mods, 0, 12); // Unity 0/1/2 = GLFW left/right/middle
            }
            Vector2 scroll = inp.mouseScrollDelta;
            if (scroll.x != 0 || scroll.y != 0) SendF(ValCraftLink.EvScroll, scroll.x, scroll.y, 0, 0, 8);
        }

        /// <summary>Gameplay: lock + hide the OS cursor (mouse look). MC screen open: free, visible cursor over the overlay.</summary>
        public void ApplyCursor(bool mcScreenOpen)
        {
            var want = mcScreenOpen ? CursorLockMode.None : CursorLockMode.Locked;
            if (Cursor.lockState != want) Cursor.lockState = want;
            if (Cursor.visible != mcScreenOpen) Cursor.visible = mcScreenOpen;
        }

        void Send(ushort type, int a, int b, int c, int d, int len = 16)
        {
            BitConverter.GetBytes(a).CopyTo(_buf, 0); BitConverter.GetBytes(b).CopyTo(_buf, 4);
            BitConverter.GetBytes(c).CopyTo(_buf, 8); BitConverter.GetBytes(d).CopyTo(_buf, 12);
            _plugin.Link.TrySend(type, _buf, len);
        }

        void SendF(ushort type, float a, float b, float c, float d, int len = 16)
        {
            BitConverter.GetBytes(a).CopyTo(_buf, 0); BitConverter.GetBytes(b).CopyTo(_buf, 4);
            BitConverter.GetBytes(c).CopyTo(_buf, 8); BitConverter.GetBytes(d).CopyTo(_buf, 12);
            _plugin.Link.TrySend(type, _buf, len);
        }

        static List<KeyValuePair<KeyCode, int>> BuildKeyTable()
        {
            var t = new List<KeyValuePair<KeyCode, int>>();
            void Add(KeyCode k, int glfw) => t.Add(new KeyValuePair<KeyCode, int>(k, glfw));
            for (int i = 0; i < 26; i++) Add(KeyCode.A + i, 65 + i);
            for (int i = 0; i < 10; i++) { Add(KeyCode.Alpha0 + i, 48 + i); Add(KeyCode.Keypad0 + i, 320 + i); }
            Add(KeyCode.Space, 32); Add(KeyCode.Quote, 39); Add(KeyCode.Comma, 44); Add(KeyCode.Minus, 45); Add(KeyCode.Period, 46);
            Add(KeyCode.Slash, 47); Add(KeyCode.Semicolon, 59); Add(KeyCode.Equals, 61); Add(KeyCode.LeftBracket, 91);
            Add(KeyCode.Backslash, 92); Add(KeyCode.RightBracket, 93); Add(KeyCode.BackQuote, 96);
            Add(KeyCode.Escape, 256); Add(KeyCode.Return, 257); Add(KeyCode.Tab, 258); Add(KeyCode.Backspace, 259);
            Add(KeyCode.Insert, 260); Add(KeyCode.Delete, 261); Add(KeyCode.RightArrow, 262); Add(KeyCode.LeftArrow, 263);
            Add(KeyCode.DownArrow, 264); Add(KeyCode.UpArrow, 265); Add(KeyCode.PageUp, 266); Add(KeyCode.PageDown, 267);
            Add(KeyCode.Home, 268); Add(KeyCode.End, 269); Add(KeyCode.CapsLock, 280);
            for (int i = 0; i < 12; i++) Add(KeyCode.F1 + i, 290 + i);
            Add(KeyCode.KeypadPeriod, 330); Add(KeyCode.KeypadDivide, 331); Add(KeyCode.KeypadMultiply, 332);
            Add(KeyCode.KeypadMinus, 333); Add(KeyCode.KeypadPlus, 334); Add(KeyCode.KeypadEnter, 335);
            Add(KeyCode.LeftShift, 340); Add(KeyCode.LeftControl, 341); Add(KeyCode.LeftAlt, 342); Add(KeyCode.LeftWindows, 343);
            Add(KeyCode.RightShift, 344); Add(KeyCode.RightControl, 345); Add(KeyCode.RightAlt, 346); Add(KeyCode.RightWindows, 347);
            return t;
        }

        /// <summary>The toggle keys (F7/F8 by default) must never reach Minecraft.</summary>
        public static void ExcludeKeys(params KeyCode[] keys) { Keys.RemoveAll(kv => Array.IndexOf(keys, kv.Key) >= 0); }
    }
}
