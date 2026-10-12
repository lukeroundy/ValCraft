package dev.valcraft.mixin;

import dev.valcraft.client.ValCraftClient;
import net.minecraft.client.Minecraft;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** The hidden window never has OS focus; treat it as active so MC doesn't pause, drop mouse movement or release input. */
@Mixin(Minecraft.class)
public abstract class MinecraftMixin {
	@Inject(method = "isWindowActive", at = @At("HEAD"), cancellable = true)
	private void valcraft$alwaysActive(CallbackInfoReturnable<Boolean> cir) {
		if (ValCraftClient.active()) cir.setReturnValue(true);
	}
}
