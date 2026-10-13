package dev.valcraft.client;

import dev.valcraft.ValCraft;
import net.minecraft.client.Minecraft;
import net.minecraft.world.entity.EntityDimensions;

/** Player hitbox matching Valheim's capsule (width = 2 x radius, height), applied to every pose proportionally. */
public final class PlayerDims {
	private PlayerDims() {}
	static final float VANILLA_W = 0.6f, VANILLA_H = 1.8f;
	private static volatile float sw = 1f, sh = 1f;

	/** w/h in metres; 0 = vanilla. */
	static void set(Minecraft mc, float w, float h) {
		float nw = w > 0.05f && w < 4f ? w / VANILLA_W : 1f, nh = h > 0.2f && h < 6f ? h / VANILLA_H : 1f;
		if (nw == sw && nh == sh) return;
		sw = nw; sh = nh;
		ValCraft.LOG.info("[dims] player hitbox {} x {} (eye {})", VANILLA_W * sw, VANILLA_H * sh, 1.62f * sh);
		if (mc.player != null) mc.player.refreshDimensions();
		if (mc.player != null) Ops.run(mc, dev.valcraft.net.ServerOps.OP_DIMS, new byte[0]);
	}

	private static Object lastPlayer;
	/** New player entities (respawn, dimension change) start from EntityType dims; refresh so our size applies. */
	static void tick(Minecraft mc) {
		if (mc.player == null || mc.player == lastPlayer) return;
		lastPlayer = mc.player;
		if (sw != 1f || sh != 1f) {
			mc.player.refreshDimensions();
			Ops.run(mc, dev.valcraft.net.ServerOps.OP_DIMS, new byte[0]);
		}
	}

	public static EntityDimensions apply(EntityDimensions d) {
		float w = sw, h = sh;
		return w == 1f && h == 1f ? d : d.scale(w, h);
	}
}
