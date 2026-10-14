package dev.valcraft.client;

import dev.valcraft.link.ValCraftLink;
import net.minecraft.client.Minecraft;
import net.minecraft.client.renderer.item.ItemProperties;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.world.item.BowItem;
import net.minecraft.world.item.CrossbowItem;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ShieldItem;
import net.neoforged.bus.api.IEventBus;
import net.neoforged.fml.event.lifecycle.FMLClientSetupEvent;
import net.neoforged.neoforge.client.event.RenderNameTagEvent;
import net.neoforged.neoforge.client.event.InputEvent;
import net.neoforged.neoforge.common.NeoForge;
import net.neoforged.neoforge.common.util.TriState;

/** 0.15.5 client fixes: bow/crossbow/shield model predicates, no name tags in Valheim captures, scroll-to-rotate while building. */
public final class Fixes155 {
	private Fixes155() {}
	static ResourceLocation mc(String p) { return ResourceLocation.withDefaultNamespace(p); }

	public static void register(IEventBus modBus) {
		modBus.addListener((FMLClientSetupEvent e) -> e.enqueueWork(Fixes155::itemProperties));
		// MC name tags drawn into the captured mob meshes came out blown-out under Valheim lighting: never capture them
		NeoForge.EVENT_BUS.addListener((RenderNameTagEvent e) -> { if (Capture.CAPTURING) e.setCanRender(TriState.FALSE); });
		// building with the hammer: mouse wheel rotates the placement ghost (Valheim's own control) instead of changing slot
		NeoForge.EVENT_BUS.addListener((InputEvent.MouseScrollingEvent e) -> {
			var m = Minecraft.getInstance();
			if (m.screen != null || !BuildLink.building(m)) return;
			double d = e.getScrollDeltaY(); if (d == 0) return;
			ValCraftClient.sendToValheim(ValCraftLink.EV_BUILD_ACTION, new byte[] { (byte) (d > 0 ? 3 : 5) });
			e.setCanceled(true);
		});
	}

	static int bows, xbows, shields;
	static void itemProperties() {
		for (var di : dev.valcraft.content.GenItems.ALL) {
			Item it = di.get();
			if (it instanceof BowItem && !(it instanceof CrossbowItem)) {
				bows++;
				ItemProperties.register(it, mc("pull"), (st, lv, en, s) -> en == null || en.getUseItem() != st ? 0f : (st.getUseDuration(en) - en.getUseItemRemainingTicks()) / 20f);
				ItemProperties.register(it, mc("pulling"), (st, lv, en, s) -> en != null && en.isUsingItem() && en.getUseItem() == st ? 1f : 0f);
			} else if (it instanceof CrossbowItem) {
				xbows++;
				ItemProperties.register(it, mc("pull"), (st, lv, en, s) -> en == null || CrossbowItem.isCharged(st) ? 0f : (float) (st.getUseDuration(en) - en.getUseItemRemainingTicks()) / CrossbowItem.getChargeDuration(st, en));
				ItemProperties.register(it, mc("pulling"), (st, lv, en, s) -> en != null && en.isUsingItem() && en.getUseItem() == st && !CrossbowItem.isCharged(st) ? 1f : 0f);
				ItemProperties.register(it, mc("charged"), (st, lv, en, s) -> CrossbowItem.isCharged(st) ? 1f : 0f);
			} else if (it instanceof ShieldItem) {
				shields++;
				ItemProperties.register(it, mc("blocking"), (st, lv, en, s) -> en != null && en.isUsingItem() && en.getUseItem() == st ? 1f : 0f);
			}
		}
		dev.valcraft.ValCraft.LOG.info("[content] item predicates: {} bows, {} crossbows, {} shields", bows, xbows, shields);
	}
}
