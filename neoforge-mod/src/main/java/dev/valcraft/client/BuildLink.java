package dev.valcraft.client;

import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.Minecraft;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.item.Items;
import net.minecraft.world.phys.HitResult;

/**
 * 0.8.3: the mace is the Valheim hammer. Right-click (not on a Valheim interactable or MC entity) -> EV_BUILD_MENU
 * (enters build mode / toggles the piece menu). In build mode (EV_BUILD_STATE 1) with the mace: left-click places
 * (EV_BUILD_ACTION 1), sneak+left-click removes (2), sneak+right-click rotates (3). MC attack/mining is suppressed.
 */
public final class BuildLink {
	private BuildLink() {}
	private static volatile boolean active;
	private static boolean usePressed;

	static void state(byte[] p) { active = p != null && p.length > 0 && p[0] != 0; }
	static void reset() { active = false; usePressed = false; }
	static void tick(Minecraft mc) { if (!mc.options.keyUse.isDown()) usePressed = false; }

	static boolean holdingMace(Minecraft mc) { return mc.player != null && mc.player.getMainHandItem().is(Items.MACE); }
	public static boolean building(Minecraft mc) { return active && ValCraftClient.active() && holdingMace(mc); }

	public static boolean tryUse(Minecraft mc) {
		if (!ValCraftClient.active() || !holdingMace(mc)) return false;
		if (mc.hitResult != null && mc.hitResult.getType() == HitResult.Type.ENTITY) return false;
		if (usePressed) return true; // one action per press
		usePressed = true;
		if (active && mc.player.isShiftKeyDown()) ValCraftClient.sendToValheim(ValCraftLink.EV_BUILD_ACTION, new byte[] { 3 });
		else ValCraftClient.sendToValheim(ValCraftLink.EV_BUILD_MENU, null);
		return true;
	}

	public static boolean tryAttack(Minecraft mc) {
		if (!building(mc)) return false;
		ValCraftClient.sendToValheim(ValCraftLink.EV_BUILD_ACTION, new byte[] { (byte) (mc.player.isShiftKeyDown() ? 2 : 1) });
		mc.player.swing(InteractionHand.MAIN_HAND);
		return true;
	}
}
