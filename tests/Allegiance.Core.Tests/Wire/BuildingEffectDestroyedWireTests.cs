using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for the P164 <see cref="BuildingEffectDestroyed"/>
/// codec (FM_S_BUILDINGEFFECT_DESTROYED, 181). Layout pinned against the native
/// struct (Messages.h:936-938: a single <c>AsteroidID asteroidID</c> field) —
/// header(4) + asteroidID(2) = 6 B, no var items, composed with the declarative
/// <see cref="ByteBuf"/> builder. Structurally the single-field sibling of
/// <see cref="DestroyTreasure"/>(30) and <see cref="AsteroidDestroyed"/>(86).
/// </summary>
public class BuildingEffectDestroyedWireTests
{
    [Fact]
    public void BuildingEffectDestroyed_FixedSize_IsSix()
    {
        // header(4) + asteroidID(2) = 6, no pad (2-aligned single field).
        Assert.Equal(6, BuildingEffectDestroyed.FixedSize);
    }

    [Fact]
    public void BuildingEffectDestroyed_RoundTrips_AsteroidId()
    {
        var msg = new BuildingEffectDestroyed { AsteroidId = 7812 };
        var d = BuildingEffectDestroyed.Decode(msg.Encode());
        Assert.Equal(BuildingEffectDestroyed.Fmid, d.GetFmid());
        Assert.Equal((short)7812, d.AsteroidId);
    }

    [Fact]
    public void BuildingEffectDestroyed_EmitsExactBytes()
    {
        var got = new BuildingEffectDestroyed { AsteroidId = 7933 }.Encode();
        var exp = ByteBuf.Build(6, w =>
        {
            w.U16(6); w.U16(181); w.I16(7933);
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void BuildingEffectDestroyed_RejectsWrongFmid_And_TooShort()
    {
        var bad = (byte[])new BuildingEffectDestroyed { AsteroidId = 7955 }.Encode();
        bad[2] = 254;
        Assert.Throws<ArgumentException>(() => BuildingEffectDestroyed.Decode(bad));
        Assert.Throws<ArgumentException>(() => BuildingEffectDestroyed.Decode(new byte[5]));
        Assert.Throws<ArgumentException>(() => BuildingEffectDestroyed.Decode([]));
    }
}
