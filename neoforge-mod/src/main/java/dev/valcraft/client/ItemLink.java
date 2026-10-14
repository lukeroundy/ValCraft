package dev.valcraft.client;

import dev.valcraft.ValCraft;
import dev.valcraft.link.ValCraftLink;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import net.minecraft.client.Minecraft;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;

/**
 * 0.8.0 item bridge, MC side. Valheim owns the mapping table; MC only gives/consumes vanilla items by id on the
 * integrated server (authoritative) and reports the held item / inventory. Ring sends happen on the render thread.
 */
final class ItemLink {
	private ItemLink() {}
	private static String lastHeld = "";

	/** 0.15.5: where items lent to Valheim (crafting / building) came from, per player and item id, so they return to the same slots. */
	static final class Slots {
		record Took(int slot, int count, long at) {}
		static final Map<java.util.UUID, Map<String, java.util.ArrayDeque<Took>>> BY = new java.util.HashMap<>();
		static void took(ServerPlayer pl, String id, int slot, int n) {
			if (pl == null || n <= 0) return;
			var q = BY.computeIfAbsent(pl.getUUID(), k -> new java.util.HashMap<>()).computeIfAbsent(id, k -> new java.util.ArrayDeque<>());
			q.addLast(new Took(slot, n, System.currentTimeMillis()));
			while (q.size() > 64) q.removeFirst();
		}
		/** Puts as much of st as fits into the remembered slots (empty, or the same item and components); st shrinks. */
		static void putBack(ServerPlayer pl, String id, ItemStack st) {
			var m = BY.get(pl.getUUID()); if (m == null) return;
			var q = m.get(id); if (q == null) return;
			var inv = pl.getInventory(); long now = System.currentTimeMillis();
			while (!st.isEmpty() && !q.isEmpty()) {
				var t = q.peekFirst();
				if (now - t.at() > 30 * 60_000L || t.slot() < 0 || t.slot() >= inv.items.size()) { q.removeFirst(); continue; }
				ItemStack cur = inv.items.get(t.slot());
				int want = Math.min(t.count(), st.getCount());
				if (cur.isEmpty()) {
					int n = Math.min(want, st.getMaxStackSize());
					inv.items.set(t.slot(), st.split(n));
					consume(q, t, n);
				} else if (ItemStack.isSameItemSameComponents(cur, st) && cur.getCount() < cur.getMaxStackSize()) {
					int n = Math.min(want, cur.getMaxStackSize() - cur.getCount());
					cur.grow(n); st.shrink(n); consume(q, t, n);
				} else q.removeFirst(); // slot taken by something else meanwhile: normal add
			}
			if (q.isEmpty()) m.remove(id);
		}
		private static void consume(java.util.ArrayDeque<Took> q, Took t, int n) { q.removeFirst(); if (t.count() > n) q.addFirst(new Took(t.slot(), t.count() - n, t.at())); }
	}

	static String str(ByteBuffer b, int off) { byte[] a = new byte[b.limit() - off]; b.get(off, a); return new String(a, StandardCharsets.UTF_8); }
	static Item item(String id) { var rl = ResourceLocation.tryParse(id); return rl == null ? Items.AIR : BuiltInRegistries.ITEM.get(rl); }
	static String id(ItemStack s) { return BuiltInRegistries.ITEM.getKey(s.getItem()).toString(); }

	/** EV_GIVE_ITEM {u32 reqId, u16 count, utf8 id}. */
	static void give(Minecraft mc, byte[] p) { Ops.run(mc, ValCraftLink.EV_GIVE_ITEM, p); }
	public static void giveOn(ServerPlayer pl, byte[] p) {
		ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		int count = b.getShort(4) & 0xFFFF, dur = b.getShort(6) & 0xFFFF; String id = str(b, 8); // v12: + u16 durability
		Item it = item(id);
		if (it == Items.AIR || count <= 0) { ValCraft.LOG.warn("[items] give: unknown item {}", id); return; }
		{
			int left = count;
			while (left > 0) {
				int n = Math.min(left, it.getDefaultMaxStackSize());
				ItemStack st = new ItemStack(it, n);
				if (dur != 0xFFFF && st.isDamageableItem()) st.setDamageValue(Math.min(st.getMaxDamage() - 1, Math.round((1f - dur / 10000f) * st.getMaxDamage())));
				Slots.putBack(pl, id, st);
				if (!st.isEmpty() && !pl.getInventory().add(st) && !st.isEmpty()) pl.drop(st, false);
				left -= n;
			}
			pl.inventoryMenu.broadcastChanges();
			ValCraft.LOG.info("[items] {} got {} x {} from Valheim", pl.getName().getString(), count, id);
		}
	}

