package dev.valcraft.client;

import dev.valcraft.link.ValCraftLink;
import dev.valcraft.mixin.KeyboardHandlerAccessor;
import dev.valcraft.mixin.MouseHandlerAccessor;
import java.nio.ByteBuffer;
import java.util.BitSet;
import net.minecraft.client.Minecraft;
import org.lwjgl.glfw.GLFW;

/**
 * Input forwarded from Valheim, injected through MC's own GLFW callback entry points (KeyboardHandler.keyPress,
 * charTyped, MouseHandler.onMove/onPress/onScroll) so key mappings, screens and chat behave as with real input.
 * InputConstants.isKeyDown is answered from {@link #isDown} while linked.
 */
public final class VirtualInput {
	private static final BitSet keys = new BitSet(GLFW.GLFW_KEY_LAST + 1);
	private static final BitSet buttons = new BitSet(8);
	private static double vx, vy;
	private static int mods;

	private VirtualInput() {}

	public static boolean isDown(int key) { return key >= 0 && key <= GLFW.GLFW_KEY_LAST && keys.get(key); }

	static void handle(Minecraft mc, ValCraftLink.Event ev) {
		long win = mc.getWindow().getWindow();
		ByteBuffer b = ev.le();
		switch (ev.type()) {
			case ValCraftLink.EV_KEY -> {
				int key = b.getInt(0), scan = b.getInt(4), action = b.getInt(8);
				mods = b.getInt(12);
				if (key < 0 || key > GLFW.GLFW_KEY_LAST) return;
				if (action == GLFW.GLFW_RELEASE) { if (!keys.get(key)) return; keys.clear(key); } else keys.set(key);
				mc.keyboardHandler.keyPress(win, key, scan, action, mods);
				// 0.13.2: Left Alt while building with a Valheim piece cycles Valheim's snap point
				if (key == GLFW.GLFW_KEY_LEFT_ALT && action == GLFW.GLFW_PRESS && BuildLink.building(mc)) ValCraftClient.sendToValheim(ValCraftLink.EV_BUILD_ACTION, new byte[] { 4 });
			}
			case ValCraftLink.EV_CHAR -> ((KeyboardHandlerAccessor) mc.keyboardHandler).valcraft$charTyped(win, b.getInt(0), b.getInt(4));
			case ValCraftLink.EV_MOUSE_MOVE -> {
				float dx = b.getFloat(0), dy = b.getFloat(4), nx = b.getFloat(8), ny = b.getFloat(12);
				MouseHandlerAccessor m = (MouseHandlerAccessor) mc.mouseHandler;
				if (mc.mouseHandler.isMouseGrabbed()) { vx = m.valcraft$getXpos() + dx; vy = m.valcraft$getYpos() + dy; }
				else { vx = nx * mc.getWindow().getScreenWidth(); vy = ny * mc.getWindow().getScreenHeight(); }
				m.valcraft$onMove(win, vx, vy);
			}
			case ValCraftLink.EV_MOUSE_BUTTON -> {
				int button = b.getInt(0), action = b.getInt(4);
				mods = b.getInt(8);
				if (button < 0 || button > 7) return;
				if (action == GLFW.GLFW_RELEASE) { if (!buttons.get(button)) return; buttons.clear(button); } else buttons.set(button);
				((MouseHandlerAccessor) mc.mouseHandler).valcraft$onPress(win, button, action, mods);
			}
			case ValCraftLink.EV_SCROLL -> ((MouseHandlerAccessor) mc.mouseHandler).valcraft$onScroll(win, b.getFloat(0), b.getFloat(4));
			case ValCraftLink.EV_RELEASE_ALL -> releaseAll(mc);
			default -> { }
		}
	}

	/** Release everything we pressed (F7 off, link lost) so MC never keeps walking. */
	static void releaseAll(Minecraft mc) {
		if (keys.isEmpty() && buttons.isEmpty()) return;
		long win = mc.getWindow().getWindow();
		for (int k = keys.nextSetBit(0); k >= 0; k = keys.nextSetBit(k + 1)) mc.keyboardHandler.keyPress(win, k, 0, GLFW.GLFW_RELEASE, 0);
		for (int bt = buttons.nextSetBit(0); bt >= 0; bt = buttons.nextSetBit(bt + 1)) ((MouseHandlerAccessor) mc.mouseHandler).valcraft$onPress(win, bt, GLFW.GLFW_RELEASE, 0);
		keys.clear();
		buttons.clear();
	}

	static void releaseIfHeld(Minecraft mc) { releaseAll(mc); }
}
