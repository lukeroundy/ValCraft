package dev.valcraft.client;

import dev.valcraft.link.ValCraftLink;
import dev.valcraft.mixin.GameRendererAccessor;
import net.minecraft.client.Camera;
import net.minecraft.client.Minecraft;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.util.Mth;
import net.minecraft.world.phys.Vec3;

/** Builds the per-frame PlayerState (camera, effective FOV, view bob, feet position) in MC coordinates. */
final class PlayerStateExporter {
	private PlayerStateExporter() {}

	static ValCraftLink.PlayerState build(Minecraft mc, float pt, long frame) {
		LocalPlayer pl = mc.player;
		Camera cam = mc.gameRenderer.getMainCamera();
		Vec3 eye = cam.getPosition();
		Vec3 feet = pl.getPosition(pt);
		float fov = (float) ((GameRendererAccessor) mc.gameRenderer).valcraft$getFov(cam, pt, true);
		int flags = ValCraftLink.PS_HAS_PLAYER;
		if (pl.onGround()) flags |= ValCraftLink.PS_ON_GROUND;
		if (pl.getAbilities().flying) flags |= ValCraftLink.PS_FLYING;
		if (pl.isCrouching()) flags |= ValCraftLink.PS_CROUCHING;
		if (pl.isInWater()) flags |= ValCraftLink.PS_IN_WATER;
		if (cam.isDetached()) flags |= ValCraftLink.PS_THIRD_PERSON;
		float bx = 0, by = 0, br = 0, bp = 0;
		if (mc.options.bobView().get() && !cam.isDetached()) {
			flags |= ValCraftLink.PS_VIEW_BOB;
			// Same math as GameRenderer.bobView (1.21.1)
			float f = pl.walkDist - pl.walkDistO;
			float f1 = -(pl.walkDist + f * pt);
			float f2 = Mth.lerp(pt, pl.oBob, pl.bob);
			bx = Mth.sin(f1 * (float) Math.PI) * f2 * 0.5F;
			by = -Math.abs(Mth.cos(f1 * (float) Math.PI) * f2);
			br = Mth.sin(f1 * (float) Math.PI) * f2 * 3.0F;
			bp = Math.abs(Mth.cos(f1 * (float) Math.PI - 0.2F) * f2) * 5.0F;
		}
		return new ValCraftLink.PlayerState(frame, eye.x, eye.y, eye.z, cam.getYRot(), cam.getXRot(), 0f, fov, flags,
				bx, by, br, bp, feet.x, feet.y, feet.z, pl.getEyeHeight());
	}
}
