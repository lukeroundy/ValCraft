package dev.valcraft.client;

import dev.valcraft.ValCraft;
import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.Minecraft;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.damagesource.DamageSource;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * 0.8.1 health link (MC authoritative). Streams EV_HEALTH {f32 hp, f32 max, u8 food, u8 flags(1=dead)} on change
 * (and every second), and applies EV_DAMAGE {f32 amount, u8 valheimHitType, f32 dirX, f32 dirZ, f32 push} from Valheim
 * to the integrated-server player.
 */
public final class HealthLink {
	private HealthLink() {}
	private static float lastHp = -1, lastMax = -1; private static int lastFood = -1, ticks; private static boolean lastDead;

	static void reset() { lastHp = -1; lastMax = -1; lastFood = -1; }

	static void tick(Minecraft mc) {
		var p = mc.player;
		if (p == null) return;
		float hp = p.getHealth(), max = p.getMaxHealth(); int food = p.getFoodData().getFoodLevel(); boolean dead = p.isDeadOrDying();
		if (hp == lastHp && max == lastMax && food == lastFood && dead == lastDead && ++ticks < 20) return;
		ticks = 0;
		byte[] b = ByteBuffer.allocate(10).order(ByteOrder.LITTLE_ENDIAN).putFloat(hp).putFloat(max).put((byte) food).put((byte) (dead ? 1 : 0)).array();
		ValCraftClient.sendToValheim(ValCraftLink.EV_HEALTH, b);
		lastHp = hp; lastMax = max; lastFood = food; lastDead = dead;
	}

	static void damage(Minecraft mc, byte[] pl) {
		if (pl.length < 17) return;
		ByteBuffer b = ByteBuffer.wrap(pl).order(ByteOrder.LITTLE_ENDIAN);
		float amount = b.getFloat(0); int type = pl[4] & 0xff; float dx = b.getFloat(5), dz = b.getFloat(9), push = b.getFloat(13);
		var server = mc.getSingleplayerServer();
		if (server == null || mc.player == null) return;
		var uuid = mc.player.getUUID();
		server.execute(() -> {
			ServerPlayer sp = server.getPlayerList().getPlayer(uuid);
			if (sp == null || sp.isDeadOrDying()) return;
			var src = sp.damageSources();
			DamageSource ds = switch (type) {
				case 4 -> src.drown();                // Drowning
				case 5, 20 -> src.onFire();           // Burning, CinderFire
				case 6 -> src.freeze();               // Freezing
				case 7 -> src.magic();                // Poisoned
				case 10 -> src.fellOutOfWorld();      // EdgeOfWorld
				default -> src.generic();             // creature hits, impacts, etc.
			};
			sp.invulnerableTime = 0; // Valheim decides hit frequency
			if (type == 5 || type == 20) sp.setRemainingFireTicks(Math.max(sp.getRemainingFireTicks(), 80)); // 0.9.0: Valheim fire ignites
			boolean ok = sp.hurt(ds, amount);
			if (ok && push > 0 && (dx != 0 || dz != 0)) { sp.knockback(Math.min(1.5, push / 40.0), -dx, -dz); sp.hurtMarked = true; }
			if (type == 5 || type == 20) ValCraft.LOG.info("[hazard] Valheim fire damage {} -> applied {} (fire ticks {}, in water {})", amount, ok, sp.getRemainingFireTicks(), sp.isInWater());
			ValCraft.LOG.debug("[health] Valheim damage {} (type {}) -> {}", amount, type, ok);
		});
	}
}
