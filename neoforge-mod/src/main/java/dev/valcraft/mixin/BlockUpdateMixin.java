package dev.valcraft.mixin;

import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.block.state.BlockState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** 0.14.1: every server block change (players, Create machines, pistons...) marks its section for an immediate Valheim push. */
@Mixin(ServerLevel.class)
public abstract class BlockUpdateMixin {
	@Inject(method = "sendBlockUpdated", at = @At("HEAD"))
	private void valcraft$blockChanged(BlockPos pos, BlockState oldState, BlockState newState, int flags, CallbackInfo ci) {
		if (oldState == newState || !net.neoforged.fml.loading.FMLEnvironment.dist.isClient()) return;
		if ((Object) this != ((ServerLevel) (Object) this).getServer().overworld()) return;
		dev.valcraft.client.BlockSync.onBlockUpdated(pos.getX(), pos.getY(), pos.getZ());
	}
}
