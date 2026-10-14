package dev.valcraft.client;

import dev.valcraft.link.ValCraftLink;
import dev.valcraft.net.ServerOps;

/** 0.13.0: server-side bodies of the Valheim link (run on the integrated server for the host, via payload for tunnel clients). */
public final class OpsInit {
	private OpsInit() {}
	public static void init() {
		ServerOps.on(ValCraftLink.EV_DAMAGE, HealthLink::damageOn);
		ServerOps.on(ValCraftLink.EV_GIVE_ITEM, ItemLink::giveOn);
		ServerOps.on(ValCraftLink.EV_CONSUME_REQ, ItemLink::consumeOn);
		ServerOps.on(ValCraftLink.EV_GIVE_ITEM_Q, ItemLink::giveQOn);
		ServerOps.on(ValCraftLink.EV_XP, dev.valcraft.content.Fixes157Common::xpOn); // 0.15.7
		ServerOps.on(ValCraftLink.EV_RESTED, dev.valcraft.content.Fixes157Common::restedOn);
		ServerOps.on(ValCraftLink.EV_TERRAIN_TILE, TerrainSync::tileOn);
		ServerOps.on(ValCraftLink.EV_VMOB_LIST, dev.valcraft.mobs.MobSync::vlistOn);
		ServerOps.on(ValCraftLink.EV_MOB_HURT, dev.valcraft.mobs.MobSync::hurtMobOn);
		ServerOps.on(ServerOps.OP_VMOB_CLEAR, dev.valcraft.mobs.MobSync::clearOn);
		ServerOps.on(ServerOps.OP_CHEST_DEPOSIT, ChestLink::depositOn);
		ServerOps.on(ServerOps.OP_HOE_WEAR, HoeLink::wearOn);
		ServerOps.on(ServerOps.OP_ATTACK, ValheimAttack::attackOn);
		ServerOps.on(ServerOps.OP_DIMS, (sp, p) -> sp.refreshDimensions());
		ServerOps.on(ServerOps.OP_SLEEP, dev.valcraft.net.SleepState::sleepOn);
		ServerOps.on(ServerOps.OP_SPAWN, dev.valcraft.net.SleepState::spawnOn);
		ServerOps.on(ServerOps.OP_TELEPORT, (sp, p) -> {
			var b = java.nio.ByteBuffer.wrap(p).order(java.nio.ByteOrder.LITTLE_ENDIAN);
			sp.connection.teleport(b.getFloat(0), b.getFloat(4), b.getFloat(8), sp.getYRot(), sp.getXRot());
		});
	}
}
