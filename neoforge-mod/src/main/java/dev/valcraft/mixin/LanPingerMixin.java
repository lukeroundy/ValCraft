package dev.valcraft.mixin;

import dev.valcraft.net.NetFlags;
import net.minecraft.client.server.LanServerPinger;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** 0.13.0: no "LAN world" broadcast for the tunnel-only host world. */
@Mixin(LanServerPinger.class)
public abstract class LanPingerMixin {
	@Inject(method = "run", at = @At("HEAD"), cancellable = true)
	private void valcraft$quiet(CallbackInfo ci) { if (NetFlags.loopbackOnly) ci.cancel(); }
}
