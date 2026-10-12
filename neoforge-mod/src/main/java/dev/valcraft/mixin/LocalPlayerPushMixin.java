package dev.valcraft.mixin;

import dev.valcraft.client.HeightField;
import net.minecraft.client.player.LocalPlayer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** On smooth terrain the player may overlap terrain blocks (they're under/through the smooth surface): no push-out. */
@Mixin(LocalPlayer.class)
public abstract class LocalPlayerPushMixin {
	@Inject(method = "moveTowardsClosestSpace", at = @At("HEAD"), cancellable = true)
	private void valcraft$noPush(double x, double z, CallbackInfo ci) {
		if (HeightField.active()) ci.cancel();
	}
}
