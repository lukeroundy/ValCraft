package dev.valcraft.client;

import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.GuiGraphics;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.network.chat.Component;
import net.minecraft.util.FormattedCharSequence;

/** 0.11.0: Valheim rune stone / vegvisir text (EV_LORE_TEXT) as a Minecraft screen. Esc or click closes; never blocks. */
public final class LoreScreen extends Screen {
	private static final ByteArrayOutputStream acc = new ByteArrayOutputStream();
	private final String body;
	private LoreScreen(String title, String body) { super(Component.literal(title)); this.body = body; }

	/** Render thread. */
	static void receive(Minecraft mc, byte[] p) {
		if (p.length < 1) return;
		int flags = p[0] & 0xFF;
		if ((flags & 1) != 0) acc.reset();
		acc.write(p, 1, p.length - 1);
		if ((flags & 2) == 0) return;
		String s = acc.toString(StandardCharsets.UTF_8); acc.reset();
		int nl = s.indexOf('\n');
		String title = nl < 0 ? s : s.substring(0, nl), body = nl < 0 ? "" : s.substring(nl + 1);
		if (mc.screen == null || mc.screen instanceof LoreScreen) mc.setScreen(new LoreScreen(title, body));
		else if (mc.player != null) mc.player.displayClientMessage(Component.literal(title + ": " + body.replace('\n', ' ')), false);
	}

	@Override public boolean isPauseScreen() { return false; }

	@Override public void render(GuiGraphics g, int mx, int my, float pt) {
		super.render(g, mx, my, pt);
		int w = Math.min(320, width - 40), x = (width - w) / 2;
		var lines = font.split(Component.literal(body), w - 16);
		int h = Math.min(height - 40, 36 + lines.size() * 10), y = (height - h) / 2;
		g.fill(x, y, x + w, y + h, 0xE0281C10);
		g.fill(x, y, x + w, y + 1, 0xFFB08850); g.fill(x, y + h - 1, x + w, y + h, 0xFFB08850);
		g.drawCenteredString(font, title, width / 2, y + 8, 0xFFE8C070);
		int ly = y + 24;
		for (FormattedCharSequence l : lines) { if (ly > y + h - 12) break; g.drawString(font, l, x + 8, ly, 0xFFE8E0D0); ly += 10; }
		g.drawCenteredString(font, "(Esc / click to close)", width / 2, y + h + 4, 0xFF908070);
	}

	@Override public boolean mouseClicked(double x, double y, int b) { onClose(); return true; }
}
