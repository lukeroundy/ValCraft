package dev.valcraft.client;

import dev.valcraft.ValCraftConfig;
import net.neoforged.neoforge.client.event.sound.PlaySoundEvent;
import net.neoforged.neoforge.common.NeoForge;

/**
 * 0.15.1: per mob family sound swap (config/valcraft.properties swapSounds.&lt;family&gt;, set by the launcher).
 * true (default): the Valheim stand-in is silent and the Minecraft mob sound plays. false: Minecraft's sound for that
 * family is muted while linked and the Valheim stand-in keeps its Valheim sounds.
 */
public final class MobSounds {
	private MobSounds() {}
	public static final String[] FAMILIES = {"zombie", "skeleton", "creeper", "spider", "enderman", "slime", "illager", "animal", "villager", "other"};

	public static String family(String mob) {
		String m = mob.toLowerCase(java.util.Locale.ROOT).replace(' ', '_');
		if (m.contains("zombie") || m.equals("husk") || m.equals("drowned") || m.contains("piglin") || m.equals("zoglin")) return "zombie";
		if (m.contains("skeleton") || m.equals("stray") || m.equals("bogged")) return "skeleton";
		if (m.contains("creeper")) return "creeper";
		if (m.contains("spider")) return "spider";
		if (m.contains("enderman") || m.contains("endermite")) return "enderman";
		if (m.contains("slime") || m.contains("magma_cube")) return "slime";
		if (m.equals("pillager") || m.equals("vindicator") || m.equals("evoker") || m.equals("ravager") || m.equals("witch") || m.equals("illusioner") || m.equals("vex")) return "illager";
		if (m.contains("villager") || m.contains("wandering_trader")) return "villager";
		if (m.matches("cow|pig|sheep|chicken|horse|donkey|mule|rabbit|wolf|cat|ocelot|fox|goat|llama|trader_llama|parrot|bee|turtle|panda|polar_bear|mooshroom|frog|axolotl|camel|sniffer|armadillo|strider|hoglin|goat")) return "animal";
		return "other";
	}

	public static void register() {
		NeoForge.EVENT_BUS.addListener((PlaySoundEvent e) -> {
			if (e.getSound() == null || !ValCraftClient.linked()) return;
			var loc = e.getSound().getLocation(); if (loc == null || !loc.getPath().startsWith("entity.")) return;
			String[] parts = loc.getPath().split("\\.");
			if (parts.length < 3) return;
			if (!ValCraftConfig.swapSounds(family(parts[1]))) e.setSound(null);
		});
	}
}
