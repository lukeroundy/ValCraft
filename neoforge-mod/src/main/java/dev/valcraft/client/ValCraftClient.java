package dev.valcraft.client;

import dev.valcraft.ValCraft;
import dev.valcraft.link.ValCraftLink;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.Optional;
import net.minecraft.client.Minecraft;
import net.neoforged.api.distmarker.Dist;
import net.neoforged.bus.api.SubscribeEvent;
import net.neoforged.fml.common.EventBusSubscriber;
import net.neoforged.neoforge.client.event.ClientTickEvent;
import net.neoforged.neoforge.client.event.RenderFrameEvent;

/**
 * Minecraft side of the link (milestone 2): connect/reconnect with acked Hello, heartbeats, watchdog + Quit,
 * hidden window, input injection, frame export, player-state publishing. Everything runs on the render thread.
 */
@EventBusSubscriber(modid = ValCraft.MOD_ID, value = Dist.CLIENT)
public final class ValCraftClient {
	/** Exit Minecraft when Valheim quits or its process disappears (JVM arg -Dvalcraft.exitWithValheim=false to disable). */
	static final boolean EXIT_WITH_VALHEIM = !"false".equalsIgnoreCase(System.getProperty("valcraft.exitWithValheim"));
	static final long PEER_TIMEOUT_MS = 2000, PEER_GONE_EXIT_MS = 5000;

	private static volatile ValCraftLink link;
	private static int session;
	private static volatile boolean acked;
	private static long nextOpenAttempt, nextHello, nextHeartbeat, playerFrame, peerGoneSince;
	private static boolean peerWasAlive;
	private static volatile boolean stopping;

	private ValCraftClient() {}

	/** True while connected to Valheim and the Hello was acknowledged: hidden window, no world render, injected input. */
	public static boolean active() { return link != null && acked && !stopping; }
	/** Valheim collision replaces MC's blocks for players (only once Valheim sent a non-empty snapshot). */
	/** Valheim streams its terrain as real blocks (0.5.0+): MC blocks then count for collision/picking again. */
	public static boolean terrainMode() { ValCraftLink l = link; return l != null && (l.peerFlags() & ValCraftLink.VFLAG_TERRAIN_BLOCKS) != 0; }
	private static long holdSince;

	public static boolean collisionActive() {
		// Also while waiting for Valheim's first snapshot (player is held in place), so MC terrain can't suffocate us;
		// an explicitly empty snapshot (Valheim ExportCollision=false) hands control back to MC's own blocks.
		return active() && (link.collisionSnapshotCount() == 0 || CollisionWorld.boxCount() > 0);
	}
	private static boolean heldForCollision;
	/**
	 * Heartbeat from a daemon thread as well: opening/joining a world blocks the render thread for seconds, which made
	 * Valheim 0.4.1 drop the link for good (see Valheim log "heartbeat lost" right after "[autoworld] opening world").
	 */
	static {
		Thread t = new Thread(() -> {
			while (true) {
				try {
					ValCraftLink l = link;
					if (l != null && acked && !stopping) l.heartbeat();
					Thread.sleep(100);
				} catch (InterruptedException e) { return; } catch (Throwable ignored) { }
			}
		}, "ValCraft heartbeat");
		t.setDaemon(true);
		t.start();
	}
	private static int loggedBoxes = -1;

	/** True while Valheim forwards input to us (F7). */
	/** Held until the terrain under the player exists (tile received and a solid block within 48 below), max 15 s. */
	private static boolean groundReady(Minecraft mc) {
		var p = mc.player;
		if (holdSince == 0) holdSince = System.currentTimeMillis();
		if (System.currentTimeMillis() - holdSince > 15000) return true;
		if (!TerrainSync.haveTileAt(p.getX(), p.getZ())) return false;
		var pos = p.blockPosition();
		for (int dy = 0; dy <= 48; dy++) if (!mc.level.getBlockState(pos.below(dy)).isAir()) { holdSince = 0; return true; }
		return false;
	}

