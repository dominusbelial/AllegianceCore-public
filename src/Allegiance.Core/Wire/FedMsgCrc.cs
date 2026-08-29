namespace Allegiance.Core.Wire;

/// <summary>
/// Faithful port of <c>MemoryCRC</c> (<c>_Utility/CRC.cpp:162-176</c>), the checksum
/// appended to every packet and validated on receipt (<c>Messages.cpp:529-537</c>,
/// <c>1015-1018</c>).
///
/// Algorithm: standard reflected IEEE CRC-32 table with seed <c>0xFFFFFFFF</c>
/// and the byte step <c>table[(crc ^ byte) &amp; 0xFF] ^ (crc &gt;&gt; 8)</c>, but with
/// NO final XOR/inversion (<c>CRC.cpp:98-102, 162-176</c>) — the raw running value
/// is returned, and a result of 0 is mapped to 1 (never-zero guard).
/// All integers are little-endian on the wire (<c>x86/x64</c>).
/// </summary>
public static class FedMsgCrc
{
    // Reflected IEEE-802.3 (PKZIP) polynomial, CRC-32. Same as CRC.cpp:11-92.
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>
    /// Equivalent of the C++ <c>update_crc</c> step: fold one byte into the state.
    /// Seed the cascade with <see cref="Seed"/>.
    /// </summary>
    public static uint Step(uint crc, byte data) =>
        Table[(byte)(crc ^ data)] ^ (crc >> 8);

    /// <summary>
    /// Compute the wire CRC over <paramref name="data"/> and return it masked to
    /// 32 bits (0 mapped to 1, exactly like <c>MemoryCRC</c>).
    /// </summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = Seed;
        foreach (byte b in data)
            crc = Step(crc, b);
        return crc == 0 ? 1u : crc;
    }

    /// <summary>Initial value (also the per-packet seed).</summary>
    public const uint Seed = 0xFFFFFFFF;
}
