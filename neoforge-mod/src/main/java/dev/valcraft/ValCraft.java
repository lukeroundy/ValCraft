package dev.valcraft;

import com.mojang.logging.LogUtils;
import net.neoforged.bus.api.IEventBus;
import net.neoforged.fml.common.Mod;
import org.slf4j.Logger;

@Mod(ValCraft.MOD_ID)
public final class ValCraft {
	public static final String MOD_ID = "valcraft";
	public static final Logger LOG = LogUtils.getLogger();

	public ValCraft(IEventBus modBus) {
		LOG.info("ValCraft loaded (0.15.6: shields on the forearm, bows face forward, Tab cancels building, O = Valheim menu, deer hide, hoe tills dirt; 0.15.5: bows/shields animate, hammer item builds, materials return to their slots; 0.15.1: Norse pixel art, capes with a cape slot, recipes, per-family mob sounds; 0.15.0: ValCraft Launcher; 0.14.3: onboarding skipped, music muted while linked; 0.14.2: Hugin talks in Minecraft; 0.14.1: instant block updates, smooth Create kinetic rotation, unshaded BE captures; 0.14.0: generic block properties for any mod + config/valcraft-blocks.json; 0.13.5: per-world persistent Valheim mapping, /valcraft offset|nudge; 0.13.4: respawn waits for Valheim; 0.13.2: sleep/spawn points across both games, MC campfires warm in Valheim, Left Alt snap cycling; 0.13.1: place on Valheim surfaces, stairs; 0.13.0: multiplayer through the Valheim server; 0.12.3: off-thread collision index; 0.12.1: Valheim map visible; 0.12.0: M = Valheim map, 24 fps animation, weather sync, fluids vs Valheim buildings, stuck arrows, emeralds)");
		dev.valcraft.mobs.MobSync.register(modBus);
		dev.valcraft.content.ModContent.register(modBus);
		dev.valcraft.content.Capes.register(modBus);
		dev.valcraft.net.ServerOps.register(modBus);
		dev.valcraft.content.Fixes155Common.register();
		dev.valcraft.content.KeepToolRecipe.register(modBus);
		if (net.neoforged.fml.loading.FMLEnvironment.dist.isClient()) { dev.valcraft.client.Fixes155.register(modBus); dev.valcraft.client.Fixes156.register(modBus); }
		if (net.neoforged.fml.loading.FMLEnvironment.dist.isClient()) dev.valcraft.client.OpsInit.init();
		net.neoforged.neoforge.common.NeoForge.EVENT_BUS.addListener(dev.valcraft.content.Emerald::onBreak);
		net.neoforged.neoforge.common.NeoForge.EVENT_BUS.addListener(dev.valcraft.net.SleepState::onContinue);
		if (net.neoforged.fml.loading.FMLEnvironment.dist.isClient()) { dev.valcraft.client.CapeClient.register(modBus); dev.valcraft.client.MobSounds.register(); dev.valcraft.client.SelfTest.register(); }
		if (net.neoforged.fml.loading.FMLEnvironment.dist.isClient()) modBus.addListener(dev.valcraft.client.MapKey::register);
		if (net.neoforged.fml.loading.FMLEnvironment.dist.isClient()) net.neoforged.neoforge.common.NeoForge.EVENT_BUS.addListener(dev.valcraft.client.OffsetCommand::register);
		if (net.neoforged.fml.loading.FMLEnvironment.dist.isClient())
			modBus.addListener((net.neoforged.neoforge.client.event.EntityRenderersEvent.RegisterRenderers e) -> e.registerEntityRenderer(dev.valcraft.mobs.MobSync.PROXY.get(), net.minecraft.client.renderer.entity.NoopRenderer::new));
	}
}
