package dev.valcraft.content;

import dev.valcraft.ValCraft;
import net.minecraft.core.Holder;
import net.minecraft.core.registries.Registries;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.sounds.SoundEvents;
import net.minecraft.tags.BlockTags;
import net.minecraft.util.valueproviders.UniformInt;
import net.minecraft.world.item.*;
import net.minecraft.world.item.crafting.Ingredient;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.DropExperienceBlock;
import net.minecraft.world.level.block.SoundType;
import net.minecraft.world.level.block.state.BlockBehaviour;
import net.minecraft.world.level.material.MapColor;
import net.neoforged.bus.api.IEventBus;
import net.neoforged.neoforge.common.SimpleTier;
import net.neoforged.neoforge.event.BuildCreativeModeTabContentsEvent;
import net.neoforged.neoforge.registries.DeferredBlock;
import net.neoforged.neoforge.registries.DeferredHolder;
import net.neoforged.neoforge.registries.DeferredItem;
import net.neoforged.neoforge.registries.DeferredRegister;

import java.util.ArrayList;
import java.util.EnumMap;
import java.util.List;
import java.util.function.Supplier;

/**
 * 0.10.0: Valheim's tin and bronze in Minecraft. Tin sits between stone and iron, bronze about iron (like Valheim's
 * bronze age after copper). Bronze = 2 copper + 1 tin (Valheim's ratio). Placeholder item for unmapped Valheim chest items.
 */
public final class ModContent {
	private ModContent() {}
	public static final DeferredRegister.Blocks BLOCKS = DeferredRegister.createBlocks(ValCraft.MOD_ID);
	public static final DeferredRegister.Items ITEMS = DeferredRegister.createItems(ValCraft.MOD_ID);
	public static final DeferredRegister<ArmorMaterial> ARMOR = DeferredRegister.create(Registries.ARMOR_MATERIAL, ValCraft.MOD_ID);

	public static final DeferredBlock<Block> TIN_ORE = BLOCKS.register("tin_ore", () -> new DropExperienceBlock(UniformInt.of(0, 1),
		BlockBehaviour.Properties.of().mapColor(MapColor.STONE).requiresCorrectToolForDrops().strength(3f, 3f)));
	public static final DeferredBlock<Block> BRONZE_BLOCK = BLOCKS.register("bronze_block", () -> new Block(
		BlockBehaviour.Properties.of().mapColor(MapColor.COLOR_ORANGE).requiresCorrectToolForDrops().strength(5f, 6f).sound(SoundType.COPPER)));

	public static final DeferredItem<BlockItem> TIN_ORE_ITEM = ITEMS.registerSimpleBlockItem(TIN_ORE);
	public static final DeferredItem<BlockItem> BRONZE_BLOCK_ITEM = ITEMS.registerSimpleBlockItem(BRONZE_BLOCK);
	public static final DeferredItem<Item> RAW_TIN = ITEMS.registerSimpleItem("raw_tin");
	public static final DeferredItem<Item> TIN_INGOT = ITEMS.registerSimpleItem("tin_ingot");
	public static final DeferredItem<Item> TIN_NUGGET = ITEMS.registerSimpleItem("tin_nugget");
	public static final DeferredItem<Item> BRONZE_INGOT = ITEMS.registerSimpleItem("bronze_ingot");
	public static final DeferredItem<Item> VALHEIM_ITEM = ITEMS.register("valheim_item", () -> new Item(new Item.Properties().stacksTo(64)));

	public static final Tier TIN_TIER = new SimpleTier(BlockTags.INCORRECT_FOR_STONE_TOOL, 190, 5.0f, 1.5f, 10, () -> Ingredient.of(TIN_INGOT.get()));
	public static final Tier BRONZE_TIER = new SimpleTier(BlockTags.INCORRECT_FOR_IRON_TOOL, 280, 6.5f, 2.0f, 12, () -> Ingredient.of(BRONZE_INGOT.get()));

	public static final DeferredHolder<ArmorMaterial, ArmorMaterial> TIN_ARMOR = ARMOR.register("tin", () -> material(new int[] { 2, 4, 5, 2 }, 10, 0f, TIN_INGOT, "tin"));
	public static final DeferredHolder<ArmorMaterial, ArmorMaterial> BRONZE_ARMOR = ARMOR.register("bronze", () -> material(new int[] { 2, 5, 6, 2 }, 12, 0.5f, BRONZE_INGOT, "bronze"));

