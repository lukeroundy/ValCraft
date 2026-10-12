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
		Path f = Path.of("config", "valcraft.properties");
		try {
			if (Files.exists(f)) try (Reader r = Files.newBufferedReader(f)) { props.load(r); }
			else { Files.createDirectories(f.getParent()); try (Writer w = Files.newBufferedWriter(f)) { props.store(w, "ValCraft (Minecraft side). dragonChancePerMinute: chance per minute per player in the Deep North (end_highlands) that an ender dragon appears; 0 = never"); } }
		} catch (Exception e) { ValCraft.LOG.warn("[config] {}: {}", f, e.toString()); }
		return props;
	}

	public static double dragonChancePerMinute() {
		try { return Double.parseDouble(p().getProperty("dragonChancePerMinute", "0.002").trim()); } catch (NumberFormatException e) { return 0.002; }
	}
}
