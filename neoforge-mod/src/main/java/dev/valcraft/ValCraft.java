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
		LOG.info("ValCraft loaded (0.14.0: generic block properties for any mod + config/valcraft-blocks.json; 0.13.5: per-world persistent Valheim mapping, /valcraft offset|nudge; 0.13.4: respawn waits for Valheim; 0.13.2: sleep/spawn points across both games, MC campfires warm in Valheim, Left Alt snap cycling; 0.13.1: place on Valheim surfaces, stairs; 0.13.0: multiplayer through the Valheim server; 0.12.3: off-thread collision index; 0.12.1: Valheim map visible; 0.12.0: M = Valheim map, 24 fps animation, weather sync, fluids vs Valheim buildings, stuck arrows, emeralds)");
		dev.valcraft.mobs.MobSync.register(modBus);
		dev.valcraft.content.ModContent.register(modBus);
		dev.valcraft.net.ServerOps.register(modBus);
		if (net.neoforged.fml.loading.FMLEnvironment.dist.isClient()) dev.valcraft.client.OpsInit.init();
		net.neoforged.neoforge.common.NeoForge.EVENT_BUS.addListener(dev.valcraft.content.Emerald::onBreak);
		net.neoforged.neoforge.common.NeoForge.EVENT_BUS.addListener(dev.valcraft.net.SleepState::onContinue);
		if (net.neoforged.fml.loading.FMLEnvironment.dist.isClient()) modBus.addListener(dev.valcraft.client.MapKey::register);
		if (net.neoforged.fml.loading.FMLEnvironment.dist.isClient()) net.neoforged.neoforge.common.NeoForge.EVENT_BUS.addListener(dev.valcraft.client.OffsetCommand::register);
		if (net.neoforged.fml.loading.FMLEnvironment.dist.isClient())
			modBus.addListener((net.neoforged.neoforge.client.event.EntityRenderersEvent.RegisterRenderers e) -> e.registerEntityRenderer(dev.valcraft.mobs.MobSync.PROXY.get(), net.minecraft.client.renderer.entity.NoopRenderer::new));
	}
}
