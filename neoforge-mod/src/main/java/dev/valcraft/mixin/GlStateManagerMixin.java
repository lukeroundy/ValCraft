package dev.valcraft.mixin;

import com.mojang.blaze3d.platform.GlStateManager;
import dev.valcraft.client.ValCraftClient;
import org.lwjgl.opengl.GL11;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.ModifyVariable;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Correct alpha for the exported overlay. Vanilla blends alpha like color (or copies the source alpha), so a
 * translucent GUI element drawn over an opaque one lowers the pixel's alpha and Valheim would show through.
 * While linked, alpha accumulates coverage instead: a_out = a_src + a_dst * (1 - a_src). Blends that explicitly
 * leave alpha untouched (ZERO, ONE) are kept, and darkening passes (source RGB factor ZERO, e.g. the vignette)
 * never change alpha. Color is then effectively premultiplied (background is 0,0,0,0).
 */
@Mixin(GlStateManager.class)
public abstract class GlStateManagerMixin {
	@Inject(method = "_blendFunc", at = @At("HEAD"), cancellable = true)
	private static void valcraft$blendFunc(int src, int dst, CallbackInfo ci) {
		if (!ValCraftClient.active()) return;
		if (src == GL11.GL_ZERO) GlStateManager._blendFuncSeparate(src, dst, GL11.GL_ZERO, GL11.GL_ONE); // darkening pass: keep alpha
		else GlStateManager._blendFuncSeparate(src, dst, GL11.GL_ONE, GL11.GL_ONE_MINUS_SRC_ALPHA);
		ci.cancel();
	}

	@ModifyVariable(method = "_blendFuncSeparate", at = @At("HEAD"), argsOnly = true, ordinal = 2)
	private static int valcraft$srcAlpha(int srcAlpha, int srcRgb, int dstRgb, int srcA, int dstAlpha) {
		if (!ValCraftClient.active() || (srcAlpha == GL11.GL_ZERO && dstAlpha == GL11.GL_ONE)) return srcAlpha;
		return srcRgb == GL11.GL_ZERO ? GL11.GL_ZERO : GL11.GL_ONE;
	}

	@ModifyVariable(method = "_blendFuncSeparate", at = @At("HEAD"), argsOnly = true, ordinal = 3)
	private static int valcraft$dstAlpha(int dstAlpha, int srcRgb, int dstRgb, int srcAlpha, int dstA) {
		if (!ValCraftClient.active() || (srcAlpha == GL11.GL_ZERO && dstAlpha == GL11.GL_ONE)) return dstAlpha;
		return srcRgb == GL11.GL_ZERO ? GL11.GL_ONE : GL11.GL_ONE_MINUS_SRC_ALPHA;
	}
}
