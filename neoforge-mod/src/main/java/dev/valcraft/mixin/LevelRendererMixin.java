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
 * In third person (F5) the local player entity alone (model, armour, held items) is drawn into the frame so you see
 * your MC character over Valheim's world (no depth exchange: Valheim walls don't hide it).
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
		if (mc.player != null && cam.isDetached()) {
			try {
				float pt = delta.getGameTimeDeltaPartialTick(false);
				Matrix4fStack mv = RenderSystem.getModelViewStack();
				mv.pushMatrix();
				mv.mul(frustumMatrix);
				RenderSystem.applyModelViewMatrix();
				var p = mc.player;
				var c = cam.getPosition();
				double x = Mth.lerp(pt, p.xOld, p.getX()) - c.x, y = Mth.lerp(pt, p.yOld, p.getY()) - c.y, z = Mth.lerp(pt, p.zOld, p.getZ()) - c.z;
				var buffers = mc.renderBuffers().bufferSource();
				var erd = mc.getEntityRenderDispatcher();
				erd.setRenderShadow(false);
				light.turnOnLightLayer();
				erd.render(p, x, y, z, Mth.lerp(pt, p.yRotO, p.getYRot()), pt, new PoseStack(), buffers, LightTexture.FULL_BRIGHT);
				buffers.endBatch();
				light.turnOffLightLayer();
				erd.setRenderShadow(mc.options.entityShadows().get());
				mv.popMatrix();
				RenderSystem.applyModelViewMatrix();
			} catch (Throwable t) {
				// never take the frame down for a cosmetic
			}
		}
		ci.cancel();
	}
}
