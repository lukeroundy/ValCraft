package dev.valcraft.client;

import dev.valcraft.ValCraft;
import it.unimi.dsi.fastutil.longs.Long2ByteOpenHashMap;
import it.unimi.dsi.fastutil.longs.Long2IntOpenHashMap;
import it.unimi.dsi.fastutil.longs.LongLinkedOpenHashSet;
import it.unimi.dsi.fastutil.longs.LongOpenHashSet;
import net.minecraft.core.Holder;
import net.minecraft.core.HolderLookup;
import net.minecraft.core.registries.Registries;
import net.minecraft.nbt.CompoundTag;
import net.minecraft.resources.ResourceKey;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.ChunkPos;
import net.minecraft.world.level.GameRules;
import net.minecraft.world.level.NaturalSpawner;
import net.minecraft.world.level.biome.Biome;
import net.minecraft.world.level.biome.Biomes;
import net.minecraft.world.level.chunk.ChunkAccess;
import net.minecraft.world.level.saveddata.SavedData;

import java.util.ArrayList;

/**
 * 0.9.7: Valheim biomes -> MC biomes in the mirrored chunks (spawn lists, grass/sky colour irrelevant since MC terrain isn't drawn),
 * plus a one-time vanilla chunk-generation creature spawn per chunk once its terrain is mirrored (the void world never ran
 * worldgen spawns there). Spawned chunks are persisted in the world's data ("valcraft_spawned"). Server thread only.
 */
final class BiomeSync {
	private BiomeSync() {}
	// wire code: 1 Meadows 2 Swamp 3 Mountain 4 BlackForest 5 Plains 6 AshLands 7 DeepNorth 8 Ocean 9 Mistlands
	@SuppressWarnings("unchecked")
	private static final ResourceKey<Biome>[] MAP = new ResourceKey[] { null,
		Biomes.PLAINS, Biomes.SWAMP, Biomes.SNOWY_SLOPES, Biomes.OLD_GROWTH_SPRUCE_TAIGA, Biomes.SAVANNA,
		Biomes.NETHER_WASTES, Biomes.END_HIGHLANDS, Biomes.OCEAN, Biomes.DARK_FOREST }; // 0.11.0: AshLands -> Nether, DeepNorth -> End
	/** 0.11.0: Ashlands is split into Nether biomes in ~64-block patches (spawn lists: piglins, ghasts, magma cubes, skeletons...). */
	@SuppressWarnings("unchecked")
	private static final ResourceKey<Biome>[] ASH = new ResourceKey[] { Biomes.NETHER_WASTES, Biomes.BASALT_DELTAS, Biomes.SOUL_SAND_VALLEY, Biomes.CRIMSON_FOREST, Biomes.NETHER_WASTES, Biomes.WARPED_FOREST };
	static ResourceKey<Biome> biomeFor(int code, int qx, int qz) {
		if (code == 6) { int h = (qx >> 4) * 73856093 ^ (qz >> 4) * 19349663; return ASH[Math.floorMod(h, ASH.length)]; }
		return MAP[code];
	}
	static final String[] NAMES = { "?", "Meadows", "Swamp", "Mountain", "BlackForest", "Plains", "AshLands", "DeepNorth", "Ocean", "Mistlands" };

	private static final Long2ByteOpenHashMap quart = new Long2ByteOpenHashMap();      // (x>>2, z>>2) -> code
	private static final Long2IntOpenHashMap tilesInChunk = new Long2IntOpenHashMap(); // chunk -> bitmask of its 4 tiles
	private static final LongLinkedOpenHashSet dirty = new LongLinkedOpenHashSet();
	private static final LongOpenHashSet doneThisSession = new LongOpenHashSet();
	private static Spawned spawned; private static ServerLevel spawnedLevel;
	private static int biomeChunks, spawnRuns;

	private static long key(int x, int z) { return ((long) x << 32) ^ (z & 0xFFFFFFFFL); }

	static void clear() { quart.clear(); tilesInChunk.clear(); dirty.clear(); doneThisSession.clear(); spawned = null; spawnedLevel = null; }

	/** One applied 8x8 terrain tile (tile coords; T = 8). bio = 4 quadrant codes or null. */
	static void onTile(ServerLevel level, int tx, int tz, int T, byte[] bio) {
		int cx = Math.floorDiv(tx * T, 16), cz = Math.floorDiv(tz * T, 16);
		long ck = key(cx, cz);
		boolean changed = false;
		if (bio != null) for (int q = 0; q < 4; q++) {
			int qx = (tx * T + (q / 2) * 4) >> 2, qz = (tz * T + (q % 2) * 4) >> 2;
			byte c = bio[q]; if (c <= 0 || c >= MAP.length) continue;
			long k = key(qx, qz);
			if (!quart.containsKey(k) || quart.get(k) != c) { quart.put(k, c); changed = true; }
		}
		int bit = 1 << ((Math.floorMod(tx * T, 16) / T) * 2 + Math.floorMod(tz * T, 16) / T);
		int m = tilesInChunk.get(ck) | bit; tilesInChunk.put(ck, m);
		if (changed || (m == 15 && !doneThisSession.contains(ck))) dirty.add(ck);
	}

