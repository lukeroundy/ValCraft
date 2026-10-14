package dev.valcraft.content;

import com.mojang.serialization.MapCodec;
import net.minecraft.core.NonNullList;
import net.minecraft.core.registries.Registries;
import net.minecraft.network.RegistryFriendlyByteBuf;
import net.minecraft.network.codec.StreamCodec;
import net.minecraft.tags.ItemTags;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.crafting.CraftingInput;
import net.minecraft.world.item.crafting.RecipeSerializer;
import net.minecraft.world.item.crafting.ShapelessRecipe;
import net.neoforged.bus.api.IEventBus;
import net.neoforged.neoforge.registries.DeferredRegister;

import java.util.function.Supplier;

/** 0.15.6: shapeless recipe ("valcraft:crafting_keep_tool") whose hoe ingredient (#minecraft:hoes) is not consumed:
 *  it stays in the grid with 1 durability used (a hoe on its last point breaks). Used for dirt + hoe -> farmland. */
public class KeepToolRecipe extends ShapelessRecipe {
	public KeepToolRecipe(ShapelessRecipe r) { super(r.getGroup(), r.category(), r.getResultItem(null), r.getIngredients()); }

	public static ItemStack remainderOf(ItemStack st) {
		if (!st.is(ItemTags.HOES)) return ItemStack.EMPTY;
		ItemStack k = st.copyWithCount(1);
		if (k.isDamageableItem()) {
			int d = k.getDamageValue() + 1;
			if (d >= k.getMaxDamage()) return ItemStack.EMPTY;
			k.setDamageValue(d);
		}
		return k;
	}

	@Override public NonNullList<ItemStack> getRemainingItems(CraftingInput in) {
		NonNullList<ItemStack> out = super.getRemainingItems(in);
		for (int i = 0; i < in.size(); i++) { ItemStack r = remainderOf(in.getItem(i)); if (!r.isEmpty()) out.set(i, r); }
		return out;
	}

	@Override public RecipeSerializer<?> getSerializer() { return SERIALIZER.get(); }

	public static final class Serializer implements RecipeSerializer<KeepToolRecipe> {
		static final MapCodec<KeepToolRecipe> CODEC = RecipeSerializer.SHAPELESS_RECIPE.codec().xmap(KeepToolRecipe::new, r -> r);
		static final StreamCodec<RegistryFriendlyByteBuf, KeepToolRecipe> STREAM = RecipeSerializer.SHAPELESS_RECIPE.streamCodec().map(KeepToolRecipe::new, r -> r);
		@Override public MapCodec<KeepToolRecipe> codec() { return CODEC; }
		@Override public StreamCodec<RegistryFriendlyByteBuf, KeepToolRecipe> streamCodec() { return STREAM; }
	}

	static final DeferredRegister<RecipeSerializer<?>> REG = DeferredRegister.create(Registries.RECIPE_SERIALIZER, "valcraft");
	public static final Supplier<RecipeSerializer<KeepToolRecipe>> SERIALIZER = REG.register("crafting_keep_tool", Serializer::new);
	public static void register(IEventBus modBus) { REG.register(modBus); }
}
