package dev.valcraft.mixin;

import dev.valcraft.content.Capes;
import net.minecraft.world.entity.player.Inventory;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.inventory.InventoryMenu;
import net.minecraft.world.item.ItemStack;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** 0.15.1: cape slot (index 46) next to the player preview, right of the armour, above the offhand slot. */
@Mixin(InventoryMenu.class)
public abstract class InventoryMenuMixin {
	@Inject(method = "<init>", at = @At("TAIL"))
	private void valcraft$capeSlot(Inventory inv, boolean active, Player owner, CallbackInfo ci) {
		((MenuAccessor) (Object) this).valcraft$addSlot(new Capes.CapeSlot(owner, 77, 44));
	}

	/** shift-click a cape from the inventory into the empty cape slot */
	@Inject(method = "quickMoveStack", at = @At("HEAD"), cancellable = true)
	private void valcraft$quickCape(Player p, int index, CallbackInfoReturnable<ItemStack> cir) {
		var menu = (InventoryMenu) (Object) this;
		if (index < 9 || index >= 45 || menu.slots.size() <= 46) return;
		var from = menu.slots.get(index); var cape = menu.slots.get(46);
		if (!from.hasItem() || !Capes.isCape(from.getItem()) || cape.hasItem() || !cape.isActive()) return;
		var s = from.getItem(); var copy = s.copy();
		cape.set(s.split(1)); from.setChanged();
		cir.setReturnValue(ItemStack.EMPTY);
	}
}
