package dev.valcraft.mobs;

import dev.valcraft.ValCraft;
import dev.valcraft.client.ValCraftClient;
import dev.valcraft.link.MobCodec;
import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.Minecraft;
import net.minecraft.core.registries.Registries;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.*;
import net.minecraft.world.entity.ai.goal.target.NearestAttackableTargetGoal;
import net.minecraft.world.entity.monster.Enemy;
import net.minecraft.world.entity.monster.Monster;
import net.neoforged.bus.api.IEventBus;
import net.neoforged.neoforge.event.entity.EntityAttributeCreationEvent;
import net.neoforged.neoforge.event.entity.EntityJoinLevelEvent;
import net.neoforged.neoforge.registries.DeferredHolder;
import net.neoforged.neoforge.registries.DeferredRegister;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.*;

/** 0.9.0 cross-game mobs, MC side. */
public final class MobSync {
	private MobSync() {}
	public static final DeferredRegister<EntityType<?>> ENTITIES = DeferredRegister.create(Registries.ENTITY_TYPE, ValCraft.MOD_ID);
	public static final DeferredHolder<EntityType<?>, EntityType<ValheimProxy>> PROXY = ENTITIES.register("valheim_proxy",
		() -> EntityType.Builder.<ValheimProxy>of(ValheimProxy::new, MobCategory.MISC).sized(0.8f, 1.8f).noSave().noSummon().clientTrackingRange(4).build("valheim_proxy"));

	public static void register(IEventBus modBus) {
		ENTITIES.register(modBus);
		modBus.addListener((EntityAttributeCreationEvent e) -> e.put(PROXY.get(), ValheimProxy.attributes().build()));
		net.neoforged.neoforge.common.NeoForge.EVENT_BUS.addListener(MobSync::onJoin);
		net.neoforged.neoforge.common.NeoForge.EVENT_BUS.addListener(MobSync::onSpawnCheck);
	}

	/** 0.9.1: no natural MC spawns under Valheim roofs (any Valheim collider 2-12 m above the spawn spot). */
	private static void onSpawnCheck(net.neoforged.neoforge.event.entity.living.MobSpawnEvent.PositionCheck e) {
		if (!dev.valcraft.client.ValCraftClient.active() || !dev.valcraft.client.CollisionWorld.ready()) return;
		var t = e.getSpawnType();
		if (t != net.minecraft.world.entity.MobSpawnType.NATURAL && t != net.minecraft.world.entity.MobSpawnType.CHUNK_GENERATION) return;
		double x = e.getX(), y = e.getY(), z = e.getZ();
		try {
			if (!dev.valcraft.client.CollisionWorld.query(new net.minecraft.world.phys.AABB(x - 0.3, y + 2, z - 0.3, x + 0.3, y + 12, z + 0.3)).isEmpty())
				e.setResult(net.neoforged.neoforge.event.entity.living.MobSpawnEvent.PositionCheck.Result.FAIL);
		} catch (RuntimeException ignored) {}
	}

	/** MC hostiles also hunt Valheim monster proxies. */
	private static void onJoin(EntityJoinLevelEvent e) {
		if (e.getLevel().isClientSide()) return;
		if (e.getEntity() instanceof Monster m) m.targetSelector.addGoal(3, new NearestAttackableTargetGoal<>(m, ValheimProxy.class, true));
	}

	// ---------- client tick: MC mobs -> Valheim ----------
	private static int ticks;
	public static void tick(Minecraft mc) {
		if (mc.player == null || mc.level == null || ++ticks % 4 != 0) return;
		var me = mc.player;
		List<Mob> near = mc.level.getEntitiesOfClass(Mob.class, me.getBoundingBox().inflate(32), m -> !(m instanceof ValheimProxy) && m.isAlive());
		near.sort(Comparator.comparingDouble(m -> m.distanceToSqr(me)));
		List<MobCodec.Mob> out = new ArrayList<>();
		for (Mob m : near) {
			if (out.size() >= 24) break;
			out.add(new MobCodec.Mob(m.getId(), m instanceof Enemy ? MobCodec.HOSTILE : MobCodec.PASSIVE, (float) m.getX(), (float) m.getY(), (float) m.getZ(), m.getBbWidth(), m.getBbHeight(), m.getHealth() / Math.max(1f, m.getMaxHealth()), m.getMaxHealth(), m.getName().getString()));
		}
		for (byte[] b : MobCodec.encodeList(out)) ValCraftClient.sendToValheim(ValCraftLink.EV_MOB_LIST, b);
	}

	// ---------- Valheim monsters -> MC proxies ----------
	private static final List<MobCodec.Mob> pending = new ArrayList<>();
	// 0.13.0: keyed by (owning player, Valheim instance id): instance ids are per Valheim process. Server thread only.
	private static final Map<Long, ValheimProxy> proxies = new HashMap<>();
	private static final Map<Integer, UUID> ownerOfSlot = new HashMap<>(); private static final Map<UUID, Integer> slotOf = new HashMap<>();
	private static int slot(UUID u) { return slotOf.computeIfAbsent(u, k -> { int s = slotOf.size() + 1; ownerOfSlot.put(s, k); return s; }); }
	private static long pk(ServerPlayer sp, int vid) { return ((long) slot(sp.getUUID()) << 32) | (vid & 0xFFFFFFFFL); }

