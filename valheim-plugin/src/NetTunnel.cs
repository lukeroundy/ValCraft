using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// 0.13.0 multiplayer: Minecraft rides on Valheim's connection. The Valheim server host runs the MC world (its MC
    /// client publishes the integrated server on a loopback-only LAN port); every joined Valheim client opens a local
    /// TCP listener that its MC client connects to, and the bytes travel over Valheim routed RPC "ValCraft_MC".
    /// </summary>
    internal static class NetTunnel
    {
        internal const string Rpc = "ValCraft_MC";
        internal const byte RoleSingle = 0, RoleHost = 1, RoleClient = 2, RoleDedicated = 3;
        internal static bool Enabled = true;
        internal static string DedicatedTarget = "127.0.0.1:25565";
        internal static int MaxSendQueue = 256 * 1024;
        internal static byte Role { get; private set; }
        internal static bool Multiplayer => Role == RoleHost || Role == RoleClient || Role == RoleDedicated;
        static int _mcPort;            // host: MC's published port (from EvNetReady)
        static TunnelCore _core;
        static object _rpcInst;        // ZRoutedRpc instance of the current session
        static object _registeredOn;   // Register throws on a duplicate name: once per instance
        static float _nextRole, _nextRoleSend, _nextStats, _nextPeerCheck;
        static byte _sentRole = 255; static int _sentPort = -1;

        static bool _init; static Type _znetT, _rpcT, _pkgT, _peerT, _sockT;
        static PropertyInfo _znetInst, _rpcInstP; static FieldInfo _openServer, _peerSocket, _peerUid, _peerName;
        static MethodInfo _isServer, _isDedicated, _getServerPeer, _getPeer, _getPeers, _getServerPeerId, _invoke, _register, _pkgWrite, _pkgRead, _queueSize;

        static void Init()
        {
            _init = true;
            _znetT = Refl.T("ZNet"); _rpcT = Refl.T("ZRoutedRpc"); _pkgT = Refl.T("ZPackage"); _peerT = Refl.T("ZNetPeer"); _sockT = Refl.T("ISocket");
            if (_znetT == null || _rpcT == null || _pkgT == null) { ValCraftPlugin.Log.LogWarning("[net] Valheim network types not found: multiplayer tunnel off"); Enabled = false; return; }
            _znetInst = AccessTools.Property(_znetT, "instance"); _openServer = AccessTools.Field(_znetT, "m_openServer");
            _isServer = AccessTools.Method(_znetT, "IsServer"); _isDedicated = AccessTools.Method(_znetT, "IsDedicated");
            _getServerPeer = AccessTools.Method(_znetT, "GetServerPeer"); _getPeer = AccessTools.Method(_znetT, "GetPeer", new[] { typeof(long) }); _getPeers = AccessTools.Method(_znetT, "GetPeers");
            _rpcInstP = AccessTools.Property(_rpcT, "instance"); _getServerPeerId = AccessTools.Method(_rpcT, "GetServerPeerID");
            _invoke = AccessTools.Method(_rpcT, "InvokeRoutedRPC", new[] { typeof(long), typeof(string), typeof(object[]) });
            foreach (var m in _rpcT.GetMethods()) if (m.Name == "Register" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1) _register = m.MakeGenericMethod(_pkgT);
            _pkgWrite = AccessTools.Method(_pkgT, "Write", new[] { typeof(byte[]) }); _pkgRead = AccessTools.Method(_pkgT, "ReadByteArray", Type.EmptyTypes);
            _peerSocket = AccessTools.Field(_peerT, "m_socket"); _peerUid = AccessTools.Field(_peerT, "m_uid"); _peerName = AccessTools.Field(_peerT, "m_playerName");
            _queueSize = _sockT == null ? null : AccessTools.Method(_sockT, "GetSendQueueSize");
            bool ok = _isServer != null && _invoke != null && _register != null && _pkgWrite != null && _pkgRead != null && _getServerPeerId != null;
            if (!ok) { Enabled = false; ValCraftPlugin.Log.LogWarning("[net] Valheim RPC API incomplete: multiplayer tunnel off"); }
            else ValCraftPlugin.Log.LogInfo("[net] multiplayer tunnel ready (RPC " + Rpc + ")");
        }

        /// <summary>Generic RPC target so Register&lt;ZPackage&gt; gets an Action&lt;long, ZPackage&gt; without compile-time Valheim types.</summary>
        static void OnRpc<T>(long sender, T pkg)
        {
            try { var b = (byte[])_pkgRead.Invoke(pkg, null); _core?.OnMessage(sender, b); }
            catch (Exception e) { Diag.Warn("netrpc", "[net] bad tunnel message: " + e.GetBaseException().Message); }
        }

        sealed class RpcTransport : ITunnelTransport
        {
            public bool CanSend(long peer)
            {
                try
                {
                    var zn = _znetInst.GetValue(null); if (zn == null) return false;
                    object p = Role == RoleClient ? _getServerPeer?.Invoke(zn, null) : _getPeer?.Invoke(zn, new object[] { peer });
                    if (p == null) return Role != RoleClient; // unknown peer: let it fail fast (dropped below)
                    var s = _peerSocket?.GetValue(p); if (s == null || _queueSize == null) return true;
                    return (int)_queueSize.Invoke(s, null) < MaxSendQueue;
                }
                catch { return true; }
            }
            public void Send(long peer, byte[] msg)
            {
                var rpc = _rpcInstP.GetValue(null); if (rpc == null) return;
                var pkg = Activator.CreateInstance(_pkgT); _pkgWrite.Invoke(pkg, new object[] { msg });
                _invoke.Invoke(rpc, new object[] { peer, Rpc, new object[] { pkg } });
            }
        }

        static byte DetectRole(object zn)
        {
            if (zn == null) return RoleSingle;
            bool server = (bool)_isServer.Invoke(zn, null);
            if (!server) return _getServerPeer?.Invoke(zn, null) != null ? RoleClient : RoleSingle;
            if (_isDedicated != null && (bool)_isDedicated.Invoke(zn, null)) return RoleDedicated;
            bool open = _openServer == null || (bool)_openServer.GetValue(null);
            return open ? RoleHost : RoleSingle;
        }

        internal static void Update(ValCraftLink link, bool connected)
        {
            if (!_init) Init();
            if (!Enabled) return;
            float t = Time.unscaledTime;
            if (t >= _nextRole)
            {
                _nextRole = t + 1f;
                object zn = null; try { zn = _znetInst.GetValue(null); } catch { }
                byte role = RoleSingle; try { role = DetectRole(zn); } catch { }
                var rpc = _rpcInstP.GetValue(null);
                if (role != Role || (role != RoleSingle && rpc != _rpcInst)) SetRole(role, rpc);
                if (_core != null && !_core.IsHost) { try { _core.ServerPeer = (long)_getServerPeerId.Invoke(rpc, null); } catch { } }
            }
            if (_core != null)
            {
                try { _core.Pump(); } catch (Exception e) { Diag.Warn("netpump", "[net] pump: " + e.GetBaseException().Message); }
                if (_core.IsHost && t >= _nextPeerCheck) { _nextPeerCheck = t + 2f; DropGonePeers(); }
                if (t >= _nextStats) { _nextStats = t + 60f; if (_core.StreamCount > 0 || _core.BytesUp + _core.BytesDown > 0) ValCraftPlugin.Log.LogInfo($"[net] tunnel: {_core.StreamCount} connection(s), {_core.BytesUp / 1024} KB up, {_core.BytesDown / 1024} KB down"); }
            }
            // tell MC its role (and the tunnel port) on change and every 3 s
            if (link != null && connected && Role != RoleDedicated)
            {
                int port = Role == RoleClient && _core != null ? _core.ListenPort : 0;
                if (Role != _sentRole || port != _sentPort || t >= _nextRoleSend)
                {
                    var name = System.Text.Encoding.UTF8.GetBytes(LocalName() ?? "");
                    int n = Math.Min(name.Length, 32); var p = new byte[4 + n];
                    p[0] = Role; p[1] = (byte)port; p[2] = (byte)(port >> 8); p[3] = (byte)n; Array.Copy(name, 0, p, 4, n);
                    if (link.TrySend(ValCraftLink.EvNetRole, p)) { if (Role != _sentRole || port != _sentPort) ValCraftPlugin.Log.LogInfo($"[net] told Minecraft: role {RoleName(Role)}{(port > 0 ? ", tunnel port " + port : "")}"); _sentRole = Role; _sentPort = port; _nextRoleSend = t + 3f; }
                }
            }
            else if (!connected) _sentRole = 255;
        }

        static string LocalName() { try { var lp = Refl.T("Player")?.GetField("m_localPlayer")?.GetValue(null); return lp == null ? null : AccessTools.Method(lp.GetType(), "GetPlayerName")?.Invoke(lp, null) as string; } catch { return null; } }
        internal static string RoleName(byte r) => r == RoleHost ? "host" : r == RoleClient ? "client" : r == RoleDedicated ? "dedicated server" : "single player";

        static void SetRole(byte role, object rpc)
        {
            ValCraftPlugin.Log.LogInfo($"[net] Valheim session: {RoleName(role)}{(role != Role ? " (was " + RoleName(Role) + ")" : " (new RPC instance)")}");
            _core?.Dispose(); _core = null; _rpcInst = null;
            if ((role == RoleSingle) != (Role == RoleSingle)) PlayerSync.ReAnchor(); // multiplayer uses the shared fixed anchor
            Role = role;
            if (role == RoleSingle || rpc == null) return;
            try
            {
                var del = Delegate.CreateDelegate(typeof(Action<,>).MakeGenericType(typeof(long), _pkgT), typeof(NetTunnel).GetMethod(nameof(OnRpc), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(_pkgT));
                if (rpc != _registeredOn) { _register.Invoke(rpc, new object[] { Rpc, del }); _registeredOn = rpc; }
                _rpcInst = rpc;
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[net] RPC register failed: " + e.GetBaseException().Message); return; }
            bool host = role == RoleHost || role == RoleDedicated;
            _core = new TunnelCore(host, new RpcTransport()) { Log = s => ValCraftPlugin.Log.LogInfo(s) };
            if (host) _core.Target = Target;
            else { _core.ServerPeer = (long)_getServerPeerId.Invoke(rpc, null); _core.StartListener(); }
        }

        static IPEndPoint Target()
        {
            if (Role == RoleDedicated)
            {
                var s = DedicatedTarget.Split(':');
                try { var addr = Dns.GetHostAddresses(s[0])[0]; return new IPEndPoint(addr, s.Length > 1 ? int.Parse(s[1]) : 25565); } catch { return null; }
            }
            return _mcPort > 0 ? new IPEndPoint(IPAddress.Loopback, _mcPort) : null;
        }

        /// <summary>EvNetReady from our MC: u8 role, u16 published port.</summary>
        internal static void Ready(byte[] p)
        {
            if (p == null || p.Length < 3) return;
            int port = p[1] | (p[2] << 8);
            if (port != _mcPort) ValCraftPlugin.Log.LogInfo($"[net] host Minecraft serves this world on 127.0.0.1:{port}; Valheim players' Minecraft joins through the tunnel");
            _mcPort = port;
        }

        static readonly HashSet<long> _known = new HashSet<long>();
        static void DropGonePeers()
        {
            try
            {
                var zn = _znetInst.GetValue(null); if (zn == null || _getPeers == null) return;
                var now = new HashSet<long>();
                foreach (var p in (System.Collections.IEnumerable)_getPeers.Invoke(zn, null)) now.Add((long)_peerUid.GetValue(p));
                foreach (var id in _known) if (!now.Contains(id)) { ValCraftPlugin.Log.LogInfo($"[net] Valheim peer {id} left: closing its Minecraft connection"); _core.DropPeer(id); }
                _known.Clear(); foreach (var id in now) _known.Add(id);
            }
            catch { }
        }

        static MethodInfo _allPlayers; static FieldInfo _localPlayer;
        /// <summary>Multiplayer authority for per-area mirrors: true if no other Valheim player is closer to pos than we are
        /// (minus a hysteresis bias for things we already own). Always true in single player.</summary>
        internal static bool Owns(Vector3 pos, float bias)
        {
            if (!Multiplayer) return true;
            try
            {
                var pt = Refl.T("Player");
                if (_allPlayers == null) { _allPlayers = AccessTools.Method(pt, "GetAllPlayers"); _localPlayer = AccessTools.Field(pt, "m_localPlayer"); }
                var me = _localPlayer.GetValue(null) as Component; if (me == null) return false;
                float mine = (me.transform.position - pos).magnitude - bias;
                foreach (Component o in (System.Collections.IEnumerable)_allPlayers.Invoke(null, null))
                    if (o != null && o != me && (o.transform.position - pos).magnitude < mine) return false;
                return true;
            }
            catch { return true; }
        }

        internal static void Shutdown() { _core?.Dispose(); _core = null; }
    }
}
