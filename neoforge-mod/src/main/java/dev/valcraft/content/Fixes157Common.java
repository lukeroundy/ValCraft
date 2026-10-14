package dev.valcraft.content;

import net.minecraft.core.particles.ParticleTypes;
import net.minecraft.core.registries.Registries;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.sounds.SoundEvents;
import net.minecraft.sounds.SoundSource;
import net.minecraft.tags.FluidTags;
import net.minecraft.world.effect.MobEffect;
import net.minecraft.world.effect.MobEffectCategory;
import net.minecraft.world.effect.MobEffectInstance;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.neoforged.bus.api.IEventBus;
import net.neoforged.neoforge.common.NeoForge;
import net.neoforged.neoforge.event.tick.EntityTickEvent;
import net.neoforged.neoforge.registries.DeferredHolder;
import net.neoforged.neoforge.registries.DeferredRegister;

/** 0.15.7 common: the Rested effect (mirrors Valheim's Rested), sand in water becomes clay, XP for Valheim kills. */
public final class Fixes157Common {
	private Fixes157Common() {}

	/** Valheim's Rested: here a gentle hunger saver (removes a little exhaustion every 5 s) and +10% XP from orbs. */
	public static final class RestedEffect extends MobEffect {
		RestedEffect() { super(MobEffectCategory.BENEFICIAL, 0xF2C66D); }
		@Override public boolean shouldApplyEffectTickThisTick(int duration, int amp) { return duration % 100 == 0; }
		@Override public boolean applyEffectTick(LivingEntity e, int amp) {
			if (e instanceof Player p) { var f = p.getFoodData(); f.addExhaustion(-Math.min(1.0f, f.getExhaustionLevel())); }
			return true;
		}
	}
	static final DeferredRegister<MobEffect> EFFECTS = DeferredRegister.create(Registries.MOB_EFFECT, "valcraft");
	public static final DeferredHolder<MobEffect, RestedEffect> RESTED = EFFECTS.register("rested", RestedEffect::new);

	public static final int CLAY_TICKS = 50, CLAY_PER_SAND = 4;

	public static void register(IEventBus modBus) {
		EFFECTS.register(modBus);
		NeoForge.EVENT_BUS.addListener((EntityTickEvent.Post e) -> { if (e.getEntity() instanceof ItemEntity ie && !ie.level().isClientSide) sandTick(ie); });
		NeoForge.EVENT_BUS.addListener((net.neoforged.neoforge.event.entity.player.PlayerXpEvent.PickupXp e) -> {
			if (e.getEntity().hasEffect(RESTED)) { var o = e.getOrb(); o.value = Math.max(o.value, Math.round(o.value * 1.1f)); }
		});
	}

	/** Sand dropped into water turns into 4 clay balls per sand after ~2.5 s in the water. */
	static void sandTick(ItemEntity ie) {
		ItemStack st = ie.getItem();
		if (!st.is(Items.SAND)) return;
		var data = ie.getPersistentData();
		boolean wet = ie.isInWater() || ie.level().getFluidState(ie.blockPosition()).is(FluidTags.WATER) || ie.level().getFluidState(ie.blockPosition().below()).is(FluidTags.WATER) && ie.getY() - ie.blockPosition().getY() < 0.6; // floating on top of the water block
		if (!wet) { double s = dev.valcraft.client.ValheimWater.waterSurface(ie); wet = !Double.isNaN(s) && s > ie.getY(); } // Valheim water while linked
		if (!wet) { if (data.contains("valcraft_soak")) data.remove("valcraft_soak"); return; }
		int t = data.getInt("valcraft_soak") + 1;
		if (t < CLAY_TICKS) { data.putInt("valcraft_soak", t); return; }
		int n = st.getCount();
		ie.setItem(new ItemStack(Items.CLAY_BALL, Math.min(64, n * CLAY_PER_SAND)));
		for (int extra = n * CLAY_PER_SAND - 64; extra > 0; extra -= 64) {
			var more = new ItemEntity(ie.level(), ie.getX(), ie.getY(), ie.getZ(), new ItemStack(Items.CLAY_BALL, Math.min(64, extra)));
			ie.level().addFreshEntity(more);
		}
		data.remove("valcraft_soak");
		dev.valcraft.ValCraft.LOG.info("[clay] {} sand soaked in water at {} -> {} clay balls", n, ie.blockPosition(), n * CLAY_PER_SAND);
		if (ie.level() instanceof ServerLevel sl) {
			sl.sendParticles(ParticleTypes.SPLASH, ie.getX(), ie.getY() + 0.3, ie.getZ(), 16, 0.25, 0.1, 0.25, 0.1);
			sl.playSound(null, ie.blockPosition(), SoundEvents.MUD_PLACE, SoundSource.BLOCKS, 0.8f, 1.2f);
		}
	}

	/** Server: XP for a Valheim kill (EV_XP, i32 points). */
	public static void xpOn(ServerPlayer sp, byte[] p) {
		if (p == null || p.length < 4) return;
		int xp = java.nio.ByteBuffer.wrap(p).order(java.nio.ByteOrder.LITTLE_ENDIAN).getInt(0);
		if (xp <= 0 || xp > 100000) return;
		if (sp.hasEffect(RESTED)) xp = Math.round(xp * 1.1f);
		sp.giveExperiencePoints(xp);
		sp.serverLevel().playSound(null, sp.blockPosition(), SoundEvents.EXPERIENCE_ORB_PICKUP, SoundSource.PLAYERS, 0.4f, 0.9f + sp.getRandom().nextFloat() * 0.2f);
		dev.valcraft.ValCraft.LOG.info("[xp] Valheim kill: +{} XP", xp);
	}

	/** Server: Valheim Rested (EV_RESTED, f32 remaining seconds; 0 = ended). */
	public static void restedOn(ServerPlayer sp, byte[] p) {
		if (p == null || p.length < 4) return;
		float s = java.nio.ByteBuffer.wrap(p).order(java.nio.ByteOrder.LITTLE_ENDIAN).getFloat(0);
		if (s <= 0 || Float.isNaN(s)) { sp.removeEffect(RESTED); return; }
		sp.addEffect(new MobEffectInstance(RESTED, Math.min(20 * 60 * 60, Math.round(s * 20f)), 0, false, false, true));
	}
}
