package dev.valcraft.mixin;

import dev.valcraft.client.CollisionWorld;
import dev.valcraft.client.HeightField;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.Vec3;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * Linked player on streamed terrain: after normal collision (Valheim boxes + non-terrain blocks), keep the feet on
 * Valheim's smooth ground (bilinear 0.5 m heightfield at the player's centre, like Valheim's capsule) and stick to it
 * going downhill, so slopes and small bumps feel like Valheim instead of 1-block stairs.
 */
@Mixin(Entity.class)
public abstract class TerrainMoveMixin {
	private static final boolean DEBUG = Boolean.getBoolean("valcraft.debugMove");
	private static int dbg;
	/** After the move: standing on the smooth ground counts as on-ground (friction, jumping, no fall damage build-up). */
	@Inject(method = "move", at = @At("TAIL"))
	private void valcraft$groundFlag(net.minecraft.world.entity.MoverType type, Vec3 move, org.spongepowered.asm.mixin.injection.callback.CallbackInfo ci) {
		Entity self = (Entity) (Object) this;
		if (!HeightField.active() || !CollisionWorld.appliesTo(self) || self.noPhysics) return;
		AABB bb = self.getBoundingBox();
		double g = HeightField.height((bb.minX + bb.maxX) / 2, (bb.minZ + bb.maxZ) / 2);
		if (Double.isNaN(g) || Math.abs(bb.minY - g) > 0.02 || self.getDeltaMovement().y > 0) return;
		self.setOnGround(true);
		self.resetFallDistance();
		Vec3 d = self.getDeltaMovement();
		self.setDeltaMovement(d.x, 0, d.z);
	}

	@Inject(method = "collide", at = @At("RETURN"), cancellable = true)
	private void valcraft$smoothGround(Vec3 move, CallbackInfoReturnable<Vec3> cir) {
		Entity self = (Entity) (Object) this;
		if (!HeightField.active() || !CollisionWorld.appliesTo(self) || self.noPhysics) return;
		Vec3 r = cir.getReturnValue();
		AABB bb = self.getBoundingBox();
		double nx = (bb.minX + bb.maxX) / 2 + r.x, nz = (bb.minZ + bb.maxZ) / 2 + r.z;
		double g = HeightField.height(nx, nz);
		if (Double.isNaN(g)) return;
		double feet = bb.minY, ny = feet + r.y;
		if (DEBUG && self.level().isClientSide && (dbg++ % 10) == 0)
			dev.valcraft.ValCraft.LOG.info("[move] in {} out {} feet {} g {} onGround {}", move, r, String.format("%.3f", feet), String.format("%.3f", g), self.onGround());
		if (ny < g - 1e-4 && g - feet < 2.5) cir.setReturnValue(new Vec3(r.x, g - feet, r.z));      // lift onto ground
		else if (self.onGround() && move.y <= 0 && ny > g && ny - g < 0.6) cir.setReturnValue(new Vec3(r.x, g - feet, r.z)); // stick downhill
	}
}