	static void teleport(Minecraft mc, float x, float y, float z) {
		ValCraft.LOG.info("[terrain] teleport MC player to {} {} {} (vertical anchor)", x, y, z);
		holdSince = 0;
		if (mc.player != null && !Ops.remote(mc)) { mc.player.setPos(x, y, z); mc.player.setDeltaMovement(0, 0, 0); } // tunnel client: the server's teleport moves us (no "moved too quickly")
		if (mc.player != null) Ops.run(mc, dev.valcraft.net.ServerOps.OP_TELEPORT, le(12).putFloat(x).putFloat(y).putFloat(z).array()); // 0.13.0: server-side (no "moved too quickly" for tunnel clients)
	}

	public static boolean sendToValheim(short type, byte[] payload) { ValCraftLink l = link; return l != null && acked && l.trySend(type, payload); }

	public static boolean inputActive() { return active() && (link.peerFlags() & ValCraftLink.VFLAG_INPUT_ACTIVE) != 0; }

	@SubscribeEvent
	public static void onRenderFramePre(RenderFrameEvent.Pre e) {
		Minecraft mc = Minecraft.getInstance();
		long now = System.currentTimeMillis();
		if (!ensureOpen(now)) return;
		if (now >= nextHeartbeat) { link.heartbeat(); nextHeartbeat = now + 100; }

		// reconnect: Valheim re-initialised the file (restart) -> new session -> say Hello again
		if (!link.isReady()) { if (acked) disconnect("link file re-initialising"); return; }
		int s = link.session();
		if (s != session) { if (acked) disconnect("Valheim session changed"); session = s; acked = false; nextHello = 0; }
		if (!acked && now >= nextHello) {
			link.trySend(ValCraftLink.EV_HELLO, ValCraftLink.ints((int) ProcessHandle.current().pid(), ValCraftLink.VERSION, session));
			nextHello = now + 500;
		}

		ValCraftLink.Event ev;
		while ((ev = link.tryReceive()) != null) {
			switch (ev.type()) {
				case ValCraftLink.EV_HELLO_ACK -> {
					if (ev.le().getInt(0) == session && !acked) {
						acked = true;
						link.setAckedSession(session);
						ValCraft.LOG.info("[link] connected to Valheim pid {} (session {})", link.peerPid(), Integer.toHexString(session));
						WindowControl.onActivated(mc);
						BlockSync.reset();
					}
				}
				case ValCraftLink.EV_PING -> link.trySend(ValCraftLink.EV_PONG, ev.payload());
				case ValCraftLink.EV_PLAYER_DIMS -> {
					var bb = java.nio.ByteBuffer.wrap(ev.payload()).order(java.nio.ByteOrder.LITTLE_ENDIAN);
					PlayerDims.set(mc, bb.getFloat(0), bb.getFloat(4));
				}
				case ValCraftLink.EV_TERRAIN_TILE -> TerrainSync.receive(ev.payload());
				case ValCraftLink.EV_GIVE_ITEM -> ItemLink.give(mc, ev.payload());
				case ValCraftLink.EV_CONSUME_REQ -> ItemLink.consume(mc, ev.payload());
				case ValCraftLink.EV_INV_REQ -> ItemLink.snapshot(mc);
				case ValCraftLink.EV_DAMAGE -> HealthLink.damage(mc, ev.payload());
				case ValCraftLink.EV_BUILD_STATE -> BuildLink.state(ev.payload());
				case ValCraftLink.EV_BUILD_PIECES -> BuildLink.pieces(mc, ev.payload());
				case ValCraftLink.EV_WATER -> ValheimWater.receive(ev.payload());
				case ValCraftLink.EV_DAY_TIME -> DayLink.receive(mc, ev.payload());
				case ValCraftLink.EV_SLEEP -> SleepLink.receive(mc, ev.payload());
				case ValCraftLink.EV_RESPAWN_READY -> DeathGate.receive(ev.payload());
				case ValCraftLink.EV_SPAWN -> SleepLink.spawn(mc, ev.payload());
				case ValCraftLink.EV_WEATHER -> WeatherLink.receive(mc, ev.payload());
				case ValCraftLink.EV_WORLD_INFO -> AutoWorld.worldInfo(ev.payload());
				case ValCraftLink.EV_NET_ROLE -> NetLink.receive(ev.payload());
				case ValCraftLink.EV_CHEST_CONTENTS -> ChestLink.contents(mc, ev.payload());
				case ValCraftLink.EV_LORE_TEXT -> LoreScreen.receive(mc, ev.payload());
				case ValCraftLink.EV_VMOB_LIST -> dev.valcraft.mobs.MobSync.receiveVList(mc, ev.payload());
				case ValCraftLink.EV_MOB_HURT -> dev.valcraft.mobs.MobSync.hurtMob(mc, ev.payload());
				case ValCraftLink.EV_BLOCK_SYNC_CONFIG -> BlockSync.receiveConfig(ev.payload());
				case ValCraftLink.EV_TERRAIN_HEIGHTS -> HeightField.receive(ev.payload());
				case ValCraftLink.EV_ATTACK_TARGET -> ValheimAttack.receiveTarget(ev.payload());
				case ValCraftLink.EV_TELEPORT -> {
					var bb = java.nio.ByteBuffer.wrap(ev.payload()).order(java.nio.ByteOrder.LITTLE_ENDIAN);
					teleport(mc, bb.getFloat(0), bb.getFloat(4), bb.getFloat(8));
				}
				case ValCraftLink.EV_HOVER_TEXT -> ValheimInteract.hoverText = new String(ev.payload(), java.nio.charset.StandardCharsets.UTF_8);
				case ValCraftLink.EV_QUIT -> { quit(mc, "Valheim sent Quit"); return; }
				default -> { if (acked) VirtualInput.handle(mc, ev); }
			}
		}
		if (!acked) return;

		// watchdog
		boolean alive = link.peerAlive(PEER_TIMEOUT_MS);
		if (alive != peerWasAlive) {
			ValCraft.LOG.info(alive ? "[link] Valheim heartbeat OK" : "[link] Valheim heartbeat lost");
			if (!alive) VirtualInput.releaseAll(mc);
			peerWasAlive = alive;
		}
		if ((link.peerFlags() & ValCraftLink.VFLAG_QUITTING) != 0) { quit(mc, "Valheim is quitting"); return; }
		Optional<ProcessHandle> valheim = ProcessHandle.of(link.peerPid());
		boolean processAlive = valheim.map(ProcessHandle::isAlive).orElse(false);
		if (processAlive) peerGoneSince = 0;
		else if (peerGoneSince == 0) peerGoneSince = now;
		else if (now - peerGoneSince > PEER_GONE_EXIT_MS) {
			if (EXIT_WITH_VALHEIM) { quit(mc, "Valheim process exited"); return; }
			disconnect("Valheim process exited");
			return;
		}
		if (!inputActive()) VirtualInput.releaseIfHeld(mc);

		WindowControl.applyRequestedSize(mc, link);
		int flags = ValCraftLink.MCFLAG_HIDDEN;
		if (mc.level != null) flags |= ValCraftLink.MCFLAG_WORLD_LOADED;
		if (mc.screen != null || !mc.mouseHandler.isMouseGrabbed()) flags |= ValCraftLink.MCFLAG_SCREEN_OPEN;
		link.setFlags(flags);
	}

