package dev.valcraft.client;

import dev.valcraft.ValCraft;
import dev.valcraft.link.ValCraftLink;
import dev.valcraft.net.ServerOps;
import net.minecraft.client.Minecraft;
import net.minecraft.core.BlockPos;
import net.minecraft.network.chat.Component;
import net.minecraft.world.level.block.CampfireBlock;
import net.minecraft.world.level.block.entity.CampfireBlockEntity;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.ArrayList;
import java.util.List;

/**
 * 0.13.2: sleeping and spawn points across both games, and lit MC campfires for Valheim (warmth + comfort).
 * MC bed -> EV_SLEEP 1 (Valheim marks its player in bed and takes the MC bed as its custom spawn point); Valheim bed
 * (EV_SLEEP 1 from Valheim) -> the MC player sleeps where it stands; EV_SPAWN -> MC respawn position. The night skip is
 * Valheim's: MC's own skip is off while linked and MC wakes when Valheim's morning arrives via EV_DAY_TIME.
 */
public final class SleepLink {
	private SleepLink() {}
	private static boolean was, fromValheim;
	private static int fireTick; private static long fireHash, nextFireSend;

	static void tick(Minecraft mc) {
		if (mc.player == null) return;
		boolean s = mc.player.isSleeping();
		if (s && !was && !fromValheim) {
			BlockPos b = mc.player.getSleepingPos().orElse(mc.player.blockPosition());
			ValCraft.LOG.info("[sleep] MC player sleeps in an MC bed at {}: asking Valheim to sleep", b);
			ValCraftClient.sendToValheim(ValCraftLink.EV_SLEEP, ByteBuffer.allocate(13).order(ByteOrder.LITTLE_ENDIAN).put((byte) 1).putFloat(b.getX() + 0.5f).putFloat(b.getY() + 0.5625f).putFloat(b.getZ() + 0.5f).array());
		}
		if (!s && was) { ValCraftClient.sendToValheim(ValCraftLink.EV_SLEEP, new byte[] { 0 }); fromValheim = false; ValCraft.LOG.info("[sleep] MC player woke up"); }
		was = s;
		if (++fireTick >= 20) { fireTick = 0; fires(mc); }
	}

	static void receive(Minecraft mc, byte[] p) {
		if (mc.player == null || p == null || p.length == 0) return;
		boolean sleeping = mc.player.isSleeping();
		switch (p[0]) {
			case 1 -> { if (!sleeping) { fromValheim = true; ValCraft.LOG.info("[sleep] Valheim player is in bed: MC sleeps too"); Ops.run(mc, ServerOps.OP_SLEEP, new byte[] { 1 }); } }
			case 0 -> { if (sleeping) Ops.run(mc, ServerOps.OP_SLEEP, new byte[] { 0 }); }
			case 2 -> {
				String why = new String(p, 1, p.length - 1, java.nio.charset.StandardCharsets.UTF_8);
				ValCraft.LOG.info("[sleep] Valheim can't sleep: {}", why);
				mc.player.displayClientMessage(Component.literal("Valheim: " + why), true);
				if (sleeping) Ops.run(mc, ServerOps.OP_SLEEP, new byte[] { 0 });
			}
			default -> {}
		}
	}

	static void spawn(Minecraft mc, byte[] p) {
		if (mc.player == null || p == null || p.length < 12) return;
		Ops.run(mc, ServerOps.OP_SPAWN, p);
	}

	static void reset() { was = false; fromValheim = false; fireHash = 0; nextFireSend = 0; }

	/** Lit campfires (incl. soul campfires) within 32 blocks -> EV_MC_FIRES on change and every 5 s. */
	private static void fires(Minecraft mc) {
		var lvl = mc.level; if (lvl == null) return;
		BlockPos pp = mc.player.blockPosition();
		List<BlockPos> lit = new ArrayList<>();
		int pcx = pp.getX() >> 4, pcz = pp.getZ() >> 4;
		for (int cx = pcx - 2; cx <= pcx + 2; cx++) for (int cz = pcz - 2; cz <= pcz + 2; cz++) {
			var ch = lvl.getChunkSource().getChunk(cx, cz, false); if (ch == null) continue;
			for (var be : ch.getBlockEntities().values()) {
				if (!(be instanceof CampfireBlockEntity)) continue;
				var st = be.getBlockState();
				if (!st.hasProperty(CampfireBlock.LIT) || !st.getValue(CampfireBlock.LIT)) continue;
				if (be.getBlockPos().distSqr(pp) > 32 * 32 || lit.size() >= 64) continue;
				lit.add(be.getBlockPos().immutable());
			}
		}
		long h = 1;
		for (var b : lit) h = h * 31 + b.asLong();
		long now = System.currentTimeMillis();
		if (h == fireHash && now < nextFireSend) return;
		if (h != fireHash) ValCraft.LOG.info("[fire] {} lit MC campfire(s) near the player -> Valheim warmth/comfort", lit.size());
		fireHash = h; nextFireSend = now + 5000;
		var b = ByteBuffer.allocate(2 + lit.size() * 12).order(ByteOrder.LITTLE_ENDIAN).putShort((short) lit.size());
		for (var f : lit) b.putInt(f.getX()).putInt(f.getY()).putInt(f.getZ());
		ValCraftClient.sendToValheim(ValCraftLink.EV_MC_FIRES, b.array());
	}
}