	private static ArmorMaterial material(int[] def, int ench, float tough, Supplier<Item> repair, String name) {
		var m = new EnumMap<ArmorItem.Type, Integer>(ArmorItem.Type.class);
		m.put(ArmorItem.Type.BOOTS, def[0]); m.put(ArmorItem.Type.LEGGINGS, def[1]); m.put(ArmorItem.Type.CHESTPLATE, def[2]); m.put(ArmorItem.Type.HELMET, def[3]); m.put(ArmorItem.Type.BODY, def[2]);
		return new ArmorMaterial(m, ench, SoundEvents.ARMOR_EQUIP_IRON, () -> Ingredient.of(repair.get()),
			List.of(new ArmorMaterial.Layer(ResourceLocation.fromNamespaceAndPath(ValCraft.MOD_ID, name))), tough, 0f);
	}

	public static final List<DeferredItem<? extends Item>> TOOLS = new ArrayList<>(), ARMOR_ITEMS = new ArrayList<>();
	static { for (String n : new String[] { "tin", "bronze" }) tools(n, n.equals("tin") ? TIN_TIER : BRONZE_TIER); }
	static { armor("tin", TIN_ARMOR, 13); armor("bronze", BRONZE_ARMOR, 16); }

	private static void tools(String n, Tier t) {
		TOOLS.add(ITEMS.register(n + "_sword", () -> new SwordItem(t, new Item.Properties().attributes(SwordItem.createAttributes(t, 3, -2.4f)))));
		TOOLS.add(ITEMS.register(n + "_pickaxe", () -> new PickaxeItem(t, new Item.Properties().attributes(DiggerItem.createAttributes(t, 1f, -2.8f)))));
		TOOLS.add(ITEMS.register(n + "_axe", () -> new AxeItem(t, new Item.Properties().attributes(DiggerItem.createAttributes(t, 6.5f, -3.1f)))));
		TOOLS.add(ITEMS.register(n + "_shovel", () -> new ShovelItem(t, new Item.Properties().attributes(DiggerItem.createAttributes(t, 1.5f, -3f)))));
		TOOLS.add(ITEMS.register(n + "_hoe", () -> new HoeItem(t, new Item.Properties().attributes(DiggerItem.createAttributes(t, -1.5f, -1.5f)))));
	}
	private static void armor(String n, Holder<ArmorMaterial> m, int durMul) {
		ARMOR_ITEMS.add(ITEMS.register(n + "_helmet", () -> new ArmorItem(m, ArmorItem.Type.HELMET, new Item.Properties().durability(ArmorItem.Type.HELMET.getDurability(durMul)))));
		ARMOR_ITEMS.add(ITEMS.register(n + "_chestplate", () -> new ArmorItem(m, ArmorItem.Type.CHESTPLATE, new Item.Properties().durability(ArmorItem.Type.CHESTPLATE.getDurability(durMul)))));
		ARMOR_ITEMS.add(ITEMS.register(n + "_leggings", () -> new ArmorItem(m, ArmorItem.Type.LEGGINGS, new Item.Properties().durability(ArmorItem.Type.LEGGINGS.getDurability(durMul)))));
		ARMOR_ITEMS.add(ITEMS.register(n + "_boots", () -> new ArmorItem(m, ArmorItem.Type.BOOTS, new Item.Properties().durability(ArmorItem.Type.BOOTS.getDurability(durMul)))));
	}

	public static void register(IEventBus bus) {
		BLOCKS.register(bus); ITEMS.register(bus); ARMOR.register(bus);
		bus.addListener(ModContent::tabs);
	}

	private static void tabs(BuildCreativeModeTabContentsEvent e) {
		var k = e.getTabKey();
		if (k == CreativeModeTabs.INGREDIENTS) { e.accept(RAW_TIN); e.accept(TIN_NUGGET); e.accept(TIN_INGOT); e.accept(BRONZE_INGOT); }
		else if (k == CreativeModeTabs.NATURAL_BLOCKS) e.accept(TIN_ORE_ITEM);
		else if (k == CreativeModeTabs.BUILDING_BLOCKS) e.accept(BRONZE_BLOCK_ITEM);
		else if (k == CreativeModeTabs.TOOLS_AND_UTILITIES) { for (var t : TOOLS) if (!(t.get() instanceof SwordItem)) e.accept(t); }
		else if (k == CreativeModeTabs.COMBAT) { for (var t : TOOLS) if (t.get() instanceof SwordItem || t.get() instanceof AxeItem) e.accept(t); for (var a : ARMOR_ITEMS) e.accept(a); }
	}
}
