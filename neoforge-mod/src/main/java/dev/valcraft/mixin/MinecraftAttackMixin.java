package dev.valcraft.mixin;

import dev.valcraft.client.ValheimAttack;
import net.minecraft.client.Minecraft;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

@Mixin(Minecraft.class)
public abstract class MinecraftAttackMixin {
	@Inject(method = "startAttack", at = @At("HEAD"), cancellable = true)
	private void valcraft$valheimHit(CallbackInfoReturnable<Boolean> cir) {
		if (ValheimAttack.tryStart((Minecraft) (Object) this)) cir.setReturnValue(true);
	}

	@Inject(method = "continueAttack", at = @At("HEAD"), cancellable = true)
	private void valcraft$valheimHitHeld(boolean leftClick, CallbackInfo ci) {
		Minecraft mc = (Minecraft) (Object) this;
		if (ValheimAttack.tryContinue(mc, leftClick && mc.screen == null)) {
			if (mc.gameMode != null) mc.gameMode.stopDestroyBlock();
			ci.cancel();
		}
	}
}
