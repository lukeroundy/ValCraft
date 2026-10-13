package dev.valcraft.net;

import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;

/** 0.13.2: shared (client + integrated server) sleep state of the Valheim link. */
public final class SleepState {
	private SleepState() {}
	/** Valheim drives the time of day (doDaylightCycle off): MC never skips/wakes on its own sleep check. */
	public static volatile boolean valheimTime;
	/** Players sleeping because their Valheim player is in a Valheim bed (no MC bed under them). */
	public static final Set<UUID> sleepers = ConcurrentHashMap.newKeySet();

	public static void onContinue(net.neoforged.neoforge.event.entity.player.CanContinueSleepingEvent e) {
		if (!sleepers.contains(e.getEntity().getUUID())) return;
		var p = e.getProblem();
		// no bed block here is fine; daytime (Valheim's morning reached MC) still wakes
		if (p != net.minecraft.world.entity.player.Player.BedSleepingProblem.NOT_POSSIBLE_NOW) e.setContinueSleeping(true);
	}

	/** Op 1007 {u8 on}: sleep where the player stands / wake. */
	public static void sleepOn(net.minecraft.server.level.ServerPlayer sp, byte[] p) {
		boolean on = p != null && p.length > 0 && p[0] != 0;
		if (on) {
			if (sp.isSleeping() || !sp.isAlive()) { dev.valcraft.ValCraft.LOG.info("[sleep] op sleep ignored (sleeping {}, alive {})", sp.isSleeping(), sp.isAlive()); return; }
			sleepers.add(sp.getUUID());
			try { sp.startSleeping(sp.blockPosition()); } catch (RuntimeException ex) { dev.valcraft.ValCraft.LOG.warn("[sleep] startSleeping failed", ex); }
			dev.valcraft.ValCraft.LOG.info("[sleep] {} sleeps (Valheim bed)", sp.getName().getString());
		} else {
			if (sp.isSleeping()) sp.stopSleepInBed(false, true);
			sleepers.remove(sp.getUUID());
		}
	}

	/** Op 1008 {f32 x,y,z}: respawn position from Valheim's custom spawn point (forced: no bed block needed). */
	public static void spawnOn(net.minecraft.server.level.ServerPlayer sp, byte[] p) {
		var b = java.nio.ByteBuffer.wrap(p).order(java.nio.ByteOrder.LITTLE_ENDIAN);
		var pos = net.minecraft.core.BlockPos.containing(b.getFloat(0), b.getFloat(4) + 0.05f, b.getFloat(8));
		sp.setRespawnPosition(sp.level().dimension(), pos, sp.getYRot(), true, true);
		dev.valcraft.ValCraft.LOG.info("[spawn] MC respawn position set to {} (Valheim spawn point)", pos);
	}
}
