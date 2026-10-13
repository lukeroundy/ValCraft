package dev.valcraft.mixin;

import dev.valcraft.client.ValCraftClient;
import net.minecraft.client.renderer.LevelRenderer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** 0.12.3: while linked, Valheim draws the weather; MC only knows it (server state): no rain/snow drawing, splashes or rain sounds. */
@Mixin(LevelRenderer.class)
public abstract class NoRainRenderMixin {
	@Inject(method = "renderSnowAndRain", at = @At("HEAD"), cancellable = true)
	private void valcraft$noRain(CallbackInfo ci) { if (ValCraftClient.active()) ci.cancel(); }
	@Inject(method = "tickRain", at = @At("HEAD"), cancellable = true)
	private void valcraft$noRainTick(CallbackInfo ci) { if (ValCraftClient.active()) ci.cancel(); }
}
