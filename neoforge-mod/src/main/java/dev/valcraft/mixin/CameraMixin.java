package dev.valcraft.mixin;

import dev.valcraft.client.CollisionWorld;
import dev.valcraft.client.ValCraftClient;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.client.Camera;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.Vec3;
import net.minecraft.world.phys.shapes.VoxelShape;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;

/** 3rd-person camera distance is limited by Valheim geometry (collision boxes), not MC's hidden blocks. */
@Mixin(Camera.class)
public abstract class CameraMixin {
	@Redirect(method = "getMaxZoom", at = @At(value = "INVOKE",
			target = "Lnet/minecraft/world/level/BlockGetter;clip(Lnet/minecraft/world/level/ClipContext;)Lnet/minecraft/world/phys/BlockHitResult;"))
	private BlockHitResult valcraft$clipValheim(BlockGetter level, ClipContext ctx) {
		if (!ValCraftClient.collisionActive() || CollisionWorld.boxCount() == 0) return level.clip(ctx);
		Vec3 from = ctx.getFrom(), to = ctx.getTo();
		Vec3 best = null; double bestD = Double.MAX_VALUE;
		for (VoxelShape s : CollisionWorld.query(new AABB(from, to).inflate(0.1))) {
			var hit = s.bounds().clip(from, to);
			if (hit.isPresent()) { double d = hit.get().distanceToSqr(from); if (d < bestD) { bestD = d; best = hit.get(); } }
		}
		Vec3 dir = to.subtract(from);
		Direction face = Direction.getNearest(dir.x, dir.y, dir.z);
		return best == null ? BlockHitResult.miss(to, face, BlockPos.containing(to)) : new BlockHitResult(best, face.getOpposite(), BlockPos.containing(best), false);
	}
}
