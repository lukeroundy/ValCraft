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
 * ValCraft shared-memory link, protocol v2, Minecraft side (client of the file created by the Valheim plugin).
 * Layout: protocol/PROTOCOL.md. Pure JDK (FileChannel.map + VarHandles), no Minecraft imports, so the
 * standalone IPC test compiles this exact file.
 */
public final class ValCraftLink implements AutoCloseable {
	public static final int MAGIC = 0x434C4156, VERSION = 4;
	static final int OFF_MAGIC = 0x0, OFF_VERSION = 0x4, OFF_VALHEIM_PID = 0x10, OFF_MC_PID = 0x14;
	static final int OFF_VALHEIM_HB = 0x18, OFF_MC_HB = 0x20, OFF_VALHEIM_FLAGS = 0x28, OFF_MC_FLAGS = 0x2C;
	static final int OFF_SESSION = 0x30, OFF_MC_ACKED_SESSION = 0x34;
	static final int OFF_PLAYER_STATE = 0x100;
	static final int OFF_FRAME_REQ_W = 0x200, OFF_FRAME_REQ_H = 0x204, OFF_FRAME_MAX_W = 0x208, OFF_FRAME_MAX_H = 0x20C;
	static final int OFF_FRAME_LATEST_SEQ = 0x210, OFF_FRAME_LATEST_INDEX = 0x218, OFF_FRAME_BUF_HDR = 0x240, FRAME_BUF_HDR_SIZE = 0x40;
	public static final int FRAME_MAX_W = 1920, FRAME_MAX_H = 1080, FRAME_FORMAT_RGBA8_BOTTOM_UP = 1;
	static final int RING_SLOTS = 256, SLOT_SIZE = 256, SLOT_PAYLOAD = SLOT_SIZE - 8, RING_HEADER = 0x80;
	static final int RING_BYTES = RING_HEADER + RING_SLOTS * SLOT_SIZE;
	static final int OFF_RING_V2M = 0x1000, OFF_RING_M2V = OFF_RING_V2M + RING_BYTES;
	static final int OFF_FRAME_DATA = 0x22000;
	public static final int FRAME_BYTES = FRAME_MAX_W * FRAME_MAX_H * 4;
	static final int OFF_COLL = OFF_FRAME_DATA + 2 * FRAME_BYTES;
	public static final int COLL_MAX_BOXES = 32768, COLL_BOX_BYTES = 24;
	static final int OFF_COLL_DATA = OFF_COLL + 0x40;
	public static final long FILE_SIZE = OFF_COLL_DATA + 2L * COLL_MAX_BOXES * COLL_BOX_BYTES;

	/** Number of collision snapshots Valheim has published (0 = none yet). */
	public int collisionSnapshotCount() { return (int) I32.getAcquire(buf, OFF_COLL + 4); }

	/** Copy the latest collision snapshot (6 floats per box, MC coords). Returns null if none or torn twice. */
	public float[] readCollision() {
		for (int attempt = 0; attempt < 4; attempt++) {
			int idx = (int) I32.getAcquire(buf, OFF_COLL);
			if (idx != 0 && idx != 1) return null;
			int ctl = OFF_COLL + 16 + idx * 16;
			int seq = (int) I32.getAcquire(buf, ctl);
			if ((seq & 1) != 0) continue;
			int count = Math.min(Math.max(buf.getInt(ctl + 4), 0), COLL_MAX_BOXES);
			float[] out = new float[count * 6];
			buf.slice(OFF_COLL_DATA + idx * COLL_MAX_BOXES * COLL_BOX_BYTES, count * COLL_BOX_BYTES).order(ByteOrder.LITTLE_ENDIAN).asFloatBuffer().get(out);
			if ((int) I32.getAcquire(buf, ctl) == seq) return out;
		}
		return null;
	}

	public static final int VFLAG_IN_GAME = 1, VFLAG_MENU_OPEN = 2, VFLAG_LOADING = 4, VFLAG_INPUT_ACTIVE = 8, VFLAG_QUITTING = 16;
	public static final int MCFLAG_WORLD_LOADED = 1, MCFLAG_HIDDEN = 2, MCFLAG_SCREEN_OPEN = 4;
	public static final int PS_ON_GROUND = 1, PS_FLYING = 2, PS_CROUCHING = 4, PS_IN_WATER = 8, PS_THIRD_PERSON = 16, PS_HAS_PLAYER = 32, PS_VIEW_BOB = 64;

