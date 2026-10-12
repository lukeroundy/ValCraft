package dev.valcraft.client;

import dev.valcraft.ValCraft;
import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.GuiGraphics;
import net.minecraft.world.phys.HitResult;
import net.neoforged.api.distmarker.Dist;
import net.neoforged.bus.api.SubscribeEvent;
import net.neoforged.fml.common.EventBusSubscriber;
import net.neoforged.neoforge.client.event.RenderGuiEvent;

/**
 * MC right-click -> Valheim interact (doors, chests, beds, workbench, item pickup), and Valheim's hover text drawn
 * under MC's crosshair. Priority: an MC entity under the crosshair (MC's own pick, entity reach) wins; otherwise, if
 * Valheim reports something interactable on its camera ray, the click goes to Valheim instead of MC's item use.
 */
@EventBusSubscriber(modid = ValCraft.MOD_ID, value = Dist.CLIENT)
public final class ValheimInteract {
	private ValheimInteract() {}
	public static volatile String hoverText = "";
	private static boolean useWasDown, consumedThisPress;

	static void tick(Minecraft mc) {
		if (!mc.options.keyUse.isDown()) { useWasDown = false; consumedThisPress = false; }
	}

	/** Called from Minecraft.startUseItem; true = Valheim took the click, MC must not use the item. */
	public static boolean tryInteract(Minecraft mc) {
		if (!ValCraftClient.active() || mc.player == null) return false;
		if (mc.hitResult != null && mc.hitResult.getType() == HitResult.Type.ENTITY) return false;
		if (hoverText.isEmpty()) return false;
		if (consumedThisPress) return true;   // held button: one Valheim interact per press, and no MC use meanwhile
		consumedThisPress = true;
		useWasDown = true;
		ValCraftClient.sendToValheim(ValCraftLink.EV_INTERACT, null);
		return true;
	}

	@SubscribeEvent
	public static void onGui(RenderGuiEvent.Post e) {
		Minecraft mc = Minecraft.getInstance();
		String t = hoverText;
		if (t.isEmpty() || !ValCraftClient.active() || mc.screen != null || mc.options.hideGui) return;
		GuiGraphics g = e.getGuiGraphics();
		int cx = g.guiWidth() / 2, y = g.guiHeight() / 2 + 12;
		for (String line : t.split("\n")) {
			if (line.isBlank()) continue;
			g.drawCenteredString(mc.font, line, cx, y, 0xFFFFFF);
			y += mc.font.lineHeight + 1;
		}
	}
}
