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
		// 0.15.5: holding the build hammer, right-click is build-only (menu / sneak = rotate): never a Valheim interact or item use
		// on the station under the crosshair (that fed the hammer to cooking stations and blocked rotating workbenches/chests)
		boolean hammer = dev.valcraft.client.BuildLink.holdingHammer((Minecraft) (Object) this);
		if (hammer ? dev.valcraft.client.BuildLink.tryUse((Minecraft) (Object) this)
				: (ValheimInteract.tryInteract((Minecraft) (Object) this) || dev.valcraft.client.HoeLink.tryUse((Minecraft) (Object) this))) {
			rightClickDelay = 4;
			ci.cancel();
			return;
		}
		dev.valcraft.client.ValheimPlace.retarget((Minecraft) (Object) this); // 0.13.1
	}
}
