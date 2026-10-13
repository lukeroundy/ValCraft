package dev.valcraft.mixin;

import dev.valcraft.client.ValCraftClient;
import net.minecraft.client.multiplayer.ClientLevel;
import net.minecraft.world.level.Level;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** 0.12.3: the client level reports no rain/thunder while linked (no sky darkening, fog, lightning flashes, ambient rain);
 * the integrated server keeps the real (Valheim-driven) weather for gameplay. */
@Mixin(Level.class)
public abstract class ClientRainLevelMixin {
	@Inject(method = "getRainLevel", at = @At("HEAD"), cancellable = true)
	private void valcraft$rain(float pt, CallbackInfoReturnable<Float> cir) { if ((Object) this instanceof ClientLevel && ValCraftClient.active()) cir.setReturnValue(0f); }
	@Inject(method = "getThunderLevel", at = @At("HEAD"), cancellable = true)
	private void valcraft$thunder(float pt, CallbackInfoReturnable<Float> cir) { if ((Object) this instanceof ClientLevel && ValCraftClient.active()) cir.setReturnValue(0f); }
}
