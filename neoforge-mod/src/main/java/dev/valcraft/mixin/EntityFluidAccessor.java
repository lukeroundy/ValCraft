package dev.valcraft.mixin;

import net.minecraft.world.entity.Entity;
import net.neoforged.neoforge.fluids.FluidType;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;

@Mixin(Entity.class)
public interface EntityFluidAccessor {
	@Accessor(value = "forgeFluidTypeOnEyes", remap = false) void valcraft$setForgeFluidTypeOnEyes(FluidType t);
}
