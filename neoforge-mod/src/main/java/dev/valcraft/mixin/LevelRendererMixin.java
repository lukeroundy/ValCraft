package dev.valcraft.mixin;

import com.mojang.blaze3d.systems.RenderSystem;
import dev.valcraft.client.ValCraftClient;
import net.minecraft.client.Minecraft;
import net.minecraft.client.renderer.LevelRenderer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** While linked, Valheim draws the world: skip MC's level pass and leave a transparent background (hand + HUD still render). */
@Mixin(LevelRenderer.class)
public abstract class LevelRendererMixin {
	@Inject(method = "renderLevel", at = @At("HEAD"), cancellable = true)
	private void valcraft$skipWorld(CallbackInfo ci) {
		if (!ValCraftClient.active()) return;
		RenderSystem.clearColor(0f, 0f, 0f, 0f);
		RenderSystem.clear(16640, Minecraft.ON_OSX); // color + depth of the main target
		ci.cancel();
	}
}
