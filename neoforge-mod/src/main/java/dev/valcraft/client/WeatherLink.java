package dev.valcraft.client;

import dev.valcraft.ValCraft;
import net.minecraft.client.Minecraft;
import net.minecraft.world.level.GameRules;

import java.nio.charset.StandardCharsets;

/**
 * 0.12.0: Valheim weather drives MC (EV_WEATHER every 2 s: u8 flags 1 rain / 2 thunder / 4 snow, u8 len, env name).
 * Server-side state only (mobs, crops, fishing, snow in cold biomes for gameplay); 0.12.3: MC draws no rain/snow while linked
 * (NoRainRenderMixin, ClientRainLevelMixin) - Valheim shows the weather. doWeatherCycle is off while linked.
 */
public final class WeatherLink {
	private WeatherLink() {}
	private static int last = -1; private static Boolean savedCycle;

	static void receive(Minecraft mc, byte[] p) {
		if (p.length < 2) return;
		int flags = p[0] & 0xFF; int n = Math.min(p[1] & 0xFF, p.length - 2);
		String name = new String(p, 2, n, StandardCharsets.UTF_8);
		var server = mc.getSingleplayerServer(); if (server == null) return;
		server.execute(() -> {
			var level = server.overworld();
			var rules = level.getGameRules();
			if (savedCycle == null) { savedCycle = rules.getBoolean(GameRules.RULE_WEATHER_CYCLE); rules.getRule(GameRules.RULE_WEATHER_CYCLE).set(false, server); }
			boolean rain = (flags & 1) != 0, thunder = (flags & 2) != 0;
			if (flags != last || level.isRaining() != rain || level.isThundering() != thunder) {
				level.setWeatherParameters(rain ? 0 : 12000, rain ? 12000 : 0, rain, thunder);
				if (flags != last) ValCraft.LOG.info("[weather] Valheim '{}' -> rain {}, thunder {}, snow {}", name, rain, thunder, (flags & 4) != 0);
				last = flags;
			}
		});
	}

	static void unlink(Minecraft mc) {
		last = -1;
		var server = mc.getSingleplayerServer(); if (server == null || savedCycle == null) return;
		boolean c = savedCycle; savedCycle = null;
		server.execute(() -> server.overworld().getGameRules().getRule(GameRules.RULE_WEATHER_CYCLE).set(c, server));
	}
}
