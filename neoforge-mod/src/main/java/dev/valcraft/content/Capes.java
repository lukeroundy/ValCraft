package dev.valcraft.content;

import dev.valcraft.ValCraft;
import net.minecraft.ChatFormatting;
import net.minecraft.core.Holder;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.network.RegistryFriendlyByteBuf;
import net.minecraft.network.chat.Component;
import net.minecraft.network.codec.StreamCodec;
import net.minecraft.network.protocol.common.custom.CustomPacketPayload;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.tags.DamageTypeTags;
import net.minecraft.world.Container;
import net.minecraft.world.effect.MobEffectInstance;
import net.minecraft.world.effect.MobEffects;
import net.minecraft.world.entity.ai.attributes.Attribute;
import net.minecraft.world.entity.ai.attributes.AttributeModifier;
import net.minecraft.world.entity.ai.attributes.Attributes;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.inventory.Slot;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.TooltipFlag;
import net.minecraft.world.level.GameRules;
import net.neoforged.bus.api.IEventBus;
import net.neoforged.neoforge.attachment.AttachmentType;
import net.neoforged.neoforge.common.NeoForge;
import net.neoforged.neoforge.event.entity.living.LivingDropsEvent;
import net.neoforged.neoforge.event.entity.living.LivingEvent;
import net.neoforged.neoforge.event.entity.living.LivingIncomingDamageEvent;
import net.neoforged.neoforge.event.entity.player.PlayerEvent;
import net.neoforged.neoforge.event.tick.PlayerTickEvent;
import net.neoforged.neoforge.network.PacketDistributor;
import net.neoforged.neoforge.network.event.RegisterPayloadHandlersEvent;
import net.neoforged.neoforge.registries.DeferredRegister;
import net.neoforged.neoforge.registries.NeoForgeRegistries;

import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.WeakHashMap;
import java.util.function.Supplier;

/**
 * 0.15.1: Valheim capes. Own cape slot in the player inventory (no Curios needed; if Curios is installed a cape in its
 * "back" slot counts too), stats like Valheim's, sync to other clients for rendering and to Valheim (EV_CAPE).
 */
public final class Capes {
	private Capes() {}
	static final DeferredRegister<AttachmentType<?>> ATT = DeferredRegister.create(NeoForgeRegistries.ATTACHMENT_TYPES, ValCraft.MOD_ID);
	public static final Supplier<AttachmentType<ItemStack>> CAPE = ATT.register("cape",
		() -> AttachmentType.builder(() -> ItemStack.EMPTY).serialize(ItemStack.OPTIONAL_CODEC, s -> !s.isEmpty()).copyOnDeath().build());
	static final ResourceLocation MOD_ID = ResourceLocation.fromNamespaceAndPath(ValCraft.MOD_ID, "cape");

