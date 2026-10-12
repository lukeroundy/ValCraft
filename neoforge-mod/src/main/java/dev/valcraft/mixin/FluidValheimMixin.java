package dev.valcraft.mixin;

import dev.valcraft.client.CollisionWorld;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.material.FlowingFluid;
import net.minecraft.world.level.material.Fluid;
import net.minecraft.world.level.material.FluidState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** 0.12.0: MC water/lava does not flow into cells occupied by Valheim buildings / stations (collision snapshot). */
@Mixin(FlowingFluid.class)
public abstract class FluidValheimMixin {
	@Inject(method = "canSpreadTo", at = @At("HEAD"), cancellable = true)
	private void valcraft$noSpreadIntoValheim(BlockGetter level, BlockPos from, BlockState fromState, Direction dir, BlockPos to, BlockState toState, FluidState toFluid, Fluid fluid, CallbackInfoReturnable<Boolean> cir) {
		if (CollisionWorld.occupies(to)) cir.setReturnValue(false);
	}
}
