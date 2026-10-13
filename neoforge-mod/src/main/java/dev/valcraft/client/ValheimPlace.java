package dev.valcraft.client;

import net.minecraft.client.Minecraft;
import net.minecraft.world.item.BlockItem;
import net.minecraft.world.phys.HitResult;

/** 0.13.1: right-click with a block aims at Valheim floors/walls too: a nearer Valheim box becomes the block hit. */
public final class ValheimPlace {
	private ValheimPlace() {}
	private static int logged;
	public static void retarget(Minecraft mc) {
		var p = mc.player;
		if (p == null || !ValCraftClient.active()) return;
		if (!(p.getMainHandItem().getItem() instanceof BlockItem) && !(p.getOffhandItem().getItem() instanceof BlockItem)) return;
		var eye = p.getEyePosition(1f);
		var end = eye.add(p.getViewVector(1f).scale(p.blockInteractionRange()));
		var v = CollisionWorld.clipFace(eye, end);
		if (v == null) return;
		var cur = mc.hitResult;
		if (cur != null && cur.getType() != HitResult.Type.MISS && cur.getLocation().distanceToSqr(eye) <= v.getLocation().distanceToSqr(eye) + 1e-6) return;
		if (!mc.level.getBlockState(v.getBlockPos()).canBeReplaced()) return;
		mc.hitResult = v;
		if (logged++ < 5) dev.valcraft.ValCraft.LOG.info("[place] on Valheim surface: {} face {} -> cell {}", v.getLocation(), v.getDirection(), v.getBlockPos());
	}
}
