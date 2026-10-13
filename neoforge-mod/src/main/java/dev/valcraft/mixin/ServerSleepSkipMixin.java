package dev.valcraft.mixin;

import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.players.SleepStatus;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;

/** 0.13.2: while Valheim drives the time, MC's "enough players asleep" check never fires (it would wake everyone after 5 s without a skip). */
@Mixin(ServerLevel.class)
public class ServerSleepSkipMixin {
	@Redirect(method = "tick", at = @At(value = "INVOKE", target = "Lnet/minecraft/server/players/SleepStatus;areEnoughDeepSleeping(ILjava/util/List;)Z"))
	private boolean valcraft$deepSleep(SleepStatus s, int pct, java.util.List<?> players) {
		if (dev.valcraft.net.SleepState.valheimTime) return false;
		@SuppressWarnings("unchecked") var l = (java.util.List<net.minecraft.server.level.ServerPlayer>) players;
		return s.areEnoughDeepSleeping(pct, l);
	}
}
