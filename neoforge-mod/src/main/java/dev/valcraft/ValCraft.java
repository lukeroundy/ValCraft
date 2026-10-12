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
		LOG.info("ValCraft loaded (0.10.1: in-world player, LOS + projectiles vs Valheim, capture fixes)");
		dev.valcraft.mobs.MobSync.register(modBus);
		dev.valcraft.content.ModContent.register(modBus);
		if (net.neoforged.fml.loading.FMLEnvironment.dist.isClient())
			modBus.addListener((net.neoforged.neoforge.client.event.EntityRenderersEvent.RegisterRenderers e) -> e.registerEntityRenderer(dev.valcraft.mobs.MobSync.PROXY.get(), net.minecraft.client.renderer.entity.NoopRenderer::new));
	}
}
