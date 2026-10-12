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
		if (s > bb.minY + 0.001 && s <= bb.maxY + 0.001) { waterLevel = s; cir.setReturnValue(true); }
	}

	@Inject(method = "isUnderwater", at = @At("HEAD"), cancellable = true)
	private void valcraft$valheimUnder(CallbackInfoReturnable<Boat.Status> cir) {
		var self = (Boat) (Object) this;
		double s = ValheimWater.waterSurface(self);
		if (!Double.isNaN(s) && s > self.getBoundingBox().maxY + 0.001) cir.setReturnValue(Boat.Status.UNDER_WATER);
	}
}
