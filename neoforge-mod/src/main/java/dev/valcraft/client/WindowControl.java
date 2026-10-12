package dev.valcraft.client;

import dev.valcraft.ValCraft;
import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.Minecraft;
import org.lwjgl.glfw.GLFW;

/** Hides the GLFW window while linked and resizes it to the frame size Valheim asks for. */
public final class WindowControl {
	/** -Dvalcraft.startHidden=true (Prism JVM args): hide as soon as Minecraft's own Window object exists. */
	public static final boolean START_HIDDEN = Boolean.getBoolean("valcraft.startHidden");
	private static long lastResizeMs;
	private static int lastReqW, lastReqH;

	private WindowControl() {}

	public static void hide(long handle) {
		GLFW.glfwHideWindow(handle);
	}

	static void onActivated(Minecraft mc) {
		long h = mc.getWindow().getWindow();
		hide(h);
		mc.getWindow().updateVsync(false); // a hidden window must not block on vsync; Valheim paces the overlay
		mc.setWindowActive(true);
		lastReqW = lastReqH = 0;
		ValCraft.LOG.info("[window] hidden, vsync off");
	}

	static void onDeactivated(Minecraft mc, boolean exiting) {
		if (exiting) return; // about to quit anyway, or waiting for Valheim to come back
		if (!START_HIDDEN) {
			GLFW.glfwShowWindow(mc.getWindow().getWindow());
			mc.getWindow().updateVsync(mc.options.enableVsync().get());
		}
	}

	static void applyRequestedSize(Minecraft mc, ValCraftLink link) {
		int w = link.requestedWidth(), h = link.requestedHeight();
		if (w <= 0 || h <= 0) return;
		w = Math.min(w, ValCraftLink.FRAME_MAX_W);
		h = Math.min(h, ValCraftLink.FRAME_MAX_H);
		var win = mc.getWindow();
		boolean differs = win.getWidth() != w || win.getHeight() != h;
		long now = System.currentTimeMillis();
		if (differs && (w != lastReqW || h != lastReqH || now - lastResizeMs > 2000)) {
			// Window size is in screen coordinates; with DPI scaling the framebuffer may differ (unverified on Windows).
			GLFW.glfwSetWindowSize(win.getWindow(), w, h);
			lastReqW = w; lastReqH = h; lastResizeMs = now;
			ValCraft.LOG.info("[window] resize to {}x{} requested by Valheim (framebuffer was {}x{})", w, h, win.getWidth(), win.getHeight());
		}
	}
}
