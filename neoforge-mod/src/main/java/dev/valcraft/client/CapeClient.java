package dev.valcraft.client;

import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.math.Axis;
import dev.valcraft.ValCraft;
import dev.valcraft.content.Capes;
import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.Minecraft;
import net.minecraft.client.model.PlayerModel;
import net.minecraft.client.player.AbstractClientPlayer;
import net.minecraft.client.renderer.MultiBufferSource;
import net.minecraft.client.renderer.RenderType;
import net.minecraft.client.renderer.entity.RenderLayerParent;
import net.minecraft.client.renderer.entity.layers.RenderLayer;
import net.minecraft.client.renderer.entity.player.PlayerRenderer;
import net.minecraft.client.renderer.texture.OverlayTexture;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.util.Mth;
import net.minecraft.world.entity.EquipmentSlot;
import net.neoforged.bus.api.IEventBus;
import net.neoforged.neoforge.client.event.ClientTickEvent;
import net.neoforged.neoforge.client.event.EntityRenderersEvent;
import net.neoforged.neoforge.common.NeoForge;

import java.nio.charset.StandardCharsets;

/** 0.15.1: renders the equipped Valheim cape on players and tells Valheim which cape to show on the Viking (EV_CAPE). */
public final class CapeClient {
	private CapeClient() {}

	public static void register(IEventBus modBus) {
		modBus.addListener((EntityRenderersEvent.AddLayers e) -> {
			for (var skin : e.getSkins()) {
				var r = e.getSkin(skin);
				if (r instanceof PlayerRenderer pr) pr.addLayer(new Layer(pr));
			}
		});
		NeoForge.EVENT_BUS.addListener((ClientTickEvent.Post e) -> tick());
	}

	static String sent = null; static int resendIn;
	static void tick() {
		var mc = Minecraft.getInstance();
		if (mc.player == null) return;
		String id = Capes.id(Capes.equipped(mc.player));
		if (--resendIn <= 0 || !id.equals(sent)) {
			resendIn = 100; // every 5 s too, so a (re)connected Valheim catches up
			if (ValCraftClient.sendToValheim(ValCraftLink.EV_CAPE, id.getBytes(StandardCharsets.UTF_8))) {
				if (!id.equals(sent)) ValCraft.LOG.info("[cape] Valheim cape -> '{}'", id);
				sent = id;
			}
		}
	}

	static ResourceLocation texture(String id) { return ResourceLocation.fromNamespaceAndPath(ValCraft.MOD_ID, "textures/entity/cape/" + id + ".png"); }

	static final class Layer extends RenderLayer<AbstractClientPlayer, PlayerModel<AbstractClientPlayer>> {
		Layer(RenderLayerParent<AbstractClientPlayer, PlayerModel<AbstractClientPlayer>> p) { super(p); }
		@Override public void render(PoseStack ps, MultiBufferSource buf, int light, AbstractClientPlayer pl, float limb, float limbAmt, float pt, float age, float yaw, float pitch) {
			if (pl.isInvisible()) return;
			var cape = Capes.equipped(pl);
			if (cape.isEmpty()) return;
			if (pl.getItemBySlot(EquipmentSlot.CHEST).is(net.minecraft.world.item.Items.ELYTRA)) return;
			ps.pushPose();
			ps.translate(0.0F, 0.0F, 0.125F);
			if (!pl.getItemBySlot(EquipmentSlot.CHEST).isEmpty()) ps.translate(0.0F, -0.053125F, 0.06875F); // over a chestplate
			double d0 = Mth.lerp(pt, pl.xCloakO, pl.xCloak) - Mth.lerp(pt, pl.xo, pl.getX());
			double d1 = Mth.lerp(pt, pl.yCloakO, pl.yCloak) - Mth.lerp(pt, pl.yo, pl.getY());
			double d2 = Mth.lerp(pt, pl.zCloakO, pl.zCloak) - Mth.lerp(pt, pl.zo, pl.getZ());
			float f = Mth.rotLerp(pt, pl.yBodyRotO, pl.yBodyRot);
			double d3 = Mth.sin(f * ((float) Math.PI / 180F)), d4 = -Mth.cos(f * ((float) Math.PI / 180F));
			float f1 = Mth.clamp((float) d1 * 10.0F, -6.0F, 32.0F);
			float f2 = Mth.clamp((float) (d0 * d3 + d2 * d4) * 100.0F, 0.0F, 150.0F);
			float f3 = Mth.clamp((float) (d0 * d4 - d2 * d3) * 100.0F, -20.0F, 20.0F);
			float f4 = Mth.lerp(pt, pl.oBob, pl.bob);
			f1 += Mth.sin(Mth.lerp(pt, pl.walkDistO, pl.walkDist) * 6.0F) * 32.0F * f4;
			if (pl.isCrouching()) f1 += 25.0F;
			ps.mulPose(Axis.XP.rotationDegrees(6.0F + f2 / 2.0F + f1));
			ps.mulPose(Axis.ZP.rotationDegrees(f3 / 2.0F));
			ps.mulPose(Axis.YP.rotationDegrees(180.0F - f3 / 2.0F));
			getParentModel().renderCloak(ps, buf.getBuffer(RenderType.entitySolid(texture(Capes.id(cape)))), light, OverlayTexture.NO_OVERLAY);
			ps.popPose();
		}
	}
}
