package dev.valcraft.client;

import com.mojang.blaze3d.pipeline.RenderTarget;
import com.mojang.blaze3d.platform.GlStateManager;
import dev.valcraft.ValCraft;
import dev.valcraft.link.ValCraftLink;
import java.nio.ByteBuffer;
import net.minecraft.client.Minecraft;
import org.lwjgl.opengl.GL11;
import org.lwjgl.opengl.GL15;
import org.lwjgl.opengl.GL21;
import org.lwjgl.opengl.GL30;
import org.lwjgl.opengl.GL32;

/**
 * Async readback of Minecraft's main render target (hand + HUD + screens on a transparent background, the world
 * is not drawn while linked) through a ring of 3 pixel-pack buffers with fences. A frame is published to shared
 * memory once its fence signals, typically 1-2 frames later; nothing ever waits on the GPU.
 */
public final class FrameExporter {
	private static final int SLOTS = 3;
	private static final int[] pbo = new int[SLOTS], pboBytes = new int[SLOTS], pboW = new int[SLOTS], pboH = new int[SLOTS];
	private static final long[] fence = new long[SLOTS];
	private static int next;
	private static long frameNo;
	private static boolean warnedSize;

	private FrameExporter() {}

	static void capture(Minecraft mc, ValCraftLink link) {
		publishReady(link);
		RenderTarget t = mc.getMainRenderTarget();
		int w = t.width, h = t.height;
		if (w <= 0 || h <= 0) return;
		if (w > ValCraftLink.FRAME_MAX_W || h > ValCraftLink.FRAME_MAX_H) {
			if (!warnedSize) { ValCraft.LOG.warn("[frame] {}x{} exceeds {}x{}; waiting for resize", w, h, ValCraftLink.FRAME_MAX_W, ValCraftLink.FRAME_MAX_H); warnedSize = true; }
			return;
		}
		warnedSize = false;
		int i = next;
		if (fence[i] != 0) return; // all slots in flight: skip this frame rather than stall
		next = (next + 1) % SLOTS;
		int bytes = w * h * 4;
		if (pbo[i] == 0) pbo[i] = GL15.glGenBuffers();
		GL15.glBindBuffer(GL21.GL_PIXEL_PACK_BUFFER, pbo[i]);
		if (pboBytes[i] != bytes) { GL15.glBufferData(GL21.GL_PIXEL_PACK_BUFFER, bytes, GL15.GL_STREAM_READ); pboBytes[i] = bytes; }
		GlStateManager._glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, t.frameBufferId);
		GlStateManager._pixelStore(GL11.GL_PACK_ROW_LENGTH, 0);
		GlStateManager._pixelStore(GL11.GL_PACK_SKIP_PIXELS, 0);
		GlStateManager._pixelStore(GL11.GL_PACK_SKIP_ROWS, 0);
		GlStateManager._pixelStore(GL11.GL_PACK_ALIGNMENT, 4);
		GL11.glReadPixels(0, 0, w, h, GL11.GL_RGBA, GL11.GL_UNSIGNED_BYTE, 0L);
		fence[i] = GL32.glFenceSync(GL32.GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
		pboW[i] = w; pboH[i] = h;
		GL15.glBindBuffer(GL21.GL_PIXEL_PACK_BUFFER, 0);
		GL11.glFlush();
	}

	/** Publish the newest finished readback; older finished ones are dropped. */
	private static void publishReady(ValCraftLink link) {
		int newest = -1;
		for (int k = 1; k <= SLOTS; k++) {
			int i = (next + k) % SLOTS; // oldest first
			if (fence[i] == 0) continue;
			int status = GL32.glClientWaitSync(fence[i], 0, 0L);
			if (status != GL32.GL_ALREADY_SIGNALED && status != GL32.GL_CONDITION_SATISFIED) break; // later ones aren't done either
			GL32.glDeleteSync(fence[i]);
			fence[i] = 0;
			if (newest >= 0) { /* superseded */ }
			newest = i;
		}
		if (newest < 0) return;
		GL15.glBindBuffer(GL21.GL_PIXEL_PACK_BUFFER, pbo[newest]);
		ByteBuffer mapped = GL30.glMapBufferRange(GL21.GL_PIXEL_PACK_BUFFER, 0, pboBytes[newest], GL30.GL_MAP_READ_BIT);
		if (mapped != null) {
			link.publishFrame(++frameNo, pboW[newest], pboH[newest], mapped);
			GL15.glUnmapBuffer(GL21.GL_PIXEL_PACK_BUFFER);
		}
		GL15.glBindBuffer(GL21.GL_PIXEL_PACK_BUFFER, 0);
	}

	/** Drop in-flight readbacks (disconnect). frameNo keeps increasing so the reader never sees a repeat. */
	static void reset() {
		for (int i = 0; i < SLOTS; i++) if (fence[i] != 0) { GL32.glDeleteSync(fence[i]); fence[i] = 0; }
	}
}
