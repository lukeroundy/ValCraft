package dev.valcraft.mixin;

import dev.valcraft.client.ValheimWater;
import net.minecraft.world.entity.vehicle.Boat;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** 0.9.1: MC boats float on Valheim water (the MC world has no water blocks there). */
@Mixin(Boat.class)
public abstract class BoatWaterMixin {
	@Shadow private double waterLevel;

	@Inject(method = "checkInWater", at = @At("HEAD"), cancellable = true)
	private void valcraft$valheimWater(CallbackInfoReturnable<Boolean> cir) {
		var self = (Boat) (Object) this;
		double s = ValheimWater.waterSurface(self);
		if (Double.isNaN(s)) return;
		var bb = self.getBoundingBox();
		if (s > bb.minY + 0.001) { waterLevel = Math.min(s, bb.maxY); cir.setReturnValue(true); }
	}

	/** 0.11.0: hard clamp: a boat on Valheim water never ends a tick with its hull below the surface. */
	@Inject(method = "tick", at = @At("TAIL"))
	private void valcraft$clamp(org.spongepowered.asm.mixin.injection.callback.CallbackInfo ci) {
		var self = (Boat) (Object) this;
		double s = ValheimWater.waterSurface(self);
		if (Double.isNaN(s)) return;
		double floatY = s - 0.12; // hull slightly in the water, like vanilla's resting height
		if (self.getY() < floatY) {
			self.setPos(self.getX(), floatY, self.getZ());
			var v = self.getDeltaMovement();
			if (v.y < 0) self.setDeltaMovement(v.x, 0, v.z);
		}
	}

	@Inject(method = "isUnderwater", at = @At("HEAD"), cancellable = true)
	private void valcraft$valheimUnder(CallbackInfoReturnable<Boat.Status> cir) {
		var self = (Boat) (Object) this;
		double s = ValheimWater.waterSurface(self);
		if (Double.isNaN(s)) return;
		// 0.9.3: Valheim waves move the surface by tens of cm between samples; only call it submerged when clearly under (vanilla would sink + eject)
		var bb = self.getBoundingBox();
		// 0.11.0: never UNDER_WATER on Valheim water (boats must not sink); the tick clamp keeps them on the surface
		if (s > bb.minY - 0.25) { waterLevel = Math.min(s, bb.maxY); cir.setReturnValue(null); }
	}
}
