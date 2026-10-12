package dev.valcraft.client;

import com.mojang.blaze3d.platform.InputConstants;
import dev.valcraft.ValCraft;
import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.KeyMapping;
import net.minecraft.client.Minecraft;
import net.neoforged.neoforge.client.event.RegisterKeyMappingsEvent;
import org.lwjgl.glfw.GLFW;

/** 0.12.0: M (rebindable) opens Valheim's large map; Valheim then takes input and closes it with M / Esc. */
public final class MapKey {
	private MapKey() {}
	public static final KeyMapping MAP = new KeyMapping("key.valcraft.map", InputConstants.Type.KEYSYM, GLFW.GLFW_KEY_M, "key.categories.valcraft");
	public static void register(RegisterKeyMappingsEvent e) { e.register(MAP); }
	static void tick(Minecraft mc) {
		while (MAP.consumeClick()) {
			if (mc.screen != null || mc.player == null) continue;
			ValCraftClient.sendToValheim(ValCraftLink.EV_MAP_TOGGLE, new byte[0]);
			ValCraft.LOG.info("[map] M pressed: Valheim large map");
		}
	}
}
