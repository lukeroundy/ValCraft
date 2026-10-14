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

	static void state(byte[] p) {
		active = p != null && p.length > 0 && p[0] != 0;
		var mc = Minecraft.getInstance();
		if (!active && mc.screen instanceof BuildScreen) mc.setScreen(null);
	}
	/** 0.15.6: Tab cancelled building; Valheim confirms with EV_BUILD_STATE 0. */
	static void cancelLocal() { active = false; usePressed = false; }
	static void reset() { active = false; usePressed = false; pending.clear(); }

	private static final java.util.List<dev.valcraft.link.BuildCodec.Piece> pending = new java.util.ArrayList<>();
	/** EV_BUILD_PIECES (render thread): collect, then open the picker. */
	static void pieces(net.minecraft.client.Minecraft mc, byte[] p) {
		if (dev.valcraft.link.BuildCodec.first(p)) pending.clear();
		pending.addAll(dev.valcraft.link.BuildCodec.decode(p));
		if (!dev.valcraft.link.BuildCodec.last(p)) return;
		var list = new java.util.ArrayList<>(pending); pending.clear();
		dev.valcraft.ValCraft.LOG.info("[build] Valheim piece list: {} pieces", list.size());
		if (list.isEmpty()) { if (mc.player != null) mc.player.displayClientMessage(net.minecraft.network.chat.Component.literal("Valheim: no build pieces known yet"), true); return; }
		if (mc.screen == null && holdingMace(mc)) mc.setScreen(new BuildScreen(list));
	}
	static void tick(Minecraft mc) {
		if (!mc.options.keyUse.isDown()) usePressed = false;
		// 0.8.6: leaving the mace ends build mode on our side at once (Valheim follows via EV_HELD_ITEM) and closes the picker
		if (!holdingMace(mc)) {
			if (active) { active = false; dev.valcraft.ValCraft.LOG.info("[build] mace no longer held: build mode off"); }
			if (mc.screen instanceof BuildScreen) mc.setScreen(null);
		}
	}

	static boolean holdingMace(Minecraft mc) { return mc.player != null && isHammer(mc.player.getMainHandItem()); }
	/** 0.15.5: the ValCraft build hammer works exactly like the mace (= Valheim hammer). */
	public static boolean isHammer(net.minecraft.world.item.ItemStack s) {
		if (s.is(Items.MACE)) return true;
		var k = net.minecraft.core.registries.BuiltInRegistries.ITEM.getKey(s.getItem());
		return k.getNamespace().equals("valcraft") && k.getPath().equals("valheim_hammer");
	}
	public static boolean holdingHammer(Minecraft mc) { return ValCraftClient.active() && holdingMace(mc); }
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
