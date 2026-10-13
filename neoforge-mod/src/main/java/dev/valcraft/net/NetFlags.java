package dev.valcraft.net;

/** 0.13.0: shared flags for the multiplayer mixins (common code). */
public final class NetFlags {
	private NetFlags() {}
	/** While ValCraft publishes the host's world: bind the LAN listener to 127.0.0.1 only and don't advertise it on the LAN. */
	public static volatile boolean loopbackOnly;
}
