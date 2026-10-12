package dev.valcraft.mixin;

import net.minecraft.client.Camera;
import net.minecraft.client.renderer.GameRenderer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Invoker;

@Mixin(GameRenderer.class)
public interface GameRendererAccessor {
	/** MC's effective vertical FOV (FOV setting × sprint/effect modifier). */
	@Invoker("getFov") double valcraft$getFov(Camera camera, float partialTick, boolean useFovSetting);
}
