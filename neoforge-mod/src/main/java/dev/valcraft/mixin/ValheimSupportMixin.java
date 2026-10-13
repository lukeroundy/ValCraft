package dev.valcraft.mixin;

import dev.valcraft.client.CollisionWorld;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.block.SupportType;
import net.minecraft.world.level.block.state.BlockBehaviour;
import net.minecraft.world.level.block.state.BlockState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * 0.13.1: an empty cell whose face is backed by a Valheim floor/wall counts as a sturdy face (isFaceSturdy covers
 * canSupportCenter / canSupportRigidBlock): torches, lanterns, buttons, carpets, rails... can be placed on Valheim
 * buildings and survive neighbour updates.
 */
@Mixin(BlockBehaviour.BlockStateBase.class)
public abstract class ValheimSupportMixin {
	@Inject(method = "isFaceSturdy(Lnet/minecraft/world/level/BlockGetter;Lnet/minecraft/core/BlockPos;Lnet/minecraft/core/Direction;Lnet/minecraft/world/level/block/SupportType;)Z", at = @At("HEAD"), cancellable = true)
	private void valcraft$valheimFace(BlockGetter level, BlockPos pos, Direction dir, SupportType type, CallbackInfoReturnable<Boolean> cir) {
		BlockState self = (BlockState) (Object) this;
		if (!self.isAir() || !CollisionWorld.ready()) return;
		if (CollisionWorld.supportsFace(pos, dir)) cir.setReturnValue(true);
	}
}
