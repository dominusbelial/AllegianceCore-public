using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P273 teleport pane layout — every emitted row is
/// printable ASCII 32..126 (the HUD spritefonts bake ASCII only) and mirrors
/// the native destination list (name + turret count for ships, name for
/// stations).
/// </summary>
public class TeleportLayoutTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);
    private static readonly ShopCatalog Catalog =
        ShopCatalog.TryLoad(ArtRoot) ?? throw new InvalidOperationException($"core not loadable from {ArtRoot}");

    private static readonly DataHullTypeIGC TurretHull = Catalog.Hulls.First(h => h.MaxWeapons > h.MaxFixedWeapons);

    private static TeleportPaneModel ModelWith(List<ShipView> ships, List<StationView> stations) =>
        TeleportPaneModel.Build(ownShipId: 999, ownSideId: 1, ownStationId: -1, ships, stations,
            id => Catalog.Hulls.FirstOrDefault(h => h.HullId == id),
            id => Catalog.StationTypes is { } s && s.TryGetValue(id, out var t) ? t : null);

    private static void AssertPureAscii(IReadOnlyList<ShopLayout.Row> rows)
    {
        foreach (var r in rows)
            foreach (var ch in r.Text)
                Assert.InRange(ch, '\u0020', '\u007e');
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rows_are_pure_ascii_and_list_ship_name_and_turret_count()
    {
        Require.Artwork();
        var m = ModelWith(
            [new ShipView(Id: 902, SideId: 1, StationId: 977, HullId: TurretHull.HullId)],
            []);
        var rows = TeleportLayout.Build(m);
        AssertPureAscii(rows);
        var joined = string.Join("\n", rows.Select(r => r.Text));
        Assert.Contains("TELEPORT", joined);
        Assert.Contains("BOARD A TEAMMATE", joined);
        Assert.Contains(TurretHull.Name, joined);
        Assert.Contains("turrets:", joined);
        Assert.Contains("[SHIP]", joined);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Highlights_the_cursor_row()
    {
        Require.Artwork();
        var m = ModelWith(
            [
                new ShipView(Id: 903, SideId: 1, StationId: 978, HullId: TurretHull.HullId),
                new ShipView(Id: 904, SideId: 1, StationId: 979, HullId: TurretHull.HullId),
            ],
            []);
        var rows0 = TeleportLayout.Build(m);
        int idx0 = rows0.ToList().FindIndex(r => r.Highlight);
        m.MoveCursor(+1);
        var rows1 = TeleportLayout.Build(m);
        int idx1 = rows1.ToList().FindIndex(r => r.Highlight);
        Assert.Equal(4, idx0);   // title/hint/blank/section precede the first item row
        Assert.Equal(5, idx1);
        Assert.NotEqual(idx0, idx1);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Empty_list_shows_no_boardable_and_has_back_hint()
    {
        Require.Artwork();
        var m = ModelWith([], []);
        Assert.True(m.IsEmpty);
        var rows = TeleportLayout.Build(m);
        AssertPureAscii(rows);
        var joined = string.Join("\n", rows.Select(r => r.Text));
        Assert.Contains("no boardable ships", joined);
        Assert.Contains("no other friendly stations", joined);
        Assert.Contains("BACK", joined);
    }
}
