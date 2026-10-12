package dev.valcraft.client;

import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.Minecraft;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.item.*;
import net.minecraft.world.phys.HitResult;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * Phase B: left-click on a Valheim rock/ore/tree/destructible (Valheim tells us via EV_ATTACK_TARGET what its camera
 * aims at) sends EV_ATTACK(tool kind, MC tier) to Valheim instead of attacking/mining in MC. One hit per click, and
 * while held one per SWING_TICKS (about a Valheim tool swing).
 */
public final class ValheimAttack {
	private ValheimAttack() {}
	static final int SWING_TICKS = 14;
	private static volatile boolean target;
	private static int cooldown;

	static void receiveTarget(byte[] p) { target = p.length > 0 && p[0] != 0; }
	static void clear() { target = false; cooldown = 0; }
	public static boolean hasTarget() { return target && ValCraftClient.active(); }

	/** startAttack HEAD: true = handled by Valheim, cancel MC's attack. */
	public static boolean tryStart(Minecraft mc) {
		if (!hasTarget() || mc.player == null) return false;
		if (mc.hitResult != null && mc.hitResult.getType() == HitResult.Type.ENTITY) return false;
		send(mc);
		cooldown = SWING_TICKS;
		return true;
	}

	/** continueAttack HEAD while the button is held: true = cancel MC block breaking. */
	public static boolean tryContinue(Minecraft mc, boolean held) {
		if (!held || !hasTarget() || mc.player == null) return false;
		if (mc.hitResult != null && mc.hitResult.getType() == HitResult.Type.ENTITY) return false;
		if (--cooldown <= 0) { send(mc); cooldown = SWING_TICKS; }
		return true;
	}

	private static void send(Minecraft mc) {
		ItemStack st = mc.player.getMainHandItem();
		Item it = st.getItem();
		int kind = it instanceof PickaxeItem ? 1 : it instanceof AxeItem ? 2 : it instanceof SwordItem ? 3 : it instanceof ShovelItem ? 4 : it instanceof HoeItem ? 5 : 0;
		int tier = -1;
		if (it instanceof TieredItem ti) {
			Tier t = ti.getTier();
			tier = t == Tiers.WOOD ? 0 : t == Tiers.STONE ? 1 : t == Tiers.IRON ? 2 : t == Tiers.GOLD ? 3 : t == Tiers.DIAMOND ? 4 : t == Tiers.NETHERITE ? 5 : 2;
		}
		byte[] p = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putInt(kind).putInt(tier).array();
		ValCraftClient.sendToValheim(ValCraftLink.EV_ATTACK, p);
		mc.player.swing(InteractionHand.MAIN_HAND);
	}
}
