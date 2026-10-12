package dev.valcraft.link;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/** 0.8.5 (v13) EV_BUILD_PIECES decoder (layout: protocol/BuildCodec.cs). */
public final class BuildCodec {
	private BuildCodec() {}
	public record Piece(int index, int category, boolean canBuild, String name, String cost) {}
	public static final String[] CATEGORIES = { "Misc", "Crafting", "Building", "Stone", "Furniture", "Feasts", "Food", "Meads" };

	public static boolean first(byte[] p) { return p.length >= 3 && p[0] != 0; }
	public static boolean last(byte[] p) { return p.length >= 3 && p[1] != 0; }

	public static List<Piece> decode(byte[] p) {
		List<Piece> r = new ArrayList<>();
		int n = p[2] & 0xFF, o = 3;
		for (int i = 0; i < n && o + 5 <= p.length; i++) {
			int idx = (p[o] & 0xFF) | (p[o + 1] & 0xFF) << 8, cat = p[o + 2] & 0xFF; boolean can = (p[o + 3] & 1) != 0;
			int nl = p[o + 4] & 0xFF; String name = new String(p, o + 5, nl, StandardCharsets.UTF_8); o += 5 + nl;
			int cl = p[o] & 0xFF; String cost = new String(p, o + 1, cl, StandardCharsets.UTF_8); o += 1 + cl;
			r.add(new Piece(idx, cat, can, name, cost));
		}
		return r;
	}
}
