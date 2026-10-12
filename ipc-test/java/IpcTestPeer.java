import dev.valcraft.link.ValCraftLink;
import java.nio.ByteBuffer;
import java.nio.file.Path;

/**
 * Minecraft-role IPC peer (protocol v2): acked Hello with reconnect on session change, Ping->Pong,
 * PlayerState writes, frames at the requested size (every byte = frameNo & 0xFF), input event counting, Quit.
 */
public final class IpcTestPeer {
	public static void main(String[] a) throws Exception {
		Path path = a.length > 0 ? Path.of(a[0]) : ValCraftLink.defaultPath();
		long start = System.currentTimeMillis();
		ValCraftLink link = null;
		while (link == null || !link.isReady()) {
			try { if (link == null) link = new ValCraftLink(path); } catch (Exception e) { }
			if (System.currentTimeMillis() - start > 30000) { System.out.println("[java] FAIL: link never ready"); System.exit(1); }
			Thread.sleep(10);
		}
		System.out.println("[java] opened " + path);
		ByteBuffer pixels = ByteBuffer.allocateDirect(ValCraftLink.FRAME_BYTES);
		int collSeen = 0, collGood = 0, collBad = 0;
		int session = 0; boolean acked = false; long nextHello = 0, lastHb = 0, frame = 0, frameNo = 0, lastFrameMs = 0;
		int keys = 0, reconnects = 0;
		while (true) {
			long now = System.currentTimeMillis();
			if (now - lastHb > 100) { link.heartbeat(); lastHb = now; }
			if (!link.isReady()) { acked = false; Thread.sleep(1); continue; }
			int s = link.session();
			int cs = link.collisionSnapshotCount();
			if (cs != collSeen) {
				collSeen = cs;
				float[] b = link.readCollision();
				if (b != null && b.length > 0) {
					boolean ok = b.length % 6 == 0;
					float base = b[0];
					for (int i = 0; ok && i < b.length / 6; i++)
						for (int k = 0; k < 6; k++) if (b[i * 6 + k] != base + i + (k >= 3 ? 1 : 0)) { ok = false; break; }
					if (ok) collGood++; else collBad++;
				}
			}
			if (s != session) { if (session != 0) reconnects++; session = s; acked = false; nextHello = 0; }
			if (!acked && now >= nextHello) {
				link.trySend(ValCraftLink.EV_HELLO, ValCraftLink.ints((int) ProcessHandle.current().pid(), ValCraftLink.VERSION, session));
				nextHello = now + 200;
			}
			frame++;
			link.writePlayerState(new ValCraftLink.PlayerState(frame, frame, frame * 2.0, -frame, (float) (frame % 360), 0f, 0f, 70f,
					ValCraftLink.PS_HAS_PLAYER, 0.25f, -0.5f, 3f, 5f, frame, frame * 2.0 - 1.62, -frame, 1.62f));
			if (now - lastFrameMs >= 8) { // ~120 fps
				lastFrameMs = now;
				int w = link.requestedWidth(), h = link.requestedHeight();
				if (w <= 0 || h <= 0) { w = 640; h = 360; }
				w = Math.min(w, ValCraftLink.FRAME_MAX_W); h = Math.min(h, ValCraftLink.FRAME_MAX_H);
				frameNo++;
				byte v = (byte) frameNo;
				int n = w * h * 4;
				pixels.clear();
				for (int i = 0; i < n; i += 8) pixels.putLong(i, (v & 0xFFL) * 0x0101010101010101L);
				link.publishFrame(frameNo, w, h, pixels.position(0));
			}
			ValCraftLink.Event ev;
			while ((ev = link.tryReceive()) != null) {
				switch (ev.type()) {
					case ValCraftLink.EV_HELLO_ACK -> {
						int ackSession = ev.le().getInt(0);
						if (ackSession == session) { acked = true; link.setAckedSession(session); System.out.println("[java] hello acked, session " + Integer.toHexString(session)); }
					}
					case ValCraftLink.EV_PING -> { while (!link.trySend(ValCraftLink.EV_PONG, ev.payload())) Thread.onSpinWait(); }
					case ValCraftLink.EV_KEY -> keys++;
					case ValCraftLink.EV_RELEASE_ALL -> { link.trySend(ValCraftLink.EV_PONG, ByteBuffer.allocate(8).order(java.nio.ByteOrder.LITTLE_ENDIAN).putLong(-keys).array()); keys = 0; }
					case ValCraftLink.EV_QUIT -> { System.out.println("[java] collision snapshots good=" + collGood + " torn/bad=" + collBad);
						System.out.println("[java] quit; frames " + frameNo + ", reconnects " + reconnects + (collBad == 0 && collGood > 0 ? "; PASS" : "; FAIL")); link.close(); return; }
					default -> { }
				}
			}
			if (now - start > 120000) { System.out.println("[java] FAIL: timeout"); System.exit(1); }
		}
	}
}
