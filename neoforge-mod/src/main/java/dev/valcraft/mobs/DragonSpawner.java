package dev.valcraft.mobs;

import dev.valcraft.ValCraft;
import dev.valcraft.ValCraftConfig;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.boss.enderdragon.EnderDragon;
import net.minecraft.world.entity.boss.enderdragon.phases.EnderDragonPhase;
import net.minecraft.world.level.biome.Biomes;
import net.minecraft.world.level.levelgen.Heightmap;

/**
 * 0.11.0: a rare ender dragon over the Deep North (MC biome end_highlands) in the overworld. Vanilla dragon, with its
 * "fight origin" (the point it circles and lands on) set to where it appeared; no EndDragonFight exists outside the End,
 * which the vanilla dragon tolerates (no portal/egg). Block griefing outside the End is off (DragonNoGriefMixin).
 */
public final class DragonSpawner {
	private DragonSpawner() {}
	private static int ticks;

	public static void tick(ServerLevel level) {
		if (++ticks < 1200) return; // once a minute
		ticks = 0;
		double chance = ValCraftConfig.dragonChancePerMinute();
		if (chance <= 0) return;
		for (var p : level.players()) {
			var biome = level.getBiome(p.blockPosition());
			if (!biome.is(Biomes.END_HIGHLANDS)) continue;
			if (level.getRandom().nextDouble() >= chance) continue;
			if (!level.getEntities(EntityType.ENDER_DRAGON, p.getBoundingBox().inflate(512), e -> true).isEmpty()) continue;
			int x = p.getBlockX() + level.getRandom().nextInt(81) - 40, z = p.getBlockZ() + level.getRandom().nextInt(81) - 40;
			int ground = level.getHeight(Heightmap.Types.MOTION_BLOCKING_NO_LEAVES, x, z);
			var d = EntityType.ENDER_DRAGON.create(level);
			if (d == null) return;
			d.moveTo(x + 0.5, Math.max(ground, p.getY()) + 40, z + 0.5, level.getRandom().nextFloat() * 360f, 0f);
			d.setFightOrigin(new BlockPos(x, ground, z));
			d.getPhaseManager().setPhase(EnderDragonPhase.HOLDING_PATTERN);
			level.addFreshEntity(d);
			ValCraft.LOG.info("[mobs] an ender dragon appeared over the Deep North at ({}, {}) (chance {}/min)", x, z, chance);
			return;
		}
	}
}
