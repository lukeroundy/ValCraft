package dev.valcraft.mixin;

import dev.valcraft.client.CollisionWorld;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.projectile.AbstractArrow;
import net.minecraft.world.entity.projectile.ProjectileUtil;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.BlockHitResult;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;

/** 0.10.1: Valheim structures block mob line of sight (targeting, ranged attacks). */
@Mixin(LivingEntity.class)
public abstract class LineOfSightMixin {
	@Redirect(method = "hasLineOfSight", at = @At(value = "INVOKE", target = "Lnet/minecraft/world/level/Level;clip(Lnet/minecraft/world/level/ClipContext;)Lnet/minecraft/world/phys/BlockHitResult;"))
	private BlockHitResult valcraft$los(Level level, ClipContext ctx) {
		BlockHitResult mc = level.clip(ctx);
		var r = CollisionWorld.clip(ctx, mc);
		return r instanceof BlockHitResult b ? b : mc;
	}
}
