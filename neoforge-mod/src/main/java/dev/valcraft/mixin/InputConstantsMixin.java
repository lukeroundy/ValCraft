package dev.valcraft.mixin;

import com.mojang.blaze3d.platform.InputConstants;
import dev.valcraft.client.ValCraftClient;
import dev.valcraft.client.VirtualInput;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

@Mixin(InputConstants.class)
public abstract class InputConstantsMixin {
	/** Polled key state (shift-click, F3 combos, tooltips) comes from forwarded input, not the hidden window. */
	@Inject(method = "isKeyDown", at = @At("HEAD"), cancellable = true)
	private static void valcraft$isKeyDown(long window, int key, CallbackInfoReturnable<Boolean> cir) {
		if (ValCraftClient.active()) cir.setReturnValue(VirtualInput.isDown(key));
	}

	/** Never capture/move the real OS cursor: Valheim owns it. MC still tracks grabbed state itself. */
	@Inject(method = "grabOrReleaseMouse", at = @At("HEAD"), cancellable = true)
	private static void valcraft$noCursorGrab(long window, int mode, double x, double y, CallbackInfo ci) {
		if (ValCraftClient.active()) ci.cancel();
	}
}
