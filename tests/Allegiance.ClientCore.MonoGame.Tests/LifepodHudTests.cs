using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P377 — the client-side lifepod oxygen countdown decision
/// (<see cref="LifepodHud"/>): whether the local player's own ship is an
/// adrift lifepod (lifepod hull + in space), and the wire-carried oxygen
/// FRACTION → mm:ss seconds conversion against the real core endurance
/// constant. Pure logic, no MonoGame types.
/// </summary>
public class LifepodHudTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static readonly ShopCatalog Catalog = LoadCatalog();
    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    /// <summary>The first real hull that carries the lifepod capability in the
    /// core the server actually boots.</summary>
    private static DataHullTypeIGC LifepodHull() =>
        Catalog.Hulls.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));

    private static DataHullTypeIGC NonLifepodHull() =>
        Catalog.Hulls.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod));

    /// <summary>⭐ Adrift (StationId NA/-1) on a real lifepod hull → oxygen is
    /// genuinely burning; the same hull docked (rescued) reads NOT adrift, and
    /// a non-lifepod hull or a missing catalogue never triggers it.</summary>
    [Fact]
    public void IsAdriftLifepod_gates_on_hull_capability_and_in_space()
    {
        short lpId = LifepodHull().HullId;
        short nlId = NonLifepodHull().HullId;

        Assert.True(LifepodHud.IsAdriftLifepod(Catalog, lpId, -1), "real lifepod hull, flying free → adrift");
        Assert.False(LifepodHud.IsAdriftLifepod(Catalog, lpId, 503), "same lifepod hull docked/rescued → not adrift");
        Assert.False(LifepodHud.IsAdriftLifepod(Catalog, nlId, -1), "non-lifepod hull → not adrift");
        Assert.False(LifepodHud.IsAdriftLifepod(null, lpId, -1), "no catalogue → not adrift");
    }

    /// <summary>⭐ The mm:ss countdown is ceil(fraction × endurance), using the
    /// REAL core endurance (PCore001 bakes FloatConstants[31] = 300 s — the
    /// same value the server arms pods with).</summary>
    [Fact]
    public void OxygenSeconds_converts_fraction_to_seconds_with_real_endurance()
    {
        Assert.Equal(300f, Catalog.LifepodEnduranceSeconds);   // the real PCore001 constant
        Assert.Equal(300, LifepodHud.OxygenSeconds(1.0f, Catalog));    // full → 5:00
        Assert.Equal(272, LifepodHud.OxygenSeconds(0.906f, Catalog));  // ~4:32
        Assert.Equal(1, LifepodHud.OxygenSeconds(0.001f, Catalog));    // tiny-but-positive → 1s (ceil)
        Assert.Equal(0, LifepodHud.OxygenSeconds(0.0f, Catalog));      // empty → 0:00
        // Fallback path when the catalogue is unavailable (default 300 s).
        Assert.Equal(300, LifepodHud.OxygenSeconds(1.0f, null));
    }
}
