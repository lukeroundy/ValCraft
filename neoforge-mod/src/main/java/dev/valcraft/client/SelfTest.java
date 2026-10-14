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
			if (t == 40) { checkTextures(mc); if (sp != null) mc.getSingleplayerServer().execute(() -> { checkRecipes(sp); sp.serverLevel().setDayTime(6000); fillInventory(sp); }); }
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
			// 0.15.6: held bow + shields, first and third person
			if (t == t2 + 30 && sp != null) mc.getSingleplayerServer().execute(() -> hold(sp, "valcraft:huntsman_bow", "valcraft:iron_tower_shield"));
			if (t == t2 + 40) { mc.options.setCameraType(CameraType.FIRST_PERSON); mc.player.setXRot(0f); }
			if (t == t2 + 48) shot(mc, "held-fp-tower");
			if (t == t2 + 50) mc.options.setCameraType(CameraType.THIRD_PERSON_FRONT);
			if (t == t2 + 58) shot(mc, "held-tp-tower");
			if (t == t2 + 60 && sp != null) mc.getSingleplayerServer().execute(() -> hold(sp, "valcraft:crude_bow", "valcraft:banded_shield"));
			if (t == t2 + 68) shot(mc, "held-tp-round");
			if (t == t2 + 70) mc.options.setCameraType(CameraType.THIRD_PERSON_BACK);
			if (t == t2 + 78) shot(mc, "held-tpback-round");
			if (t == t2 + 80) mc.options.setCameraType(CameraType.FIRST_PERSON);
			if (t == t2 + 88) shot(mc, "held-fp-round");
			if (t == t2 + 92) {
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

	/** 0.15.4: every valcraft recipe loaded, none still asks for leather scraps, leather recipes use minecraft:leather. */
	static void checkRecipes(ServerPlayer sp) {
		var rm = sp.server.getRecipeManager(); var reg = sp.server.registryAccess();
		int vc = 0, scraps = 0, leather = 0;
		var scrapIt = net.minecraft.core.registries.BuiltInRegistries.ITEM.get(net.minecraft.resources.ResourceLocation.parse("valcraft:leather_scraps"));
		for (var h : rm.getRecipes()) {
			if (!h.id().getNamespace().equals("valcraft")) continue; vc++;
			boolean s = false, l = false;
			for (var ing : h.value().getIngredients()) for (var st : ing.getItems()) { if (st.is(scrapIt)) s = true; if (st.is(net.minecraft.world.item.Items.LEATHER)) l = true; }
			if (s) { scraps++; ValCraft.LOG.warn("[selftest] recipe {} still uses leather scraps", h.id()); }
			if (l) leather++;
		}
		int adv = 0; for (var a : sp.server.getAdvancements().getAllAdvancements()) if (a.id().getNamespace().equals("valcraft") && a.id().getPath().startsWith("recipes/")) adv++;
		String r = "recipes valcraft=" + vc + " leather=" + leather + " scraps=" + scraps + " recipeAdvancements=" + adv + " scrapsItem=" + (scrapIt != net.minecraft.world.item.Items.AIR);
		report.add(r); ValCraft.LOG.info("[selftest] {}", r);
		// 0.15.6: dirt + hoe -> farmland keeps the hoe (1 durability used); deer hide <-> rabbit hide
		var lv = sp.serverLevel();
		var in = net.minecraft.world.item.crafting.CraftingInput.of(2, 1, java.util.List.of(new net.minecraft.world.item.ItemStack(net.minecraft.world.item.Items.DIRT), new net.minecraft.world.item.ItemStack(net.minecraft.world.item.Items.IRON_HOE)));
		var hoe = rm.getRecipeFor(net.minecraft.world.item.crafting.RecipeType.CRAFTING, in, lv);
		String h = "hoe recipe missing";
		if (hoe.isPresent()) {
			var res = hoe.get().value().assemble(in, reg); var rem = hoe.get().value().getRemainingItems(in);
			h = "dirt+iron_hoe -> " + res.getItem() + " x" + res.getCount() + ", remainder " + rem.get(1).getItem() + " damage " + rem.get(1).getDamageValue() + " (" + hoe.get().id() + ")";
		}
		int hide = 0;
		for (var id : new String[] { "minecraft:rabbit_hide", "valcraft:deer_hide" }) {
			var it = net.minecraft.core.registries.BuiltInRegistries.ITEM.get(net.minecraft.resources.ResourceLocation.parse(id));
			var in1 = net.minecraft.world.item.crafting.CraftingInput.of(1, 1, java.util.List.of(new net.minecraft.world.item.ItemStack(it)));
			var rr = rm.getRecipeFor(net.minecraft.world.item.crafting.RecipeType.CRAFTING, in1, lv);
			if (rr.isPresent()) { hide++; h += " | " + id + " -> " + rr.get().value().assemble(in1, reg).getItem(); }
		}
		h += " | hide swaps " + hide + "/2";
		report.add(h); ValCraft.LOG.info("[selftest] {}", h);
	}

	static void hold(ServerPlayer sp, String main, String off) {
		var g = (java.util.function.Function<String, net.minecraft.world.item.ItemStack>) id -> new net.minecraft.world.item.ItemStack(net.minecraft.core.registries.BuiltInRegistries.ITEM.get(net.minecraft.resources.ResourceLocation.parse(id)));
		sp.setItemSlot(net.minecraft.world.entity.EquipmentSlot.MAINHAND, g.apply(main));
		sp.setItemSlot(net.minecraft.world.entity.EquipmentSlot.OFFHAND, g.apply(off));
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
