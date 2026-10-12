package dev.valcraft.client;

import dev.valcraft.ValCraft;
import dev.valcraft.link.ValCraftLink;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import net.minecraft.client.Camera;
import net.minecraft.client.Minecraft;
import net.minecraft.world.phys.Vec3;
import net.neoforged.api.distmarker.Dist;
import net.neoforged.bus.api.SubscribeEvent;
import net.neoforged.fml.common.EventBusSubscriber;
import net.neoforged.neoforge.client.event.ClientTickEvent;
import net.neoforged.neoforge.client.event.RenderFrameEvent;

/** Client side of the link: connects when Valheim has created the file, heartbeats, answers pings, publishes the camera. */
@EventBusSubscriber(modid = ValCraft.MOD_ID, value = Dist.CLIENT)
public final class LinkClient {
	private static ValCraftLink link;
	private static long nextConnectAttempt, nextHeartbeat, frameId;
	private static boolean peerWasAlive;

	private LinkClient() {}

	@SubscribeEvent
	public static void onClientTick(ClientTickEvent.Post e) {
		long now = System.currentTimeMillis();
		if (link == null) {
			if (now < nextConnectAttempt) return;
			nextConnectAttempt = now + 1000;
			try {
				ValCraftLink l = new ValCraftLink(ValCraftLink.defaultPath());
				if (!l.isReady()) { l.close(); return; }
				link = l;
				ValCraft.LOG.info("[link] opened {} (Valheim pid {})", ValCraftLink.defaultPath(), link.peerPid());
				link.trySend(ValCraftLink.EV_HELLO, ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt((int) ProcessHandle.current().pid()).array());
			} catch (Exception ex) {
				return; // Valheim not running yet
			}
		}
		if (now >= nextHeartbeat) { link.heartbeat(); nextHeartbeat = now + 100; }
		ValCraftLink.Event ev;
		while ((ev = link.tryReceive()) != null) {
			if (ev.type() == ValCraftLink.EV_PING) link.trySend(ValCraftLink.EV_PONG, ev.payload());
			else if (ev.type() == ValCraftLink.EV_PONG) ValCraft.LOG.info("[link] pong from Valheim — round trip OK");
		}
		boolean alive = link.peerAlive(2000);
		if (alive != peerWasAlive) { ValCraft.LOG.info(alive ? "[link] Valheim connected" : "[link] Valheim heartbeat lost"); peerWasAlive = alive; }
	}

	@SubscribeEvent
	public static void onRenderFrame(RenderFrameEvent.Post e) {
		if (link == null) return;
		Minecraft mc = Minecraft.getInstance();
		if (mc.player == null) return;
		Camera cam = mc.gameRenderer.getMainCamera();
		Vec3 p = cam.getPosition();
		link.writePlayerState(new ValCraftLink.PlayerState(++frameId, p.x, p.y, p.z, cam.getYRot(), cam.getXRot(), 0f,
				mc.options.fov().get().floatValue(), 0));
	}
}
