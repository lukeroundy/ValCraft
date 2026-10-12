package dev.valcraft.client;

import dev.valcraft.ValCraft;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.core.registries.Registries;
import net.minecraft.world.Difficulty;
import net.minecraft.world.level.GameRules;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.LevelSettings;
import net.minecraft.world.level.WorldDataConfiguration;
import net.minecraft.world.level.levelgen.WorldOptions;
import net.minecraft.world.level.levelgen.flat.FlatLevelGeneratorPresets;
import net.minecraft.world.level.levelgen.presets.WorldPresets;
import net.minecraft.world.level.levelgen.FlatLevelSource;
import net.neoforged.fml.loading.FMLPaths;

import java.io.Reader;
import java.io.Writer;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Properties;

/**
 * When linked to Valheim and sitting on the title screen, open (or create) a dedicated ValCraft world: no world selection.
 * config/valcraft.properties: autoWorld=true, worldName=ValCraft, gameMode=survival|creative|adventure, generator=void|flat
 */
final class AutoWorld {
	private AutoWorld() {}
	private static boolean tried;
	private static Properties props;

	static Properties config() {
		if (props != null) return props;
		props = new Properties();
		props.setProperty("autoWorld", "true");
		props.setProperty("worldName", "ValCraft");
		props.setProperty("gameMode", "survival");
		props.setProperty("generator", "void");
		Path p = FMLPaths.CONFIGDIR.get().resolve("valcraft.properties");
		try {
			if (Files.exists(p)) try (Reader r = Files.newBufferedReader(p)) { props.load(r); }
			else try (Writer w = Files.newBufferedWriter(p)) { props.store(w, "ValCraft (Minecraft side). generator applies only when the world is first created."); }
		} catch (Exception e) { ValCraft.LOG.warn("[autoworld] config {}: {}", p, e.toString()); }
		return props;
	}

	static void reset() { tried = false; vUid = null; vName = null; waitSince = 0; }
	private static long waitSince, lastWaitLog;

	// ---------- 0.9.4: one MC world per Valheim world ----------
	private static volatile Long vUid; private static volatile String vName;
	private static Properties map; private static long switchingSince;

	private static Path mapPath() { return FMLPaths.CONFIGDIR.get().resolve("valcraft-worlds.properties"); }
	private static Properties map() {
		if (map != null) return map;
		map = new Properties();
		try { if (Files.exists(mapPath())) try (Reader r = Files.newBufferedReader(mapPath())) { map.load(r); } } catch (Exception e) { ValCraft.LOG.warn("[autoworld] {}: {}", mapPath(), e.toString()); }
		return map;
	}
	private static void saveMap() {
		try (Writer w = Files.newBufferedWriter(mapPath())) { map.store(w, "ValCraft: Valheim world uid -> Minecraft save folder"); }
		catch (Exception e) { ValCraft.LOG.warn("[autoworld] save {}: {}", mapPath(), e.toString()); }
	}
	static String sanitize(String s) {
		String t = s.replaceAll("[^A-Za-z0-9 _\\-()]", "_").trim();
		return t.isEmpty() ? "world" : t.length() > 60 ? t.substring(0, 60) : t;
	}

	/** EV_WORLD_INFO: i64 uid, u8 len, utf8 name. */
	static void worldInfo(byte[] p) {
		if (p.length < 9) return;
		long uid = java.nio.ByteBuffer.wrap(p).order(java.nio.ByteOrder.LITTLE_ENDIAN).getLong(0);
		int n = Math.min(p[8] & 0xFF, p.length - 9);
		String name = new String(p, 9, n, java.nio.charset.StandardCharsets.UTF_8);
		if (vUid == null || vUid != uid) ValCraft.LOG.info("[autoworld] Valheim world '{}' (uid {})", name, uid);
		vName = name; vUid = uid;
	}

	/** MC save folder for the current Valheim world (creates the mapping; the legacy single world is adopted by the first one). */
	private static String folderFor(Minecraft mc, Properties cfg) {
		var m = map(); String key = Long.toString(vUid);
		String f = m.getProperty(key);
		if (f != null) return f;
		String legacy = cfg.getProperty("worldName", "ValCraft").trim();
		boolean legacyTaken = m.containsValue(legacy) || "true".equals(m.getProperty("legacyAdopted"));
		try {
			if (!legacyTaken && mc.getLevelSource().levelExists(legacy)) {
				f = legacy; m.setProperty("legacyAdopted", "true");
				ValCraft.LOG.info("[autoworld] migration: existing MC world '{}' adopted by Valheim world '{}' (uid {})", legacy, vName, vUid);
			}
		} catch (Exception ignored) {}
		if (f == null) {
			String base = sanitize("ValCraft - " + vName); f = base;
			try { if (m.containsValue(f) || mc.getLevelSource().levelExists(f)) f = sanitize(base + " " + Long.toHexString(vUid)); } catch (Exception ignored) {}
		}
		m.setProperty(key, f); saveMap();
		return f;
	}

