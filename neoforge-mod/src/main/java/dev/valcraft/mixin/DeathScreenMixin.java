package dev.valcraft.mixin;

import net.minecraft.client.gui.components.Button;
import net.minecraft.client.gui.screens.DeathScreen;
import org.spongepowered.asm.mixin.Final;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

import java.util.List;

/** 0.13.4: respawn waits for Valheim's respawn (see DeathGate). */
@Mixin(DeathScreen.class)
public class DeathScreenMixin {
	@Shadow @Final private List<Button> exitButtons;
	@Shadow @Final private boolean hardcore;

	@Inject(method = "tick", at = @At("TAIL"))
	private void valcraft$gate(CallbackInfo ci) {
		dev.valcraft.client.DeathGate.tick(net.minecraft.client.Minecraft.getInstance(), exitButtons, hardcore);
	}
}
