package dev.valcraft.mixin;

import dev.valcraft.client.ValheimInteract;
import net.minecraft.client.Minecraft;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

@Mixin(Minecraft.class)
public abstract class MinecraftUseMixin {
	@Shadow private int rightClickDelay;

	@Inject(method = "startUseItem", at = @At("HEAD"), cancellable = true)
	private void valcraft$valheimInteract(CallbackInfo ci) {
		if (ValheimInteract.tryInteract((Minecraft) (Object) this) || dev.valcraft.client.HoeLink.tryUse((Minecraft) (Object) this) || dev.valcraft.client.BuildLink.tryUse((Minecraft) (Object) this)) {
			rightClickDelay = 4;
			ci.cancel();
		}
	}
}
