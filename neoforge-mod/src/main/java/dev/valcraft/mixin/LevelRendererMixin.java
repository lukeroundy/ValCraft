package dev.valcraft.mixin;

import com.mojang.blaze3d.systems.RenderSystem;
import com.mojang.blaze3d.vertex.PoseStack;
import dev.valcraft.client.ValCraftClient;
import net.minecraft.client.Camera;
import net.minecraft.client.DeltaTracker;
import net.minecraft.client.Minecraft;
import net.minecraft.client.renderer.GameRenderer;
import net.minecraft.client.renderer.LevelRenderer;
import net.minecraft.client.renderer.LightTexture;
import net.minecraft.util.Mth;
import org.joml.Matrix4f;
import org.joml.Matrix4fStack;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * While linked, Valheim draws the world: skip MC's level pass and leave a transparent background (hand + HUD still render).
 * Third person (F5): the player is a captured mesh in Valheim's world (EntitySync), not drawn here.
 */
@Mixin(LevelRenderer.class)
public abstract class LevelRendererMixin {
	@Inject(method = "renderLevel", at = @At("HEAD"), cancellable = true)
	private void valcraft$skipWorld(DeltaTracker delta, boolean outline, Camera cam, GameRenderer gr, LightTexture light,
			Matrix4f frustumMatrix, Matrix4f projection, CallbackInfo ci) {
		if (!ValCraftClient.active()) return;
		Minecraft mc = Minecraft.getInstance();
		// 0.3.0 crash fix: these prepare() calls normally happen in renderLevel; inventory's player doll needs the camera.
		if (mc.level != null) {
			mc.getBlockEntityRenderDispatcher().prepare(mc.level, cam, mc.hitResult);
			mc.getEntityRenderDispatcher().prepare(mc.level, cam, mc.crosshairPickEntity);
		}
		RenderSystem.clearColor(0f, 0f, 0f, 0f);
		RenderSystem.clear(16640, Minecraft.ON_OSX); // color + depth of the main target
		// 0.10.1: third person no longer draws the player here: EntitySync sends it as an in-world mesh (depth-tested in Valheim)
		ci.cancel();
	}
}
