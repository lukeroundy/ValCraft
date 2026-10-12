package dev.valcraft;

import com.mojang.logging.LogUtils;
import net.neoforged.fml.common.Mod;
import org.slf4j.Logger;

@Mod(ValCraft.MOD_ID)
public final class ValCraft {
	public static final String MOD_ID = "valcraft";
	public static final Logger LOG = LogUtils.getLogger();

	public ValCraft() {
		LOG.info("ValCraft loaded (0.8.2: health link + combat)");
	}
}
