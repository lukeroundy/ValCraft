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
        long maxHbAge = 0; bool connected = false, input = true; uint mcPid = 0; int cmdLine = 0;
        long lastSeq = 0, frames = 0, rejected = 0, waitUntil = 0;
        int reqW = 1280, reqH = 720;
        var pending = new Queue<string>();
        var copy = new byte[ValCraftLink.FrameBytes]; int cw = 0, ch = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            link.Heartbeat();
            link.RequestFrameSize(reqW, reqH);
            link.Flags = ValCraftLink.VFlagInGame | (input ? ValCraftLink.VFlagInputActive : 0);
            if (connected && link.PeerHeartbeatMs != 0)
            {
                long age = ValCraftLink.NowMs() - link.PeerHeartbeatMs;
                if (age > maxHbAge) { maxHbAge = age; if (age > 1000) Console.WriteLine($"[host] MC heartbeat gap {age} ms at {sw.Elapsed.TotalSeconds:F1}s"); }
            }
            while (link.TryReceive(out var ev))
                if (ev.Type == ValCraftLink.EvHello && Payload.GetU32(ev.Payload, 2) == link.Session)
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
