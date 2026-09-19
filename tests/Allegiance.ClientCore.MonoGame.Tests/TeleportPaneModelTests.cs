using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P273 docked teleport pane model. No graphics device —
/// same discipline as <see cref="ShopModelTests"/>. Uses the real PCore001.igc
/// catalogue (the same core the server boots) so the turret-slot and
/// restart-capability gating runs against real hull/station data.
/// </summary>
public class TeleportPaneModelTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static readonly ShopCatalog Catalog = LoadCatalog();
    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    // A real hull with turret hardpoints (MaxWeapons != MaxFixedWeapons) and one without.
    private static readonly DataHullTypeIGC TurretHull = Catalog.Hulls.First(h => h.MaxWeapons > h.MaxFixedWeapons);
    private static readonly DataHullTypeIGC PlainHull = Catalog.Hulls.First(h => h.MaxWeapons == h.MaxFixedWeapons);

    private static DataHullTypeIGC? HullLookup(short id) => Catalog.Hulls.FirstOrDefault(h => h.HullId == id);
    private static DataStationTypeIGC? StationTypeLookup(short id) =>
        Catalog.StationTypes is { } sts && sts.TryGetValue(id, out var t) ? t : null;

    /// <summary>Return the first station type matching <paramref name="pred"/>,
    /// as a nullable (the real core almost always has one, but a degraded/core-
    /// absent test machine should skip rather than fail).</summary>
    private static DataStationTypeIGC? FindStationType(Func<DataStationTypeIGC, bool> pred)
    {
        if (Catalog.StationTypes is not { } sts) return null;
        foreach (var t in sts.Values)
            if (pred(t)) return t;
        return null;
    }

    private static TeleportPaneModel Build(
        short ownShip = 900,
        short ownSide = 1,
        short ownStation = 990,
        IReadOnlyCollection<ShipView>? ships = null,
        IReadOnlyCollection<StationView>? stations = null) =>
        TeleportPaneModel.Build(ownShip, ownSide, ownStation,
            ships ?? Array.Empty<ShipView>(),
            stations ?? Array.Empty<StationView>(),
            HullLookup, StationTypeLookup);

    [Fact]
    public void Includes_docked_same_side_ship_with_turret_slots()
    {
        var m = Build(ships:
        [
            new ShipView(Id: 903, SideId: 1, StationId: 980, HullId: TurretHull.HullId),
        ]);
        Assert.Single(m.Ships);
        Assert.Equal(903, m.Ships[0].Id);
        Assert.Equal(TurretHull.MaxWeapons - TurretHull.MaxFixedWeapons, m.Ships[0].TurretSlots);
        Assert.False(m.IsEmpty);
    }

    [Fact]
    public void Excludes_enemy_side_ship()
    {
        var m = Build(ships:
        [
            new ShipView(Id: 904, SideId: 2, StationId: 981, HullId: TurretHull.HullId),
        ]);
        Assert.Empty(m.Ships);
        Assert.True(m.IsEmpty);
    }

    [Fact]
    public void Excludes_self()
    {
        var m = Build(ownShip: 905, ships:
        [
            new ShipView(Id: 906, SideId: 1, StationId: 982, HullId: TurretHull.HullId),
            new ShipView(Id: 910, SideId: 1, StationId: 983, HullId: TurretHull.HullId),
        ]);
        // ownShip 905 isn't in the list; both listed ships are on our side+docked+turret → both included.
        Assert.Equal(2, m.Ships.Count);
    }

    [Fact]
    public void Excludes_flying_ship_not_docked()
    {
        var m = Build(ships:
        [
            new ShipView(Id: 911, SideId: 1, StationId: -1, HullId: TurretHull.HullId),  // flying (NA)
            new ShipView(Id: 912, SideId: 1, StationId: 984, HullId: TurretHull.HullId),
        ]);
        Assert.Single(m.Ships);
        Assert.Equal(912, m.Ships[0].Id);
    }

    [Fact]
    public void Excludes_hull_with_no_turret_slots()
    {
        var m = Build(ships:
        [
            new ShipView(Id: 913, SideId: 1, StationId: 986, HullId: PlainHull.HullId),
        ]);
        Assert.Empty(m.Ships);
    }

    [Fact]
    public void Excludes_ship_whose_hull_lookup_misses()
    {
        var m = Build(ships:
        [
            new ShipView(Id: 914, SideId: 1, StationId: 988, HullId: -7),   // not in the core
        ]);
        Assert.Empty(m.Ships);
    }

    [Fact]
    public void Sorts_ships_by_name()
    {
        // Pick three turret hulls and build ships whose names sort non-trivially.
        var hulls = Catalog.Hulls.Where(h => h.MaxWeapons > h.MaxFixedWeapons)
            .OrderBy(h => h.Name).Take(3).ToList();
        // Feed in reverse id/name order to prove the sort.
        var m = Build(ships:
        [
            new ShipView(Id: 920, SideId: 1, StationId: 970, HullId: hulls[2].HullId),
            new ShipView(Id: 919, SideId: 1, StationId: 971, HullId: hulls[0].HullId),
            new ShipView(Id: 918, SideId: 1, StationId: 972, HullId: hulls[1].HullId),
        ]);
        Assert.Equal(3, m.Ships.Count);
        // The displayed order must match name-sorting (native's default sortName).
        var names = m.Ships.Select(s => s.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);
    }

    [Fact]
    public void Includes_same_side_restart_station_excluding_current()
    {
        // Need a real restart-capable station type + a station of ours.
        var rstype = FindStationType(t => t.HasCapability(DataStationTypeIGC.SabmRestart));
        // Degrade gracefully if the real core lacks one (extremely unlikely); skip then.
        if (rstype is null) return;

        var m = Build(ownStation: 996, stations:
        [
            new StationView(Id: 996, SideId: 1, StationTypeId: rstype.Value.StationTypeId, Name: "Alpha base"),
            new StationView(Id: 997, SideId: 1, StationTypeId: rstype.Value.StationTypeId, Name: "Bravo base"),
        ]);
        Assert.Single(m.Stations);                       // 997 only (996 is our current station)
        Assert.Equal(997, m.Stations[0].Id);
    }

    [Fact]
    public void Excludes_enemy_and_non_restart_stations()
    {
        var restart = FindStationType(t => t.HasCapability(DataStationTypeIGC.SabmRestart));
        var nonRestart = FindStationType(t => !t.HasCapability(DataStationTypeIGC.SabmRestart));
        // Guard: if the real core has no non-restart station type, this can't be exercised.
        if (restart is null || nonRestart is null) return;

        var m = Build(ownStation: -1, stations:
        [
            new StationView(Id: 994, SideId: 2, StationTypeId: restart.Value.StationTypeId, Name: "enemy base"),
            new StationView(Id: 993, SideId: 1, StationTypeId: nonRestart.Value.StationTypeId, Name: "no restart"),
            new StationView(Id: 992, SideId: 1, StationTypeId: restart.Value.StationTypeId, Name: "good base"),
        ]);
        Assert.Single(m.Stations);
        Assert.Equal(992, m.Stations[0].Id);
    }

    [Fact]
    public void Cursor_clamps_and_cycles_across_sections()
    {
        var m = Build(ships:
        [
            new ShipView(Id: 922, SideId: 1, StationId: 974, HullId: TurretHull.HullId),
            new ShipView(Id: 923, SideId: 1, StationId: 975, HullId: TurretHull.HullId),
        ],
        stations:
        [
            new StationView(Id: 924, SideId: 1, StationTypeId: StationTypeAny(), Name: "Stn"),
        ]);

        Assert.Equal(3, m.RowCount);
        Assert.Equal(0, m.Cursor);
        Assert.True(m.CursorOnShip);

        m.MoveCursor(-5); Assert.Equal(0, m.Cursor);          // clamp bottom
        m.MoveCursor(+1); Assert.Equal(1, m.Cursor);
        m.MoveCursor(+1); Assert.Equal(2, m.Cursor);          // first station row
        Assert.False(m.CursorOnShip);
        m.MoveCursor(+5); Assert.Equal(2, m.Cursor);          // clamp top
    }

    private static short StationTypeAny() =>
        Catalog.StationTypes!.Values.First().StationTypeId;

    [Fact]
    public void Activate_classifies_board_vs_teleport()
    {
        var m = Build(ships:
        [
            new ShipView(Id: 925, SideId: 1, StationId: 976, HullId: TurretHull.HullId),
        ],
        stations:
        [
            new StationView(Id: 927, SideId: 1, StationTypeId: StationTypeAny(), Name: "Stn"),
        ]);

        // Cursor 0 = ship → board.
        var a1 = m.Activate();
        Assert.Equal(TeleportPaneModel.ActionKind.BoardShip, a1.Kind);
        Assert.Equal(925, a1.TargetId);

        // Move to the station row → teleport.
        m.MoveCursor(+1);
        var a2 = m.Activate();
        Assert.Equal(TeleportPaneModel.ActionKind.TeleportToStation, a2.Kind);
        Assert.Equal(927, a2.TargetId);
    }

    [Fact]
    public void Activate_empty_list_returns_none()
    {
        var m = Build();   // no ships, no stations
        Assert.True(m.IsEmpty);
        Assert.Equal(TeleportPaneModel.ActionKind.None, m.Activate().Kind);
        Assert.Equal(TeleportPaneModel.Na, m.Activate().TargetId);
    }
}
