using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P371 — the client's target-pick candidate pool is narrowed to the player's OWN
/// current sector before frustum/FOV selection. Previously
/// <see cref="Game1.CollectTargetCandidates"/> gathered EVERY known ship, warp,
/// station and asteroid from the client's full, map-wide, unconditionally-exported
/// sets with no sector filter, so a cross-sector object that happened to project
/// inside the camera frustum was offered as a real target ("large list of unnamed
/// targets with T"). These tests pin the pure gather
/// (<see cref="Game1.GatherSectorCandidates"/>) that does the narrowing, mirroring
/// the warp-draw loop's proven `wp.ClusterId != myWarpSector` filter:
///
/// <list type="bullet">
/// <item>Only objects whose sector/cluster id equals the player's current sector
/// become candidates — ships by <c>ShipEntity.SectorId</c>, warps/stations/asteroids
/// by their <c>ClusterId</c>.</item>
/// <item>Two different sectors produce disjoint candidate id sets.</item>
/// <item>The existing self / gone / no-update ship exclusions still hold.</item>
/// </list>
///
/// The worlds are seeded through the REAL <see cref="ClientWorld.Apply(IFedMessage)"/>
/// wire-apply path (the same data plane the MonoGame client uses), so these exercise
/// the actual dictionaries the render/target loops read — not synthetic stand-ins.
/// </summary>
public class TargetPickSectorScopeTests
{
    private const short Sector = 900;   // the local player's current sector
    private const short Other = 910;    // a DIFFERENT sector with its own objects

