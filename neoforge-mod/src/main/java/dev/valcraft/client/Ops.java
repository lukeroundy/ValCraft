package dev.valcraft.client;

import dev.valcraft.net.ServerOps;
import net.minecraft.client.Minecraft;
import net.neoforged.neoforge.network.PacketDistributor;

/** Client side of {@link ServerOps}: run an op for our player on whatever server we play on. Render thread. */
public final class Ops {
	private Ops() {}
	public static void run(Minecraft mc, short op, byte[] p) {
		if (mc.player == null) return;
		var srv = mc.getSingleplayerServer();
		if (srv != null) {
			var uuid = mc.player.getUUID();
			srv.execute(() -> { var sp = srv.getPlayerList().getPlayer(uuid); if (sp != null) ServerOps.runLocal(sp, op, p); });
			return;
		}
		if (mc.getConnection() != null) PacketDistributor.sendToServer(new ServerOps.Op(op, p));
	}
	/** Joined to another player's world through the Valheim tunnel (no integrated server here). */
	public static boolean remote(Minecraft mc) { return mc.getSingleplayerServer() == null && mc.getConnection() != null; }
}
