package dev.valcraft.mixin;

import net.minecraft.client.sounds.MusicManager;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** 0.14.3: Minecraft music off while linked (Valheim plays its own); config muteMusic in valcraft.properties. */
@Mixin(MusicManager.class)
public abstract class MusicManagerMixin {
	@Inject(method = "tick", at = @At("HEAD"), cancellable = true)
	private void valcraft$noMusic(CallbackInfo ci) {
		if (dev.valcraft.client.Onboarding.muteMusic()) { ((MusicManager) (Object) this).stopPlaying(); ci.cancel(); }
	}
}
