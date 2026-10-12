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

	static String str(ByteBuffer b, int off) { byte[] a = new byte[b.limit() - off]; b.get(off, a); return new String(a, StandardCharsets.UTF_8); }
	static Item item(String id) { var rl = ResourceLocation.tryParse(id); return rl == null ? Items.AIR : BuiltInRegistries.ITEM.get(rl); }
	static String id(ItemStack s) { return BuiltInRegistries.ITEM.getKey(s.getItem()).toString(); }

	private static ServerPlayer sp(Minecraft mc) {
		var server = mc.getSingleplayerServer();
		return server == null || mc.player == null ? null : server.getPlayerList().getPlayer(mc.player.getUUID());
	}

	/** EV_GIVE_ITEM {u32 reqId, u16 count, utf8 id}. */
	static void give(Minecraft mc, byte[] p) {
		ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		int count = b.getShort(4) & 0xFFFF, dur = b.getShort(6) & 0xFFFF; String id = str(b, 8); // v12: + u16 durability
		Item it = item(id);
		if (it == Items.AIR || count <= 0) { ValCraft.LOG.warn("[items] give: unknown item {}", id); return; }
		var server = mc.getSingleplayerServer();
		if (server == null) return;
		server.execute(() -> {
			ServerPlayer pl = sp(mc);
			if (pl == null) return;
			int left = count;
			while (left > 0) {
				int n = Math.min(left, it.getDefaultMaxStackSize());
				ItemStack st = new ItemStack(it, n);
				if (dur != 0xFFFF && st.isDamageableItem()) st.setDamageValue(Math.min(st.getMaxDamage() - 1, Math.round((1f - dur / 10000f) * st.getMaxDamage())));
				if (!pl.getInventory().add(st) && !st.isEmpty()) pl.drop(st, false);
				left -= n;
			}
			pl.inventoryMenu.broadcastChanges();
			ValCraft.LOG.info("[items] got {} x {} from Valheim", count, id);
		});
	}

	/** EV_CONSUME_REQ {u32 reqId, u16 count, u8 mode (0 held item, 1 by id anywhere), utf8 id ("" = whatever is held)} -> EV_CONSUME_ACK {u32 reqId, u16 removed, utf8 id}. */
	static void consume(Minecraft mc, byte[] p) {
		ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		int req = b.getInt(0), count = b.getShort(4) & 0xFFFF, mode = b.get(6); String want = str(b, 7);
		var server = mc.getSingleplayerServer();
		if (server == null) { ack(mc, req, 0, 0xFFFF, want); return; }
		server.execute(() -> {
			ServerPlayer pl = sp(mc);
			int removed = 0, dur = 0xFFFF; String got = want;
			if (pl != null) {
				var inv = pl.getInventory();
				if (mode == 0) {
					ItemStack h = pl.getMainHandItem();
					if (!h.isEmpty() && (want.isEmpty() || id(h).equals(want))) { got = id(h); removed = Math.min(count, h.getCount()); if (h.isDamageableItem()) dur = Math.round((1f - (float) h.getDamageValue() / h.getMaxDamage()) * 10000f); if (!pl.getAbilities().instabuild) h.shrink(removed); }
				} else {
					Item it = item(want);
					for (int i = 0; i < inv.getContainerSize() && removed < count; i++) {
						ItemStack s = inv.getItem(i);
						if (s.isEmpty() || s.getItem() != it) continue;
						int n = Math.min(count - removed, s.getCount());
						s.shrink(n); removed += n;
					}
				}
				pl.inventoryMenu.broadcastChanges();
			}
			ack(mc, req, removed, dur, got);
			ValCraft.LOG.info("[items] Valheim used {} x {} (asked {})", removed, got, count);
		});
	}

	private static void ack(Minecraft mc, int req, int removed, int dur, String id) {
		byte[] name = id.getBytes(StandardCharsets.UTF_8);
		ByteBuffer b = ByteBuffer.allocate(8 + Math.min(name.length, 200)).order(ByteOrder.LITTLE_ENDIAN);
		b.putInt(req).putShort((short) removed).putShort((short) dur).put(name, 0, Math.min(name.length, 200));
		byte[] a = b.array();
		mc.execute(() -> ValCraftClient.sendToValheim(ValCraftLink.EV_CONSUME_ACK, a));
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