	/** Server thread: proxy of a Valheim creature (instance id in that player's Valheim), or null. */
	public static ValheimProxy proxy(ServerPlayer sp, int valheimId) { var p = proxies.get(pk(sp, valheimId)); return p != null && p.isAlive() ? p : null; }
	private static final List<byte[]> pendingRaw = new ArrayList<>();

	public static void receiveVList(Minecraft mc, byte[] p) {
		if (p[0] != 0) pendingRaw.clear();
		pendingRaw.add(p);
		if (p[1] == 0) return;
		// whole snapshot as one op: u16 n, n x (u16 len, bytes)
		int size = 2; for (byte[] b : pendingRaw) size += 2 + b.length;
		ByteBuffer bb = ByteBuffer.allocate(size).order(ByteOrder.LITTLE_ENDIAN).putShort((short) pendingRaw.size());
		for (byte[] b : pendingRaw) bb.putShort((short) b.length).put(b);
		pendingRaw.clear();
		dev.valcraft.client.Ops.run(mc, ValCraftLink.EV_VMOB_LIST, bb.array());
	}

	/** Server thread: a player's Valheim reports the monsters it mirrors (it is their nearest Valheim player). */
	public static void vlistOn(ServerPlayer sp, byte[] a) {
		ByteBuffer bb = ByteBuffer.wrap(a).order(ByteOrder.LITTLE_ENDIAN);
		List<MobCodec.Mob> list = new ArrayList<>();
		int n = bb.getShort() & 0xFFFF;
		for (int i = 0; i < n; i++) { byte[] part = new byte[bb.getShort() & 0xFFFF]; bb.get(part); list.addAll(MobCodec.decodeList(part)); }
		long mine = (long) slot(sp.getUUID()) << 32;
		{
			ServerLevel lvl = sp.serverLevel(); long now = System.currentTimeMillis();
			Set<Long> keep = new HashSet<>();
			for (var m : list) {
				long key = pk(sp, m.id());
				keep.add(key);
				ValheimProxy px = proxies.get(key);
				if (px == null || px.isRemoved() || px.level() != lvl) {
					px = PROXY.get().create(lvl); if (px == null) continue;
					px.vid = m.id(); px.setSize(m.w(), m.h()); px.moveTo(m.x(), m.y(), m.z(), 0, 0); px.lastSeen = now;
					px.owner = sp.getUUID();
					lvl.addFreshEntity(px); proxies.put(key, px);
					ValCraft.LOG.info("[mobs] proxy for Valheim monster {} ({}x{} m)", m.id(), m.w(), m.h());
				}
				px.setSize(m.w(), m.h()); px.setPos(m.x(), m.y(), m.z()); px.lastSeen = now;
			}
			proxies.entrySet().removeIf(en -> { if ((en.getKey() & 0xFFFFFFFF00000000L) != mine) return false; if (keep.contains(en.getKey()) && !en.getValue().isRemoved()) return false; en.getValue().discard(); return true; });
		}
	}

	public static void clear(Minecraft mc) {
		pending.clear(); pendingRaw.clear();
		if (mc.player != null) dev.valcraft.client.Ops.run(mc, dev.valcraft.net.ServerOps.OP_VMOB_CLEAR, new byte[0]);
	}
	/** Server thread: drop the proxies a player's Valheim owned (it disconnected / cleared). */
	public static void clearOn(ServerPlayer sp, byte[] unused) {
		long mine = (long) slot(sp.getUUID()) << 32;
		proxies.entrySet().removeIf(en -> { if ((en.getKey() & 0xFFFFFFFF00000000L) != mine) return false; en.getValue().discard(); return true; });
	}

	/** Server thread: something hit a proxy -> the Valheim that owns it. */
	static void forwardToValheim(ValheimProxy px, int vid, float amount, int attackerId) {
		byte[] b = MobCodec.vmobHurt(vid, amount, attackerId);
		var sp = px.owner == null ? null : px.getServer().getPlayerList().getPlayer(px.owner);
		if (sp != null) dev.valcraft.net.ServerOps.toValheim(sp, ValCraftLink.EV_VMOB_HURT, b);
	}

	/** EV_MOB_HURT: a Valheim monster (proxy attacker) hit an MC mob. */
	public static void hurtMob(Minecraft mc, byte[] p) {
		if (p.length < 20) return;
		dev.valcraft.client.Ops.run(mc, ValCraftLink.EV_MOB_HURT, p);
	}
	public static void hurtMobOn(ServerPlayer sp, byte[] p) {
		ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		int id = b.getInt(0), att = b.getInt(16); float dmg = b.getFloat(4), dx = b.getFloat(8), dz = b.getFloat(12);
		{
			if (!(sp.serverLevel().getEntity(id) instanceof LivingEntity le) || !le.isAlive()) return;
			ValheimProxy src = proxies.get(pk(sp, att));
			var ds = src != null && !src.isRemoved() ? le.damageSources().mobAttack(src) : le.damageSources().generic();
			if (le.hurt(ds, dmg) && (dx != 0 || dz != 0)) le.knockback(0.4, -dx, -dz);
		}
	}
}
