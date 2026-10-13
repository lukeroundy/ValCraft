package dev.valcraft.client;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import dev.valcraft.ValCraft;
import net.minecraft.client.Minecraft;
import net.minecraft.client.renderer.ItemBlockRenderTypes;
import net.minecraft.client.renderer.RenderType;
import net.minecraft.client.renderer.block.model.BakedQuad;
import net.minecraft.client.renderer.texture.TextureAtlasSprite;
import net.minecraft.client.resources.model.BakedModel;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.core.registries.Registries;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.tags.BlockTags;
import net.minecraft.tags.TagKey;
import net.minecraft.util.RandomSource;
import net.minecraft.world.level.EmptyBlockGetter;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.SoundType;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.block.state.properties.BooleanProperty;
import net.neoforged.neoforge.client.model.data.ModelData;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * 0.14.0: property-driven block classification for any mod (render layer, emission, glow colour, reflections, warmth),
 * with config/valcraft-blocks.json overrides keyed by block id ("create:framed_glass") or tag ("#c:glass_blocks"):
 * { "render": "solid|cutout|translucent", "emissive": bool, "light": 0-15, "lightColor": "#RRGGBB", "shiny": bool,
 *   "smoothness": 0-1, "metallic": 0-1, "warm": bool }  (shiny = shiny || smoothness >= 0.5 || metallic >= 0.5; warm = counts as a campfire for Valheim).
 */
public final class BlockProps {
	private BlockProps() {}
	public static final int SOLID = 0, CUTOUT = 1, TRANSLUCENT = 2;

	// ---- overrides ----
	private static Map<String, JsonObject> byId, byTag;
	static synchronized void load() {
		if (byId != null) return;
		byId = new HashMap<>(); byTag = new HashMap<>();
		Path p = net.neoforged.fml.loading.FMLPaths.CONFIGDIR.get().resolve("valcraft-blocks.json");
		try {
			if (!Files.exists(p)) {
				Files.writeString(p, "{\n  \"_comment\": \"ValCraft block overrides: key = block id or #tag; fields: render (solid|cutout|translucent), emissive, light (0-15), lightColor (#RRGGBB), shiny, smoothness, metallic, warm\"\n}\n");
				return;
			}
			var root = JsonParser.parseString(Files.readString(p)).getAsJsonObject();
			for (var e : root.entrySet()) {
				if (e.getKey().startsWith("_") || !e.getValue().isJsonObject()) continue;
				if (e.getKey().startsWith("#")) byTag.put(e.getKey().substring(1), e.getValue().getAsJsonObject()); else byId.put(e.getKey(), e.getValue().getAsJsonObject());
			}
			ValCraft.LOG.info("[blocks] {} block override(s) from {} ({} by id, {} by tag)", byId.size() + byTag.size(), p, byId.size(), byTag.size());
		} catch (Exception ex) { ValCraft.LOG.warn("[blocks] reading {}: {}", p, ex.toString()); }
	}

	/** Merged override for this state (tag entries first, block id wins). */
	static JsonObject override(BlockState s) {
		load();
		if (byId.isEmpty() && byTag.isEmpty()) return null;
		JsonObject out = null;
		for (var e : byTag.entrySet()) {
			var rl = ResourceLocation.tryParse(e.getKey()); if (rl == null) continue;
			if (s.is(TagKey.create(Registries.BLOCK, rl))) { if (out == null) out = new JsonObject(); for (var f : e.getValue().entrySet()) out.add(f.getKey(), f.getValue()); }
		}
		var id = byId.get(BuiltInRegistries.BLOCK.getKey(s.getBlock()).toString());
		if (id != null) { if (out == null) out = new JsonObject(); for (var f : id.entrySet()) out.add(f.getKey(), f.getValue()); }
		return out;
	}

	// ---- render layer ----
	/** Model render types (NeoForge: model JSON "render_type", Registrate addLayer, ...) + the legacy map + texture alpha fallback. */
	static int renderClass(Minecraft mc, BlockState s, BakedModel model, JsonObject ov) {
		if (ov != null && ov.has("render")) { var r = ov.get("render").getAsString(); return r.startsWith("trans") ? TRANSLUCENT : r.startsWith("cut") ? CUTOUT : SOLID; }
		int c = SOLID;
		try {
			var set = model.getRenderTypes(s, RandomSource.create(42), ModelData.EMPTY);
			for (RenderType rt : set) c = Math.max(c, cls(rt));
		} catch (Throwable t) { }
		try { c = Math.max(c, cls(ItemBlockRenderTypes.getChunkRenderType(s))); } catch (Throwable t) { }
		if (c == SOLID && !s.isSolidRender(EmptyBlockGetter.INSTANCE, BlockPos.ZERO)) c = Math.max(c, alphaClass(s, model)); // non-occluding but declared solid: look at the pixels
		return c;
	}
	private static int cls(RenderType rt) {
		if (rt == null || rt == RenderType.solid()) return SOLID;
		if (rt == RenderType.translucent() || rt == RenderType.tripwire() || rt.toString().contains("translucent")) return TRANSLUCENT;
		return CUTOUT;
	}
	/** Texture alpha of the model's sprites: semi-transparent pixels -> translucent, fully transparent ones -> cutout. */
	static int alphaClass(BlockState s, BakedModel model) {
		int best = SOLID;
		for (TextureAtlasSprite sp : sprites(s, model)) {
			try {
				var img = sp.contents().getOriginalImage(); int w = sp.contents().width(), h = sp.contents().height(); int semi = 0, clear = 0, n = 0;
				for (int y = 0; y < h; y += Math.max(1, h / 16)) for (int x = 0; x < w; x += Math.max(1, w / 16)) {
					int a = (img.getPixelRGBA(x, y) >>> 24) & 0xFF; n++;
					if (a == 0) clear++; else if (a < 250) semi++;
				}
				if (semi * 20 > n) return TRANSLUCENT;
				if (clear > 0) best = CUTOUT;
			} catch (Throwable t) { }
		}
		return best;
	}