	/** Stats per cape (ids as in valheim_items.py). */
	public record Stat(Holder<Attribute> attr, double amount, AttributeModifier.Operation op, String tip) {}
	public static final Map<String, List<Stat>> STATS = new HashMap<>();
	public static final Map<String, List<String>> EXTRA = new HashMap<>(); // non-attribute effects: frost, cold, stealth, feather, stamina
	static {
		var MB = AttributeModifier.Operation.ADD_MULTIPLIED_BASE; var AV = AttributeModifier.Operation.ADD_VALUE; var MT = AttributeModifier.Operation.ADD_MULTIPLIED_TOTAL;
		STATS.put("deer_hide_cape", List.of(new Stat(Attributes.MOVEMENT_SPEED, 0.03, MB, "+3% movement speed")));
		STATS.put("troll_hide_cape", List.of(new Stat(Attributes.SNEAKING_SPEED, 0.15, AV, "+50% sneaking speed")));
		EXTRA.put("troll_hide_cape", List.of("stealth"));
		STATS.put("wolf_fur_cape", List.of(new Stat(Attributes.ARMOR, 1, AV, "+1 armor")));
		EXTRA.put("wolf_fur_cape", List.of("frost"));
		STATS.put("lox_cape", List.of(new Stat(Attributes.ARMOR, 2, AV, "+2 armor")));
		EXTRA.put("lox_cape", List.of("cold"));
		STATS.put("linen_cape", List.of(new Stat(Attributes.MOVEMENT_SPEED, 0.07, MB, "+7% movement speed")));
		STATS.put("feather_cape", List.of(new Stat(Attributes.FALL_DAMAGE_MULTIPLIER, -1.0, MT, "No fall damage")));
		EXTRA.put("feather_cape", List.of("feather"));
		STATS.put("asksvin_cloak", List.of(new Stat(Attributes.MOVEMENT_SPEED, 0.06, MB, "+6% movement speed")));
		EXTRA.put("asksvin_cloak", List.of("stamina"));
		STATS.put("ash_cape", List.of(new Stat(Attributes.ARMOR, 1, AV, "+1 armor")));
		EXTRA.put("ash_cape", List.of("fire"));
		STATS.put("cape_of_odin", List.of(new Stat(Attributes.MOVEMENT_SPEED, 0.02, MB, "+2% movement speed")));
	}
	static final Map<String, String> EXTRA_TIP = Map.of("stealth", "Harder to notice while sneaking", "frost", "Frost resistance: no freezing", "cold", "Cold resistance: no freezing",
		"feather", "Slow fall (hold sneak to drop)", "stamina", "Sprinting costs less hunger", "fire", "Fire resistance against burning");

	public static String id(ItemStack s) { return s.isEmpty() ? "" : BuiltInRegistries.ITEM.getKey(s.getItem()).getPath(); }
	public static boolean isCape(ItemStack s) { return !s.isEmpty() && s.getItem() instanceof CapeItem; }

	/** Equipped cape: our slot first, then Curios "back" if that mod is installed. */
	public static ItemStack equipped(Player p) {
		var s = p.getData(CAPE);
		if (!s.isEmpty()) return s;
		return CuriosBridge.find(p);
	}
	public static boolean has(Player p, String effect) { var l = EXTRA.get(id(equipped(p))); return l != null && l.contains(effect); }

	public static final class CapeItem extends Item {
		public CapeItem(Properties p) { super(p); }
		@Override public void appendHoverText(ItemStack stack, TooltipContext ctx, List<Component> tip, TooltipFlag flag) {
			String id = id(stack);
			tip.add(Component.literal("Cape - wear it in the cape slot").withStyle(ChatFormatting.GRAY));
			for (var s : STATS.getOrDefault(id, List.of())) tip.add(Component.literal(s.tip()).withStyle(ChatFormatting.BLUE));
			for (var e : EXTRA.getOrDefault(id, List.of())) tip.add(Component.literal(EXTRA_TIP.get(e)).withStyle(ChatFormatting.BLUE));
		}
	}

	/** Container view of the cape attachment, used by the inventory slot. */
	static final class CapeContainer implements Container {
		final Player p; CapeContainer(Player p) { this.p = p; }
		@Override public int getContainerSize() { return 1; }
		@Override public boolean isEmpty() { return p.getData(CAPE).isEmpty(); }
		@Override public ItemStack getItem(int i) { return p.getData(CAPE); }
		@Override public ItemStack removeItem(int i, int n) { var s = p.getData(CAPE); if (s.isEmpty() || n <= 0) return ItemStack.EMPTY; p.setData(CAPE, ItemStack.EMPTY); return s; }
		@Override public ItemStack removeItemNoUpdate(int i) { return removeItem(i, 1); }
		@Override public void setItem(int i, ItemStack s) { p.setData(CAPE, s); }
		@Override public int getMaxStackSize() { return 1; }
		@Override public void setChanged() {}
		@Override public boolean stillValid(Player pl) { return true; }
		@Override public void clearContent() { p.setData(CAPE, ItemStack.EMPTY); }
	}
	public static final ResourceLocation EMPTY_ICON = ResourceLocation.fromNamespaceAndPath(ValCraft.MOD_ID, "item/empty_slot_cape");
	public static final class CapeSlot extends Slot {
		final Player p;
		public CapeSlot(Player p, int x, int y) { super(new CapeContainer(p), 0, x, y); this.p = p; setBackground(net.minecraft.world.inventory.InventoryMenu.BLOCK_ATLAS, EMPTY_ICON); }
		@Override public boolean mayPlace(ItemStack s) { return isCape(s); }
		@Override public int getMaxStackSize() { return 1; }
		// the creative inventory tab lays slots out by index; hide ours there (survival inventory only)
		@Override public boolean isActive() { return !p.isCreative(); }
	}

