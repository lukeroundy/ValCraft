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
			tier = t == Tiers.WOOD ? 0 : t == Tiers.STONE ? 1 : t == Tiers.IRON ? 2 : t == Tiers.GOLD ? 3 : t == Tiers.DIAMOND ? 4 : t == Tiers.NETHERITE ? 5
					: t.getUses() < 200 ? 1 : t.getUses() < 600 ? 2 : t.getUses() < 1800 ? 4 : 5; // 0.11.0: ValCraft tiers by durability
		}
		float str = mc.player.getAttackStrengthScale(0.5f);
		mc.player.resetAttackStrengthTicker();
		final int fKind = kind, fTier = tier;
		// 0.11.0: the final vanilla melee damage, computed on the integrated server like Player.attack: ATTACK_DAMAGE attribute
		// (item modifiers incl. multipliers, Strength/Weakness), enchantment bonus (vs. the Valheim proxy if there is one),
		// cooldown scaling and critical hits. Valheim multiplies it by [Combat] DamageScale only.
		var server = mc.getSingleplayerServer();
		var uuid = mc.player.getUUID();
		boolean crit = str > 0.9f && mc.player.fallDistance > 0 && !mc.player.onGround() && !mc.player.onClimbable() && !mc.player.isInWater()
				&& !mc.player.hasEffect(net.minecraft.world.effect.MobEffects.BLINDNESS) && !mc.player.isPassenger() && !mc.player.isSprinting();
		if (server == null) return;
		server.execute(() -> {
			var sp = server.getPlayerList().getPlayer(uuid);
			if (sp == null) return;
			float base = (float) sp.getAttributeValue(net.minecraft.world.entity.ai.attributes.Attributes.ATTACK_DAMAGE);
			ItemStack held = sp.getMainHandItem();
			var src = sp.damageSources().playerAttack(sp);
			net.minecraft.world.entity.Entity target = nearestProxy(sp);
			float ench = 0f;
			try { ench = net.minecraft.world.item.enchantment.EnchantmentHelper.modifyDamage(sp.serverLevel(), held, target != null ? target : sp, src, base) - base; } catch (Throwable ignored) { }
			float dmg = base * (0.2f + str * str * 0.8f) + ench * str;
			if (crit) dmg *= 1.5f;
			byte[] p = ByteBuffer.allocate(20).order(ByteOrder.LITTLE_ENDIAN).putInt(fKind).putInt(fTier).putFloat(dmg).putFloat(str).putInt(1).array();
			mc.execute(() -> ValCraftClient.sendToValheim(ValCraftLink.EV_ATTACK, p));
		});
		mc.player.swing(InteractionHand.MAIN_HAND);
	}

	/** The Valheim proxy the player looks at (for enchantment bonuses like Smite), or null. Server thread. */
	private static net.minecraft.world.entity.Entity nearestProxy(net.minecraft.server.level.ServerPlayer sp) {
		var eye = sp.getEyePosition(); var look = sp.getLookAngle();
		net.minecraft.world.entity.Entity best = null; double bd = 0.9;
		for (var e : sp.serverLevel().getEntitiesOfClass(dev.valcraft.mobs.ValheimProxy.class, sp.getBoundingBox().inflate(6))) {
			var d = e.getBoundingBox().getCenter().subtract(eye); double len = d.length(); if (len < 1e-3) continue;
			double dot = d.dot(look) / len; if (dot > bd) { bd = dot; best = e; }
		}
		return best;
	}
}
