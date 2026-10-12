package dev.valcraft.mixin;

import net.minecraft.world.entity.boss.enderdragon.EnderDragon;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.AABB;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** 0.11.0: an ender dragon outside the End doesn't chew through blocks (the mirrored Valheim terrain). */
@Mixin(EnderDragon.class)
public abstract class DragonNoGriefMixin {
	@Inject(method = "checkWalls", at = @At("HEAD"), cancellable = true)
	private void valcraft$noGrief(AABB box, CallbackInfoReturnable<Boolean> cir) {
		var self = (EnderDragon) (Object) this;
		if (self.level().dimension() != Level.END) cir.setReturnValue(false);
	}
}
