package dev.valcraft.mixin;

import dev.valcraft.client.CollisionWorld;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.shapes.VoxelShape;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.Redirect;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

import java.util.ArrayList;
import java.util.List;

/**
 * Players collide with Valheim boxes: they're added to the "entity collisions" list Entity.collide passes into
 * collideBoundingBox, which is also used for the step-up attempts (stairs, 0.6 block steps).
 * MC's own blocks are ignored for players while linked (Valheim is the world), and so is suffocation.
 * Applies to the client LocalPlayer and the integrated server's ServerPlayer alike, so the server agrees.
 */
@Mixin(Entity.class)
public abstract class EntityCollisionMixin {
	@Redirect(method = "collide", at = @At(value = "INVOKE", target = "Lnet/minecraft/world/level/Level;getEntityCollisions(Lnet/minecraft/world/entity/Entity;Lnet/minecraft/world/phys/AABB;)Ljava/util/List;"))
	private List<VoxelShape> valcraft$addValheim(Level level, Entity entity, AABB box) {
		List<VoxelShape> base = level.getEntityCollisions(entity, box);
		if (!CollisionWorld.appliesTo(entity)) return base;
		List<VoxelShape> extra = CollisionWorld.query(box.expandTowards(0, 1.0, 0)); // include room for step-up
		if (extra.isEmpty()) return base;
		List<VoxelShape> all = new ArrayList<>(base.size() + extra.size());
		all.addAll(base);
		all.addAll(extra);
		return all;
	}

	@Redirect(method = "collectColliders", at = @At(value = "INVOKE", target = "Lnet/minecraft/world/level/Level;getBlockCollisions(Lnet/minecraft/world/entity/Entity;Lnet/minecraft/world/phys/AABB;)Ljava/lang/Iterable;"))
	private static Iterable<VoxelShape> valcraft$ignoreMcBlocks(Level level, Entity entity, AABB box) {
		if (CollisionWorld.IGNORE_MC_BLOCKS && CollisionWorld.appliesTo(entity)) return List.of();
		return level.getBlockCollisions(entity, box);
	}

	@Inject(method = "isInWall", at = @At("HEAD"), cancellable = true)
	private void valcraft$noSuffocation(CallbackInfoReturnable<Boolean> cir) {
		if (CollisionWorld.IGNORE_MC_BLOCKS && CollisionWorld.appliesTo((Entity) (Object) this)) cir.setReturnValue(false);
	}
}
