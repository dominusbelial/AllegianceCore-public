using Allegiance.Core.Model;

namespace Allegiance.Core.Tests;

/// <summary>
/// Pure unit tests for <see cref="TechTreeBitMask.RequiredTechsBuyable"/> —
/// the shared CanBuy containment P283 wired into <c>Server.StationSellsHull</c>
/// and P284 reuses for the docked-shop filter. Exercises the exact
/// <c>need ⊆ (local | sideDev)</c> bitmath directly, including the defensive
/// shorter-mask handling (a byte past a mask's length is treated as all-clear).
/// </summary>
public class TechTreeBitMaskRequiredBuyableTests
{
    private static byte[] Mask(params int[] byteAndBit)
    {
        var m = new byte[TechTreeBitMask.ByteCount];
        for (int i = 0; i + 1 < byteAndBit.Length; i += 2)
            m[byteAndBit[i]] |= (byte)(1 << byteAndBit[i + 1]);
        return m;
    }

    private static TechTreeBitMask SideMask(params int[] byteAndBit) =>
        TechTreeBitMask.FromBytes(Mask(byteAndBit));

    /// <summary>An item needing only bits already in the union is buyable.</summary>
    [Fact]
    public void NeedsOnlyLocalOrSide_IsBuyable()
    {
        // need = {byte0 bit0}; local provides it -> buyable.
        Assert.True(TechTreeBitMask.RequiredTechsBuyable(Mask(0, 0), Mask(0, 0), SideMask()));
        // need = {byte2 bit5}; only the side dev provides it -> buyable.
        Assert.True(TechTreeBitMask.RequiredTechsBuyable(Mask(2, 5), Mask(), SideMask(2, 5)));
        // need split across both local and side.
        Assert.True(TechTreeBitMask.RequiredTechsBuyable(Mask(0, 3, 1, 7), Mask(0, 3), SideMask(1, 7)));
    }

    /// <summary>An item with ANY required bit absent from (local | sideDev) is not.</summary>
    [Fact]
    public void AnyMissingBit_IsNotBuyable()
    {
        Assert.False(TechTreeBitMask.RequiredTechsBuyable(Mask(0, 0), Mask(), SideMask()));
        Assert.False(TechTreeBitMask.RequiredTechsBuyable(Mask(1, 1, 5, 0), Mask(1, 1), SideMask()));  // bit 40 absent
        Assert.False(TechTreeBitMask.RequiredTechsBuyable(Mask(3, 7), Mask(), SideMask(3, 6)));         // bit 31 absent
    }

    /// <summary>Nil requirement (all zeros) is always buyable regardless of the masks.</summary>
    [Fact]
    public void NoRequirement_IsAlwaysBuyable()
    {
        Assert.True(TechTreeBitMask.RequiredTechsBuyable(Mask(), Mask(), SideMask()));
        Assert.True(TechTreeBitMask.RequiredTechsBuyable(Mask(), null, SideMask()));
    }

    /// <summary>A null local or null need degrades to all-clear for that mask —
    /// a shorter/absent mask never blank a buyable (the P284 degradation).</summary>
    [Fact]
    public void NullOrShortMasks_DegradeGracefully()
    {
        // need = {0,0}; no local given -> must be supplied by the side.
        Assert.False(TechTreeBitMask.RequiredTechsBuyable(Mask(0, 0), null, SideMask()));
        Assert.True(TechTreeBitMask.RequiredTechsBuyable(Mask(0, 0), null, SideMask(0, 0)));
        // Short local (shorter than 50): bits past its length come from side only.
        var shortLocal = new byte[8];
        shortLocal[0] = 0b0000_0001;
        Assert.True(TechTreeBitMask.RequiredTechsBuyable(Mask(0, 0, 3, 1), shortLocal, SideMask(3, 1)));
        Assert.False(TechTreeBitMask.RequiredTechsBuyable(Mask(0, 0, 3, 1), shortLocal, SideMask()));
    }
}