	public static final short EV_HELLO = 1, EV_PING = 2, EV_PONG = 3, EV_HELLO_ACK = 4, EV_QUIT = 5;
	public static final short EV_KEY = 10, EV_CHAR = 11, EV_MOUSE_MOVE = 12, EV_MOUSE_BUTTON = 13, EV_SCROLL = 14, EV_RELEASE_ALL = 15;
	public static final short EV_PLAYER_DIMS = 20, EV_HOVER_TEXT = 21, EV_INTERACT = 22, EV_TERRAIN_TILE = 23, EV_BLOCK_CHANGED = 24, EV_TELEPORT = 25, EV_TERRAIN_HEIGHTS = 26, EV_ATTACK_TARGET = 27, EV_ATTACK = 28;
	public static final int HEIGHT_CELL = 4, HEIGHT_RES = 9;
	public static final int TERRAIN_TILE = 8, VFLAG_TERRAIN_BLOCKS = 32;

	private static final VarHandle I32 = MethodHandles.byteBufferViewVarHandle(int[].class, ByteOrder.LITTLE_ENDIAN);
	private static final VarHandle I64 = MethodHandles.byteBufferViewVarHandle(long[].class, ByteOrder.LITTLE_ENDIAN);

	/** MC coordinates. x/y/z = camera eye; feet = entity position; bob = MC view-bob transform (applied to the world in view space). */
	public record PlayerState(long frameId, double x, double y, double z, float yaw, float pitch, float roll, float fovDeg, int flags,
			float bobX, float bobY, float bobRollDeg, float bobPitchDeg, double feetX, double feetY, double feetZ, float eyeHeight) {
		public PlayerState(long frameId, double x, double y, double z, float yaw, float pitch, float roll, float fovDeg, int flags) {
			this(frameId, x, y, z, yaw, pitch, roll, fovDeg, flags, 0f, 0f, 0f, 0f, x, y, z, 0f);
		}
	}
	public record Event(short type, byte[] payload) {
		public ByteBuffer le() { return ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN); }
	}

	private final FileChannel channel;
	private final MappedByteBuffer buf;

	public static Path defaultPath() {
		String env = System.getenv("VALCRAFT_SHM");
		if (env != null && !env.isEmpty()) return Paths.get(env);
		String local = System.getenv("LOCALAPPDATA");
		if (local == null) local = System.getProperty("user.home");
		return Paths.get(local, "ValCraft", "valcraft.shm");
	}

	/** Opens the link; throws if the Valheim side has not created it yet. Check {@link #isReady()} too. */
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
	public int session() { return (int) I32.getAcquire(buf, OFF_SESSION); }
	public void setAckedSession(int s) { I32.setRelease(buf, OFF_MC_ACKED_SESSION, s); }
	public int flags() { return (int) I32.getAcquire(buf, OFF_MC_FLAGS); }
	public void setFlags(int f) { I32.setRelease(buf, OFF_MC_FLAGS, f); }
	public void setFlag(int bit, boolean on) { int f = flags(); setFlags(on ? f | bit : f & ~bit); }

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
		buf.putFloat(b + 60, s.bobX()); buf.putFloat(b + 64, s.bobY()); buf.putFloat(b + 68, s.bobRollDeg()); buf.putFloat(b + 72, s.bobPitchDeg());
		buf.putDouble(b + 80, s.feetX()); buf.putDouble(b + 88, s.feetY()); buf.putDouble(b + 96, s.feetZ()); buf.putFloat(b + 104, s.eyeHeight());
		I32.setRelease(buf, b, seq + 2);
	}

	public PlayerState readPlayerState() {
		int b = OFF_PLAYER_STATE;
		for (int i = 0; i < 64; i++) {
			int s1 = (int) I32.getAcquire(buf, b);
			if ((s1 & 1) != 0) { Thread.onSpinWait(); continue; }
			PlayerState s = new PlayerState(buf.getLong(b + 8), buf.getDouble(b + 16), buf.getDouble(b + 24), buf.getDouble(b + 32),
					buf.getFloat(b + 40), buf.getFloat(b + 44), buf.getFloat(b + 48), buf.getFloat(b + 52), buf.getInt(b + 56),
					buf.getFloat(b + 60), buf.getFloat(b + 64), buf.getFloat(b + 68), buf.getFloat(b + 72),
					buf.getDouble(b + 80), buf.getDouble(b + 88), buf.getDouble(b + 96), buf.getFloat(b + 104));
			VarHandle.acquireFence();
			if ((int) I32.getAcquire(buf, b) == s1) return s1 == 0 ? null : s;
		}
		return null;
	}

	// ---- frames (MC writes) ----
	public int requestedWidth() { return (int) I32.getAcquire(buf, OFF_FRAME_REQ_W); }
	public int requestedHeight() { return (int) I32.getAcquire(buf, OFF_FRAME_REQ_H); }

	/**
	 * Publishes one RGBA8 bottom-up frame into the buffer the reader is not on. {@code frameNo} must increase from 1.
	 * {@code src} position..position+w*h*4 is copied (position untouched).
	 */
	/**
	 * MC's exported colour is premultiplied by coverage alpha; Valheim has no premultiplied UI shader (0.3.0 log:
	 * "shaders with _SrcBlend/_DstBlend: none"), so convert partially transparent pixels to straight alpha here.
	 * -Dvalcraft.unpremultiply=false disables it.
	 */
	public static final boolean UNPREMULTIPLY = !"false".equals(System.getProperty("valcraft.unpremultiply"));

	private void unpremultiply(int off, int bytes) {
		for (int i = off, end = off + bytes; i < end; i += 4) {
			int a = buf.get(i + 3) & 0xFF;
			if (a == 0 || a == 255) continue;
			for (int c = 0; c < 3; c++) {
				int v = (buf.get(i + c) & 0xFF) * 255 / a;
				buf.put(i + c, (byte) Math.min(255, v));
			}
		}
	}

	public boolean publishFrame(long frameNo, int w, int h, ByteBuffer src) {
		int bytes = w * h * 4;
		if (w <= 0 || h <= 0 || w > FRAME_MAX_W || h > FRAME_MAX_H || src.remaining() < bytes) return false;
		long latest = (long) I64.getAcquire(buf, OFF_FRAME_LATEST_SEQ);
		int idx = latest == 0 ? 0 : 1 - ((int) I32.getAcquire(buf, OFF_FRAME_LATEST_INDEX) & 1);
		int hdr = OFF_FRAME_BUF_HDR + idx * FRAME_BUF_HDR_SIZE;
		I64.setOpaque(buf, hdr, frameNo * 2 - 1);
		VarHandle.fullFence();
		buf.put(OFF_FRAME_DATA + idx * FRAME_BYTES, src, src.position(), bytes);
		if (UNPREMULTIPLY) unpremultiply(OFF_FRAME_DATA + idx * FRAME_BYTES, bytes);
		buf.putInt(hdr + 8, w); buf.putInt(hdr + 12, h); buf.putInt(hdr + 16, w * 4); buf.putInt(hdr + 20, FRAME_FORMAT_RGBA8_BOTTOM_UP);
		I64.setRelease(buf, hdr, frameNo * 2);
		I32.setRelease(buf, OFF_FRAME_LATEST_INDEX, idx);
		I64.setRelease(buf, OFF_FRAME_LATEST_SEQ, frameNo);
		return true;
	}

	// ---- SPSC rings: MC sends on M2V, receives on V2M ----
	public boolean trySend(short type, byte[] payload) {
		int len = payload == null ? 0 : payload.length;
		if (len > SLOT_PAYLOAD) throw new IllegalArgumentException("payload too large");
		int r = OFF_RING_M2V;
		long head = (long) I64.getOpaque(buf, r);
		long tail = (long) I64.getAcquire(buf, r + 0x40);
		if (head - tail >= RING_SLOTS || tail > head) return false;
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
		if (head - tail > RING_SLOTS || tail > head) { I64.setRelease(buf, r + 0x40, head); return null; } // host re-initialised: resync
		int slot = r + RING_HEADER + (int) (tail % RING_SLOTS) * SLOT_SIZE;
		int len = Math.min(buf.getInt(slot + 4), SLOT_PAYLOAD);
		byte[] data = new byte[len];
		buf.get(slot + 8, data);
		Event ev = new Event(buf.getShort(slot), data);
		I64.setRelease(buf, r + 0x40, tail + 1);
		return ev;
	}

	public static byte[] ints(int... v) {
		ByteBuffer b = ByteBuffer.allocate(v.length * 4).order(ByteOrder.LITTLE_ENDIAN);
		for (int x : v) b.putInt(x);
		return b.array();
	}

	@Override public void close() throws IOException { channel.close(); }
}
