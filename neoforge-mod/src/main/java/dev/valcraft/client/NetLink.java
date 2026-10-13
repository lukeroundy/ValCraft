package dev.valcraft.client;

import dev.valcraft.ValCraft;
import dev.valcraft.link.ValCraftLink;
import dev.valcraft.net.NetFlags;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.screens.ConnectScreen;
import net.minecraft.client.gui.screens.DisconnectedScreen;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.client.multiplayer.ServerData;
import net.minecraft.client.multiplayer.resolver.ServerAddress;

import java.nio.charset.StandardCharsets;

/**
 * 0.13.0 multiplayer through Valheim. Valheim tells us its session role (EV_NET_ROLE):
 *  - host (Valheim "Start server" world): our integrated server is published on a free 127.0.0.1 port (offline mode,
 *    flight allowed, no LAN broadcast) and the port goes back to Valheim (EV_NET_READY), which tunnels joined players to it;
 *  - client (joined a Valheim server): no local world; we connect to Valheim's local tunnel port instead (retrying);
 *  - single player: unchanged.
 */
public final class NetLink {
	private NetLink() {}
	public static final int SINGLE = 0, HOST = 1, CLIENT = 2;
	private static volatile int role = -1, port; private static volatile String valheimName = "";
	private static long nextReady, nextConnect, nextPublish, roleSince;
	private static boolean viaTunnel;

	static void reset() { role = -1; port = 0; viaTunnel = false; }
	public static boolean known() { return role >= 0; }
	public static boolean client() { return role == CLIENT && port > 0; }
	public static int role() { return role; }

	/** EV_NET_ROLE {u8 role, u16 port, u8 len, utf8 Valheim player name}. */
	static void receive(byte[] p) {
		if (p.length < 4) return;
		int r = p[0] & 0xFF, pt = (p[1] & 0xFF) | ((p[2] & 0xFF) << 8), n = Math.min(p[3] & 0xFF, p.length - 4);
		String name = new String(p, 4, n, StandardCharsets.UTF_8);
		if (r != role || pt != port) {
			ValCraft.LOG.info("[net] Valheim session: {}{}{}", r == HOST ? "server host" : r == CLIENT ? "joined a Valheim server" : "single player",
				pt > 0 ? ", tunnel 127.0.0.1:" + pt : "", name.isEmpty() ? "" : " (Valheim player " + name + ")");
			roleSince = System.currentTimeMillis(); nextConnect = 0;
		}
		role = r; port = pt; valheimName = name;
	}

	static void tick(Minecraft mc) {
		if (!ValCraftClient.active() || role < 0) return;
		long now = System.currentTimeMillis();
		var srv = mc.getSingleplayerServer();
		if (role == HOST && srv != null && mc.level != null && mc.player != null) {
			if (!srv.isPublished() && now >= nextPublish) {
				nextPublish = now + 10000;
				int pt = net.minecraft.util.HttpUtil.getAvailablePort();
				NetFlags.loopbackOnly = true;
				srv.setUsesAuthentication(false); // players are authenticated by Valheim; MC identities are their offline names
				srv.setFlightAllowed(true);       // they stand on Valheim colliders the server doesn't know
				boolean ok = srv.publishServer(null, false, pt);
				ValCraft.LOG.info("[net] host world {} on 127.0.0.1:{} for Valheim players", ok ? "published" : "could NOT be published", pt);
			}
			if (srv.isPublished() && now >= nextReady) {
				nextReady = now + 2000;
				int pt = srv.getPort();
				ValCraftClient.sendToValheim(ValCraftLink.EV_NET_READY, new byte[] { (byte) HOST, (byte) pt, (byte) (pt >> 8) });
			}
		}
		if (role == CLIENT && srv != null && mc.level != null) {
			ValCraft.LOG.info("[net] joined a Valheim server: leaving the local MC world for the host's");
			mc.level.disconnect(); mc.disconnect(new TitleScreen());
			return;
		}
		if (role != CLIENT && viaTunnel && mc.level != null && srv == null) {
			ValCraft.LOG.info("[net] Valheim left the server: leaving the host's MC world");
			viaTunnel = false;
			mc.level.disconnect(); mc.disconnect(new TitleScreen());
			return;
		}
		if (role == CLIENT && mc.level == null && mc.screen instanceof DisconnectedScreen && now >= nextConnect) mc.setScreen(new TitleScreen());
	}

	/** AutoWorld on the title screen while we are a Valheim client. */
	static void connect(Minecraft mc) {
		long now = System.currentTimeMillis();
		if (now < nextConnect || mc.screen instanceof ConnectScreen) return;
		nextConnect = now + 8000;
		String addr = "127.0.0.1:" + port;
		ValCraft.LOG.info("[net] connecting to the host's Minecraft world through the Valheim tunnel ({})", addr);
		viaTunnel = true;
		ConnectScreen.startConnecting(new TitleScreen(), mc, new ServerAddress("127.0.0.1", port), new ServerData("ValCraft (Valheim server)", addr, ServerData.Type.OTHER), false, null);
	}
}
