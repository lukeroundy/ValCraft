package dev.valcraft.mixin;

import dev.valcraft.client.CollisionWorld;
import net.minecraft.world.entity.projectile.AbstractArrow;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.BlockHitResult;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;

/** 0.10.1: arrows stop at Valheim structures (hit point = block hit on air); 0.12.0: they stay stuck there (ArrowStickMixin). */
@Mixin(AbstractArrow.class)
public abstract class ArrowClipMixin {
	@Redirect(method = "tick", at = @At(value = "INVOKE", target = "Lnet/minecraft/world/level/Level;clip(Lnet/minecraft/world/level/ClipContext;)Lnet/minecraft/world/phys/BlockHitResult;"))
	private BlockHitResult valcraft$clip(Level level, ClipContext ctx) {
		BlockHitResult mc = level.clip(ctx);
		var r = CollisionWorld.clip(ctx, mc);
		return r instanceof BlockHitResult b ? b : mc;
	}
}
