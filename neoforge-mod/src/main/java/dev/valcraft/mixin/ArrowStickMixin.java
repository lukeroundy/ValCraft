package dev.valcraft.mixin;

import dev.valcraft.client.CollisionWorld;
import net.minecraft.world.entity.projectile.AbstractArrow;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** 0.12.0: an arrow stuck in a Valheim structure (no MC block there) stays stuck instead of dropping. */
@Mixin(AbstractArrow.class)
public abstract class ArrowStickMixin {
	@Inject(method = "shouldFall", at = @At("HEAD"), cancellable = true)
	private void valcraft$stuckInValheim(CallbackInfoReturnable<Boolean> cir) {
		if (CollisionWorld.near(((AbstractArrow) (Object) this).position(), 0.12)) cir.setReturnValue(false);
	}
}
