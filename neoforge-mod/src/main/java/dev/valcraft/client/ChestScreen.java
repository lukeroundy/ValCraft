package dev.valcraft.client;

import net.minecraft.client.gui.GuiGraphics;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.network.chat.Component;
import net.minecraft.world.item.ItemStack;

/**
 * 0.10.0: a Valheim chest in Minecraft. Top: the chest (Valheim's grid; Valheim-only items show as parchment).
 * Bottom: your inventory. Click a chest stack to take it (right-click: half), click an inventory stack to store it.
 */
public final class ChestScreen extends Screen {
	private static final int S = 18;
	ChestScreen() { super(Component.literal("Valheim chest")); }

	private int cw() { return ChestLink.header == null ? 1 : ChestLink.header.w(); }
	private int ch() { return ChestLink.header == null ? 1 : ChestLink.header.h(); }
	private int left() { return (width - Math.max(cw(), 9) * S) / 2; }
	private int chestTop() { return Math.max(20, (height - (ch() * S + 4 * S + 40)) / 2); }
	private int invTop() { return chestTop() + ch() * S + 24; }

	@Override public boolean isPauseScreen() { return false; }

	@Override public void render(GuiGraphics g, int mx, int my, float pt) {
		super.render(g, mx, my, pt);
		if (ChestLink.header == null) return;
		int l = left(), ct = chestTop(), it = invTop();
		g.drawString(font, ChestLink.header.name() + "  (click: take, right-click: half)", l, ct - 12, 0xFFE0C080);
		ItemStack hover = ItemStack.EMPTY;
		for (int i = 0; i < cw() * ch(); i++) {
			int x = l + (i % cw()) * S, y = ct + (i / cw()) * S;
			g.fill(x, y, x + S - 1, y + S - 1, 0xC0302418);
			var st = ChestLink.stackOf(i < ChestLink.slots.length ? ChestLink.slots[i] : null);
			if (!st.isEmpty()) {
				g.renderItem(st, x + 1, y + 1);
				var e = ChestLink.slots[i];
				g.renderItemDecorations(font, st, x + 1, y + 1, e.count() > 1 ? Integer.toString(e.count()) : "");
				if (mx >= x && mx < x + S && my >= y && my < y + S) hover = st;
			}
		}
		g.drawString(font, "Inventory  (click: store in chest)", l, it - 12, 0xFFC0C0C0);
		var inv = minecraft.player.getInventory();
		for (int i = 0; i < 36; i++) {
			int row = i < 9 ? 3 : (i - 9) / 9, col = i % 9;
			int x = l + col * S, y = it + row * S + (i < 9 ? 4 : 0);
			g.fill(x, y, x + S - 1, y + S - 1, 0xC0202020);
			var st = inv.getItem(i);
			if (!st.isEmpty()) { g.renderItem(st, x + 1, y + 1); g.renderItemDecorations(font, st, x + 1, y + 1); if (mx >= x && mx < x + S && my >= y && my < y + S) hover = st; }
		}
		if (!hover.isEmpty()) g.renderTooltip(font, hover, mx, my);
	}

	@Override public boolean mouseClicked(double mx, double my, int button) {
		if (ChestLink.header == null) return super.mouseClicked(mx, my, button);
		int l = left(), ct = chestTop(), it = invTop();
		int cx = (int) Math.floor((mx - l) / S), cy = (int) Math.floor((my - ct) / S);
		if (cx >= 0 && cx < cw() && cy >= 0 && cy < ch()) {
			int slot = cy * cw() + cx;
			var e = slot < ChestLink.slots.length ? ChestLink.slots[slot] : null;
			if (e != null) ChestLink.take(slot, button == 1 ? Math.max(1, e.count() / 2) : e.count());
			return true;
		}
		int ix = (int) Math.floor((mx - l) / S);
		double ry = my - it;
		if (ix >= 0 && ix < 9 && ry >= 0) {
			int row = (int) Math.floor(ry / S);
			int idx = row < 3 ? 9 + row * 9 + ix : (ry >= 3 * S + 4 && ry < 4 * S + 4) ? ix : -1;
			if (idx >= 0) { ChestLink.deposit(minecraft, idx, button == 1); return true; }
		}
		return super.mouseClicked(mx, my, button);
	}

	@Override public void onClose() { ChestLink.close(); super.onClose(); }
}
