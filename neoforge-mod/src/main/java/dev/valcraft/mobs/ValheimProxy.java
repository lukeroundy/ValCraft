package dev.valcraft.mobs;

import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.*;
import net.minecraft.world.entity.ai.attributes.AttributeSupplier;
import net.minecraft.world.entity.ai.attributes.Attributes;
import net.minecraft.world.level.Level;

/**
 * 0.9.0: invisible, AI-less stand-in for a Valheim monster so MC hostiles target and hit it. Never saved, not
 * pickable by the player's crosshair, positioned by MobSync every update; damage is forwarded to Valheim.
 */
public class ValheimProxy extends Mob {
	public int vid; public long lastSeen;
	private float w = 0.8f, h = 1.8f;

	public ValheimProxy(EntityType<? extends ValheimProxy> type, Level level) {
		super(type, level);
		setNoAi(true); setNoGravity(true); setSilent(true); setPersistenceRequired();
		noPhysics = true;
	}

	public static AttributeSupplier.Builder attributes() { return Mob.createMobAttributes().add(Attributes.MAX_HEALTH, 1000.0); }

	public void setSize(float nw, float nh) {
		nw = Math.max(0.3f, nw); nh = Math.max(0.3f, nh);
		if (Math.abs(nw - w) > 0.05f || Math.abs(nh - h) > 0.05f) { w = nw; h = nh; refreshDimensions(); }
	}

	@Override public EntityDimensions getDefaultDimensions(Pose pose) { return EntityDimensions.scalable(w, h); }
	@Override public boolean isPickable() { return false; }
	@Override public boolean canBeHitByProjectile() { return isAlive(); }
	@Override public boolean isPushable() { return false; }
	@Override protected void doPush(Entity e) {}
	@Override public void push(Entity e) {}
	@Override public boolean shouldBeSaved() { return false; }
	@Override public boolean removeWhenFarAway(double d) { return false; }
	@Override public boolean isInvulnerableTo(DamageSource s) { return false; }

	@Override public boolean hurt(DamageSource source, float amount) {
		if (level().isClientSide || amount <= 0 || vid == 0) return false;
		Entity att = source.getEntity();
		MobSync.forwardToValheim(vid, amount, att == null ? 0 : att.getId());
		return true; // health untouched: Valheim owns the monster's health
	}

	@Override public void tick() {
		super.tick();
		if (!level().isClientSide && System.currentTimeMillis() - lastSeen > 3000) discard();
	}
}
