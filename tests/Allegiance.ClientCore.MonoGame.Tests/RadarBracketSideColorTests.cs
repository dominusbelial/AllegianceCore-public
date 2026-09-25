using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using NumericsVec2 = System.Numerics.Vector2;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P372 — unit tests for the per-side radar contact colouring. Pin the mapping
/// <see cref="RadarBracketRenderer.ContactColour"/> uses (GPU-free, so testable
/// without a graphics device): a ship contact coloured by its REAL side colour
/// (native <c>pside-&gt;GetColor()</c>, radarimage.cpp:682-685) differs from an
/// enemy's, and each side resolves to a genuinely different colour — not a
/// binary friendly/everyone-else boolean. Station/warp keep their type-based
/// hues unless a side colour is explicitly supplied (which this task chooses
/// not to do — see P372 report).
/// </summary>
public class RadarBracketSideColorTests
{
    // Native side palette (P363): side 0 = Gold, side 1 = Blue.
    private static readonly Color Gold = new(0.738f, 0.628f, 0.003f);
    private static readonly Color Blue = new(0.002f, 0.542f, 0.852f);

    [Fact]
    public void ShipContact_UsesItsRealSideColour()
    {
        var c = RadarBracketRenderer.ContactColour(
            RadarBracketRenderer.RadarContactType.Ship, Gold);
        Assert.Equal(Gold, c);
    }

    [Fact]
    public void ShipContact_WithoutRosterColour_FallsBackToTypeTeal()
    {
        var c = RadarBracketRenderer.ContactColour(
            RadarBracketRenderer.RadarContactType.Ship, null);
        Assert.Equal(RadarBracketRenderer.ShipColour, c);
    }

    [Fact]
    public void StationAndWarp_WithoutSideColour_KeepTypeHues()
    {
        Assert.Equal(RadarBracketRenderer.StationColour,
            RadarBracketRenderer.ContactColour(RadarBracketRenderer.RadarContactType.Station, null));
        Assert.Equal(RadarBracketRenderer.WarpColour,
            RadarBracketRenderer.ContactColour(RadarBracketRenderer.RadarContactType.Warp, null));
    }

    [Fact]
    public void SuppliedSideColour_HonouredForAnyType()
    {
        // Native applies pside->GetColor() to every side-bearing blip, so if a
        // station ever carried a side colour this mapping honours it.
        Assert.Equal(Blue,
            RadarBracketRenderer.ContactColour(RadarBracketRenderer.RadarContactType.Station, Blue));
    }

    [Fact]
    public void TwoSides_ResolveToGenuinelyDifferentColours()
    {
        // Not a friendly/everyone-else boolean: each side maps to its OWN real
        // colour, so a 3+-side mission would show three distinct hues.
        Assert.NotEqual(Gold, Blue);
        Assert.Equal(Gold,
            RadarBracketRenderer.ContactColour(RadarBracketRenderer.RadarContactType.Ship, Gold));
        Assert.Equal(Blue,
            RadarBracketRenderer.ContactColour(RadarBracketRenderer.RadarContactType.Ship, Blue));
    }

    [Fact]
    public void RadarContactRecord_CarriesSideColourThrough()
    {
        var contact = new RadarBracketRenderer.RadarContact(
            RadarBracketRenderer.RadarContactType.Ship,
            new NumericsVec2(100f, 200f), OnScreen: true, Opacity: 1f, SideColour: Blue);
        Assert.Equal(Blue, contact.SideColour);
        // The on-screen ship's blip therefore renders in that real side colour.
        Assert.Equal(Blue, RadarBracketRenderer.ContactColour(contact.Type, contact.SideColour));
    }
}
