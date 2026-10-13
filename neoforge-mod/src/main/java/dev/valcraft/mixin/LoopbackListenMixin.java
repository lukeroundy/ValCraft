package dev.valcraft.mixin;

import dev.valcraft.net.NetFlags;
import net.minecraft.server.network.ServerConnectionListener;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.ModifyVariable;

import java.net.InetAddress;

/** 0.13.0: the host's published world is only reachable through the Valheim tunnel (127.0.0.1), never from the LAN directly. */
@Mixin(ServerConnectionListener.class)
public abstract class LoopbackListenMixin {
	@ModifyVariable(method = "startTcpServerListener", at = @At("HEAD"), argsOnly = true)
	private InetAddress valcraft$loopback(InetAddress addr) {
		if (!NetFlags.loopbackOnly) return addr;
		try { return InetAddress.getByName("127.0.0.1"); } catch (java.net.UnknownHostException e) { return InetAddress.getLoopbackAddress(); } // IPv4: the Valheim side dials 127.0.0.1
	}
}