	static void tick(ServerLevel level) {
		if (dirty.isEmpty()) return;
		var reg = level.registryAccess().registryOrThrow(Registries.BIOME);
		var resend = new ArrayList<ChunkAccess>();
		for (int n = 0; n < 8 && !dirty.isEmpty(); n++) {
			long ck = dirty.removeFirstLong();
			int cx = (int) (ck >> 32), cz = (int) ck;
			var chunk = level.getChunkSource().getChunkNow(cx, cz);
			if (chunk == null) continue; // not loaded: redone when its tiles come again
			boolean[] any = { false };
			chunk.fillBiomesFromNoise((qx, qy, qz, s) -> {
				long k = key(qx, qz);
				if (quart.containsKey(k)) { var h = reg.getHolder(biomeFor(quart.get(k), qx, qz)); if (h.isPresent()) { any[0] = true; return h.get(); } }
				return chunk.getNoiseBiome(qx, qy, qz);
			}, null);
			if (any[0]) { chunk.setUnsaved(true); resend.add(chunk); biomeChunks++; }
			if (tilesInChunk.get(ck) == 15 && doneThisSession.add(ck)) spawnOnce(level, chunk, cx, cz);
		}
		if (!resend.isEmpty()) {
			level.getChunkSource().chunkMap.resendBiomesForChunks(resend);
			if (biomeChunks % 64 < resend.size()) ValCraft.LOG.info("[spawn] Valheim biomes applied to {} MC chunks so far", biomeChunks);
		}
	}

	private static void spawnOnce(ServerLevel level, ChunkAccess chunk, int cx, int cz) {
		if (spawnedLevel != level) { spawnedLevel = level; spawned = level.getDataStorage().computeIfAbsent(new SavedData.Factory<>(Spawned::new, Spawned::load, null), "valcraft_spawned"); }
		long ck = key(cx, cz);
		if (spawned.chunks.contains(ck)) return;
		if (!level.getGameRules().getBoolean(GameRules.RULE_DOMOBSPAWNING)) return; // try again next session
		spawned.chunks.add(ck); spawned.setDirty();
		int bx = cx * 16 + 8, bz = cz * 16 + 8;
		Holder<Biome> biome = chunk.getNoiseBiome(bx >> 2, level.getSeaLevel() >> 2, bz >> 2);
		var pos = new ChunkPos(cx, cz);
		int before = level.getEntitiesOfClass(net.minecraft.world.entity.animal.Animal.class, new net.minecraft.world.phys.AABB(cx * 16, level.getMinBuildHeight(), cz * 16, cx * 16 + 16, level.getMaxBuildHeight(), cz * 16 + 16)).size();
		try { NaturalSpawner.spawnMobsForChunkGeneration(level, biome, pos, level.getRandom()); }
		catch (RuntimeException e) { ValCraft.LOG.warn("[spawn] chunk ({}, {}) worldgen spawn failed: {}", cx, cz, e.toString()); return; }
		spawnRuns++;
		int after = level.getEntitiesOfClass(net.minecraft.world.entity.animal.Animal.class, new net.minecraft.world.phys.AABB(cx * 16, level.getMinBuildHeight(), cz * 16, cx * 16 + 16, level.getMaxBuildHeight(), cz * 16 + 16)).size();
		String bn = biome.unwrapKey().map(k -> k.location().getPath()).orElse("?");
		if (after > before) ValCraft.LOG.info("[spawn] chunk ({}, {}) {}: worldgen spawned {} animal(s)", cx, cz, bn, after - before);
		else if (spawnRuns % 25 == 1) ValCraft.LOG.info("[spawn] chunk-generation spawns run for {} chunks (latest ({}, {}) {}: none, vanilla chance)", spawnRuns, cx, cz, bn);
	}

	static final class Spawned extends SavedData {
		final LongOpenHashSet chunks = new LongOpenHashSet();
		static Spawned load(CompoundTag t, HolderLookup.Provider p) { var s = new Spawned(); for (long l : t.getLongArray("chunks")) s.chunks.add(l); return s; }
		@Override public CompoundTag save(CompoundTag t, HolderLookup.Provider p) { t.putLongArray("chunks", chunks.toLongArray()); return t; }
	}
}