	private static String currentFolder(Minecraft mc) {
		var srv = mc.getSingleplayerServer(); if (srv == null) return null;
		var root = srv.getWorldPath(net.minecraft.world.level.storage.LevelResource.ROOT).toAbsolutePath().normalize();
		return root.getFileName() == null ? null : root.getFileName().toString();
	}

	/** In a different MC world than the Valheim world maps to: save and leave to the title screen (tick() then opens the right one). */
	private static void checkSwitch(Minecraft mc, Properties cfg) {
		if (vUid == null || mc.level == null || mc.getSingleplayerServer() == null) return;
		String want = folderFor(mc, cfg), cur = currentFolder(mc);
		if (cur == null || cur.equals(want)) return;
		long now = System.currentTimeMillis(); if (now - switchingSince < 10000) return; switchingSince = now;
		ValCraft.LOG.info("[autoworld] Valheim world changed to '{}': leaving MC world '{}' for '{}'", vName, cur, want);
		mc.level.disconnect();
		mc.disconnect(new TitleScreen());
		tried = false;
	}

	static void tick(Minecraft mc) {
		var cfg0 = config();
		if (!Boolean.parseBoolean(cfg0.getProperty("autoWorld", "true"))) return;
		if (mc.level != null) { checkSwitch(mc, cfg0); return; }
		if (tried || !(mc.screen instanceof TitleScreen)) return;
		var cfg = cfg0;
		String name;
		if (vUid == null) {
			// 0.9.5: wait for Valheim's world identity, but never forever: after 20 s open the legacy/default world
			long now = System.currentTimeMillis();
			if (waitSince == 0) { waitSince = now; ValCraft.LOG.info("[autoworld] waiting for Valheim world identity..."); }
			if (now - waitSince < 20000) { if (now - lastWaitLog > 5000) { lastWaitLog = now; ValCraft.LOG.info("[autoworld] still waiting for Valheim world ({} s)", (now - waitSince) / 1000); } return; }
			name = cfg.getProperty("worldName", "ValCraft").trim();
			ValCraft.LOG.warn("[autoworld] no Valheim world identity after 20 s: opening default world '{}'", name);
		} else {
			name = folderFor(mc, cfg);
			ValCraft.LOG.info("[autoworld] got identity '{}' (uid {}) -> MC world '{}'", vName, vUid, name);
		}
		tried = true;
		try {
			if (mc.getLevelSource().levelExists(name)) {
				ValCraft.LOG.info("[autoworld] opening world '{}'", name);
				mc.createWorldOpenFlows().openWorld(name, () -> mc.setScreen(new TitleScreen()));
				return;
			}
			GameType mode = GameType.byName(cfg.getProperty("gameMode", "survival").trim(), GameType.SURVIVAL);
			boolean flat = "flat".equalsIgnoreCase(cfg.getProperty("generator", "void").trim());
			ValCraft.LOG.info("[autoworld] creating world '{}' ({}, {})", name, flat ? "superflat" : "void", mode.getName());
			var settings = new LevelSettings(name, mode, false, Difficulty.NORMAL, true, new GameRules(), WorldDataConfiguration.DEFAULT);
			var options = new WorldOptions(WorldOptions.randomSeed(), false, false);
			mc.createWorldOpenFlows().createFreshLevel(name, settings, options, ra -> {
				var dims = ra.registryOrThrow(Registries.WORLD_PRESET).getHolderOrThrow(WorldPresets.FLAT).value().createWorldDimensions();
				if (flat) return dims;
				var voidPreset = ra.registryOrThrow(Registries.FLAT_LEVEL_GENERATOR_PRESET).getHolderOrThrow(FlatLevelGeneratorPresets.THE_VOID).value();
				return dims.replaceOverworldGenerator(ra, new FlatLevelSource(voidPreset.settings()));
			}, new TitleScreen());
		} catch (Exception e) {
			ValCraft.LOG.error("[autoworld] failed: {}", e.toString());
		}
	}
}
