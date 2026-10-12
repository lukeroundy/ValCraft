import dev.valcraft.link.ValCraftLink;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.file.Path;

/** Minecraft-role IPC peer: heartbeats, answers Ping with Pong, hammers PlayerState writes until Quit. */
public final class IpcTestPeer {
	public static void main(String[] a) throws Exception {
		Path path = a.length > 0 ? Path.of(a[0]) : ValCraftLink.defaultPath();
		ValCraftLink link = null;
		long start = System.currentTimeMillis();
		while (link == null || !link.isReady()) {
			try { if (link == null) link = new ValCraftLink(path); } catch (Exception e) { }
			if (System.currentTimeMillis() - start > 30000) { System.out.println("[java] FAIL: link never ready"); System.exit(1); }
			Thread.sleep(10);
		}
		System.out.println("[java] opened " + path + ", host pid " + link.peerPid());
		link.trySend(ValCraftLink.EV_HELLO, ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt((int) ProcessHandle.current().pid()).array());
		long frame = 0, pongs = 0, lastHb = 0;
		while (true) {
			long now = System.currentTimeMillis();
			if (now - lastHb > 100) { link.heartbeat(); lastHb = now; }
			frame++;
			link.writePlayerState(new ValCraftLink.PlayerState(frame, frame, frame * 2.0, -frame, (float) (frame % 360), 0f, 0f, 70f, 0));
			ValCraftLink.Event ev;
			while ((ev = link.tryReceive()) != null) {
				if (ev.type() == 100) { System.out.println("[java] quit after " + pongs + " pongs, " + frame + " state writes; PASS"); link.close(); return; }
				if (ev.type() == ValCraftLink.EV_PING) { while (!link.trySend(ValCraftLink.EV_PONG, ev.payload())) Thread.onSpinWait(); pongs++; }
			}
			if (now - start > 90000) { System.out.println("[java] FAIL: timeout"); System.exit(1); }
		}
	}
}
