package dev.valcraft.client;

import dev.valcraft.link.MobCodec;
import net.minecraft.client.Minecraft;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.phys.Vec3;

/** 0.9.0: Valheim water/tar surface around the player (EV_WATER), read by the water mixin on client and server threads. */
public final class ValheimWater {
	private ValheimWater() {}
	private static volatile MobCodec.Water grid;

	static void receive(byte[] p) { grid = MobCodec.decodeWater(p, System.currentTimeMillis()); }
	static void clear() { grid = null; }

	/** Valheim water surface (MC Y) for this entity's column, NaN if none. Players only; tar doesn't count. */
	public static double waterSurface(Entity e) {
		var g = grid;
		if (g == null || !(e instanceof Player) || System.currentTimeMillis() - g.time() > 2000) return Double.NaN;
		if (g.tarAt(e.getX(), e.getZ())) return Double.NaN;
		return g.surfaceAt(e.getX(), e.getZ());
	}

	/** Client tick: Valheim tar slows the local player. */
	static void tick(Minecraft mc) {
		var g = grid; var p = mc.player;
		if (g == null || p == null || System.currentTimeMillis() - g.time() > 2000) return;
		float s = g.surfaceAt(p.getX(), p.getZ());
		if (g.tarAt(p.getX(), p.getZ()) && !Float.isNaN(s) && s > p.getY()) p.makeStuckInBlock(Blocks.COBWEB.defaultBlockState(), new Vec3(0.45, 0.6, 0.45));
	}
}
