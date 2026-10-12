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
        long maxHbAge = 0; bool terrainFlag = false; var heightQueue = new System.Collections.Generic.Queue<byte[]>();
            var tileQueue = new Queue<byte[]>(); bool connected = false, input = true; uint mcPid = 0; int cmdLine = 0;
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
                    case "hold": link.TrySend(ValCraftLink.EvKey, Payload.I32((int)F(0), 0, 1, 0)); pending.Enqueue("release " + a[0]); waitUntil = sw.ElapsedMilliseconds + (long)F(1); continue;
                    case "release": link.TrySend(ValCraftLink.EvKey, Payload.I32((int)F(0), 0, 0, 0)); break;
                    case "type": foreach (char ch2 in c[1]) link.TrySend(ValCraftLink.EvChar, Payload.I32(ch2, 0)); break;
                    case "move": link.TrySend(ValCraftLink.EvMouseMove, Payload.F32(F(0), F(1), 0.5f, 0.5f)); break;
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
                    case "terrainflag": terrainFlag = a[0] == "on"; break;
                    case "tp": link.TrySend(ValCraftLink.EvTeleport, Payload.F32(F(0), F(1), F(2))); break;
                    case "noisy": // noisy <cx> <cz> <radiusTiles> <baseY> <bumpAmp> <slope>: smooth heightfield + small bumps
                        {
                            int T = ValCraftLink.TerrainTile, ctx = (int)Math.Floor(F(0) / T), ctz = (int)Math.Floor(F(1) / T), rr = (int)F(2);
                            float by = F(3), amp = F(4), sl = F(5), ox = F(0);
                            Func<double, double, double> H = (x, z) => by + sl * Math.Max(0, x - ox) + amp * (Math.Sin(x * 1.7) * Math.Cos(z * 2.3) + 0.6 * Math.Sin(x * 3.1 + z * 0.7));
                            const int C = ValCraftLink.HeightCell, R = ValCraftLink.HeightRes;
                            for (int tx = ctx - rr; tx <= ctx + rr; tx++)
                                for (int tz = ctz - rr; tz <= ctz + rr; tz++)
                                {
                                    for (int cx = tx * T / C; cx < (tx + 1) * T / C; cx++)
                                        for (int cz = tz * T / C; cz < (tz + 1) * T / C; cz++)
                                        {
                                            var hp = new byte[12 + R * R * 2]; double mn = double.MaxValue;
                                            for (int i = 0; i < R; i++) for (int j = 0; j < R; j++) mn = Math.Min(mn, H(cx * C + i * 0.5, cz * C + j * 0.5));
                                            BitConverter.GetBytes(cx).CopyTo(hp, 0); BitConverter.GetBytes(cz).CopyTo(hp, 4); BitConverter.GetBytes((float)mn).CopyTo(hp, 8);
                                            for (int i = 0; i < R; i++) for (int j = 0; j < R; j++) BitConverter.GetBytes((short)Math.Round((H(cx * C + i * 0.5, cz * C + j * 0.5) - mn) * 100)).CopyTo(hp, 12 + (i * R + j) * 2);
                                            heightQueue.Enqueue(hp);
                                        }
                                    var pl = new byte[8 + T * T * 3];
                                    BitConverter.GetBytes(tx).CopyTo(pl, 0); BitConverter.GetBytes(tz).CopyTo(pl, 4);
                                    for (int i = 0; i < T; i++) for (int j = 0; j < T; j++)
                                    {
                                        int o = 8 + (i * T + j) * 3;
                                        short top = (short)(Math.Floor(H(tx * T + i + 0.5, tz * T + j + 0.5)) - 1);
                                        BitConverter.GetBytes(top).CopyTo(pl, o); pl[o + 2] = 1;
                                    }
                                    tileQueue.Enqueue(pl);
                                }
                            Console.WriteLine($"[host] queued {tileQueue.Count} noisy tiles, {heightQueue.Count} height cells");
                            break;
                        }
                    case "terrain": // terrain <centerX> <centerZ> <radiusTiles> <baseY>: staircase rising +1 every 4 blocks in +X
                        {
                            int T = ValCraftLink.TerrainTile, ctx = (int)Math.Floor(F(0) / T), ctz = (int)Math.Floor(F(1) / T), rr = (int)F(2), by = (int)F(3);
                            for (int tx = ctx - rr; tx <= ctx + rr; tx++)
                                for (int tz = ctz - rr; tz <= ctz + rr; tz++)
                                {
                                    var pl = new byte[8 + T * T * 3];
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
                    case "dims": link.TrySend(ValCraftLink.EvPlayerDims, Payload.F32(F(0), F(1))); break;
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
