package dev.valcraft;

import java.io.Reader;
import java.io.Writer;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Properties;

/** 0.11.0: MC-side options, config/valcraft.properties (created with defaults on first use). */
public final class ValCraftConfig {
	private ValCraftConfig() {}
	private static Properties props;

	private static synchronized Properties p() {
		if (props != null) return props;
		props = new Properties();
		props.setProperty("dragonChancePerMinute", "0.002");
		props.setProperty("emeraldChance", "0.005");
		Path f = Path.of("config", "valcraft.properties");
		try {
			if (Files.exists(f)) try (Reader r = Files.newBufferedReader(f)) { props.load(r); }
			else { Files.createDirectories(f.getParent()); try (Writer w = Files.newBufferedWriter(f)) { props.store(w, "ValCraft (Minecraft side). dragonChancePerMinute: chance per minute per player in the Deep North (end_highlands) that an ender dragon appears; 0 = never. emeraldChance: chance that breaking a stone block drops an emerald"); } }
		} catch (Exception e) { ValCraft.LOG.warn("[config] {}: {}", f, e.toString()); }
		return props;
	}

	/** 0.12.0: missing keys of older files get their defaults. */
	public static double emeraldChance() {
		try { return Double.parseDouble(p().getProperty("emeraldChance", "0.005").trim()); } catch (NumberFormatException e) { return 0.005; }
	}

	/** 0.15.1: launcher settings page: swapSounds.<family> (default true = Minecraft sound on, Valheim stand-in muted). */
	public static boolean swapSounds(String family) { return !"false".equalsIgnoreCase(p().getProperty("swapSounds." + family, "true").trim()); }

	public static double dragonChancePerMinute() {
		try { return Double.parseDouble(p().getProperty("dragonChancePerMinute", "0.002").trim()); } catch (NumberFormatException e) { return 0.002; }
	}
}
