using System;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P519 — pure tests for the hull-preview orbit behind the docked HULLS/PARTS
/// panes: the orbit advances at native's <c>cmExternalOrbit</c> rates, and the
/// preview's hull (which selects the model) tracks the shop's effective hull.
/// </summary>
public class DockedHullOrbitTests
{
    /// <summary>⭐ P519 — the orbit advances at native's exact cmExternalOrbit
    /// rates (WinTrek.cpp:7186-7191: Pitch 0.35, Yaw 0.2, Roll 0.5 rad/s), so
    /// after one second the accumulated angles are exactly those increments.</summary>
    [Fact]
    public void Orbit_advances_at_native_cmExternalOrbit_rates()
    {
        var (p, y, r) = DockedHullOrbit.Advance(0f, 0f, 0f, 1.0);
        Assert.Equal(DockedHullOrbit.PitchRate, p, precision: 5);
        Assert.Equal(DockedHullOrbit.YawRate, y, precision: 5);
        Assert.Equal(DockedHullOrbit.RollRate, r, precision: 5);
    }

    /// <summary>⭐ P519 — over dt=0.5s the orbit advances by half the per-second
    /// rates (frame-rate independence of the rotation speeds).</summary>
    [Fact]
    public void Orbit_advances_frame_rate_independently()
    {
        var (p, y, r) = DockedHullOrbit.Advance(0f, 0f, 0f, 0.5);
        Assert.Equal(0.35f * 0.5f, p, precision: 5);
        Assert.Equal(0.20f * 0.5f, y, precision: 5);
        Assert.Equal(0.50f * 0.5f, r, precision: 5);
    }

    /// <summary>⭐ P519 — the hull the preview renders is the shop's EFFECTIVE
    /// hull, so cycling/selecting a hull immediately swaps the preview's model
    /// (no hitch, models are already cached).</summary>
    [Fact]
    public void Hull_selection_drives_the_preview_hull_id()
    {
        var cat = ShopCatalog.TryLoad(
            Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork")
            ?? throw new InvalidOperationException("core not loadable");
        short current = cat.Hulls.OrderBy(h => h.HullId).First().HullId;
        short other = cat.Hulls.OrderBy(h => h.HullId).Last(h => h.HullId != current).HullId;

        var m = new ShopModel(cat, current, Array.Empty<ExpandedPartData>());
        Assert.Equal(current, m.EffectiveHullId);          // preview = the current hull

        m.CycleHullTo(other);
        Assert.Equal(other, m.EffectiveHullId);            // preview swaps to the chosen hull
    }
}
