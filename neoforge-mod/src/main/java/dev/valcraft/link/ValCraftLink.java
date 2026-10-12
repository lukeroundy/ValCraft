package dev.valcraft.link;

import java.io.IOException;
import java.lang.invoke.MethodHandles;
import java.lang.invoke.VarHandle;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.MappedByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardOpenOption;

/**
 * ValCraft shared-memory link, Minecraft side (client of the file created by the Valheim plugin).
 * Layout: see protocol/PROTOCOL.md. Pure JDK (FileChannel.map + VarHandles), no Minecraft imports,
 * so the standalone IPC test compiles this exact file.
 */
public final class ValCraftLink implements AutoCloseable {
	public static final int MAGIC = 0x434C4156, VERSION = 1;
	static final int OFF_MAGIC = 0x0, OFF_VERSION = 0x4, OFF_VALHEIM_PID = 0x10, OFF_MC_PID = 0x14;
	static final int OFF_VALHEIM_HB = 0x18, OFF_MC_HB = 0x20, OFF_VALHEIM_FLAGS = 0x28, OFF_MC_FLAGS = 0x2C;
	static final int OFF_PLAYER_STATE = 0x100;
	static final int RING_SLOTS = 256, SLOT_SIZE = 256, SLOT_PAYLOAD = SLOT_SIZE - 8, RING_HEADER = 0x80;
	static final int RING_BYTES = RING_HEADER + RING_SLOTS * SLOT_SIZE;
	static final int OFF_RING_V2M = 0x1000, OFF_RING_M2V = OFF_RING_V2M + RING_BYTES;
	public static final long FILE_SIZE = 0x22100;

	public static final short EV_HELLO = 1, EV_PING = 2, EV_PONG = 3;

	private static final VarHandle I32 = MethodHandles.byteBufferViewVarHandle(int[].class, ByteOrder.LITTLE_ENDIAN);
	private static final VarHandle I64 = MethodHandles.byteBufferViewVarHandle(long[].class, ByteOrder.LITTLE_ENDIAN);

	public record PlayerState(long frameId, double x, double y, double z, float yaw, float pitch, float roll, float fovDeg, int flags) {}
	public record Event(short type, byte[] payload) {}

	private final FileChannel channel;
	private final MappedByteBuffer buf;

	public static Path defaultPath() {
		String env = System.getenv("VALCRAFT_SHM");
		if (env != null && !env.isEmpty()) return Paths.get(env);
		String local = System.getenv("LOCALAPPDATA");
		if (local == null) local = System.getProperty("user.home");
		return Paths.get(local, "ValCraft", "valcraft.shm");
	}

	/** Opens the link; throws if the Valheim side has not created/initialised it yet. */
	public ValCraftLink(Path path) throws IOException {
		if (!Files.exists(path) || Files.size(path) < FILE_SIZE) throw new IOException("ValCraft link file not ready: " + path);
		channel = FileChannel.open(path, StandardOpenOption.READ, StandardOpenOption.WRITE);
		buf = channel.map(FileChannel.MapMode.READ_WRITE, 0, FILE_SIZE);
		buf.order(ByteOrder.LITTLE_ENDIAN);
	}

	public boolean isReady() {
		return (int) I32.getAcquire(buf, OFF_MAGIC) == MAGIC && (int) I32.getAcquire(buf, OFF_VERSION) == VERSION;
	}

	public static long nowMs() { return System.currentTimeMillis(); }

	public void heartbeat() {
		I32.setRelease(buf, OFF_MC_PID, (int) ProcessHandle.current().pid());
		I64.setRelease(buf, OFF_MC_HB, nowMs());
	}
	public long peerHeartbeatMs() { return (long) I64.getAcquire(buf, OFF_VALHEIM_HB); }
	public boolean peerAlive(long timeoutMs) { long hb = peerHeartbeatMs(); return hb != 0 && nowMs() - hb < timeoutMs; }
	public int peerPid() { return (int) I32.getAcquire(buf, OFF_VALHEIM_PID); }
	public int peerFlags() { return (int) I32.getAcquire(buf, OFF_VALHEIM_FLAGS); }
	public void setFlags(int f) { I32.setRelease(buf, OFF_MC_FLAGS, f); }

	// ---- seqlock PlayerState (MC is the writer) ----
	public void writePlayerState(PlayerState s) {
		int b = OFF_PLAYER_STATE;
		int seq = (int) I32.getOpaque(buf, b);
		I32.setOpaque(buf, b, seq + 1);
		VarHandle.fullFence();
		buf.putLong(b + 8, s.frameId());
		buf.putDouble(b + 16, s.x()); buf.putDouble(b + 24, s.y()); buf.putDouble(b + 32, s.z());
		buf.putFloat(b + 40, s.yaw()); buf.putFloat(b + 44, s.pitch()); buf.putFloat(b + 48, s.roll()); buf.putFloat(b + 52, s.fovDeg());
		buf.putInt(b + 56, s.flags());
		I32.setRelease(buf, b, seq + 2);
	}

	public PlayerState readPlayerState() {
		int b = OFF_PLAYER_STATE;
		for (int i = 0; i < 64; i++) {
			int s1 = (int) I32.getAcquire(buf, b);
			if ((s1 & 1) != 0) { Thread.onSpinWait(); continue; }
			PlayerState s = new PlayerState(buf.getLong(b + 8), buf.getDouble(b + 16), buf.getDouble(b + 24), buf.getDouble(b + 32),
					buf.getFloat(b + 40), buf.getFloat(b + 44), buf.getFloat(b + 48), buf.getFloat(b + 52), buf.getInt(b + 56));
			VarHandle.acquireFence();
			if ((int) I32.getAcquire(buf, b) == s1) return s1 == 0 ? null : s;
		}
		return null;
	}

	// ---- SPSC rings: MC sends on M2V, receives on V2M ----
	public boolean trySend(short type, byte[] payload) {
		int len = payload == null ? 0 : payload.length;
		if (len > SLOT_PAYLOAD) throw new IllegalArgumentException("payload too large");
		int r = OFF_RING_M2V;
		long head = (long) I64.getOpaque(buf, r);
		long tail = (long) I64.getAcquire(buf, r + 0x40);
		if (head - tail >= RING_SLOTS) return false;
		int slot = r + RING_HEADER + (int) (head % RING_SLOTS) * SLOT_SIZE;
		buf.putShort(slot, type); buf.putShort(slot + 2, (short) 0); buf.putInt(slot + 4, len);
		for (int i = 0; i < len; i++) buf.put(slot + 8 + i, payload[i]);
		I64.setRelease(buf, r, head + 1);
		return true;
	}

	public Event tryReceive() {
		int r = OFF_RING_V2M;
		long tail = (long) I64.getOpaque(buf, r + 0x40);
		long head = (long) I64.getAcquire(buf, r);
		if (tail == head) return null;
		int slot = r + RING_HEADER + (int) (tail % RING_SLOTS) * SLOT_SIZE;
		int len = Math.min(buf.getInt(slot + 4), SLOT_PAYLOAD);
		byte[] data = new byte[len];
		for (int i = 0; i < len; i++) data[i] = buf.get(slot + 8 + i);
		Event ev = new Event(buf.getShort(slot), data);
		I64.setRelease(buf, r + 0x40, tail + 1);
		return ev;
	}

	@Override public void close() throws IOException { channel.close(); }
}
