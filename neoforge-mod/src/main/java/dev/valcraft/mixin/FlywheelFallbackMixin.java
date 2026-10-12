package dev.valcraft.mixin;

import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Pseudo;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * 0.10.0 (soft dependency, applies only if Flywheel is installed, e.g. with Create): while ValCraft captures a block-entity /
 * entity renderer, report "no visualization" so Create's renderers draw their classic (non-instanced) geometry into our capture.
 */
@Pseudo
@Mixin(targets = "dev.engine_room.flywheel.impl.visualization.VisualizationManagerImpl", remap = false)
public abstract class FlywheelFallbackMixin {
	@Inject(method = "supportsVisualization", at = @At("HEAD"), cancellable = true, remap = false, require = 0)
	private static void valcraft$fallbackWhileCapturing(CallbackInfoReturnable<Boolean> cir) {
		if (dev.valcraft.client.Capture.CAPTURING) cir.setReturnValue(false);
	}
}