	@SubscribeEvent
	public static void onRenderFramePost(RenderFrameEvent.Post e) {
		if (!active()) return;
		Minecraft mc = Minecraft.getInstance();
		FrameExporter.capture(mc, link);
		if (mc.player != null) link.writePlayerState(PlayerStateExporter.build(mc, e.getPartialTick().getGameTimeDeltaPartialTick(true), ++playerFrame));
	}

	@SubscribeEvent
	public static void onClientTick(ClientTickEvent.Post e) {
		// Rendering may be skipped (noRender); keep the heartbeat alive from the tick too.
		if (link != null && System.currentTimeMillis() >= nextHeartbeat) { link.heartbeat(); nextHeartbeat = System.currentTimeMillis() + 100; }
		Minecraft mc = Minecraft.getInstance();
		if (active()) {
			AutoWorld.tick(mc);
			NetLink.tick(mc);
			BlockSync.clientTick(mc);
			MapKey.tick(mc);
			ReachLink.tick(mc);
			PlayerDims.tick(mc);
			ValheimInteract.tick(mc);
			ItemLink.tick(mc);
			HealthLink.tick(mc);
			BuildLink.tick(mc);
			SleepLink.tick(mc);
			DeathGate.tick(mc);
			ValheimWater.tick(mc);
			dev.valcraft.mobs.MobSync.tick(mc);
			CollisionWorld.poll(link);
			if (CollisionWorld.boxCount() != loggedBoxes && (loggedBoxes <= 0 || Math.abs(CollisionWorld.boxCount() - loggedBoxes) > 500)) {
				loggedBoxes = CollisionWorld.boxCount();
				ValCraft.LOG.info("[collision] {} Valheim boxes", loggedBoxes);
			}
		}
		// Until Valheim's first collision snapshot arrives, hover so we don't fall through MC's (ignored/void) world.
		boolean hold = active() && mc.player != null && (link.collisionSnapshotCount() == 0 || (terrainMode() && !groundReady(mc)));
		if (mc.player != null && hold != heldForCollision) {
			mc.player.setNoGravity(hold);
			heldForCollision = hold;
		}
		if (hold) mc.player.setDeltaMovement(mc.player.getDeltaMovement().multiply(1, 0, 1));
	}

