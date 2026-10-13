package dev.valcraft.mixin;

import dev.valcraft.client.ValCraftClient;
import net.minecraft.client.Minecraft;
import net.minecraft.client.multiplayer.LevelLoadStatusManager;
import net.minecraft.client.renderer.LevelRenderer;
import net.minecraft.core.BlockPos;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;

/** 0.13.0: MC's world isn't drawn while linked, so "Loading terrain..." waited for a section compile that never comes
 *  (30 s timeout on every join, very visible when joining through the tunnel). Ready once the player's chunk is there. */
@Mixin(LevelLoadStatusManager.class)
public abstract class LevelLoadMixin {
	@Redirect(method = "tick", at = @At(value = "INVOKE", target = "Lnet/minecraft/client/renderer/LevelRenderer;isSectionCompiled(Lnet/minecraft/core/BlockPos;)Z"))
	private boolean valcraft$ready(LevelRenderer r, BlockPos pos) {
		if (r.isSectionCompiled(pos)) return true;
		var lvl = Minecraft.getInstance().level;
		return ValCraftClient.active() && lvl != null && lvl.hasChunkAt(pos);
	}
}