	static List<TextureAtlasSprite> sprites(BlockState s, BakedModel model) {
		var out = new ArrayList<TextureAtlasSprite>();
		RandomSource rand = RandomSource.create(42);
		for (int d = 0; d <= 6; d++) {
			rand.setSeed(42);
			try { for (BakedQuad q : model.getQuads(s, d < 6 ? Direction.values()[d] : null, rand)) if (!out.contains(q.getSprite())) out.add(q.getSprite()); } catch (Throwable t) { }
			if (out.size() >= 6) break;
		}
		if (out.isEmpty()) out.add(model.getParticleIcon());
		return out;
	}

	// ---- emission ----
	/** Any quad with a baked lightmap (NeoForge emissive quads / "neoforge_data" block_light) or unshaded full-bright. */
	static boolean emissiveQuads(BlockState s, BakedModel model) {
		RandomSource rand = RandomSource.create(42);
		for (int d = 0; d <= 6; d++) {
			rand.setSeed(42);
			try {
				for (BakedQuad q : model.getQuads(s, d < 6 ? Direction.values()[d] : null, rand)) {
					int[] v = q.getVertices(); int stride = v.length / 4;
					if (stride >= 7) for (int i = 0; i < 4; i++) { int l = v[i * stride + 6]; if ((l & 0xFFFF) >= 0xD0 || ((l >>> 16) & 0xFFFF) >= 0xD0) return true; }
				}
			} catch (Throwable t) { }
		}
		return false;
	}

	/** Light colour from the textures: average of the brighter half of the opaque pixels. */
	static int lightColorFromTexture(BlockState s, BakedModel model) {
		long r = 0, g = 0, b = 0; int n = 0;
		for (TextureAtlasSprite sp : sprites(s, model)) {
			try {
				var img = sp.contents().getOriginalImage(); int w = sp.contents().width(), h = sp.contents().height();
				for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
					int c = img.getPixelRGBA(x, y); if (((c >>> 24) & 0xFF) < 128) continue;
					int R = c & 0xFF, G = (c >> 8) & 0xFF, B = (c >> 16) & 0xFF; // ABGR
					if (R + G + B < 400) continue;
					r += R; g += G; b += B; n++;
				}
			} catch (Throwable t) { }
		}
		if (n == 0) return 0xFFB060;
		int R = (int) (r / n), G = (int) (g / n), B = (int) (b / n), mx = Math.max(1, Math.max(R, Math.max(G, B)));
		R = R * 255 / mx; G = G * 255 / mx; B = B * 255 / mx;
		return (R << 16) | (G << 8) | B;
	}

	// ---- reflections ----
	private static final TagKey<Block> C_GLASS = tag("c:glass_blocks"), C_PANES = tag("c:glass_panes"), C_STORAGE = tag("c:storage_blocks");
	private static TagKey<Block> tag(String id) { return TagKey.create(Registries.BLOCK, ResourceLocation.parse(id)); }
	static boolean shiny(BlockState s, int render, String path, JsonObject ov) {
		if (ov != null) {
			if (ov.has("shiny")) return ov.get("shiny").getAsBoolean();
			if (ov.has("smoothness") || ov.has("metallic")) return (ov.has("smoothness") && ov.get("smoothness").getAsFloat() >= 0.5f) || (ov.has("metallic") && ov.get("metallic").getAsFloat() >= 0.5f);
		}
		SoundType st = null; try { st = s.getSoundType(); } catch (Throwable t) { }
		if (s.is(C_GLASS) || s.is(C_PANES) || (render == TRANSLUCENT && st == SoundType.GLASS)) return true;
		if (st == SoundType.METAL || st == SoundType.NETHERITE_BLOCK || st == SoundType.COPPER || st == SoundType.COPPER_BULB || st == SoundType.AMETHYST || st == SoundType.CHAIN) return true;
		if (s.is(C_STORAGE) && st != SoundType.WOOD && st != SoundType.GRASS && st != SoundType.WOOL) return true;
		if (s.is(BlockTags.ICE) || s.getBlock().getFriction() > 0.9f) return true;
		if (path.startsWith("polished_") || path.startsWith("smooth_") || path.contains("glazed_terracotta") || path.contains("quartz") || path.contains("prismarine") || path.equals("obsidian")) return true;
		return BlockSync.isShiny(path); // vanilla name list kept as a fallback
	}

	// ---- warmth (Valheim campfire) ----
	private static final java.util.Map<BlockState, Boolean> warmCache = new java.util.concurrent.ConcurrentHashMap<>();
	/** Lit campfire of any mod: #minecraft:campfires, CampfireBlock subclass, or override "warm"; a "lit" property must be true if present. */
	static boolean warm(BlockState s) {
		return warmCache.computeIfAbsent(s, st -> {
			var ov = override(st);
			boolean base = ov != null && ov.has("warm") ? ov.get("warm").getAsBoolean() : st.is(BlockTags.CAMPFIRES) || st.getBlock() instanceof net.minecraft.world.level.block.CampfireBlock;
			if (!base) return false;
			for (var p : st.getProperties()) if (p.getName().equals("lit") && p instanceof BooleanProperty bp) return st.getValue(bp);
			return true;
		});
	}
}