	private static boolean ensureOpen(long now) {
		if (link != null) return true;
		if (now < nextOpenAttempt) return false;
		nextOpenAttempt = now + 1000;
		try {
			link = new ValCraftLink(ValCraftLink.defaultPath());
			ValCraft.LOG.info("[link] opened {}", ValCraftLink.defaultPath());
			return true;
		} catch (Exception ex) {
			return false; // Valheim not started yet
		}
	}

	private static void disconnect(String why) {
		ValCraft.LOG.info("[link] disconnected: {}", why);
		Minecraft mc = Minecraft.getInstance();
		VirtualInput.releaseAll(mc);
		FrameExporter.reset();
		CollisionWorld.clear();
		ValheimInteract.hoverText = "";
		TerrainSync.clearClient(); HeightField.clear(); BlockSync.disconnected(); ItemLink.reset(); HealthLink.reset(); BuildLink.reset(); SleepLink.reset(); ValheimWater.clear(); DayLink.unlink(mc); WeatherLink.unlink(mc); ChestLink.reset(mc); dev.valcraft.mobs.MobSync.clear(mc); ValheimAttack.clear();
		PlayerDims.set(mc, 0, 0);
		AutoWorld.reset();
		loggedBoxes = -1;
		if (heldForCollision && mc.player != null) mc.player.setNoGravity(false);
		heldForCollision = false;
		acked = false;
		peerWasAlive = false;
		peerGoneSince = 0;
		WindowControl.onDeactivated(mc, EXIT_WITH_VALHEIM);
	}

	private static void quit(Minecraft mc, String why) {
		if (stopping) return;
		stopping = true;
		ValCraft.LOG.info("[link] {} -> stopping Minecraft", why);
		VirtualInput.releaseAll(mc);
		mc.stop(); // normal shutdown: integrated server saves the world
	}

	static ByteBuffer le(int n) { return ByteBuffer.allocate(n).order(ByteOrder.LITTLE_ENDIAN); }
}
