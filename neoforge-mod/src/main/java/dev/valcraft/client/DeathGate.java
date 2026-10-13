package dev.valcraft.client;

import dev.valcraft.ValCraft;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.components.Button;
import net.minecraft.network.chat.Component;

import java.util.List;

/**
 * 0.13.4: after an MC death the respawn button waits for Valheim (EV_RESPAWN_READY 0 = still respawning, 1 = Valheim's new
 * player is alive and placed). On 1 MC respawns by itself; Valheim then moves the MC player to its respawn point. Not gated if
 * Valheim never says "wait" (health link off); a 60 s timeout re-enables the button.
 */
public final class DeathGate {
	private DeathGate() {}
	private static volatile long waitSeen, readySeen;
	private static long deathAt; private static boolean respawned, gated;
	static final long TIMEOUT = 60_000;

	static void receive(byte[] p) {
		if (p == null || p.length == 0) return;
		if (p[0] == 0) waitSeen = System.currentTimeMillis(); else readySeen = System.currentTimeMillis();
	}

	/** DeathScreen.tick tail (mixin). */
	public static void tick(Minecraft mc, List<Button> buttons, boolean hardcore) {
		if (hardcore || buttons.isEmpty() || mc.player == null || !ValCraftClient.active()) return;
		long now = System.currentTimeMillis();
		if (deathAt == 0) { deathAt = now; respawned = false; gated = false; }
		Button respawn = buttons.get(0);
		boolean ready = readySeen >= deathAt;
		boolean waiting = !ready && waitSeen >= deathAt && now - deathAt < TIMEOUT;
		if (waiting) {
			if (!gated) { gated = true; ValCraft.LOG.info("[health] MC died: respawn waits for Valheim"); }
			respawn.active = false;
			respawn.setMessage(Component.literal("Waiting for Valheim..."));
			return;
		}
		if (gated) {
			respawn.setMessage(Component.translatable("deathScreen.respawn"));
			respawn.active = true;
			if (ready && !respawned) {
				respawned = true;
				ValCraft.LOG.info("[health] Valheim respawned: respawning MC");
				mc.player.respawn();
				respawn.active = false;
			} else if (!ready && !respawned) { respawned = true; ValCraft.LOG.info("[health] Valheim respawn timed out after {} s: respawn button enabled", TIMEOUT / 1000); }
		}
	}

	/** Client tick: reset once alive again. */
	static void tick(Minecraft mc) { if (mc.player != null && mc.player.isAlive() && deathAt != 0) deathAt = 0; }
}
