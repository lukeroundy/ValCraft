package dev.valcraft.client;

import dev.valcraft.ValCraft;
import net.minecraft.client.Minecraft;
import net.minecraft.client.NarratorStatus;
import net.minecraft.client.gui.screens.AccessibilityOnboardingScreen;

/** 0.14.3: a fresh Minecraft shows the accessibility onboarding ("Welcome to Minecraft!") before the title screen; with
 *  input owned by Valheim nobody can press Continue and the auto-world flow waited forever. Skip it and turn it off. */
public final class Onboarding {
	private static boolean optionsDone;

	static void tick(Minecraft mc) {
		if (!optionsDone && mc.options != null) {
			optionsDone = true;
			boolean changed = false;
			if (mc.options.onboardAccessibility) { mc.options.onboardAccessibility = false; changed = true; }
			if (mc.options.narrator().get() != NarratorStatus.OFF) { mc.options.narrator().set(NarratorStatus.OFF); changed = true; }
			if (changed) { mc.options.save(); ValCraft.LOG.info("[onboarding] accessibility onboarding + narrator turned off"); }
		}
		if (mc.screen instanceof AccessibilityOnboardingScreen s) {
			ValCraft.LOG.info("[onboarding] skipping the accessibility welcome screen");
			s.onClose(); // vanilla Continue: saves onboardAccessibility=false and opens the title screen
		}
	}

	/** 0.14.3: no Minecraft music while linked to Valheim (valcraft.properties muteMusic=true, default). */
	public static boolean muteMusic() {
		return ValCraftClient.active() && Boolean.parseBoolean(AutoWorld.config().getProperty("muteMusic", "true"));
	}
}
