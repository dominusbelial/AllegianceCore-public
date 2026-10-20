using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-3b — pure rule tests for <see cref="TargetPaneModel"/>. No graphics
/// device; each test builds a <see cref="ClientWorld"/> from the real wire
/// messages the model reads (the same discipline as
/// <see cref="ClientWorldUnitTests"/>).
/// </summary>
public class TargetPaneModelTests
{
    private const short Sector = 10;
    private const short OtherSector = 11;

    private static ServerShipUpdate Pose(short id, (float x, float y, float z) pos, (float x, float y, float z) vel,
        float hull = 1f, float shield = 1f) =>
        ServerShipUpdate.FromValues(id, 0, new ControlData(0f, 0f, 0f, 1f), 1u,
            new SimVec(pos.x, pos.y, pos.z), Orientation.Identity, new SimVec(vel.x, vel.y, vel.z),
            [0f, 0f, 0f], hull, shield, 1f, 1f, 1f, 0f, 1f);

    private static ClientWorld World()
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster { SectorId = Sector, Ship = Pose(1, (0f, 0f, 0f), (0f, 0f, 0f)) });
        return w;
    }

    private static void AddShip(ClientWorld w, short id, short sector, (float x, float y, float z) pos,
        (float x, float y, float z) vel, float hull, float shield, short side = 1)
    {
        w.Apply(new SingleShipUpdate { OidTarget = id, Ship = Pose(id, pos, vel, hull, shield) });
        w.Apply(new ShipStatusMessage { ShipId = id, SectorId = sector, StationId = -1 });
        w.Apply(new ExportShip { ShipId = id, SideId = side, CharacterName = $"ship {id}", HullId = 501 });
    }

    private static void AddStation(ClientWorld w, short id, short cluster, short side,
        (float x, float y, float z) pos, float? hull = null, float? shield = null)
    {
        w.Apply(new ExportStation
        {
            Station = new DataStationIGC(
                [pos.x, pos.y, pos.z], [0f, 1f, 0f], [0f, 0f, 1f], [0f, 0f, 0f, 0f],
                cluster, side, id, 1, 255, 255, $"station {id}"),
        });
        if (hull is { } h && shield is { } s)
            w.Apply(new StationsUpdate
            {
                Entries = [new StationsUpdateEntry(id, (byte)Math.Round(h * 240f), (byte)Math.Round(s * 240f))],
            });
    }

    private static void AddAsteroid(ClientWorld w, short id, short cluster, (float x, float y, float z) pos)
    {
        w.Apply(new ExportAsteroid
        {
            Asteroid = new DataAsteroidIGC(
                1f, [pos.x, pos.y, pos.z], [0f, 1f, 0f], [0f, 0f, 1f], [0f, 0f, 0f, 0f],
                0f, 1000f, DataAsteroidIGC.AabmBuildable, id, 25000f, 200,
                "bgrnd03", "tex", "icon", cluster, $"asteroid {id}", 200),
        });
    }

    private static void AddWarp(ClientWorld w, short id, short cluster, (float x, float y, float z) pos)
    {
        w.Apply(new ExportWarp
        {
            Warp = new DataWarpIGC(
                id, -1, 100, "tex", "icon", $"warp {id}",
                [pos.x, pos.y, pos.z], [0f, 0f, 1f], [0f, 0f, 0f, 0f], 1f, cluster),
        });
    }

    [Fact]
    public void Speed_is_zero_for_static_targets_and_minus_one_for_unseen_dynamic()
    {
        var w = World();
        AddStation(w, 100, Sector, -1, (300f, 0f, 0f));
        AddAsteroid(w, 200, Sector, (400f, 0f, 0f));
        AddWarp(w, 300, Sector, (500f, 0f, 0f));

        // Static targets (c_mtStatic, consoledata.cpp:136-150) → 0.
        Assert.Equal(0f, TargetPaneModel.Build(w, IgcObjectType.Station, 100).Speed);
        Assert.Equal(0f, TargetPaneModel.Build(w, IgcObjectType.Asteroid, 200).Speed);
        Assert.Equal(0f, TargetPaneModel.Build(w, IgcObjectType.Warp, 300).Speed);

        // A visible dynamic ship → velocity magnitude.
        AddShip(w, 400, Sector, (600f, 0f, 0f), (30f, 0f, 0f), 1f, 1f);
        Assert.Equal(30f, TargetPaneModel.Build(w, IgcObjectType.Ship, 400).Speed);

        // Left view → unseen dynamic sentinel -1.
        w.Apply(ShipDelete.ForShipLeftSector(400));
        Assert.Equal(-1f, TargetPaneModel.Build(w, IgcObjectType.Ship, 400).Speed);

        // Never seen → -1 too.
        Assert.Equal(-1f, TargetPaneModel.Build(w, IgcObjectType.Ship, 999).Speed);
    }

    [Fact]
    public void Range_uses_same_cluster_separation_and_is_null_otherwise()
    {
        var w = World();
        AddStation(w, 100, Sector, -1, (300f, 0f, 0f));
        AddStation(w, 101, OtherSector, -1, (900f, 0f, 0f));

        var same = TargetPaneModel.Build(w, IgcObjectType.Station, 100);
        Assert.NotNull(same.Distance);
        Assert.Equal(300.0, same.Distance!.Value, 1);

        // The port has no native last-seen sector, so the -1 → LOCATION:
        // sector fallback stays unavailable (Distance stays null); the pane
        // still resolves the target's name.
        var other = TargetPaneModel.Build(w, IgcObjectType.Station, 101);
        Assert.Null(other.Distance);
        Assert.Equal("station 101", other.Name);
    }

    [Fact]
    public void Unseen_or_unlive_target_reads_zero_hull_and_shield()
    {
        var w = World();
        AddShip(w, 400, Sector, (600f, 0f, 0f), (30f, 0f, 0f), 0.8f, 0.6f);
        var visible = TargetPaneModel.Build(w, IgcObjectType.Ship, 400);
        Assert.Equal(0.8f, visible.HullFraction!.Value, 2);
        Assert.Equal(0.6f, visible.ShieldFraction!.Value, 2);

        w.Apply(ShipDelete.ForShipLeftSector(400));
        var gone = TargetPaneModel.Build(w, IgcObjectType.Ship, 400);
        Assert.Equal(0f, gone.HullFraction);
        Assert.Equal(0f, gone.ShieldFraction);
        Assert.Equal(-1f, gone.Speed);

        AddStation(w, 100, Sector, -1, (300f, 0f, 0f), 0.7f, 0.5f);
        var live = TargetPaneModel.Build(w, IgcObjectType.Station, 100);
        Assert.Equal(0.7f, live.HullFraction!.Value, 2);
        Assert.Equal(0.5f, live.ShieldFraction!.Value, 2);

        // A station with no STATIONS_UPDATE yet has no live fraction → 0/0.
        AddStation(w, 101, Sector, -1, (400f, 0f, 0f));
        var unlive = TargetPaneModel.Build(w, IgcObjectType.Station, 101);
        Assert.Equal(0f, unlive.HullFraction);
        Assert.Equal(0f, unlive.ShieldFraction);
    }

    [Fact]
    public void Side_colour_is_native_neutral_grey_or_side_rgb_times_hud_bright_at_half_alpha()
    {
        var w = World();
        w.Apply(new SideRoster
        {
            Sides = [new SideRoster.SideRecord(0, 1, 0, "Alpha", 1f, 0.5f, 0.25f, 1f)],
        });

        // Neutral (no side) → grey (0.5,0.5,0.5,0.5), never white
        // (WinTrek/WinTrek.cpp:8784-8786).
        AddStation(w, 100, Sector, -1, (300f, 0f, 0f));
        var neutral = TargetPaneModel.Build(w, IgcObjectType.Station, 100);
        AssertColor(0.5f, 0.5f, 0.5f, 0.5f, neutral.SideColor);
        Assert.Null(neutral.SideName);

        // Sided → side RGB × g_hudBright (0.85), alpha 0.5
        // (WinTrek/WinTrek.cpp:8784-8818).
        AddStation(w, 101, Sector, 0, (400f, 0f, 0f));
        var sided = TargetPaneModel.Build(w, IgcObjectType.Station, 101);
        AssertColor(0.85f, 0.425f, 0.2125f, 0.5f, sided.SideColor);
        Assert.Equal("Alpha", sided.SideName);
    }

    [Fact]
    public void Hull_and_station_shield_are_cluster_gated_with_the_docked_station_exception()
    {
        var w = World();

        // Same-cluster live station → live fractions.
        AddStation(w, 100, Sector, -1, (300f, 0f, 0f), 0.7f, 0.5f);
        var same = TargetPaneModel.Build(w, IgcObjectType.Station, 100);
        Assert.Equal(0.7f, same.HullFraction!.Value, 2);
        Assert.Equal(0.5f, same.ShieldFraction!.Value, 2);

        // Different cluster, not docked → 0/0 (consoledata.cpp:202-212, 272-276).
        AddStation(w, 101, OtherSector, -1, (900f, 0f, 0f), 0.7f, 0.5f);
        var other = TargetPaneModel.Build(w, IgcObjectType.Station, 101);
        Assert.Equal(0f, other.HullFraction);
        Assert.Equal(0f, other.ShieldFraction);

        // The docked station is native's exception even when its cluster differs.
        AddStation(w, 102, OtherSector, -1, (900f, 0f, 0f), 0.7f, 0.5f);
        w.Apply(new ShipStatusMessage { ShipId = 1, SectorId = Sector, StationId = 102, State = ShipStatusMessage.StateDocked });
        var docked = TargetPaneModel.Build(w, IgcObjectType.Station, 102);
        Assert.Equal(0.7f, docked.HullFraction!.Value, 2);
        Assert.Equal(0.5f, docked.ShieldFraction!.Value, 2);

        // A ship in another cluster: hull is cluster-gated to 0, while shield
        // and speed are not cluster-gated natively (consoledata.cpp:136-150,
        // 202-212 vs 252-270).
        var w2 = World();
        AddShip(w2, 400, OtherSector, (600f, 0f, 0f), (30f, 0f, 0f), 0.8f, 0.6f);
        var ship = TargetPaneModel.Build(w2, IgcObjectType.Ship, 400);
        Assert.Equal(0f, ship.HullFraction);
        Assert.Equal(0.6f, ship.ShieldFraction!.Value, 2);
        Assert.Equal(30f, ship.Speed);
    }

    private static void AssertColor(float r, float g, float b, float a, HudColor actual)
    {
        Assert.True(MathF.Abs(actual.R - r) < 1e-5f, $"R expected {r}, got {actual.R}");
        Assert.True(MathF.Abs(actual.G - g) < 1e-5f, $"G expected {g}, got {actual.G}");
        Assert.True(MathF.Abs(actual.B - b) < 1e-5f, $"B expected {b}, got {actual.B}");
        Assert.True(MathF.Abs(actual.A - a) < 1e-5f, $"A expected {a}, got {actual.A}");
    }
}
