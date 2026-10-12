package dev.valcraft.client;

import com.mojang.blaze3d.platform.GlStateManager;
import com.mojang.blaze3d.platform.NativeImage;
import com.mojang.blaze3d.systems.RenderSystem;
import com.mojang.blaze3d.vertex.VertexConsumer;
import com.mojang.blaze3d.vertex.VertexFormat;
import dev.valcraft.ValCraft;
import dev.valcraft.link.BlockCodec;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import net.minecraft.client.Minecraft;
import net.minecraft.client.renderer.MultiBufferSource;
import net.minecraft.client.renderer.RenderStateShard;
import net.minecraft.client.renderer.RenderType;
import net.minecraft.core.Direction;
import net.minecraft.resources.ResourceLocation;
import org.lwjgl.opengl.GL11;

/**
 * 0.7.3: a MultiBufferSource that records what a BlockEntityRenderer / EntityRenderer / ItemRenderer draws (QUADS
 * render types only) as quads with the texture each RenderType binds. Textures are read back from the GPU once
 * (works for plain entity textures and for atlases like chest/sign/bed sheets), and each quad's UV rectangle is cropped
 * into its own ValCraft atlas tile, so the result is sent like baked-model quads (EV 37 format). Render thread only.
 */
public final class Capture implements MultiBufferSource {
	/** 0.10.0: true while ValCraft captures a renderer: Flywheel-instanced mods (Create) take their non-instanced fallback path. */
	public static volatile boolean CAPTURING;
	record CQuad(ResourceLocation tex, float[] pos, float[] uv, int argb, float nx, float ny, float nz) {}
	private final List<float[]> verts = new ArrayList<>(); // x y z u v argb(bits) nx ny nz + tex index
	private final List<ResourceLocation> texs = new ArrayList<>();
	private final List<Integer> vtex = new ArrayList<>();
	static final int MAX_QUADS = 4096;

	private static final VertexConsumer NOOP = new VertexConsumer() {
		public VertexConsumer addVertex(float x, float y, float z) { return this; }
		public VertexConsumer setColor(int r, int g, int b, int a) { return this; }
		public VertexConsumer setUv(float u, float v) { return this; }
		public VertexConsumer setUv1(int u, int v) { return this; }
		public VertexConsumer setUv2(int u, int v) { return this; }
		public VertexConsumer setNormal(float x, float y, float z) { return this; }
	};

	private final class Cons implements VertexConsumer {
		final int tex;
		Cons(int tex) { this.tex = tex; }
		float[] cur() { return verts.get(verts.size() - 1); }
		public VertexConsumer addVertex(float x, float y, float z) {
			if (verts.size() < MAX_QUADS * 4) { verts.add(new float[] { x, y, z, 0, 0, Float.intBitsToFloat(-1), 0, 1, 0 }); vtex.add(tex); }
			return this;
		}
		public VertexConsumer setColor(int r, int g, int b, int a) { if (!verts.isEmpty()) cur()[5] = Float.intBitsToFloat((a << 24) | (r << 16) | (g << 8) | b); return this; }
		public VertexConsumer setUv(float u, float v) { if (!verts.isEmpty()) { cur()[3] = u; cur()[4] = v; } return this; }
		public VertexConsumer setUv1(int u, int v) { return this; }
		public VertexConsumer setUv2(int u, int v) { return this; }
		public VertexConsumer setNormal(float x, float y, float z) { if (!verts.isEmpty()) { cur()[6] = x; cur()[7] = y; cur()[8] = z; } return this; }
	}

	private final Map<RenderType, Cons> cons = new HashMap<>();
	private int consStart;

	@Override
	public VertexConsumer getBuffer(RenderType rt) {
		if (rt.mode() != VertexFormat.Mode.QUADS) return NOOP;
		ResourceLocation tex = textureOf(rt);
		if (tex == null) return NOOP;
		// a new consumer must start on a quad boundary
		while (verts.size() % 4 != 0) { verts.remove(verts.size() - 1); vtex.remove(vtex.size() - 1); }
		return cons.computeIfAbsent(rt, r -> { texs.add(tex); return new Cons(texs.size() - 1); });
	}

