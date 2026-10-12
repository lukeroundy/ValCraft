package dev.valcraft.mixin;

import com.mojang.blaze3d.vertex.PoseStack;
import dev.valcraft.client.ValCraftClient;
import net.minecraft.client.Minecraft;
import net.minecraft.client.renderer.ScreenEffectRenderer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** No in-wall (block texture), underwater or on-fire screen overlays from MC's hidden world while linked. */
@Mixin(ScreenEffectRenderer.class)
public abstract class ScreenEffectMixin {
	@Inject(method = "renderScreenEffect", at = @At("HEAD"), cancellable = true)
	private static void valcraft$none(Minecraft mc, PoseStack pose, CallbackInfo ci) {
		if (ValCraftClient.active()) ci.cancel();
	}
}
