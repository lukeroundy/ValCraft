package dev.valcraft.net;

import dev.valcraft.ValCraft;
import io.netty.buffer.ByteBuf;
import net.minecraft.network.codec.StreamCodec;
import net.minecraft.network.protocol.common.custom.CustomPacketPayload;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.server.level.ServerPlayer;
import net.neoforged.bus.api.IEventBus;
import net.neoforged.neoforge.network.PacketDistributor;
import net.neoforged.neoforge.network.event.RegisterPayloadHandlersEvent;

import java.util.HashMap;
import java.util.Map;

/**
 * 0.13.0 multiplayer: everything the Valheim link used to do directly on the integrated server ("server.execute(...)
 * with my player") now goes through here. Single player / the host: runs on the integrated server as before. A client
 * joined through the Valheim tunnel: the same op is sent to the host's server as a custom payload and runs there for
 * that player. Server code answers its own player's Valheim with {@link #toValheim}, which is a local ring send for the
 * host and a payload back to the client otherwise.
 */
public final class ServerOps {
	private ServerOps() {}

	public interface Handler { void run(ServerPlayer sp, byte[] p); }
	private static final Map<Short, Handler> HANDLERS = new HashMap<>();
	public static void on(short op, Handler h) { HANDLERS.put(op, h); }

	// ops that are not plain Valheim event ids
	public static final short OP_CHEST_DEPOSIT = 1001, OP_HOE_WEAR = 1002, OP_ATTACK = 1003, OP_TELEPORT = 1004, OP_DIMS = 1005, OP_VMOB_CLEAR = 1006, OP_SLEEP = 1007, OP_SPAWN = 1008;

	public record Op(short op, byte[] data) implements CustomPacketPayload {
		public static final Type<Op> TYPE = new Type<>(ResourceLocation.fromNamespaceAndPath(ValCraft.MOD_ID, "op"));
		public static final StreamCodec<ByteBuf, Op> CODEC = StreamCodec.of((b, o) -> { b.writeShort(o.op); net.minecraft.network.FriendlyByteBuf.writeByteArray(b, o.data); }, b -> new Op(b.readShort(), net.minecraft.network.FriendlyByteBuf.readByteArray(b)));
		@Override public Type<? extends CustomPacketPayload> type() { return TYPE; }
	}
	/** Server -> client: forward this event to your Valheim. */
	public record ToV(short ev, byte[] data) implements CustomPacketPayload {
		public static final Type<ToV> TYPE = new Type<>(ResourceLocation.fromNamespaceAndPath(ValCraft.MOD_ID, "to_valheim"));
		public static final StreamCodec<ByteBuf, ToV> CODEC = StreamCodec.of((b, o) -> { b.writeShort(o.ev); net.minecraft.network.FriendlyByteBuf.writeByteArray(b, o.data); }, b -> new ToV(b.readShort(), net.minecraft.network.FriendlyByteBuf.readByteArray(b)));
		@Override public Type<? extends CustomPacketPayload> type() { return TYPE; }
	}

	private static int unknownLogged;
	public static void register(IEventBus modBus) {
		modBus.addListener((RegisterPayloadHandlersEvent e) -> {
			var r = e.registrar("1").optional();
			r.playToServer(Op.TYPE, Op.CODEC, (o, ctx) -> ctx.enqueueWork(() -> {
				if (!(ctx.player() instanceof ServerPlayer sp)) return;
				var h = HANDLERS.get(o.op());
				if (h == null) { if (unknownLogged++ < 5) ValCraft.LOG.warn("[net] unknown op {} from {}", o.op(), sp.getName().getString()); return; }
				try { h.run(sp, o.data()); } catch (RuntimeException ex) { ValCraft.LOG.warn("[net] op {} from {} failed: {}", o.op(), sp.getName().getString(), ex.toString()); }
			}));
			r.playToClient(ToV.TYPE, ToV.CODEC, (o, ctx) -> ctx.enqueueWork(() -> ClientSide.toValheim(o.ev(), o.data())));
		});
	}

	/** Server thread: run an op handler directly (integrated server, our own player). */
	public static void runLocal(ServerPlayer sp, short op, byte[] p) {
		var h = HANDLERS.get(op);
		if (h != null) h.run(sp, p);
	}

	/** Server thread: deliver an event to the Valheim of that player. */
	public static void toValheim(ServerPlayer sp, short type, byte[] p) {
		if (sp == null) return;
		if (sp.server.isSingleplayerOwner(sp.getGameProfile())) ClientSide.toValheimLater(type, p);
		else PacketDistributor.sendToPlayer(sp, new ToV(type, p));
	}

	/** Client-only bits, loaded only when used on the client. */
	static final class ClientSide {
		static void toValheim(short type, byte[] p) { dev.valcraft.client.ValCraftClient.sendToValheim(type, p); }
		static void toValheimLater(short type, byte[] p) { net.minecraft.client.Minecraft.getInstance().execute(() -> dev.valcraft.client.ValCraftClient.sendToValheim(type, p)); }
	}
}
