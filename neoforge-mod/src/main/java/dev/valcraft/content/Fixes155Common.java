package dev.valcraft.content;

import net.minecraft.resources.ResourceLocation;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.ai.attributes.AttributeModifier;
import net.minecraft.world.entity.ai.attributes.Attributes;
import net.neoforged.neoforge.common.NeoForge;
import net.neoforged.neoforge.event.entity.EntityJoinLevelEvent;

/** 0.15.5: players step up half a block more (MC step_height 0.6 -> 1.1, closer to Valheim's walking over small ledges). */
public final class Fixes155Common {
	private Fixes155Common() {}
	public static final ResourceLocation STEP = ResourceLocation.fromNamespaceAndPath("valcraft", "step_bonus");
	public static final double STEP_BONUS = 0.5;
	public static void register() {
		NeoForge.EVENT_BUS.addListener((EntityJoinLevelEvent e) -> { if (e.getEntity() instanceof ServerPlayer sp) applyStep(sp); });
	}
	public static void applyStep(ServerPlayer sp) {
		var a = sp.getAttribute(Attributes.STEP_HEIGHT);
		if (a != null && a.getModifier(STEP) == null) a.addPermanentModifier(new AttributeModifier(STEP, STEP_BONUS, AttributeModifier.Operation.ADD_VALUE));
	}
}