	List<CQuad> quads() {
		List<CQuad> out = new ArrayList<>();
		for (int q = 0; q + 3 < verts.size(); q += 4) {
			int t = vtex.get(q);
			if (vtex.get(q + 3) != t) continue;
			float[] pos = new float[12], uv = new float[8];
			for (int i = 0; i < 4; i++) { float[] v = verts.get(q + i); pos[i * 3] = v[0]; pos[i * 3 + 1] = v[1]; pos[i * 3 + 2] = v[2]; uv[i * 2] = v[3]; uv[i * 2 + 1] = v[4]; }
			float[] v0 = verts.get(q);
			out.add(new CQuad(texs.get(t), pos, uv, Float.floatToRawIntBits(v0[5]), v0[6], v0[7], v0[8]));
		}
		return out;
	}

	// ---- RenderType -> texture (private fields; mojmap names at runtime in NeoForge) ----
	private static Method stateM, cutoutM; private static Field texF; private static boolean reflFailed;
	static ResourceLocation textureOf(RenderType rt) {
		if (reflFailed) return null;
		try {
			if (stateM == null) {
				stateM = Class.forName("net.minecraft.client.renderer.RenderType$CompositeRenderType").getDeclaredMethod("state"); stateM.setAccessible(true);
				texF = Class.forName("net.minecraft.client.renderer.RenderType$CompositeState").getDeclaredField("textureState"); texF.setAccessible(true);
				cutoutM = RenderStateShard.EmptyTextureStateShard.class.getDeclaredMethod("cutoutTexture"); cutoutM.setAccessible(true);
			}
			if (!stateM.getDeclaringClass().isInstance(rt)) return null;
			Object ts = texF.get(stateM.invoke(rt));
			@SuppressWarnings("unchecked") Optional<ResourceLocation> o = (Optional<ResourceLocation>) cutoutM.invoke(ts);
			return o.orElse(null);
		} catch (Throwable t) { reflFailed = true; ValCraft.LOG.warn("[capture] RenderType texture lookup unavailable: {}", t.toString()); return null; }
	}

	// ---- GPU texture readback, cached ----
	private static final Map<ResourceLocation, NativeImage> images = new HashMap<>();
	static NativeImage image(ResourceLocation rl) {
		if (images.containsKey(rl)) return images.get(rl);
		NativeImage img = null;
		try {
			var tex = Minecraft.getInstance().getTextureManager().getTexture(rl);
			RenderSystem.bindTexture(tex.getId());
			int w = GlStateManager._getTexLevelParameter(GL11.GL_TEXTURE_2D, 0, GL11.GL_TEXTURE_WIDTH), h = GlStateManager._getTexLevelParameter(GL11.GL_TEXTURE_2D, 0, GL11.GL_TEXTURE_HEIGHT);
			if (w > 0 && h > 0 && (long) w * h <= 8192L * 8192L) { img = new NativeImage(w, h, false); img.downloadTexture(0, false); }
		} catch (Throwable t) { ValCraft.LOG.warn("[capture] could not read texture {}: {}", rl, t.toString()); }
		images.put(rl, img);
		return img;
	}
	static void clearImages() { for (var i : images.values()) if (i != null) i.close(); images.clear(); }

