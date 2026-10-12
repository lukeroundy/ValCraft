package dev.valcraft.content;

import dev.valcraft.ValCraftConfig;
import net.minecraft.tags.BlockTags;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.neoforged.neoforge.event.level.BlockEvent;

/** 0.12.0: breaking stone (survival) has a very small chance (config emeraldChance, default 0.5%) to drop an emerald. */
public final class Emerald {
	private Emerald() {}
	public static void onBreak(BlockEvent.BreakEvent e) {
		var st = e.getState();
		if (e.getPlayer() == null || e.getPlayer().isCreative() || e.getLevel().isClientSide()) return;
		if (!(st.is(BlockTags.BASE_STONE_OVERWORLD) || st.is(Blocks.COBBLESTONE) || st.is(Blocks.MOSSY_COBBLESTONE))) return;
		double c = ValCraftConfig.emeraldChance();
		if (c <= 0 || e.getPlayer().getRandom().nextDouble() >= c) return;
		if (e.getLevel() instanceof net.minecraft.world.level.Level lvl) Block.popResource(lvl, e.getPos(), new ItemStack(Items.EMERALD));
	}
}
