using System;
namespace ValCraft.Protocol
{
    /// <summary>0.8.1 (protocol v11) health/damage payloads.</summary>
    public static class HealthCodec
    {
        /// EvHealth MC->V: f32 hp, f32 max, u8 food, u8 flags (1 = dead)
        public static (float hp, float max, int food, bool dead) ReadHealth(byte[] p) => (BitConverter.ToSingle(p, 0), BitConverter.ToSingle(p, 4), p[8], (p[9] & 1) != 0);
        /// EvDamage V->MC: f32 amount (MC hp), u8 Valheim HitType, f32 dirX, f32 dirZ (MC axes), f32 push
        /// <summary>0.10.0: + u32 attacker (Valheim creature instance id = EvVMobList id, 0 = none) so MC shields can block.</summary>
        public static byte[] Damage(float amount, byte hitType, float dx, float dz, float push, uint attacker = 0)
        {
            var b = new byte[21];
            BitConverter.GetBytes(attacker).CopyTo(b, 17);
            BitConverter.GetBytes(amount).CopyTo(b, 0); b[4] = hitType;
            BitConverter.GetBytes(dx).CopyTo(b, 5); BitConverter.GetBytes(dz).CopyTo(b, 9); BitConverter.GetBytes(push).CopyTo(b, 13);
            return b;
        }
    }
}
