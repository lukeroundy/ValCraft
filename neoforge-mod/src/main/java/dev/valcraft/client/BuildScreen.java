package dev.valcraft.client;

import dev.valcraft.link.BuildCodec;
import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.gui.GuiGraphics;
import net.minecraft.client.gui.components.Button;
import net.minecraft.client.gui.components.Tooltip;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.network.chat.Component;

import java.util.ArrayList;
import java.util.List;

/** 0.8.5: MC-side Valheim build picker (mace right-click). Click a piece -> EV_BUILD_SELECT; Esc closes. */
public final class BuildScreen extends Screen {
	private final List<BuildCodec.Piece> pieces;
	private int category, page;
	private static int lastCategory = -1;

	BuildScreen(List<BuildCodec.Piece> pieces) {
		super(Component.literal("Valheim build pieces"));
		this.pieces = pieces;
		category = lastCategory >= 0 && has(lastCategory) ? lastCategory : firstCategory();
	}

	private boolean has(int c) { for (var p : pieces) if (p.category() == c) return true; return false; }
	private int firstCategory() { for (int c = 0; c < 256; c++) if (has(c)) return c; return 0; }
	private static String catName(int c) { return c < BuildCodec.CATEGORIES.length ? BuildCodec.CATEGORIES[c] : "Other " + c; }

	@Override protected void init() {
		clearWidgets();
		// category tabs
		List<Integer> cats = new ArrayList<>();
		for (int c = 0; c < 256; c++) if (has(c)) cats.add(c);
		int tabW = Math.min(80, (width - 20) / Math.max(1, cats.size())), x = (width - tabW * cats.size()) / 2;
		for (int c : cats) {
			final int cc = c;
			var b = Button.builder(Component.literal((c == category ? "> " : "") + catName(c)), btn -> { category = cc; lastCategory = cc; page = 0; init(); }).bounds(x, 20, tabW - 2, 20).build();
			addRenderableWidget(b); x += tabW;
		}
		List<BuildCodec.Piece> list = new ArrayList<>();
		for (var p : pieces) if (p.category() == category) list.add(p);
		int cols = Math.max(1, Math.min(4, (width - 20) / 150)), rows = Math.max(1, (height - 100) / 22), per = cols * rows;
		int pages = Math.max(1, (list.size() + per - 1) / per); page = Math.min(page, pages - 1);
		int bw = 146, x0 = (width - cols * (bw + 4)) / 2, y0 = 50;
		for (int i = 0; i < per && page * per + i < list.size(); i++) {
			var p = list.get(page * per + i);
			String label = (p.canBuild() ? "" : "\u00a77") + p.name();
			var b = Button.builder(Component.literal(label), btn -> select(p))
				.bounds(x0 + (i % cols) * (bw + 4), y0 + (i / cols) * 22, bw, 20)
				.tooltip(Tooltip.create(Component.literal(p.name() + "\n" + (p.cost().isEmpty() ? "free" : p.cost()) + (p.canBuild() ? "" : "\n\u00a7cnot enough materials (or no workbench in range)"))))
				.build();
			addRenderableWidget(b);
		}
		if (pages > 1) {
			addRenderableWidget(Button.builder(Component.literal("<"), b -> { page = Math.max(0, page - 1); init(); }).bounds(width / 2 - 60, height - 30, 20, 20).build());
			addRenderableWidget(Button.builder(Component.literal(">"), b -> { page = page + 1; init(); }).bounds(width / 2 + 40, height - 30, 20, 20).build());
		}
	}

	private void select(BuildCodec.Piece p) {
		ValCraftClient.sendToValheim(ValCraftLink.EV_BUILD_SELECT, new byte[] { (byte) p.index(), (byte) (p.index() >> 8) });
		onClose();
	}

	@Override public void render(GuiGraphics g, int mx, int my, float pt) {
		super.render(g, mx, my, pt);
		g.drawCenteredString(font, "Valheim build pieces \u2014 pick one, then aim and left-click to place (sneak+left removes, sneak+right rotates)", width / 2, 6, 0xFFFFFF);
	}

	@Override public boolean isPauseScreen() { return false; }
}
