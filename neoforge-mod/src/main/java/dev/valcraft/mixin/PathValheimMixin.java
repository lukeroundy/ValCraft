package dev.valcraft.mixin;

import dev.valcraft.client.CollisionWorld;
import net.minecraft.world.level.pathfinder.PathType;
import net.minecraft.world.level.pathfinder.PathfindingContext;
import net.minecraft.world.level.pathfinder.WalkNodeEvaluator;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * 0.10.0: walking mobs path around Valheim walls/pieces. A node is blocked when a Valheim box fills the body band of the cell
 * (y+1.05 .. y+1.8 above the cell floor), so ground-hugging terrain sample boxes (tops below y+1) don't block everything.
 */
@Mixin(WalkNodeEvaluator.class)
public abstract class PathValheimMixin {
	@Inject(method = "getPathType(Lnet/minecraft/world/level/pathfinder/PathfindingContext;III)Lnet/minecraft/world/level/pathfinder/PathType;", at = @At("HEAD"), cancellable = true)
	private void valcraft$valheimWalls(PathfindingContext ctx, int x, int y, int z, CallbackInfoReturnable<PathType> cir) {
		if (!CollisionWorld.ready()) return;
		if (CollisionWorld.occupied(x + 0.1, y + 0.3, z + 0.1, x + 0.9, y + 1.6, z + 0.9) && CollisionWorld.occupied(x + 0.1, y + 1.05, z + 0.1, x + 0.9, y + 1.8, z + 0.9))
			cir.setReturnValue(PathType.BLOCKED);
	}
}
