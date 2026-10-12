package dev.valcraft.mixin;

import dev.valcraft.client.CollisionWorld;
import net.minecraft.tags.TagKey;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.level.material.Fluid;
import org.spongepowered.asm.mixin.Final;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

import java.util.Set;

/**
 * While linked (Valheim collision active), MC's own world must not affect the player: no water/lava physics, no
 * drowning or underwater state, no fire/cactus/cobweb/portal/powder-snow from blocks the player overlaps.
 */
@Mixin(Entity.class)
public abstract class McWorldIrrelevantMixin {
	@Shadow protected boolean wasEyeInWater;
	@Shadow protected boolean wasTouchingWater;
	@Shadow protected it.unimi.dsi.fastutil.objects.Object2DoubleMap<TagKey<Fluid>> fluidHeight;
	@Shadow(remap = false) protected it.unimi.dsi.fastutil.objects.Object2DoubleMap<net.neoforged.neoforge.fluids.FluidType> forgeFluidTypeHeight;
	@Shadow public abstract double getEyeY();
	@Shadow public abstract double getY();
	@Shadow public abstract void clearFire();
	@Shadow public abstract void resetFallDistance();
	@Shadow @Final private Set<TagKey<Fluid>> fluidOnEyes;

	private boolean valcraft$ignore() {
		return CollisionWorld.IGNORE_MC_BLOCKS && CollisionWorld.appliesTo((Entity) (Object) this);
	}

	@Inject(method = "updateFluidHeightAndDoFluidPushing()V", at = @At("HEAD"), cancellable = true, remap = false)
	private void valcraft$noFluids(CallbackInfo ci) { if (valcraft$ignore()) ci.cancel(); }

	@Inject(method = "updateFluidOnEyes", at = @At("TAIL"))
	private void valcraft$noEyeFluid(CallbackInfo ci) {
		if (!valcraft$ignore()) return;
		wasEyeInWater = false;
		fluidOnEyes.clear();
		((EntityFluidAccessor) this).valcraft$setForgeFluidTypeOnEyes(net.neoforged.neoforge.common.NeoForgeMod.EMPTY_TYPE.value());
		// 0.9.0: Valheim water counts as water (air supply, drowning, underwater view)
		double s = dev.valcraft.client.ValheimWater.waterSurface((Entity) (Object) this);
		if (!Double.isNaN(s) && getEyeY() - 0.11111 < s) {
			wasEyeInWater = true;
			fluidOnEyes.add(net.minecraft.tags.FluidTags.WATER);
			((EntityFluidAccessor) this).valcraft$setForgeFluidTypeOnEyes(net.neoforged.neoforge.common.NeoForgeMod.WATER_TYPE.value());
		}
	}

	/** 0.9.0: below the Valheim water surface the player is in water (vanilla swimming, no fall damage, fire out). */
	@Inject(method = "updateInWaterStateAndDoFluidPushing", at = @At("RETURN"), cancellable = true)
	private void valcraft$valheimWater(org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable<Boolean> cir) {
		if (!valcraft$ignore()) return;
		double s = dev.valcraft.client.ValheimWater.waterSurface((Entity) (Object) this);
		if (Double.isNaN(s) || s <= getY()) return;
		double depth = s - getY();
		fluidHeight.put(net.minecraft.tags.FluidTags.WATER, depth);
		forgeFluidTypeHeight.put(net.neoforged.neoforge.common.NeoForgeMod.WATER_TYPE.value(), depth);
		wasTouchingWater = true;
		resetFallDistance();
		clearFire();
		cir.setReturnValue(true);
	}

	@Inject(method = "checkInsideBlocks", at = @At("HEAD"), cancellable = true)
	private void valcraft$noInsideBlocks(CallbackInfo ci) { if (valcraft$ignore()) ci.cancel(); }
}
