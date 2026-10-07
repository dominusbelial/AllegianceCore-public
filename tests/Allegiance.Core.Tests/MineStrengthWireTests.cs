using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P574 — wire round-trip proof for <see cref="MineStrength"/>, the port-internal
/// "mine strength fraction after a hit" update that drives the client's decal
/// thinning (native <c>SetMineStrength</c>, mineigc.h:142,180). Checks the fixed
/// 12-byte layout (header + clusterID + mineID + float) survives an
/// encode→decode round trip exactly.
/// </summary>
public sealed class MineStrengthWireTests
{
    [Fact]
    public void MineStrength_round_trips_exactly()
    {
        var msg = new MineStrength
        {
            ClusterId = 774,
            MineId = 9033,
            Strength = 0.617f,
        };

        var decoded = MineStrength.Decode(msg.Encode());

        Assert.Equal(MineStrength.Fmid, decoded.GetFmid());
        Assert.Equal(MineStrength.FixedSize, msg.Encode().Length);
        Assert.Equal(774, decoded.ClusterId);
        Assert.Equal((short)9033, decoded.MineId);
        Assert.Equal(0.617f, decoded.Strength, 5);
    }

    [Fact]
    public void MineStrength_layout_is_12_bytes_header_plus_ids_plus_float()
    {
        var buf = new MineStrength { ClusterId = 1, MineId = 2, Strength = 1f }.Encode();
        Assert.Equal(12, buf.Length);
        // offset 2: fmid = 853 (little-endian)
        Assert.Equal(MineStrength.Fmid, (ushort)(buf[2] | (buf[3] << 8)));
    }
}
