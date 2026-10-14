package dev.valcraft.client;

import net.minecraft.client.model.HumanoidModel;
import net.minecraft.client.model.geom.PartPose;
import net.minecraft.client.model.geom.builders.CubeDeformation;
import net.minecraft.client.model.geom.builders.CubeListBuilder;
import net.minecraft.client.model.geom.builders.LayerDefinition;
import net.minecraft.client.model.geom.builders.MeshDefinition;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.item.ItemStack;
import net.neoforged.bus.api.IEventBus;
import net.neoforged.neoforge.client.extensions.common.IClientItemExtensions;
import net.neoforged.neoforge.client.extensions.common.RegisterClientExtensionsEvent;

/** 0.15.7: the worn Pointy Hat (Valheim HelmetPointyHat) as a real 3D wizard hat: a wide brim and a 4-tier cone with a
 *  bent tip, textured by textures/models/armor/pointy_layer_1.png (layout in tools/pixelart.pointy_hat_layer). */
public final class PointyHatModel {
	private PointyHatModel() {}
	static HumanoidModel<LivingEntity> model;

	static HumanoidModel<LivingEntity> build() {
		MeshDefinition mesh = HumanoidModel.createMesh(CubeDeformation.NONE, 0f);
		var root = mesh.getRoot();
		var head = root.addOrReplaceChild("head", CubeListBuilder.create()
				.texOffs(0, 0).addBox(-6f, -7.6f, -6f, 12, 1, 12)                              // brim (just above the eyes)
				.texOffs(0, 13).addBox(-4f, -10.5f, -4f, 8, 3, 8, new CubeDeformation(0.3f)),  // crown with the gold band
				PartPose.ZERO);
		var t2 = head.addOrReplaceChild("tier2", CubeListBuilder.create().texOffs(32, 13).addBox(-3f, -3f, -3f, 6, 3, 6), PartPose.offsetAndRotation(0f, -10.6f, 0.3f, -0.12f, 0f, 0f));
		var t3 = t2.addOrReplaceChild("tier3", CubeListBuilder.create().texOffs(0, 24).addBox(-2f, -3f, -2f, 4, 3, 4), PartPose.offsetAndRotation(0f, -2.8f, 0.4f, -0.25f, 0f, 0f));
		t3.addOrReplaceChild("tip", CubeListBuilder.create().texOffs(16, 24).addBox(-1f, -3f, -1f, 2, 3, 2), PartPose.offsetAndRotation(0f, -2.7f, 0.5f, -0.45f, 0f, 0f));
		root.addOrReplaceChild("hat", CubeListBuilder.create(), PartPose.ZERO);
		return new HumanoidModel<>(LayerDefinition.create(mesh, 64, 32).bakeRoot());
	}

	public static void register(IEventBus modBus) {
		modBus.addListener((RegisterClientExtensionsEvent e) -> {
			var item = BuiltInRegistries.ITEM.get(ResourceLocation.fromNamespaceAndPath("valcraft", "pointy_hat"));
			e.registerItem(new IClientItemExtensions() {
				@Override public HumanoidModel<?> getHumanoidArmorModel(LivingEntity le, ItemStack st, EquipmentSlot slot, HumanoidModel<?> original) {
					if (slot != EquipmentSlot.HEAD) return original;
					if (model == null) model = build();
					return model;
				}
			}, item);
		});
	}
}
