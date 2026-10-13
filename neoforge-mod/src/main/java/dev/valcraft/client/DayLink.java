package dev.valcraft.client;

import dev.valcraft.ValCraft;
import net.minecraft.client.Minecraft;
import net.minecraft.world.Difficulty;
import net.minecraft.world.level.GameRules;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * 0.9.1: Valheim is authoritative for time of day (EV_DAY_TIME, f32 day fraction every second).
 * MC time = (fraction * 24000 + 18000) % 24000 (Valheim 0 = midnight = MC 18000), keeping MC's day count.
 * doDaylightCycle is turned off while linked (restored on unlink) so MC sleeping can't skip the night out of sync.
 * Also, on link: doMobSpawning forced on and a warning if the difficulty is peaceful.
 */
public final class DayLink {
	private DayLink() {}
	private static Boolean savedCycle; // server thread
	private static boolean checkedSpawning;

	static void receive(Minecraft mc, byte[] p) {
		if (p.length < 4) return;
		float f = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN).getFloat(0);
		if (Float.isNaN(f)) return;
		var server = mc.getSingleplayerServer(); if (server == null) return;
		server.execute(() -> {
			var level = server.overworld();
			var rules = level.getGameRules();
			if (savedCycle == null) {
				savedCycle = rules.getBoolean(GameRules.RULE_DAYLIGHT);
				rules.getRule(GameRules.RULE_DAYLIGHT).set(false, server);
				dev.valcraft.net.SleepState.valheimTime = true;
				ValCraft.LOG.info("[time] Valheim drives the time of day; doDaylightCycle off (was {})", savedCycle);
			}
			if (!checkedSpawning) {
				checkedSpawning = true;
				if (!rules.getBoolean(GameRules.RULE_DOMOBSPAWNING)) { rules.getRule(GameRules.RULE_DOMOBSPAWNING).set(true, server); ValCraft.LOG.info("[spawn] doMobSpawning was off; turned on"); }
				if (server.getWorldData().getDifficulty() == Difficulty.PEACEFUL) ValCraft.LOG.warn("[spawn] difficulty is PEACEFUL: no MC hostile mobs will spawn");
			}
			long target = (long) (((f % 1f + 1f) % 1f) * 24000f + 18000f) % 24000L;
			long cur = level.getDayTime(), day = Math.floorDiv(cur, 24000L), t = day * 24000L + target;
			if (t < cur - 12000) t += 24000L;     // wrapped past midnight: next day (stays monotonic)
			else if (t < cur) t = cur > t + 600 ? t + 24000L : cur; // small backward jitter: hold
			if (t != cur) level.setDayTime(t);
		});
	}

	static void unlink(Minecraft mc) {
		checkedSpawning = false;
		var server = mc.getSingleplayerServer(); if (server == null) return;
		server.execute(() -> {
			if (savedCycle == null) return;
			server.overworld().getGameRules().getRule(GameRules.RULE_DAYLIGHT).set(savedCycle, server);
			ValCraft.LOG.info("[time] unlinked; doDaylightCycle restored to {}", savedCycle);
			savedCycle = null;
			dev.valcraft.net.SleepState.valheimTime = false;
		});
	}
}