	// ---------------- sync ----------------
	public record CapeSync(int entity, ItemStack stack) implements CustomPacketPayload {
		public static final Type<CapeSync> TYPE = new Type<>(ResourceLocation.fromNamespaceAndPath(ValCraft.MOD_ID, "cape"));
		public static final StreamCodec<RegistryFriendlyByteBuf, CapeSync> CODEC = StreamCodec.of(
			(b, o) -> { b.writeVarInt(o.entity); ItemStack.OPTIONAL_STREAM_CODEC.encode(b, o.stack); },
			b -> new CapeSync(b.readVarInt(), ItemStack.OPTIONAL_STREAM_CODEC.decode(b)));
		@Override public Type<? extends CustomPacketPayload> type() { return TYPE; }
	}
	static final Map<Player, String> lastSent = new WeakHashMap<>();

	public static void register(IEventBus modBus) {
		ATT.register(modBus);
		modBus.addListener((RegisterPayloadHandlersEvent e) -> e.registrar("1").optional().playToClient(CapeSync.TYPE, CapeSync.CODEC,
			(o, ctx) -> ctx.enqueueWork(() -> { var lvl = ctx.player().level(); var ent = lvl.getEntity(o.entity()); if (ent instanceof Player pl) pl.setData(CAPE, o.stack()); })));
		NeoForge.EVENT_BUS.addListener(Capes::tick);
		NeoForge.EVENT_BUS.addListener((PlayerEvent.StartTracking e) -> {
			if (e.getTarget() instanceof Player t && e.getEntity() instanceof ServerPlayer sp) PacketDistributor.sendToPlayer(sp, new CapeSync(t.getId(), t.getData(CAPE).copy()));
		});
		NeoForge.EVENT_BUS.addListener((PlayerEvent.PlayerLoggedInEvent e) -> lastSent.remove(e.getEntity()));
		NeoForge.EVENT_BUS.addListener((PlayerEvent.PlayerRespawnEvent e) -> lastSent.remove(e.getEntity()));
		NeoForge.EVENT_BUS.addListener((PlayerEvent.PlayerChangedDimensionEvent e) -> lastSent.remove(e.getEntity()));
		NeoForge.EVENT_BUS.addListener(Capes::drops);
		NeoForge.EVENT_BUS.addListener(Capes::damage);
		NeoForge.EVENT_BUS.addListener(Capes::visibility);
	}

	static void tick(PlayerTickEvent.Post e) {
		var p = e.getEntity();
		if (p.level().isClientSide) return;
		var cape = equipped(p); String id = id(cape);
		if (p instanceof ServerPlayer sp && !id.equals(lastSent.get(p))) {
			lastSent.put(p, id);
			var msg = new CapeSync(p.getId(), p.getData(CAPE).copy());
			PacketDistributor.sendToPlayersTrackingEntityAndSelf(sp, msg);
		}
		if (p.tickCount % 10 == 0) applyStats(p, id);
		var ex = EXTRA.getOrDefault(id, List.of());
		if ((ex.contains("frost") || ex.contains("cold")) && p.getTicksFrozen() > 0) p.setTicksFrozen(0);
		if (ex.contains("feather") && !p.onGround() && p.getDeltaMovement().y < -0.25 && !p.isShiftKeyDown() && !p.getAbilities().flying)
			p.addEffect(new MobEffectInstance(MobEffects.SLOW_FALLING, 10, 0, true, false, true));
		if (ex.contains("stamina") && p.isSprinting() && p.tickCount % 20 == 0) p.getFoodData().addExhaustion(-0.05f);
	}

