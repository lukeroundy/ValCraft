package dev.valcraft.mixin;

import dev.valcraft.client.CollisionWorld;
import dev.valcraft.client.ValCraftClient;
import net.minecraft.server.network.ServerGamePacketListenerImpl;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Integrated server: don't rubber-band the player for overlapping MC blocks we deliberately ignore. */
@Mixin(ServerGamePacketListenerImpl.class)
public abstract class ServerMoveCheckMixin {
	@Inject(method = "isPlayerCollidingWithAnythingNew", at = @At("HEAD"), cancellable = true)
	private void valcraft$ignore(CallbackInfoReturnable<Boolean> cir) {
		if (CollisionWorld.IGNORE_MC_BLOCKS && ValCraftClient.collisionActive() && (!ValCraftClient.terrainMode() || dev.valcraft.client.HeightField.active())) cir.setReturnValue(false);
	}
}
