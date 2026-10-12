package dev.valcraft.mixin;

import dev.valcraft.client.CollisionWorld;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.item.MapItem;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.levelgen.Heightmap;
import net.minecraft.world.level.material.MapColor;
import net.minecraft.world.level.saveddata.maps.MapItemSavedData;
import net.minecraft.world.phys.AABB;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * 0.11.0: filled maps also show Valheim structures (from the collision snapshot, ~32 m around the player) in wood
 * colour on top of the mirrored terrain blocks, which vanilla maps already draw.
 */
@Mixin(MapItem.class)
public abstract class MapValheimMixin {
	@Inject(method = "update", at = @At("TAIL"))
	private void valcraft$structures(Level level, Entity viewer, MapItemSavedData data, CallbackInfo ci) {
		if (level.isClientSide || !CollisionWorld.ready() || data.dimension != level.dimension()) return;
		int s = 1 << data.scale;
		int cx = data.centerX, cz = data.centerZ;
		int px = (int) Math.floor((viewer.getX() - cx) / s) + 64, pz = (int) Math.floor((viewer.getZ() - cz) / s) + 64;
		int r = Math.max(1, 32 / s);
		byte wood = MapColor.WOOD.getPackedId(MapColor.Brightness.NORMAL), woodHi = MapColor.WOOD.getPackedId(MapColor.Brightness.HIGH);
		for (int x = Math.max(0, px - r); x < Math.min(128, px + r); x++)
			for (int z = Math.max(0, pz - r); z < Math.min(128, pz + r); z++) {
				int wx = cx + (x - 64) * s, wz = cz + (z - 64) * s;
				int ground = level.getHeight(Heightmap.Types.WORLD_SURFACE, wx, wz);
				double top = Double.NEGATIVE_INFINITY;
				for (var sh : CollisionWorld.query(new AABB(wx + 0.05, ground - 1, wz + 0.05, wx + s - 0.05, ground + 24, wz + s - 0.05)))
					top = Math.max(top, sh.bounds().maxY);
				if (top > ground + 0.4) data.updateColor(x, z, top > ground + 3 ? woodHi : wood);
			}
	}
}
