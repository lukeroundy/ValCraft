package dev.valcraft.client;

import com.mojang.blaze3d.platform.InputConstants;
import dev.valcraft.ValCraft;
import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.KeyMapping;
import net.minecraft.client.Minecraft;
import net.neoforged.bus.api.IEventBus;
import net.neoforged.neoforge.client.event.ClientTickEvent;
import net.neoforged.neoforge.client.event.RegisterKeyMappingsEvent;
import net.neoforged.neoforge.common.NeoForge;
import org.lwjgl.glfw.GLFW;

/** 0.15.6: O (rebindable in Controls > ValCraft) opens Valheim's Esc menu; Tab while building cancels Valheim building
 *  instead of opening the Minecraft inventory. */
public final class Fixes156 {
	private Fixes156() {}
	public static final KeyMapping VALHEIM_MENU = new KeyMapping("key.valcraft.valheim_menu", InputConstants.Type.KEYSYM, GLFW.GLFW_KEY_O, "key.categories.valcraft");

	public static void register(IEventBus modBus) {
		modBus.addListener((RegisterKeyMappingsEvent e) -> e.register(VALHEIM_MENU));
		// Pre runs before Minecraft.handleKeybinds in the same tick, so draining the inventory key here keeps the inventory shut
		NeoForge.EVENT_BUS.addListener((ClientTickEvent.Pre e) -> tick(Minecraft.getInstance()));
	}

	static void tick(Minecraft mc) {
		while (VALHEIM_MENU.consumeClick()) {
			if (mc.screen != null || mc.player == null || !ValCraftClient.active()) continue;
			ValCraftClient.sendToValheim(ValCraftLink.EV_MAP_TOGGLE, new byte[] { 1 });
			ValCraft.LOG.info("[menu] O pressed: Valheim menu");
		}
		if (mc.screen == null && mc.player != null && BuildLink.building(mc)) {
			boolean tab = false;
			while (mc.options.keyInventory.consumeClick()) tab = true;
			if (tab) {
				ValCraftClient.sendToValheim(ValCraftLink.EV_BUILD_ACTION, new byte[] { 6 });
				BuildLink.cancelLocal();
				ValCraft.LOG.info("[build] Tab: Valheim building cancelled (inventory not opened)");
			}
		}
	}
}