    private static ServerShipUpdate PoseFor(short id) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 1u,
            SimVec.Zero, Orientation.Identity, SimVec.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f);

    private static DataWarpIGC Warp(short id, short cluster) => new(
        WarpId: id, DestinationId: (short)(id + 1), Radius: 100,
        TextureName: "t", IconName: "i", Name: $"warp{id}",
        Position: new[] { (float)id, 0f, 0f },
        Forward: new[] { 0f, 0f, 1f }, Rotation: new[] { 0f, 0f, 0f },
        Signature: 1f, ClusterId: cluster);

    private static DataStationIGC Station(short id, short cluster) => new(
        Position: new[] { (float)id, 0f, 0f },
        Up: new[] { 0f, 1f, 0f }, Forward: new[] { 0f, 0f, 1f }, Rotation: new[] { 0f, 0f, 0f },
        ClusterId: cluster, SideId: 1, StationId: id, StationTypeId: 3,
        BpHull: 10, BpShield: 10, Name: $"sta{id}");

    private static DataAsteroidIGC Asteroid(short id, short cluster) => new(
        Signature: 1f, Position: new[] { (float)id, 0f, 0f },
        Up: new[] { 0f, 1f, 0f }, Forward: new[] { 0f, 0f, 1f }, Rotation: new[] { 0f, 0f, 0f },
        Ore: 10f, OreMax: 20f, AabmCapabilities: 0, AsteroidId: id, HitPoints: 100f,
        Radius: 50, ModelName: "rock", TextureName: "tx", IconName: "ic",
        ClusterId: cluster, Name: $"ast{id}", Fraction: 0);

    /// <summary>Seed a real ClientWorld (own ship in <see cref="Sector"/>; ships,
    /// warps, stations and asteroids split across <see cref="Sector"/> and
    /// <see cref="Other"/>, plus one ghost ship).</summary>
    private static ClientWorld SeedWorld(out short me)
    {
        var w = new ClientWorld();
        me = 100;
        w.Apply(new SetCluster { SectorId = Sector, Ship = PoseFor(me) });

        // A buddy in OUR sector, and two ships in a DIFFERENT sector.
        SeedShip(w, 201, Sector);
        SeedShip(w, 202, Other);
        SeedShip(w, 203, Other);

        // A ship in our sector that has DOCKED (gone from view, P370) — must stay
        // excluded even though it shares our sector.
        w.Apply(new SingleShipUpdate { OidTarget = 204, Ship = PoseFor(204) });
        w.Apply(new ShipStatusMessage { ShipId = 204, SectorId = Sector });
        w.Apply(new ShipDelete { ShipId = 204, Sdr = ShipDelete.SdrDocked });

        // Static objects in both sectors.
        w.Apply(new ExportWarp { Warp = Warp(301, Sector) });
        w.Apply(new ExportWarp { Warp = Warp(302, Other) });
        w.Apply(new ExportStation { Station = Station(401, Sector) });
        w.Apply(new ExportStation { Station = Station(402, Other) });
        w.Apply(new ExportAsteroid { Asteroid = Asteroid(501, Sector) });
        w.Apply(new ExportAsteroid { Asteroid = Asteroid(502, Other) });
        return w;
    }

    private static void SeedShip(ClientWorld w, short id, short sector)
    {
        w.Apply(new SingleShipUpdate { OidTarget = id, Ship = PoseFor(id) });
        w.Apply(new ShipStatusMessage { ShipId = id, SectorId = sector });
    }

    private static List<Game1.CandidateObject> Gather(ClientWorld w, short me, short sector) =>
        Game1.GatherSectorCandidates(w.Ships, w.Warps, w.Stations, w.Asteroids, me, sector);

    /// <summary>⭐ Only same-sector objects survive the gather — a ship, warp,
    /// station and asteroid from another sector must never become candidates.</summary>
    [Fact]
    public void Gather_keeps_only_the_players_current_sector_objects()
    {
        var w = SeedWorld(out var me);
        var got = Gather(w, me, Sector);

        Assert.Contains(got, c => c.Kind == Game1.TargetKind.Ship && c.Id == 201);       // buddy in-sector
        Assert.Contains(got, c => c.Kind == Game1.TargetKind.Warp && c.Id == 301);
        Assert.Contains(got, c => c.Kind == Game1.TargetKind.Station && c.Id == 401);
        Assert.Contains(got, c => c.Kind == Game1.TargetKind.Asteroid && c.Id == 501);

        Assert.DoesNotContain(got, c => c.Kind == Game1.TargetKind.Ship && c.Id == 202); // cross-sector ship
        Assert.DoesNotContain(got, c => c.Kind == Game1.TargetKind.Ship && c.Id == 203);
        Assert.DoesNotContain(got, c => c.Kind == Game1.TargetKind.Warp && c.Id == 302);
        Assert.DoesNotContain(got, c => c.Kind == Game1.TargetKind.Station && c.Id == 402);
        Assert.DoesNotContain(got, c => c.Kind == Game1.TargetKind.Asteroid && c.Id == 502);
    }

    /// <summary>⭐ Cross-sector ships — the very noise the bug produced (with ~134
    /// map-wide asteroids and every known ship pooled) — are never offered.</summary>
    [Fact]
    public void Out_of_sector_ship_is_never_a_target_candidate()
    {
        var w = SeedWorld(out var me);
        var got = Gather(w, me, Sector);

        Assert.DoesNotContain(got, c => c.Kind == Game1.TargetKind.Ship && c.Id == 202);
        Assert.DoesNotContain(got, c => c.Kind == Game1.TargetKind.Ship && c.Id == 203);
        // sanity: the buddy really is there, so the assertion above isn't trivially empty
        Assert.Contains(got, c => c.Kind == Game1.TargetKind.Ship && c.Id == 201);
    }

    /// <summary>⭐ Two clients in DIFFERENT sectors see DISJOINT candidate pools —
    /// each sees exactly its own sector's objects, never the other's.</summary>
    [Fact]
    public void Two_different_sectors_yield_disjoint_candidate_ids()
    {
        var w = SeedWorld(out var me);
        var inSector = Gather(w, me, Sector).Select(c => c.Id).ToHashSet();
        var inOther = Gather(w, me, Other).Select(c => c.Id).ToHashSet();

        Assert.Empty(inSector.Intersect(inOther));

        // the "other" sector's gather contains ITS ship/warp/station/asteroid
        Assert.Contains((short)202, inOther);
        Assert.Contains((short)302, inOther);
        Assert.Contains((short)402, inOther);
        Assert.Contains((short)502, inOther);
        // and must NOT contain our sector's buddy/warp/station/asteroid
        Assert.DoesNotContain((short)201, inOther);
        Assert.DoesNotContain((short)301, inOther);
        Assert.DoesNotContain((short)401, inOther);
        Assert.DoesNotContain((short)501, inOther);
    }

    /// <summary>⭐ The self ship and a gone/docked ship are excluded even when in the
    /// same sector — the original exclusions are preserved by the extract.</summary>
    [Fact]
    public void Self_and_gone_ships_are_excluded_from_any_sector()
    {
        var w = SeedWorld(out var me);
        var got = Gather(w, me, Sector);

        Assert.DoesNotContain(got, c => c.Id == me);
        Assert.DoesNotContain(got, c => c.Kind == Game1.TargetKind.Ship && c.Id == 204);
    }
}
