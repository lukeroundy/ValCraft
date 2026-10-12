package dev.valcraft.mixin;

import net.minecraft.client.MouseHandler;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;
import org.spongepowered.asm.mixin.gen.Invoker;

@Mixin(MouseHandler.class)
public interface MouseHandlerAccessor {
	@Invoker("onMove") void valcraft$onMove(long window, double x, double y);
	@Invoker("onPress") void valcraft$onPress(long window, int button, int action, int mods);
	@Invoker("onScroll") void valcraft$onScroll(long window, double xOffset, double yOffset);
	@Accessor("xpos") double valcraft$getXpos();
	@Accessor("ypos") double valcraft$getYpos();
}
