package dev.valcraft.client;

import com.mojang.brigadier.arguments.FloatArgumentType;
import dev.valcraft.link.ValCraftLink;
import net.minecraft.commands.Commands;
import net.minecraft.network.chat.Component;
import net.neoforged.neoforge.client.event.RegisterClientCommandsEvent;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * 0.13.5: "/valcraft offset <x> <z>" sets the Valheim world's horizontal mapping offset; "/valcraft nudge <dx> <dz>" shifts it.
 * Only where MC builds appear in Valheim moves; MC blocks themselves are never moved. Stored by Valheim per world.
 */
public final class OffsetCommand {
	private OffsetCommand() {}
	public static void register(RegisterClientCommandsEvent e) {
		e.getDispatcher().register(Commands.literal("valcraft")
			.then(Commands.literal("offset").then(Commands.argument("x", FloatArgumentType.floatArg()).then(Commands.argument("z", FloatArgumentType.floatArg())
				.executes(c -> send(c.getSource(), false, FloatArgumentType.getFloat(c, "x"), FloatArgumentType.getFloat(c, "z"))))))
			.then(Commands.literal("nudge").then(Commands.argument("dx", FloatArgumentType.floatArg()).then(Commands.argument("dz", FloatArgumentType.floatArg())
				.executes(c -> send(c.getSource(), true, FloatArgumentType.getFloat(c, "dx"), FloatArgumentType.getFloat(c, "dz")))))));
	}
	private static int send(net.minecraft.commands.CommandSourceStack src, boolean rel, float x, float z) {
		boolean ok = ValCraftClient.sendToValheim(ValCraftLink.EV_OFFSET, ByteBuffer.allocate(9).order(ByteOrder.LITTLE_ENDIAN).put((byte) (rel ? 1 : 0)).putFloat(x).putFloat(z).array());
		src.sendSystemMessage(Component.literal(ok ? (rel ? "Valheim mapping offset nudged by " : "Valheim mapping offset set to ") + x + ", " + z + " (MC builds move in Valheim; you are moved to your Valheim position)" : "Valheim not linked"));
		dev.valcraft.ValCraft.LOG.info("[sync] /valcraft {} {} {} sent={}", rel ? "nudge" : "offset", x, z, ok);
		return ok ? 1 : 0;
	}
}
