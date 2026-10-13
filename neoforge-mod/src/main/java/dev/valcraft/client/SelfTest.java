package dev.valcraft.client;

import dev.valcraft.ValCraft;
import dev.valcraft.content.Capes;
import dev.valcraft.content.GenItems;
import net.minecraft.client.CameraType;
import net.minecraft.client.Minecraft;
import net.minecraft.client.Screenshot;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.client.gui.screens.inventory.InventoryScreen;
import net.minecraft.client.renderer.texture.MissingTextureAtlasSprite;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.core.registries.Registries;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.Difficulty;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.level.GameRules;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.LevelSettings;
import net.minecraft.world.level.WorldDataConfiguration;
import net.minecraft.world.level.levelgen.WorldOptions;
import net.minecraft.world.level.levelgen.presets.WorldPresets;
import net.neoforged.neoforge.client.event.ClientTickEvent;
import net.neoforged.neoforge.common.NeoForge;

import java.io.File;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;

/**
 * 0.15.1 dev self test (-Dvalcraft.selftest=true, never in normal play): makes a flat test world, checks every
 * ValCraft item model / armour / cape texture for missing textures, equips armour sets and capes and takes screenshots.
 */
public final class SelfTest {
	private SelfTest() {}
	static int t = -1, step; static final String WORLD = "valcraft-selftest";
	static final String[][] LOOKS = {{"wolf", "wolf_fur_cape"}, {"iron", "troll_hide_cape"}, {"flametal", "asksvin_cloak"}, {"carapace", "feather_cape"}, {"fenris", "lox_cape"}, {"root", "deer_hide_cape"}, {"bronze", "linen_cape"}, {"padded", "ash_cape"}};
	static final List<String> report = new ArrayList<>();

	public static void register() {
		if (!Boolean.getBoolean("valcraft.selftest")) return;
		ValCraft.LOG.info("[selftest] enabled");
		NeoForge.EVENT_BUS.addListener((ClientTickEvent.Post e) -> tick(Minecraft.getInstance()));
	}

	static void tick(Minecraft mc) {
		try {
			if (mc.level == null) {
				if (t < 0 && mc.screen instanceof TitleScreen) {
					t = 0;
					var gr = new GameRules(); gr.getRule(GameRules.RULE_DAYLIGHT).set(false, null); gr.getRule(GameRules.RULE_WEATHER_CYCLE).set(false, null); gr.getRule(GameRules.RULE_DOMOBSPAWNING).set(false, null);
					mc.createWorldOpenFlows().createFreshLevel(WORLD, new LevelSettings(WORLD, GameType.SURVIVAL, false, Difficulty.PEACEFUL, true, gr, WorldDataConfiguration.DEFAULT),
						new WorldOptions(1234L, false, false), ra -> ra.registryOrThrow(Registries.WORLD_PRESET).getHolderOrThrow(WorldPresets.FLAT).value().createWorldDimensions(), null);
				}
				return;
			}
			if (mc.player == null) return;
			t++;
			var sp = mc.getSingleplayerServer() == null ? null : mc.getSingleplayerServer().getPlayerList().getPlayer(mc.player.getUUID());
			if (t == 40) { checkTextures(mc); if (sp != null) mc.getSingleplayerServer().execute(() -> { sp.serverLevel().setDayTime(6000); fillInventory(sp); }); }
			int look = (t - 80) / 60;
			if (t >= 80 && look < LOOKS.length && (t - 80) % 60 == 0 && sp != null) {
				final int li = look;
				mc.getSingleplayerServer().execute(() -> equip(sp, LOOKS[li][0], LOOKS[li][1]));
				mc.player.setYRot(180f); mc.player.setXRot(10f);
			}
			if (t >= 80 && look < LOOKS.length) {
				int k = (t - 80) % 60;
				if (k == 20) { mc.options.setCameraType(CameraType.THIRD_PERSON_BACK); mc.player.setYRot(180f); mc.player.setXRot(15f); }
				if (k == 34) shot(mc, "back-" + LOOKS[look][0]);
				if (k == 36) mc.options.setCameraType(CameraType.THIRD_PERSON_FRONT);
				if (k == 50) shot(mc, "front-" + LOOKS[look][0]);
				if (k == 52) mc.options.setCameraType(CameraType.FIRST_PERSON);
			}
			int t2 = 80 + LOOKS.length * 60;
			if (t == t2) mc.setScreen(new InventoryScreen(mc.player));
			if (t == t2 + 20) shot(mc, "inventory");
			if (t == t2 + 24) { mc.setScreen(null); report.add("cape slot item: " + Capes.id(mc.player.getData(Capes.CAPE))); }
			if (t == t2 + 30) {
				report.add("done");
				Files.write(new File(mc.gameDirectory, "valcraft-selftest.txt").toPath(), report);
				ValCraft.LOG.info("[selftest] finished: {}", String.join(" | ", report));
				mc.stop();
			}
		} catch (Exception ex) { ValCraft.LOG.error("[selftest] failed", ex); report.add("ERROR " + ex); }
	}

