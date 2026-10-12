package dev.valcraft.mixin;

import com.mojang.blaze3d.platform.Window;
import dev.valcraft.client.WindowControl;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** -Dvalcraft.startHidden=true: hide the window as soon as MC takes it over from NeoForge's early loading screen. */
@Mixin(Window.class)
public abstract class WindowMixin {
	@Shadow public abstract long getWindow();

	@Inject(method = "<init>", at = @At("TAIL"))
	private void valcraft$startHidden(CallbackInfo ci) {
		if (WindowControl.START_HIDDEN) WindowControl.hide(getWindow());
	}
}
