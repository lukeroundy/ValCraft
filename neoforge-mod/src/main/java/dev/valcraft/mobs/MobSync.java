package dev.valcraft.mobs;

import dev.valcraft.ValCraft;
import dev.valcraft.client.ValCraftClient;
import dev.valcraft.link.MobCodec;
import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.Minecraft;
import net.minecraft.core.registries.Registries;
import net.minecraft.server.level.ServerLevel;
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
	private static final Map<Integer, ValheimProxy> proxies = new HashMap<>(); // server thread only

	/** Server thread: proxy of a Valheim creature (instance id), or null. */
	public static ValheimProxy proxy(int valheimId) { var p = proxies.get(valheimId); return p != null && p.isAlive() ? p : null; }

	public static void receiveVList(Minecraft mc, byte[] p) {
		if (p[0] != 0) pending.clear();
		pending.addAll(MobCodec.decodeList(p));
		if (p[1] == 0) return;
		var list = new ArrayList<>(pending); pending.clear();
		var server = mc.getSingleplayerServer(); if (server == null || mc.player == null) return;
		var uuid = mc.player.getUUID();
		server.execute(() -> {
			var sp = server.getPlayerList().getPlayer(uuid); if (sp == null) return;
			ServerLevel lvl = sp.serverLevel(); long now = System.currentTimeMillis();
			Set<Integer> keep = new HashSet<>();
			for (var m : list) {
				keep.add(m.id());
				ValheimProxy px = proxies.get(m.id());
				if (px == null || px.isRemoved() || px.level() != lvl) {
					px = PROXY.get().create(lvl); if (px == null) continue;
					px.vid = m.id(); px.setSize(m.w(), m.h()); px.moveTo(m.x(), m.y(), m.z(), 0, 0); px.lastSeen = now;
					lvl.addFreshEntity(px); proxies.put(m.id(), px);
					ValCraft.LOG.info("[mobs] proxy for Valheim monster {} ({}x{} m)", m.id(), m.w(), m.h());
				}
				px.setSize(m.w(), m.h()); px.setPos(m.x(), m.y(), m.z()); px.lastSeen = now;
			}
			proxies.entrySet().removeIf(en -> { if (keep.contains(en.getKey()) && !en.getValue().isRemoved()) return false; en.getValue().discard(); return true; });
		});
	}

	public static void clear(Minecraft mc) {
		pending.clear();
		var server = mc.getSingleplayerServer(); if (server == null) return;
		server.execute(() -> { proxies.values().forEach(Entity::discard); proxies.clear(); });
	}

	/** Server thread: something hit a proxy -> Valheim. Sent from the render thread. */
	static void forwardToValheim(int vid, float amount, int attackerId) {
		byte[] b = MobCodec.vmobHurt(vid, amount, attackerId);
		Minecraft.getInstance().execute(() -> ValCraftClient.sendToValheim(ValCraftLink.EV_VMOB_HURT, b));
	}

	/** EV_MOB_HURT: a Valheim monster (proxy attacker) hit an MC mob. */
	public static void hurtMob(Minecraft mc, byte[] p) {
		if (p.length < 20) return;
		ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		int id = b.getInt(0), att = b.getInt(16); float dmg = b.getFloat(4), dx = b.getFloat(8), dz = b.getFloat(12);
		var server = mc.getSingleplayerServer(); if (server == null || mc.player == null) return;
		var uuid = mc.player.getUUID();
		server.execute(() -> {
			var sp = server.getPlayerList().getPlayer(uuid); if (sp == null) return;
			if (!(sp.serverLevel().getEntity(id) instanceof LivingEntity le) || !le.isAlive()) return;
			ValheimProxy src = proxies.get(att);
			var ds = src != null && !src.isRemoved() ? le.damageSources().mobAttack(src) : le.damageSources().generic();
			if (le.hurt(ds, dmg) && (dx != 0 || dz != 0)) le.knockback(0.4, -dx, -dz);
		});
	}
}