	static void shot(Minecraft mc, String name) {
		Screenshot.grab(mc.gameDirectory, "selftest-" + name + ".png", mc.getMainRenderTarget(), c -> ValCraft.LOG.info("[selftest] {}", c.getString()));
	}

	static void checkTextures(Minecraft mc) {
		int items = 0, missing = 0; List<String> miss = new ArrayList<>();
		var missingLoc = MissingTextureAtlasSprite.getLocation();
		for (var item : BuiltInRegistries.ITEM) {
			var key = BuiltInRegistries.ITEM.getKey(item);
			if (!key.getNamespace().equals(ValCraft.MOD_ID)) continue;
			items++;
			var model = mc.getItemRenderer().getModel(new ItemStack(item), null, null, 0);
			if (model == mc.getModelManager().getMissingModel() || model.getParticleIcon().contents().name().equals(missingLoc)) { missing++; miss.add(key.getPath()); }
		}
		report.add("item models: " + items + " checked, " + missing + " missing" + (miss.isEmpty() ? "" : " " + miss));
		int tex = 0, texMissing = 0; List<String> tm = new ArrayList<>();
		var rm = mc.getResourceManager();
		for (var r : rm.listResources("textures/models/armor", l -> l.getNamespace().equals(ValCraft.MOD_ID) && l.getPath().endsWith(".png")).keySet()) tex++;
		for (var s : GenItems.SETS_NAMES()) for (var layer : new String[]{"_layer_1", "_layer_2"}) {
			var loc = ResourceLocation.fromNamespaceAndPath(ValCraft.MOD_ID, "textures/models/armor/" + s + layer + ".png");
			if (rm.getResource(loc).isEmpty()) { texMissing++; tm.add(loc.getPath()); }
		}
		for (var e : Capes.STATS.keySet()) {
			var loc = ResourceLocation.fromNamespaceAndPath(ValCraft.MOD_ID, "textures/entity/cape/" + e + ".png");
			if (rm.getResource(loc).isEmpty()) { texMissing++; tm.add(loc.getPath()); }
		}
		var slot = mc.getTextureAtlas(net.minecraft.world.inventory.InventoryMenu.BLOCK_ATLAS).apply(Capes.EMPTY_ICON);
		if (slot.contents().name().equals(missingLoc)) { texMissing++; tm.add("empty_slot_cape sprite"); }
		report.add("armor layer files: " + tex + "; armor/cape/slot textures missing: " + texMissing + (tm.isEmpty() ? "" : " " + tm));
		ValCraft.LOG.info("[selftest] {}", String.join(" | ", report));
	}

	static void fillInventory(ServerPlayer sp) {
		var inv = sp.getInventory(); int i = 0;
		String[] pick = {"iron_sword", "silver_sword", "mistwalker", "dyrnwyn", "frostner", "porcupine", "blackmetal_axe", "draugr_fang", "spinesnap", "arbalest", "banded_shield", "serpentscale_shield",
			"flametal_tower_shield", "trophy_eikthyr", "trophy_elder", "trophy_bonemass", "trophy_moder", "trophy_yagluth", "trophy_queen", "trophy_fader", "trophy_boar", "trophy_deer", "silver_ingot",
			"black_metal", "flametal", "mead_minor_healing", "mead_frost_resistance", "carrot_soup", "cooked_lox_meat", "bread", "surtling_core", "crystal", "wolf_pelt", "fine_wood", "bronze_ingot", "tin_ingot"};
		for (var id : pick) {
			var it = BuiltInRegistries.ITEM.get(ResourceLocation.fromNamespaceAndPath(ValCraft.MOD_ID, id));
			if (it != net.minecraft.world.item.Items.AIR && i < 36) inv.setItem(i++, new ItemStack(it));
		}
	}

	static void equip(ServerPlayer sp, String set, String cape) {
		for (var slot : new EquipmentSlot[]{EquipmentSlot.HEAD, EquipmentSlot.CHEST, EquipmentSlot.LEGS, EquipmentSlot.FEET}) {
			String n = switch (slot) { case HEAD -> "helmet"; case CHEST -> "chestplate"; case LEGS -> "leggings"; default -> "boots"; };
			var it = BuiltInRegistries.ITEM.get(ResourceLocation.fromNamespaceAndPath(ValCraft.MOD_ID, set + "_" + n));
			sp.setItemSlot(slot, it == net.minecraft.world.item.Items.AIR ? ItemStack.EMPTY : new ItemStack(it));
		}
		sp.setData(Capes.CAPE, new ItemStack(BuiltInRegistries.ITEM.get(ResourceLocation.fromNamespaceAndPath(ValCraft.MOD_ID, cape))));
		report.add("equipped " + set + " + " + cape + " -> attrs speed " + String.format("%.3f", sp.getAttributeValue(net.minecraft.world.entity.ai.attributes.Attributes.MOVEMENT_SPEED)));
	}
}
