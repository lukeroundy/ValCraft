package dev.valcraft.client;

import dev.valcraft.ValCraft;
import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.Minecraft;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.item.HoeItem;
import net.minecraft.world.phys.HitResult;

/** 0.9.1: Shift + right-click with any hoe levels Valheim ground at the aimed point (Valheim does the edit). */
public final class HoeLink {
	private HoeLink() {}
	private static long next;

	public static boolean tryUse(Minecraft mc) {
		var p = mc.player;
		if (p == null || !ValCraftClient.active() || !p.isShiftKeyDown()) return false;
		var stack = p.getMainHandItem();
		if (!(stack.getItem() instanceof HoeItem)) return false;
		if (mc.hitResult != null && mc.hitResult.getType() == HitResult.Type.ENTITY) return false;
		long now = System.currentTimeMillis();
		if (now < next) return true; // holding the button: throttle, still swallow vanilla tilling
		next = now + 400;
		ValCraftClient.sendToValheim(ValCraftLink.EV_LEVEL_GROUND, new byte[0]);
		p.swing(InteractionHand.MAIN_HAND);
		Ops.run(mc, dev.valcraft.net.ServerOps.OP_HOE_WEAR, new byte[0]);
		ValCraft.LOG.debug("[hoe] level ground request");
		return true;
	}

	public static void wearOn(net.minecraft.server.level.ServerPlayer sp, byte[] unused) {
		if (sp.getAbilities().instabuild) return;
		var st = sp.getMainHandItem();
		if (st.getItem() instanceof HoeItem) st.hurtAndBreak(1, sp, EquipmentSlot.MAINHAND);
	}
}
