package dev.valcraft.client;

import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.Minecraft;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/** 0.9.7: MC reach (block / entity interaction range) -> Valheim every 2 s (EV_REACH f32 block, f32 entity). */
final class ReachLink {
	private ReachLink() {}
	private static long next;
	static void tick(Minecraft mc) {
		var p = mc.player; long now = System.currentTimeMillis();
		if (p == null || now < next) return;
		next = now + 2000;
		byte[] b = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putFloat((float) p.blockInteractionRange()).putFloat((float) p.entityInteractionRange()).array();
		ValCraftClient.sendToValheim(ValCraftLink.EV_REACH, b);
	}
}
