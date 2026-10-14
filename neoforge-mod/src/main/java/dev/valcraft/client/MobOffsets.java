package dev.valcraft.client;

import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.projectile.ProjectileUtil;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.EntityHitResult;
import net.minecraft.world.phys.Vec3;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.Optional;
import java.util.concurrent.ConcurrentHashMap;
import java.util.function.Predicate;

/** 0.15.7: Valheim draws MC mobs re-seated on its smooth ground (up to ±3 m from their MC height); EV_MOB_OFFSETS tells us
 *  by how much, and the crosshair entity pick uses the boxes moved by the same amount, so you hit what you see. */
public final class MobOffsets {
	private MobOffsets() {}
	static final ConcurrentHashMap<Integer, Float> OFF = new ConcurrentHashMap<>();
	static volatile long lastMs;

	public static void receive(byte[] p) {
		if (p == null || p.length < 2) return;
		var b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		int n = b.getShort(0) & 0xFFFF;
		var fresh = new java.util.HashMap<Integer, Float>();
		for (int i = 0; i < n && 2 + i * 8 + 8 <= p.length; i++) {
			float dy = b.getFloat(6 + i * 8);
			if (Math.abs(dy) <= 3.5f) fresh.put(b.getInt(2 + i * 8), dy);
		}
		OFF.keySet().retainAll(fresh.keySet()); OFF.putAll(fresh); lastMs = System.currentTimeMillis();
	}
	public static float get(Entity e) { if (System.currentTimeMillis() - lastMs > 2000) return 0f; Float f = OFF.get(e.getId()); return f == null ? 0f : f; }
	public static void clear() { OFF.clear(); }

	/** ProjectileUtil.getEntityHitResult with every entity box moved by its Valheim re-seat offset (vanilla logic otherwise). */
	public static EntityHitResult pick(Entity shooter, Vec3 start, Vec3 end, AABB area, Predicate<Entity> filter, double maxSq) {
		if (OFF.isEmpty() || System.currentTimeMillis() - lastMs > 2000) return ProjectileUtil.getEntityHitResult(shooter, start, end, area, filter, maxSq);
		double best = maxSq; Entity hit = null; Vec3 at = null;
		for (Entity e : shooter.level().getEntities(shooter, area.inflate(0, 3.5, 0), filter)) {
			AABB bb = e.getBoundingBox().inflate(e.getPickRadius());
			float dy = get(e); if (dy != 0f) bb = bb.move(0, dy, 0);
			Optional<Vec3> o = bb.clip(start, end);
			if (bb.contains(start)) { if (best >= 0) { hit = e; at = o.orElse(start); best = 0; } }
			else if (o.isPresent()) {
				double d = start.distanceToSqr(o.get());
				if (d < best || best == 0) {
					if (e.getRootVehicle() == shooter.getRootVehicle() && !e.canRiderInteract()) { if (best == 0) { hit = e; at = o.get(); } }
					else { hit = e; at = o.get(); best = d; }
				}
			}
		}
		return hit == null ? null : new EntityHitResult(hit, at);
	}
}