	/** Convert captured quads to atlas-tile quads (each quad's UV rect cropped into a tile). Positions are kept as captured. */
	static List<BlockCodec.Quad> toTileQuads(List<CQuad> in) {
		List<BlockCodec.Quad> out = new ArrayList<>();
		Map<String, Integer> seenPos = new HashMap<>();
		for (CQuad q : in) {
			NativeImage img = image(q.tex);
			if (img == null) continue;
			int W = img.getWidth(), H = img.getHeight();
			float umin = 1e9f, umax = -1e9f, vmin = 1e9f, vmax = -1e9f;
			for (int i = 0; i < 4; i++) { umin = Math.min(umin, q.uv[i * 2] * W); umax = Math.max(umax, q.uv[i * 2] * W); vmin = Math.min(vmin, q.uv[i * 2 + 1] * H); vmax = Math.max(vmax, q.uv[i * 2 + 1] * H); }
			int x0 = Math.max(0, (int) Math.floor(umin + 1e-3f)), x1 = Math.min(W, (int) Math.ceil(umax - 1e-3f));
			int y0 = Math.max(0, (int) Math.floor(vmin + 1e-3f)), y1 = Math.min(H, (int) Math.ceil(vmax - 1e-3f));
			if (x1 <= x0) x1 = Math.min(W, x0 + 1); if (y1 <= y0) y1 = Math.min(H, y0 + 1);
			if (x1 <= x0 || y1 <= y0) continue;
			int tint = normTint(q.argb);
			int tile = BlockSync.tileForRegion(img, q.tex + "@" + x0 + "," + y0 + "," + x1 + "," + y1, x0, y0, x1 - x0, y1 - y0, tint == 0xFFFFFF ? -1 : tint);
			float[] uv = new float[8];
			for (int i = 0; i < 4; i++) { uv[i * 2] = (q.uv[i * 2] * W - x0) / (x1 - x0); uv[i * 2 + 1] = (q.uv[i * 2 + 1] * H - y0) / (y1 - y0); }
			int dir = Direction.getNearest(q.nx, q.ny, q.nz).ordinal();
			if (q.nx == 0 && q.ny == 0 && q.nz == 0) dir = normalDir(q.pos);
			float[] pos = q.pos;
			// 0.10.1: layer renderers (villager type/profession, sheep wool, armour, eyes) redraw the same faces with another
			// texture; MC's draw order hides that, Valheim's depth buffer z-fights. Push each repeat out along its normal.
			String pk = posKey(pos);
			int rep = seenPos.merge(pk, 1, Integer::sum) - 1;
			if (rep > 0) {
				var nd = Direction.values()[dir].getNormal(); float o = 0.004f * rep;
				pos = pos.clone(); for (int i = 0; i < 4; i++) { pos[i * 3] += nd.getX() * o; pos[i * 3 + 1] += nd.getY() * o; pos[i * 3 + 2] += nd.getZ() * o; }
			}
			out.add(new BlockCodec.Quad(tile, dir, 6, pos, uv));
			if (out.size() >= 2048) break;
		}
		return out;
	}

	/** 0.10.1: vertex colour -> tile tint. Grey colours are baked shading/light (Create BERs, AO) that change every re-capture and
	 *  used to mint a new atlas tile each time until the atlas was full (broken textures on later mobs / water wheels): ignored.
	 *  Real tints (leaves, dyed leather, sheep) are quantized so near-identical colours share a tile. */
	static int normTint(int argb) {
		if (argb == -1) return -1;
		int r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
		int mx = Math.max(r, Math.max(g, b)), mn = Math.min(r, Math.min(g, b));
		if (mx - mn < 24) return -1;
		// normalise brightness (shading) away, keep the hue/saturation, quantize to 16 levels
		float k = 255f / Math.max(1, mx);
		r = Math.min(255, Math.round(r * k)) & 0xF0 | 0x08; g = Math.min(255, Math.round(g * k)) & 0xF0 | 0x08; b = Math.min(255, Math.round(b * k)) & 0xF0 | 0x08;
		return (r << 16) | (g << 8) | b;
	}

	private static String posKey(float[] p) {
		long[] k = new long[4];
		for (int i = 0; i < 4; i++) k[i] = (Math.round(p[i * 3] * 256) & 0x1FFFFF) << 42 | (Math.round(p[i * 3 + 1] * 256) & 0x1FFFFF) << 21 | (Math.round(p[i * 3 + 2] * 256) & 0x1FFFFF);
		java.util.Arrays.sort(k);
		return k[0] + ":" + k[1] + ":" + k[2] + ":" + k[3];
	}

	/** Facing from geometry (MC quads are counter-clockwise seen from the front). */
	static int normalDir(float[] p) {
		float ax = p[3] - p[0], ay = p[4] - p[1], az = p[5] - p[2], bx = p[6] - p[0], by = p[7] - p[1], bz = p[8] - p[2];
		return Direction.getNearest(ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx).ordinal();
	}
}