	/** EV_CONSUME_REQ {u32 reqId, u16 count, u8 mode (0 held item, 1 by id anywhere), utf8 id ("" = whatever is held)} -> EV_CONSUME_ACK {u32 reqId, u16 removed, utf8 id}. */
	static void consume(Minecraft mc, byte[] p) {
		if (mc.player == null) { ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN); ValCraftClient.sendToValheim(ValCraftLink.EV_CONSUME_ACK, ackBytes(b.getInt(0), 0, 0xFFFF, str(b, 7))); return; }
		Ops.run(mc, ValCraftLink.EV_CONSUME_REQ, p);
	}
	public static void consumeOn(ServerPlayer pl, byte[] p) {
		ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		int req = b.getInt(0), count = b.getShort(4) & 0xFFFF, mode = b.get(6); String want = str(b, 7);
		{
			int removed = 0, dur = 0xFFFF; String got = want;
			if (pl != null) {
				var inv = pl.getInventory();
				if (mode == 0) {
					ItemStack h = pl.getMainHandItem();
					if (!h.isEmpty() && (want.isEmpty() || id(h).equals(want))) { got = id(h); removed = Math.min(count, h.getCount()); if (h.isDamageableItem()) dur = Math.round((1f - (float) h.getDamageValue() / h.getMaxDamage()) * 10000f); if (!pl.getAbilities().instabuild) h.shrink(removed); }
				} else if (mode == 2) { // v27: one gear item (main inventory only) with its quality + crafter -> EV_CONSUME_ACK_Q
					Item it = item(want); int q = 1; String crafter = "";
					for (int i = 0; i < inv.items.size(); i++) {
						ItemStack s = inv.items.get(i);
						if (s.isEmpty() || s.getItem() != it) continue;
						if (s.isDamageableItem()) dur = Math.round((1f - (float) s.getDamageValue() / s.getMaxDamage()) * 10000f);
						q = quality(s); crafter = crafter(s);
						s.shrink(1); removed = 1; Slots.took(pl, want, i, 1); break;
					}
					pl.inventoryMenu.broadcastChanges();
					dev.valcraft.net.ServerOps.toValheim(pl, ValCraftLink.EV_CONSUME_ACK_Q, qBytes(req, removed, dur, q, crafter, want));
					ValCraft.LOG.info("[items] Valheim took {} x {} (quality {}) for crafting", removed, want, q);
					return;
				} else {
					Item it = item(want);
					for (int i = 0; i < inv.getContainerSize() && removed < count; i++) {
						ItemStack s = inv.getItem(i);
						if (s.isEmpty() || s.getItem() != it) continue;
						int n = Math.min(count - removed, s.getCount());
						s.shrink(n); removed += n;
						Slots.took(pl, want, i, n); // 0.15.5: comes back to this slot
					}
				}
				pl.inventoryMenu.broadcastChanges();
			}
			dev.valcraft.net.ServerOps.toValheim(pl, ValCraftLink.EV_CONSUME_ACK, ackBytes(req, removed, dur, got));
			ValCraft.LOG.info("[items] Valheim used {} x {} (asked {})", removed, got, count);
		}
	}

	// ---------------- v27 (0.15.2): crafted Valheim items with quality + crafter ----------------
	static final String Q_KEY = "valcraft_quality", C_KEY = "valcraft_crafter";
	static int quality(ItemStack s) { var cd = s.get(net.minecraft.core.component.DataComponents.CUSTOM_DATA); if (cd == null) return 1; var t = cd.copyTag(); return t.contains(Q_KEY) ? Math.max(1, t.getInt(Q_KEY)) : 1; }
	static String crafter(ItemStack s) { var cd = s.get(net.minecraft.core.component.DataComponents.CUSTOM_DATA); return cd == null ? "" : cd.copyTag().getString(C_KEY); }

	static byte[] qBytes(int req, int count, int dur, int q, String crafter, String id) {
		byte[] c = crafter.getBytes(StandardCharsets.UTF_8); if (c.length > 32) c = java.util.Arrays.copyOf(c, 32);
		byte[] n = id.getBytes(StandardCharsets.UTF_8);
		ByteBuffer b = ByteBuffer.allocate(10 + c.length + n.length).order(ByteOrder.LITTLE_ENDIAN);
		b.putInt(req).putShort((short) count).putShort((short) dur).put((byte) Math.max(1, Math.min(255, q))).put((byte) c.length).put(c).put(n);
		return b.array();
	}

	/** Quality q (Valheim upgrade level): custom_data + lore, attack/armour +20 %/level, max durability +50 %/level. Single-stack items only. */
	static void applyQuality(ItemStack st, int q, String crafter) {
		if (st.getMaxStackSize() != 1 || (q <= 1 && crafter.isEmpty())) return;
		var tag = new net.minecraft.nbt.CompoundTag(); tag.putInt(Q_KEY, q); if (!crafter.isEmpty()) tag.putString(C_KEY, crafter);
		st.set(net.minecraft.core.component.DataComponents.CUSTOM_DATA, net.minecraft.world.item.component.CustomData.of(tag));
		var lore = new ArrayList<net.minecraft.network.chat.Component>();
		lore.add(net.minecraft.network.chat.Component.literal("Quality " + q).withStyle(net.minecraft.ChatFormatting.GOLD));
		if (!crafter.isEmpty()) lore.add(net.minecraft.network.chat.Component.literal("Crafted by " + crafter).withStyle(net.minecraft.ChatFormatting.GRAY));
		st.set(net.minecraft.core.component.DataComponents.LORE, new net.minecraft.world.item.component.ItemLore(lore));
		if (q <= 1) return;
		if (st.isDamageableItem()) st.set(net.minecraft.core.component.DataComponents.MAX_DAMAGE, Math.round(st.getMaxDamage() * (1f + 0.5f * (q - 1))));
		var mods = st.getAttributeModifiers();
		if (mods != null && !mods.modifiers().isEmpty()) {
			float f = 1f + 0.2f * (q - 1);
			var bld = net.minecraft.world.item.component.ItemAttributeModifiers.builder();
			for (var e : mods.modifiers()) {
				var a = e.attribute(); var m = e.modifier();
				boolean scale = m.operation() == net.minecraft.world.entity.ai.attributes.AttributeModifier.Operation.ADD_VALUE && m.amount() > 0
					&& (a.equals(net.minecraft.world.entity.ai.attributes.Attributes.ATTACK_DAMAGE) || a.equals(net.minecraft.world.entity.ai.attributes.Attributes.ARMOR) || a.equals(net.minecraft.world.entity.ai.attributes.Attributes.ARMOR_TOUGHNESS));
				bld.add(a, scale ? new net.minecraft.world.entity.ai.attributes.AttributeModifier(m.id(), m.amount() * f, m.operation()) : m, e.slot());
			}
			st.set(net.minecraft.core.component.DataComponents.ATTRIBUTE_MODIFIERS, bld.build());
		}
	}

	/** EV_GIVE_ITEM_Q (server thread). Overflow drops at the player's feet, like EV_GIVE_ITEM. */
	public static void giveQOn(ServerPlayer pl, byte[] p) {
		ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		int count = b.getShort(4) & 0xFFFF, dur = b.getShort(6) & 0xFFFF, q = Math.max(1, b.get(8) & 0xFF), cl = b.get(9) & 0xFF;
		byte[] c = new byte[cl]; b.get(10, c); String crafter = new String(c, StandardCharsets.UTF_8); String id = str(b, 10 + cl);
		Item it = item(id);
		if (it == Items.AIR || count <= 0) { ValCraft.LOG.warn("[items] give: unknown item {}", id); return; }
		int left = count, dropped = 0;
		while (left > 0) {
			int n = Math.min(left, it.getDefaultMaxStackSize());
			ItemStack st = new ItemStack(it, n);
			applyQuality(st, q, crafter);
			if (dur != 0xFFFF && st.isDamageableItem()) st.setDamageValue(Math.min(st.getMaxDamage() - 1, Math.round((1f - dur / 10000f) * st.getMaxDamage())));
			Slots.putBack(pl, id, st);
			if (!st.isEmpty() && !pl.getInventory().add(st) && !st.isEmpty()) { dropped += st.getCount(); pl.drop(st, false); }
			left -= n;
		}
		pl.inventoryMenu.broadcastChanges();
		ValCraft.LOG.info("[items] {} got {} x {} (quality {}, crafter '{}') from Valheim{}", pl.getName().getString(), count, id, q, crafter, dropped > 0 ? " (" + dropped + " dropped: inventory full)" : "");
	}

	private static byte[] ackBytes(int req, int removed, int dur, String id) {
		byte[] name = id.getBytes(StandardCharsets.UTF_8);
		ByteBuffer b = ByteBuffer.allocate(8 + Math.min(name.length, 200)).order(ByteOrder.LITTLE_ENDIAN);
		b.putInt(req).putShort((short) removed).putShort((short) dur).put(name, 0, Math.min(name.length, 200));
		return b.array();
	}

	/** Render thread, every client tick: EV_HELD_ITEM {u16 count, utf8 id} when the main-hand stack changes. */
	private static int heldResend;
	static void tick(Minecraft mc) {
		if (mc.player == null) return;
		ItemStack h = mc.player.getMainHandItem();
		String now = h.isEmpty() ? "" : id(h) + "#" + h.getCount();
		if (mc.player.isDeadOrDying()) now = "";
		if (now.equals(lastHeld) && ++heldResend < 40) return; // also resend every 2 s in case a message was lost
		heldResend = 0;
		if (mc.player.isDeadOrDying()) h = ItemStack.EMPTY;
		byte[] name = (h.isEmpty() ? "" : id(h)).getBytes(StandardCharsets.UTF_8);
		ByteBuffer b = ByteBuffer.allocate(2 + name.length).order(ByteOrder.LITTLE_ENDIAN);
		b.putShort((short) (h.isEmpty() ? 0 : h.getCount())).put(name);
		if (ValCraftClient.sendToValheim(ValCraftLink.EV_HELD_ITEM, b.array())) lastHeld = now;
	}
	static void reset() { lastHeld = ""; }

	/** EV_INV_REQ -> EV_INV_SNAPSHOT {u8 first, u8 last, u8 n, n x (u16 count, u8 len, utf8 id)}: totals per item id. */
	static void snapshot(Minecraft mc) {
		if (mc.player == null) return;
		Map<String, Integer> tot = new LinkedHashMap<>();
		var inv = mc.player.getInventory();
		for (int i = 0; i < inv.getContainerSize(); i++) { ItemStack s = inv.getItem(i); if (!s.isEmpty()) tot.merge(id(s), s.getCount(), Integer::sum); }
		List<byte[]> entries = new ArrayList<>();
		for (var e : tot.entrySet()) {
			byte[] n = e.getKey().getBytes(StandardCharsets.UTF_8);
			ByteBuffer b = ByteBuffer.allocate(3 + n.length).order(ByteOrder.LITTLE_ENDIAN);
			b.putShort((short) Math.min(65535, e.getValue())).put((byte) n.length).put(n);
			entries.add(b.array());
		}
		List<byte[]> msgs = new ArrayList<>();
		ByteBuffer cur = null; int cnt = 0;
		for (int i = 0; i <= entries.size(); i++) {
			byte[] en = i < entries.size() ? entries.get(i) : null;
			if (cur == null || en == null || cur.position() + en.length > 240) {
				if (cur != null) { cur.put(2, (byte) cnt); byte[] a = new byte[cur.position()]; cur.get(0, a); msgs.add(a); }
				if (en == null) break;
				cur = ByteBuffer.allocate(248).order(ByteOrder.LITTLE_ENDIAN); cur.put((byte) 0).put((byte) 0).put((byte) 0); cnt = 0;
			}
			cur.put(en); cnt++;
		}
		if (msgs.isEmpty()) msgs.add(new byte[] { 0, 0, 0 });
		msgs.get(0)[0] = 1; msgs.get(msgs.size() - 1)[1] = 1;
		for (byte[] m : msgs) ValCraftClient.sendToValheim(ValCraftLink.EV_INV_SNAPSHOT, m);
		ValCraft.LOG.info("[items] inventory snapshot to Valheim: {} item types", tot.size());
	}
}
