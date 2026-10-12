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

	static void reset() { tried = false; }

	static void tick(Minecraft mc) {
		if (tried || mc.level != null || !(mc.screen instanceof TitleScreen)) return;
		var cfg = config();
		if (!Boolean.parseBoolean(cfg.getProperty("autoWorld", "true"))) { tried = true; return; }
		tried = true;
		String name = cfg.getProperty("worldName", "ValCraft").trim();
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
