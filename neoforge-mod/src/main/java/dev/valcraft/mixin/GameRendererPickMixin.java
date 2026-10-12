package dev.valcraft.mixin;

import dev.valcraft.client.ValCraftClient;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.client.renderer.GameRenderer;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.Vec3;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;

/**
 * While linked: crosshair never targets MC terrain (no outline, no breaking/using hidden blocks); entities stay pickable
 * at full range. Held items / hands render full-bright instead of taking MC's (often underground = black) light.
 */
@Mixin(GameRenderer.class)
public abstract class GameRendererPickMixin {
	@Redirect(method = "pick(Lnet/minecraft/world/entity/Entity;DDF)Lnet/minecraft/world/phys/HitResult;",
			at = @At(value = "INVOKE", target = "Lnet/minecraft/world/entity/Entity;pick(DFZ)Lnet/minecraft/world/phys/HitResult;"))
	private HitResult valcraft$noBlockPick(Entity e, double range, float pt, boolean fluids) {
		if (!ValCraftClient.active() || ValCraftClient.terrainMode()) return e.pick(range, pt, fluids); // terrain blocks are minable
		Vec3 eye = e.getEyePosition(pt), view = e.getViewVector(pt);
		Vec3 end = eye.add(view.scale(range));
		return BlockHitResult.miss(end, Direction.getNearest(view.x, view.y, view.z), BlockPos.containing(end));
	}

	@Redirect(method = "renderItemInHand", at = @At(value = "INVOKE",
			target = "Lnet/minecraft/client/renderer/entity/EntityRenderDispatcher;getPackedLightCoords(Lnet/minecraft/world/entity/Entity;F)I"))
	private int valcraft$handLight(net.minecraft.client.renderer.entity.EntityRenderDispatcher d, Entity e, float pt) {
		return ValCraftClient.active() ? 0xF000F0 : d.getPackedLightCoords(e, pt);
	}
}
