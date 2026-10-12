package dev.valcraft.client;

import dev.valcraft.ValCraft;
import dev.valcraft.link.ChestCodec;
import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.Minecraft;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;

/** 0.10.0: Valheim chest contents (EV_CHEST_CONTENTS) -> ChestScreen; clicks -> EV_CHEST_OP. Render thread. */
final class ChestLink {
	private ChestLink() {}
	private static final ChestCodec.Reader reader = new ChestCodec.Reader();
	static ChestCodec.Header header; static ChestCodec.Entry[] slots = new ChestCodec.Entry[0];

	static void contents(Minecraft mc, byte[] p) {
		if (!reader.add(p)) return;
		header = reader.header;
		if (header == null || header.w() == 0) { if (mc.screen instanceof ChestScreen) mc.setScreen(null); header = null; return; }
		slots = new ChestCodec.Entry[Math.max(1, header.w() * header.h())];
		for (var e : reader.entries) if (e.slot() < slots.length) slots[e.slot()] = e;
		if (!(mc.screen instanceof ChestScreen)) {
			if (mc.screen != null) return; // something else open: don't steal it
			mc.setScreen(new ChestScreen());
			ValCraft.LOG.info("[chest] opened Valheim chest '{}' ({}x{}, {} stacks)", header.name(), header.w(), header.h(), reader.entries.size());
		}
	}

	static ItemStack stackOf(ChestCodec.Entry e) {
		if (e == null) return ItemStack.EMPTY;
		var it = e.mcId().isEmpty() ? null : BuiltInRegistries.ITEM.getOptional(ResourceLocation.tryParse(e.mcId())).orElse(null);
		ItemStack st;
		if (it == null || it == Items.AIR) {
			st = new ItemStack(dev.valcraft.content.ModContent.VALHEIM_ITEM.get(), Math.max(1, Math.min(99, e.count())));
			st.set(net.minecraft.core.component.DataComponents.CUSTOM_NAME, net.minecraft.network.chat.Component.literal(e.name() + " (Valheim only)"));
		} else {
			st = new ItemStack(it, Math.max(1, Math.min(it.getDefaultMaxStackSize() > 1 ? 99 : 1, e.count())));
			if (e.dur() != 0xFFFF && st.isDamageableItem()) st.setDamageValue(Math.min(st.getMaxDamage() - 1, Math.round((1f - e.dur() / 10000f) * st.getMaxDamage())));
		}
		return st;
	}

	static void take(int slot, int count) {
		if (header == null) return;
		ValCraftClient.sendToValheim(ValCraftLink.EV_CHEST_OP, ChestCodec.op(header.serial(), ChestCodec.OP_TAKE, slot, count, 0xFFFF, ""));
	}

	/** Remove from the MC inventory slot on the server, then tell Valheim to store it (Valheim gives it back if it can't). */
	static void deposit(Minecraft mc, int invSlot, boolean half) {
		if (header == null || mc.player == null) return;
		var server = mc.getSingleplayerServer(); if (server == null) return;
		long serial = header.serial(); var uuid = mc.player.getUUID();
		server.execute(() -> {
			var sp = server.getPlayerList().getPlayer(uuid); if (sp == null) return;
			ItemStack st = sp.getInventory().getItem(invSlot);
			if (st.isEmpty()) return;
			int n = half ? Math.max(1, st.getCount() / 2) : st.getCount();
			String id = BuiltInRegistries.ITEM.getKey(st.getItem()).toString();
			int dur = st.isDamageableItem() ? Math.round((1f - (float) st.getDamageValue() / st.getMaxDamage()) * 10000f) : 0xFFFF;
			st.shrink(n);
			sp.inventoryMenu.broadcastChanges();
			byte[] op = ChestCodec.op(serial, ChestCodec.OP_DEPOSIT, 0, n, dur, id);
			mc.execute(() -> ValCraftClient.sendToValheim(ValCraftLink.EV_CHEST_OP, op));
		});
	}

	static void close() {
		if (header != null) ValCraftClient.sendToValheim(ValCraftLink.EV_CHEST_OP, ChestCodec.op(header.serial(), ChestCodec.OP_CLOSE, 0, 0, 0xFFFF, ""));
		header = null;
	}
	static void reset(Minecraft mc) { header = null; if (mc.screen instanceof ChestScreen) mc.setScreen(null); }
}
