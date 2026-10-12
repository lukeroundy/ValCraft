package dev.valcraft.client;

import com.mojang.blaze3d.vertex.PoseStack;
import dev.valcraft.ValCraft;
import dev.valcraft.link.BlockCodec;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import net.minecraft.client.Minecraft;
import net.minecraft.client.renderer.texture.OverlayTexture;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.entity.decoration.ArmorStand;
import net.minecraft.world.entity.decoration.ItemFrame;
import net.minecraft.world.entity.item.FallingBlockEntity;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.world.entity.item.PrimedTnt;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.entity.vehicle.AbstractMinecart;
import net.minecraft.world.entity.vehicle.Boat;
import net.minecraft.world.item.ItemDisplayContext;
import net.minecraft.world.level.block.Block;

/**
 * 0.7.3: nearby non-player entities (dropped items, armor stands, item frames, falling blocks, primed TNT, minecarts,
 * boats) for Valheim. Geometry is captured once per "look" (type + rotation bucket + equipment/item/state) with the
 * same capture as block entities and cached by mesh id; every 100 ms a full snapshot of (entity, mesh, position) is
 * sent. Dropped items are captured static; Valheim spins/bobs them. Render thread (called from BlockSync).
 */
final class EntitySync {
	private EntitySync() {}
	static volatile int maxEntities = 64;
	private static final Map<String, Integer> meshOf = new HashMap<>();
	private static int nextMesh = 1;
	private static long nextUpdate;

	static void reset() { meshOf.clear(); nextMesh = 1; }

	static void tick(Minecraft mc, java.util.ArrayDeque<dev.valcraft.link.ValCraftLink.Event> outbox) {
		long now = System.currentTimeMillis();
		if (now < nextUpdate || mc.level == null || mc.player == null || maxEntities <= 0) return;
		nextUpdate = now + 100;
		double r = BlockSync.radius, r2 = r * r;
		List<Entity> list = new ArrayList<>();
		for (Entity e : mc.level.entitiesForRendering()) {
			if (e instanceof Player || !wanted(e) || e.isInvisible() && !(e instanceof ArmorStand)) continue;
			if (e.distanceToSqr(mc.player) > r2) continue;
			list.add(e);
		}
		list.sort((a, b) -> Double.compare(a.distanceToSqr(mc.player), b.distanceToSqr(mc.player)));
		if (list.size() > maxEntities) list = list.subList(0, maxEntities);
		List<BlockCodec.Ent> ents = new ArrayList<>();
		int captures = 0;
		for (Entity e : list) {
			String key = key(e);
			Integer mesh = meshOf.get(key);
			if (mesh == null) {
				if (captures++ >= 4) continue; // spread capture cost over frames
				mesh = nextMesh++;
				meshOf.put(key, mesh);
				var q = capture(mc, e);
				outbox.addAll(BlockCodec.entityMesh(mesh, q));
				if (meshOf.size() <= 20 || meshOf.size() % 50 == 0) ValCraft.LOG.info("[entities] mesh {} for {} ({} quads)", mesh, key, q.size());
			}
			var p = e.position();
			ents.add(new BlockCodec.Ent(e.getId(), mesh, (float) p.x, (float) p.y, (float) p.z, e instanceof ItemEntity ? 1 : 0));
		}
		outbox.addAll(BlockCodec.entityUpdate(ents));
	}

	static boolean wanted(Entity e) {
		return e instanceof ItemEntity || e instanceof ArmorStand || e instanceof ItemFrame || e instanceof FallingBlockEntity || e instanceof PrimedTnt
				|| e instanceof AbstractMinecart || e instanceof Boat;
	}

	private static int bucket(float deg) { return Math.round(((deg % 360) + 360) % 360 / 5f); }

	static String key(Entity e) {
		String t = BuiltInRegistries.ENTITY_TYPE.getKey(e.getType()).toString();
		if (e instanceof ItemEntity ie) return t + "|" + ie.getItem().getItem() + "|" + ie.getItem().getComponentsPatch().hashCode();
		if (e instanceof FallingBlockEntity fb) return t + "|" + Block.getId(fb.getBlockState());
		if (e instanceof PrimedTnt tnt) return t + "|" + Math.min(10, tnt.getFuse() / 4);
		StringBuilder k = new StringBuilder(t).append('|').append(bucket(e.getYRot())).append('|').append(bucket(e.getXRot()));
		if (e instanceof ArmorStand as) {
			k.append('|').append(bucket(as.yBodyRot)).append(as.isSmall()).append(as.isShowArms()).append(as.isNoBasePlate());
			for (EquipmentSlot sl : EquipmentSlot.values()) k.append(',').append(as.getItemBySlot(sl).getItem());
			k.append(as.getHeadPose()).append(as.getBodyPose()).append(as.getLeftArmPose()).append(as.getRightArmPose()).append(as.getLeftLegPose()).append(as.getRightLegPose());
		}
		if (e instanceof ItemFrame f) k.append('|').append(f.getDirection()).append(f.getItem().getItem()).append(f.getRotation());
		if (e instanceof AbstractMinecart m) k.append('|').append(Block.getId(m.getDisplayBlockState()));
		return k.toString();
	}

	@SuppressWarnings({ "unchecked", "rawtypes" })
	static List<BlockCodec.Quad> capture(Minecraft mc, Entity e) {
		Capture cap = new Capture();
		try {
			PoseStack ps = new PoseStack();
			if (e instanceof ItemEntity ie) {
				ps.translate(0, 0.25, 0);
				mc.getItemRenderer().renderStatic(ie.getItem(), ItemDisplayContext.GROUND, 0xF000F0, OverlayTexture.NO_OVERLAY, ps, cap, mc.level, 0);
			} else {
				var r = (net.minecraft.client.renderer.entity.EntityRenderer) mc.getEntityRenderDispatcher().getRenderer(e);
				r.render(e, e.getYRot(), 1f, ps, cap, 0xF000F0);
			}
		} catch (Throwable t) { ValCraft.LOG.warn("[entities] capture of {} failed: {}", e, t.toString()); }
		return Capture.toTileQuads(cap.quads());
	}
}
