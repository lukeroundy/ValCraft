// "Fake Valheim" host for smoke-testing the real Minecraft client without Valheim:
//   dotnet IpcTest.dll host <shm> <seconds> <cmdFile>
// Acks Hello, requests 1280x720, tracks frames, and executes commands appended to <cmdFile>, one per line:
//   click <nx> <ny> | rclick <nx> <ny> | key <glfw> | hold <glfw> <ms> | type <text> | move <dx> <dy> | scroll <dy>
//   input on|off | dump <file.pam> | size <w> <h> | quit
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ValCraft.Protocol;

static unsafe class FakeValheim
{
    public static int Run(string path, int seconds, string cmdFile)
    {
        var link = new ValCraftLink(path, isHost: true);
        Console.WriteLine($"[host] created {path}, session {link.Session:x8}; commands from {cmdFile}");
        File.WriteAllText(cmdFile, "");
        var sw = Stopwatch.StartNew();
        // 0.13.0 multiplayer simulation (see TunnelTest.cs)
        TunnelCore net = null; byte netRole = 0; int mcPort = 0; long nextRole = 0; float lastHp = -1; string lastHeld = "";
        long maxHbAge = 0; bool terrainFlag = false; var heightQueue = new System.Collections.Generic.Queue<byte[]>();
            var tileQueue = new Queue<byte[]>(); bool connected = false, input = true; uint mcPid = 0; int cmdLine = 0;
        var holes = new System.Collections.Generic.Dictionary<long, float>(); float[] nz = new float[6];
        void QueueNoisy(float ox, float oz, int rr, float by, float amp, float sl, int onlyTx, int onlyTz)
        {
            int T = ValCraftLink.TerrainTile;
            double Hs(double x, double z) => by + sl * Math.Max(0, x - ox) + amp * (Math.Sin(x * 1.7) * Math.Cos(z * 2.3) + 0.6 * Math.Sin(x * 3.1 + z * 0.7));
            double H(double x, double z) { int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z); return holes.TryGetValue(((long)bx << 32) ^ (uint)bz, out float d) ? Math.Floor(Hs(bx + 0.5, bz + 0.5)) - d : Hs(x, z); }
            const int C = ValCraftLink.HeightCell, R = ValCraftLink.HeightRes;
            int ctx = (int)Math.Floor(ox / T), ctz = (int)Math.Floor(oz / T);
            for (int tx = ctx - rr; tx <= ctx + rr; tx++)
                for (int tz = ctz - rr; tz <= ctz + rr; tz++)
                {
                    int TX = onlyTx == int.MinValue ? tx : onlyTx, TZ = onlyTx == int.MinValue ? tz : onlyTz;
                    for (int cx = TX * T / C; cx < (TX + 1) * T / C; cx++)
                        for (int cz = TZ * T / C; cz < (TZ + 1) * T / C; cz++)
                        {
                            var hp = new byte[12 + R * R * 2]; double mn = double.MaxValue;
                            // sample just inside cell edges for hole columns so the hole shows in the bilinear field
                            double S(int i, int j) => H(cx * C + i * 0.5 - (i == R - 1 ? 1e-3 : 0), cz * C + j * 0.5 - (j == R - 1 ? 1e-3 : 0));
                            for (int i = 0; i < R; i++) for (int j = 0; j < R; j++) mn = Math.Min(mn, S(i, j));
                            BitConverter.GetBytes(cx).CopyTo(hp, 0); BitConverter.GetBytes(cz).CopyTo(hp, 4); BitConverter.GetBytes((float)mn).CopyTo(hp, 8);
                            for (int i = 0; i < R; i++) for (int j = 0; j < R; j++) BitConverter.GetBytes((short)Math.Round((S(i, j) - mn) * 100)).CopyTo(hp, 12 + (i * R + j) * 2);
                            heightQueue.Enqueue(hp);
                        }
                    var pl = new byte[8 + T * T * 3];
                    BitConverter.GetBytes(TX).CopyTo(pl, 0); BitConverter.GetBytes(TZ).CopyTo(pl, 4);
                    for (int i = 0; i < T; i++) for (int j = 0; j < T; j++)
                    {
                        int o = 8 + (i * T + j) * 3;
                        short top = (short)(Math.Floor(H(TX * T + i + 0.5, TZ * T + j + 0.5) + 1e-6) - 1);
                        BitConverter.GetBytes(top).CopyTo(pl, o); pl[o + 2] = 1;
                    }
                    tileQueue.Enqueue(pl);
                    if (onlyTx != int.MinValue) return;
                }
        }
        long lastSeq = 0, frames = 0, rejected = 0, waitUntil = 0;
        int reqW = 1280, reqH = 720;
        var pending = new Queue<string>();
        var copy = new byte[ValCraftLink.FrameBytes]; int cw = 0, ch = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            link.Heartbeat();
            link.RequestFrameSize(reqW, reqH);
            link.Flags = ValCraftLink.VFlagInGame | (input ? ValCraftLink.VFlagInputActive : 0) | (terrainFlag ? ValCraftLink.VFlagTerrainBlocks : 0);
            if (connected && link.PeerHeartbeatMs != 0)
            {
                long age = ValCraftLink.NowMs() - link.PeerHeartbeatMs;
                if (age > maxHbAge) { maxHbAge = age; if (age > 1000) Console.WriteLine($"[host] MC heartbeat gap {age} ms at {sw.Elapsed.TotalSeconds:F1}s"); }
            }
            while (link.TryReceive(out var ev))
                if (ev.Type == ValCraftLink.EvBlockChanged)
                    Console.WriteLine($"[host] MC block {(BitConverter.ToInt32(ev.Payload, 12) == 0 ? "BROKEN" : "PLACED")} {System.Text.Encoding.UTF8.GetString(ev.Payload, 16, ev.Payload.Length - 16)} at ({BitConverter.ToInt32(ev.Payload, 0)},{BitConverter.ToInt32(ev.Payload, 4)},{BitConverter.ToInt32(ev.Payload, 8)})");
                else if (ev.Type == ValCraftLink.EvAttack) Console.WriteLine($"[host] MC ATTACK kind {BitConverter.ToInt32(ev.Payload, 0)} tier {BitConverter.ToInt32(ev.Payload, 4)} at {sw.Elapsed.TotalSeconds:F1}s");
                else if (ev.Type == ValCraftLink.EvNetReady) { int pt = ev.Payload[1] | (ev.Payload[2] << 8); if (pt != mcPort) Console.WriteLine($"[host] MC NET READY: host world published on 127.0.0.1:{pt} at {sw.Elapsed.TotalSeconds:F1}s"); mcPort = pt; }
                else if (ev.Type == ValCraftLink.EvHealth) { float hp = BitConverter.ToSingle(ev.Payload, 0); if (hp != lastHp) Console.WriteLine($"[host] MC health {hp} at {sw.Elapsed.TotalSeconds:F1}s"); lastHp = hp; }
                else if (ev.Type == ValCraftLink.EvSleep) Console.WriteLine($"[host] MC sleep {ev.Payload[0]}" + (ev.Payload.Length >= 13 ? $" bed ({BitConverter.ToSingle(ev.Payload, 1):F2},{BitConverter.ToSingle(ev.Payload, 5):F2},{BitConverter.ToSingle(ev.Payload, 9):F2})" : "") + $" at {sw.Elapsed.TotalSeconds:F1}s");
                else if (ev.Type == ValCraftLink.EvMcFires) { int fn = BitConverter.ToUInt16(ev.Payload, 0); var fs = new System.Text.StringBuilder(); for (int fi = 0; fi < fn; fi++) fs.Append($" ({BitConverter.ToInt32(ev.Payload, 2 + fi * 12)},{BitConverter.ToInt32(ev.Payload, 6 + fi * 12)},{BitConverter.ToInt32(ev.Payload, 10 + fi * 12)})"); Console.WriteLine($"[host] MC fires {fn}:{fs} at {sw.Elapsed.TotalSeconds:F1}s"); }
                else if (ev.Type == ValCraftLink.EvBuildAction) Console.WriteLine($"[host] MC build action {ev.Payload[0]} at {sw.Elapsed.TotalSeconds:F1}s");
                else if (ev.Type == ValCraftLink.EvHeldItem) { string h = System.Text.Encoding.UTF8.GetString(ev.Payload, 2, ev.Payload.Length - 2) + " x" + BitConverter.ToUInt16(ev.Payload, 0); if (h != lastHeld) Console.WriteLine($"[host] MC held item {h} at {sw.Elapsed.TotalSeconds:F1}s"); lastHeld = h; }
                else if (ev.Type == ValCraftLink.EvMapToggle) Console.WriteLine($"[host] MC MAP TOGGLE at {sw.Elapsed.TotalSeconds:F1}s");
                else if (ev.Type == ValCraftLink.EvInteract) Console.WriteLine($"[host] MC asked Valheim to INTERACT at {sw.Elapsed.TotalSeconds:F1}s");
                else if (ev.Type == ValCraftLink.EvHello && Payload.GetU32(ev.Payload, 2) == link.Session)
                {
                    mcPid = Payload.GetU32(ev.Payload, 0);
                    link.TrySend(ValCraftLink.EvHelloAck, Payload.U32(link.Session, mcPid));
                    if (!connected) { connected = true; Console.WriteLine($"[host] Minecraft pid {mcPid} connected at {sw.Elapsed.TotalSeconds:F1}s"); }
                }
            if (link.TryGetLatestFrame(lastSeq, out var v))
            {
                fixed (byte* d = copy) Buffer.MemoryCopy(v.Data, d, copy.Length, v.Bytes);
                if (link.FrameStillValid(v)) { lastSeq = v.Seq; frames++; cw = v.Width; ch = v.Height; } else rejected++;
            }
            for (int q = 0; q < 24 && heightQueue.Count > 0; q++) { if (link.TrySend(ValCraftLink.EvTerrainHeights, heightQueue.Peek())) heightQueue.Dequeue(); else break; }
            for (int q = 0; q < 8 && tileQueue.Count > 0; q++) { if (link.TrySend(ValCraftLink.EvTerrainTile, tileQueue.Peek())) tileQueue.Dequeue(); else break; }
            if (net != null) net.Pump();
            if (connected && sw.ElapsedMilliseconds >= nextRole)
            {
                nextRole = sw.ElapsedMilliseconds + 2000;
                int lp = net != null && !net.IsHost ? net.ListenPort : 0; var nm = System.Text.Encoding.UTF8.GetBytes(netRole == 2 ? "Bjorn" : "Astrid");
                var rp = new byte[4 + nm.Length]; rp[0] = netRole; rp[1] = (byte)lp; rp[2] = (byte)(lp >> 8); rp[3] = (byte)nm.Length; nm.CopyTo(rp, 4);
                link.TrySend(ValCraftLink.EvNetRole, rp);
            }
            var lines = File.ReadAllLines(cmdFile);
            for (; cmdLine < lines.Length; cmdLine++) pending.Enqueue(lines[cmdLine]);
            if (sw.ElapsedMilliseconds >= waitUntil && pending.Count > 0)
            {
                var c = pending.Dequeue().Split(new[] { ' ' }, 2);
                var a = c.Length > 1 ? c[1].Split(' ') : new string[0];
                float F(int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
                switch (c[0])
                {
                    case "click": case "rclick":
                        int b = c[0] == "click" ? 0 : 1;
                        link.TrySend(ValCraftLink.EvMouseMove, Payload.F32(0, 0, F(0), F(1)));
                        link.TrySend(ValCraftLink.EvMouseButton, Payload.I32(b, 1, 0));
                        link.TrySend(ValCraftLink.EvMouseButton, Payload.I32(b, 0, 0)); break;
                    case "key": link.TrySend(ValCraftLink.EvKey, Payload.I32((int)F(0), 0, 1, 0)); link.TrySend(ValCraftLink.EvKey, Payload.I32((int)F(0), 0, 0, 0)); break;
                    case "hold": link.TrySend(ValCraftLink.EvKey, Payload.I32((int)F(0), 0, 1, 0)); { var rest = pending.ToArray(); pending.Clear(); pending.Enqueue("release " + a[0]); foreach (var r in rest) pending.Enqueue(r); } waitUntil = sw.ElapsedMilliseconds + (long)F(1); continue;
                    case "release": link.TrySend(ValCraftLink.EvKey, Payload.I32((int)F(0), 0, 0, 0)); break;
                    case "type": foreach (char ch2 in c[1]) link.TrySend(ValCraftLink.EvChar, Payload.I32(ch2, 0)); break;
                    case "move": link.TrySend(ValCraftLink.EvMouseMove, Payload.F32(F(0), F(1), 0.5f, 0.5f)); break;
                    case "world": { var nb = System.Text.Encoding.UTF8.GetBytes(a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : "World"); var wp = new byte[9 + nb.Length]; BitConverter.GetBytes(long.Parse(a[0])).CopyTo(wp, 0); wp[8] = (byte)nb.Length; Array.Copy(nb, 0, wp, 9, nb.Length); Console.WriteLine($"[host] world identity {a[0]} sent={link.TrySend(ValCraftLink.EvWorldInfo, wp)} at {sw.Elapsed.TotalSeconds:F1}s"); break; }
                    case "scroll": link.TrySend(ValCraftLink.EvScroll, Payload.F32(0, F(0))); break;
                    case "input": input = a[0] == "on"; if (!input) link.TrySend(ValCraftLink.EvReleaseAll, null); break;
                    case "size": reqW = (int)F(0); reqH = (int)F(1); break;
                    case "dump": Dump(copy, cw, ch, a[0]); Console.WriteLine($"[host] dump {a[0]} {cw}x{ch} frames={frames} rejected={rejected} mcFlags={link.PeerFlags}"); break;
                    case "wait": waitUntil = sw.ElapsedMilliseconds + (long)F(0); break;
                    case "hb": Console.WriteLine($"[host] max MC heartbeat gap so far: {maxHbAge} ms"); break;
                    case "state":
                        if (link.TryReadPlayerState(out var ps))
                        {
                            CoordMap.ToUnity(ps.FeetX, ps.FeetY, ps.FeetZ, out var ux, out var uy, out var uz);
                            Console.WriteLine($"[host] MC frame {ps.FrameId} eye ({ps.X:F2},{ps.Y:F2},{ps.Z:F2}) feet ({ps.FeetX:F2},{ps.FeetY:F2},{ps.FeetZ:F2}) eyeH {ps.EyeHeight:F2} yaw {ps.Yaw:F1} pitch {ps.Pitch:F1} fov {ps.FovDeg:F1} flags {ps.Flags} bob ({ps.BobX:F3},{ps.BobY:F3},{ps.BobRollDeg:F2},{ps.BobPitchDeg:F2}) -> unity feet ({ux:F2},{uy:F2},{uz:F2}) yaw {CoordMap.YawToUnity(ps.Yaw):F1}");
                        }
                        break;
                    case "coll": // coll <file>: 6 floats per line (MC coords minX minY minZ maxX maxY maxZ); empty file = "no collision"
                        {
                            var vals = new List<float>();
                            foreach (var line in File.ReadAllLines(a[0]))
                                foreach (var tok in line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
                                    vals.Add(float.Parse(tok, System.Globalization.CultureInfo.InvariantCulture));
                            link.PublishCollision(vals.ToArray(), vals.Count / 6);
                            Console.WriteLine($"[host] published {vals.Count / 6} collision boxes");
                            break;
                        }
                    case "hover": link.TrySend(ValCraftLink.EvHoverText, System.Text.Encoding.UTF8.GetBytes(c.Length > 1 ? c[1] : "")); break;
                    case "mbutton": link.TrySend(ValCraftLink.EvMouseButton, Payload.I32((int)F(0), (int)F(1), 0)); break;
                    case "blocksync": link.TrySend(ValCraftLink.EvBlockSyncConfig, Payload.U32(1u, (uint)F(0), 4000u, 64u)); break;
                    case "terrainflag": terrainFlag = a[0] == "on"; break;
                    case "tp": link.TrySend(ValCraftLink.EvTeleport, Payload.F32(F(0), F(1), F(2))); break;
                    case "attacktarget": link.TrySend(ValCraftLink.EvAttackTarget, new[] { (byte)(a[0] == "on" ? 1 : 0) }); break;
                    case "hole": // hole <x> <z> <depth>: lower one column of the last noisy terrain (like a Valheim dig) and re-send its tile
                        holes[((long)(int)F(0) << 32) ^ (uint)(int)F(1)] = F(2);
                        QueueNoisy(nz[0], nz[1], 0, nz[3], nz[4], nz[5], (int)Math.Floor(F(0) / ValCraftLink.TerrainTile), (int)Math.Floor(F(1) / ValCraftLink.TerrainTile));
                        Console.WriteLine($"[host] hole at {F(0)},{F(1)} depth {F(2)}: re-sent tile");
                        break;
                    case "noisy": // noisy <cx> <cz> <radiusTiles> <baseY> <bumpAmp> <slope>: smooth heightfield + small bumps
                        nz = new[] { F(0), F(1), F(2), F(3), F(4), F(5) };
                        QueueNoisy(F(0), F(1), (int)F(2), F(3), F(4), F(5), int.MinValue, 0);
                        Console.WriteLine($"[host] queued {tileQueue.Count} noisy tiles, {heightQueue.Count} height cells");
                        break;
                    case "terrain": // terrain <centerX> <centerZ> <radiusTiles> <baseY>: staircase rising +1 every 4 blocks in +X
                        {
                            int T = ValCraftLink.TerrainTile, ctx = (int)Math.Floor(F(0) / T), ctz = (int)Math.Floor(F(1) / T), rr = (int)F(2), by = (int)F(3);
                            for (int tx = ctx - rr; tx <= ctx + rr; tx++)
                                for (int tz = ctz - rr; tz <= ctz + rr; tz++)
                                {
                                    var pl = new byte[8 + T * T * 3 + 4]; // 0.9.7: + 4 quadrant biome codes (1 = Meadows)
                                    for (int q = 0; q < 4; q++) pl[8 + T * T * 3 + q] = 1;
                                    BitConverter.GetBytes(tx).CopyTo(pl, 0); BitConverter.GetBytes(tz).CopyTo(pl, 4);
                                    for (int i = 0; i < T; i++) for (int j = 0; j < T; j++)
                                    {
                                        int bx = tx * T + i, o = 8 + (i * T + j) * 3;
                                        short top = (short)(by + Math.Max(0, (bx - (int)F(0)) / 4));
                                        BitConverter.GetBytes(top).CopyTo(pl, o); pl[o + 2] = 1;
                                    }
                                    tileQueue.Enqueue(pl);
                                }
                            Console.WriteLine($"[host] queued {tileQueue.Count} terrain tiles");
                            break;
                        }
                    case "weather": // weather <flags> <name>: 0.12.0 EvWeather (1 rain, 2 thunder, 4 snow)
                    {
                        var wn = System.Text.Encoding.UTF8.GetBytes(a.Length > 1 ? a[1] : "Rain"); var wp = new byte[2 + wn.Length]; wp[0] = (byte)F(0); wp[1] = (byte)wn.Length; Array.Copy(wn, 0, wp, 2, wn.Length);
                        Console.WriteLine($"[host] weather sent={link.TrySend(ValCraftLink.EvWeather, wp)}"); break;
                    }
                    case "lore": // lore <text>: 0.11.0 EvLoreText ("|" = newline), split in 2 chunks
                    {
                        var lb = System.Text.Encoding.UTF8.GetBytes((c.Length > 1 ? c[1] : "Rune\nText").Replace('|', '\n')); int h = lb.Length / 2;
                        var p1 = new byte[1 + h]; p1[0] = 1; Array.Copy(lb, 0, p1, 1, h); var p2 = new byte[1 + lb.Length - h]; p2[0] = 2; Array.Copy(lb, h, p2, 1, lb.Length - h);
                        Console.WriteLine($"[host] lore sent={link.TrySend(ValCraftLink.EvLoreText, p1) && link.TrySend(ValCraftLink.EvLoreText, p2)}"); break;
                    }
                    case "dims": link.TrySend(ValCraftLink.EvPlayerDims, Payload.F32(F(0), F(1))); break;
                    case "nethost": { var t = TcpTransport.Host(int.Parse(a[0])); net = new TunnelCore(true, t) { Target = () => mcPort > 0 ? new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, mcPort) : null, Log = Console.WriteLine }; t.OnMessage = net.OnMessage; netRole = 1; nextRole = 0; Console.WriteLine($"[host] simulated Valheim server on sim port {a[0]}"); break; }
                    case "netclient": { var t = TcpTransport.Client(int.Parse(a[0])); net = new TunnelCore(false, t) { ServerPeer = 0, Log = Console.WriteLine }; t.OnMessage = net.OnMessage; net.StartListener(); netRole = 2; nextRole = 0; Console.WriteLine($"[host] simulated Valheim client, tunnel port {net.ListenPort}"); break; }
                    case "netleave": { net?.Dispose(); net = null; netRole = 0; nextRole = 0; Console.WriteLine("[host] left the simulated Valheim server"); break; }
                    case "netstat": Console.WriteLine($"[host] tunnel: {(net == null ? "none" : $"{net.StreamCount} connection(s), {net.BytesUp / 1024} KB up, {net.BytesDown / 1024} KB down")}"); break;
                    case "give": { var nb = System.Text.Encoding.UTF8.GetBytes(a[1]); var gp = new byte[8 + nb.Length]; BitConverter.GetBytes(7u).CopyTo(gp, 0); BitConverter.GetBytes((ushort)int.Parse(a[0])).CopyTo(gp, 4); BitConverter.GetBytes((ushort)0xFFFF).CopyTo(gp, 6); nb.CopyTo(gp, 8); Console.WriteLine($"[host] give {a[0]} {a[1]} sent={link.TrySend(ValCraftLink.EvGiveItem, gp)}"); break; }
                    case "damage": { var dp = new byte[17]; BitConverter.GetBytes(F(0)).CopyTo(dp, 0); dp[4] = 0; Console.WriteLine($"[host] damage {a[0]} sent={link.TrySend(ValCraftLink.EvDamage, dp)}"); break; }
                    case "sleep": { var sp = new byte[1 + (a.Length > 1 ? 1 + c[1].Length * 3 : 0)]; sp[0] = (byte)int.Parse(a[0]); int sl = 1; if (a.Length > 1) sl += System.Text.Encoding.UTF8.GetBytes(string.Join(" ", a, 1, a.Length - 1), 0, string.Join(" ", a, 1, a.Length - 1).Length, sp, 1); Console.WriteLine($"[host] sleep {a[0]} sent={link.TrySend(ValCraftLink.EvSleep, sp.AsSpan(0, sl).ToArray())}"); break; }
                    case "spawn": Console.WriteLine($"[host] spawn sent={link.TrySend(ValCraftLink.EvSpawn, Payload.F32(F(0), F(1), F(2)))}"); break;
                    case "day": link.TrySend(ValCraftLink.EvDayTime, Payload.F32(F(0))); break;
                    case "buildstate": link.TrySend(ValCraftLink.EvBuildState, new[] { (byte)int.Parse(a[0]) }); break;
                    case "keydown": link.TrySend(ValCraftLink.EvKey, Payload.I32((int)F(0), 0, 1, 0)); break;
                    case "quit": goto done;
                }
                Console.WriteLine($"[host] {string.Join(" ", c)}");
            }
            Thread.Sleep(5);
        }
        done:
        Console.WriteLine($"[host] frames received {frames}, torn rejected {rejected}, connected={connected}");
        link.SetFlag(ValCraftLink.VFlagQuitting, true);
        link.TrySend(ValCraftLink.EvQuit, null);
        bool exited = false;
        var qs = Stopwatch.StartNew();
        if (mcPid != 0) try { exited = Process.GetProcessById((int)mcPid).WaitForExit(30000); } catch (ArgumentException) { exited = true; }
        Console.WriteLine($"[host] sent Quit; Minecraft exited: {exited} after {qs.ElapsedMilliseconds} ms");
        return connected && frames > 0 ? 0 : 1;
    }

    static void Dump(byte[] rgbaBottomUp, int w, int h, string file)
    {
        if (w == 0) return;
        using var fs = File.Create(file);
        var hdr = System.Text.Encoding.ASCII.GetBytes($"P7\nWIDTH {w}\nHEIGHT {h}\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n");
        fs.Write(hdr, 0, hdr.Length);
        for (int y = h - 1; y >= 0; y--) fs.Write(rgbaBottomUp, y * w * 4, w * 4);
    }
}