	static final Map<Holder<Attribute>, Boolean> USED = new HashMap<>();
	static void applyStats(Player p, String id) {
		var want = STATS.getOrDefault(id, List.of());
		for (var a : List.of(Attributes.MOVEMENT_SPEED, Attributes.SNEAKING_SPEED, Attributes.ARMOR, Attributes.FALL_DAMAGE_MULTIPLIER)) {
			var inst = p.getAttribute(a); if (inst == null) continue;
			Stat s = null; for (var w : want) if (w.attr().equals(a)) s = w;
			if (s == null) { if (inst.getModifier(MOD_ID) != null) inst.removeModifier(MOD_ID); }
			else inst.addOrUpdateTransientModifier(new AttributeModifier(MOD_ID, s.amount(), s.op()));
		}
	}

	static void drops(LivingDropsEvent e) {
		if (!(e.getEntity() instanceof Player p) || p.level().isClientSide) return;
		if (p.level().getGameRules().getBoolean(GameRules.RULE_KEEPINVENTORY)) return;
		var s = p.getData(CAPE);
		if (s.isEmpty()) return;
		e.getDrops().add(new ItemEntity(p.level(), p.getX(), p.getY(), p.getZ(), s.copy()));
		p.setData(CAPE, ItemStack.EMPTY);
	}

	static void damage(LivingIncomingDamageEvent e) {
		if (!(e.getEntity() instanceof Player p)) return;
		if (e.getSource().is(DamageTypeTags.IS_FREEZING) && (has(p, "frost") || has(p, "cold"))) e.setCanceled(true);
		else if (e.getSource().is(DamageTypeTags.IS_FIRE) && has(p, "fire")) e.setAmount(e.getAmount() * 0.6f);
	}

	static void visibility(LivingEvent.LivingVisibilityEvent e) {
		if (e.getEntity() instanceof Player p && has(p, "stealth")) e.modifyVisibility(p.isCrouching() ? 0.5 : 0.85);
	}

	/** Optional Curios support by reflection: no compile or runtime dependency. */
	static final class CuriosBridge {
		static Boolean ok; static java.lang.reflect.Method getInv, findFirst, stackOf;
		static ItemStack find(Player p) {
			try {
				if (ok == null) {
					ok = false;
					if (!net.neoforged.fml.ModList.get().isLoaded("curios")) return ItemStack.EMPTY;
					var api = Class.forName("top.theillusivec4.curios.api.CuriosApi");
					getInv = api.getMethod("getCuriosInventory", net.minecraft.world.entity.LivingEntity.class);
					findFirst = Class.forName("top.theillusivec4.curios.api.type.capability.ICuriosItemHandler").getMethod("findFirstCurio", java.util.function.Predicate.class);
					stackOf = Class.forName("top.theillusivec4.curios.api.SlotResult").getMethod("stack");
					ok = true; ValCraft.LOG.info("[cape] Curios found: capes also work in its back slot");
				}
				if (!ok) return ItemStack.EMPTY;
				var inv = (java.util.Optional<?>) getInv.invoke(null, p);
				if (inv.isEmpty()) return ItemStack.EMPTY;
				java.util.function.Predicate<ItemStack> pr = Capes::isCape;
				var r = (java.util.Optional<?>) findFirst.invoke(inv.get(), pr);
				return r.isEmpty() ? ItemStack.EMPTY : (ItemStack) stackOf.invoke(r.get());
			} catch (Throwable t) { ok = false; return ItemStack.EMPTY; }
		}
	}
}
