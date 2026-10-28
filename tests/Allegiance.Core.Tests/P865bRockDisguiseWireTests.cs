using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

using Server = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

/// <summary>
/// P865b — SIM-0643: the consumed-rock disguise for sides that cannot see the
/// new station, the port of native BuildStation's #121 Imago blocks
/// (<c>FedSrv.CPP:11443-11454</c>, re-grepped) and its two consumption paths.
/// Native keeps such a side's rock on its client: the station record is tagged
/// with the asteroid's signature/position/radius/capabilities
/// (<c>SetRoidSig/Pos/Radius/Caps/Side</c>, <c>:11449-11453</c>) and the side
/// is sent NOTHING — its client keeps drawing the rock with the data it
/// originally received. The port is equivalent by construction: the consumed
/// rock's original record is retained (<see cref="Station.ConsumedAsteroid"/>),
/// the side lands in <see cref="Station.StaleRockSides"/>, and no wire message
/// about rock or station reaches it while it stays blind. The two native
/// consumption paths are ported: the per-tick zone-eye
/// (<c>clusterIGC.cpp:141-155</c> → <c>Sector.ResolveConsumedRockVisibility</c>
/// → side-scoped ASTEROID_DESTROYED) and the station-death retirement
/// (<c>FedSrv.CPP:12384-12389</c> → <c>ServerHost.KillStationEvent</c>), and a
/// side that first SEES the station gets native's show-time pair — the silent
/// rock pop riding the station EXPORT (FedSrv.CPP:11035-11062 ShowObject, via
/// <c>ExportDiscoveredStations</c>).
/// These are the wire-level, per-side pins: every assertion is read off one
/// seated peer's captured publications. Detection radii (P132 math,
/// <c>VisibilityMath.DetectionRadius</c> = scanner + target + range·sig, with
/// the observer hull's 10,000 scanner range) are tuned so exactly the intended
/// side is in range: the blind sides sit at 30 km (out of every radius here),
/// the moved observer at 2–3 km.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P865bRockDisguiseWireTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm
            {
                Name = "P865b Rock Disguise", Map = "XXV_simple.igc", Teams = 3,
                CivIds = [1, 3, 4], SideNames = ["Alpha", "Bravo", "Charlie"],
            },
        ],
    };

    private static DataAsteroidIGC MkRock(short id, Vector3 pos, short radius, short sectorId, short aabmCap,
        float signature) => new(
        Signature: signature, Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: 0f, OreMax: 0f, AabmCapabilities: aabmCap,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    private static Ship SeatShip(Mission mission, short id, short? side, Vector3 position, DataHullTypeIGC hull)
    {
        var ship = new Ship(id, hull, position, Vector3.Zero, Orientation.Identity) { SideId = side };
        mission.PlaceShip(ship, mission.Sectors[0]);
        return ship;
    }

    private static List<(ConnectionId Conn, ushort Fmid)> Capture(Server server)
    {
        var sent = new List<(ConnectionId, ushort)>();
        server.TestPublicationCapture = (peer, bytes, _, _) =>
        {
            foreach (var m in FedPacket.Unpack(bytes)) sent.Add((peer, (ushort)(m[2] | m[3] << 8)));
        };
        return sent;
    }


    /// <summary>The sector holds 321 authored map asteroids, warps and the home
    /// station — a hand-picked offset lands inside one and the parked hull grinds
    /// to death on collision damage. Pick a point at <paramref name="dist"/> from
    /// <paramref name="center"/> that is clear of every rock/warp/station and at
    /// least 1000 u from <paramref name="avoid"/>.</summary>
    private static Vector3 ClearSpot(Sector sector, Vector3 center, float dist, Vector3 avoid)
    {
        for (int a = 0; a < 36; a++)
        {
            float ang = a * 10f * MathF.PI / 180f;
            var p = center + new Vector3(MathF.Cos(ang) * dist, 0f, MathF.Sin(ang) * dist);
            if ((p - avoid).Length() < 1000f) continue;
            if (Blocked(sector, p)) continue;
            return p;
        }
        throw new InvalidOperationException("no clear spot found");
    }

    private static bool Blocked(Sector sector, Vector3 p)
    {
        foreach (var m in sector.Mineable)
            if (m.Data.Position is { Length: 3 } q && (new Vector3(q[0], q[1], q[2]) - p).Length() < m.Radius + 300f) return true;
        foreach (var wp in sector.Warps)
            if ((wp.Position - p).Length() < 500f) return true;
        foreach (var st in sector.StationRuntimes)
            if ((st.Center - p).Length() < st.Radius + 500f) return true;
        return false;
    }

    /// <summary>Whether the segment from <paramref name="from"/> to
    /// <paramref name="to"/> passes within rock+50 of any authored asteroid (the
    /// sweep's line-of-sight occluders) — so a moved observer's spot has a clean
    /// line to the station.</summary>
    private static bool LineBlocked(Sector sector, Vector3 from, Vector3 to)
    {
        var d = to - from;
        float len2 = d.LengthSquared();
        foreach (var m in sector.Mineable)
        {
            if (m.Data.Position is not { Length: 3 } q) continue;
            var c = new Vector3(q[0], q[1], q[2]);
            float t = len2 == 0f ? 0f : Math.Clamp(((c - from) * d) / len2, 0f, 1f);
            var closest = from + d * t;
            if (t < 0.999f && (closest - c).Length() < m.Radius + 50f) return true;
        }
        return false;
    }

    /// <summary>First clear point at <paramref name="dist"/> from
    /// <paramref name="center"/> with an unobstructed line to it.</summary>
    private static Vector3 ClearLineSpot(Sector sector, Vector3 center, float dist)
    {
        for (int a = 0; a < 36; a++)
        {
            float ang = a * 10f * MathF.PI / 180f;
            var p = center + new Vector3(MathF.Cos(ang) * dist, 0f, MathF.Sin(ang) * dist);
            if (!Blocked(sector, p) && !LineBlocked(sector, p, center)) return p;
        }
        throw new InvalidOperationException("no clear line spot found");
    }

    private sealed class World
    {
        public Server Server = null!;
        public Mission Mission = null!;
        public Sector Sector = null!;
        public Asteroid Rock = null!;
        public Station Station = null!;
        public ConnectionId OwnerConn, BlindConn, FarConn;
        public Ship BlindObserver = null!, FarObserver = null!;

        /// <summary>Boot + build a station with two DISGUISED sides: side 1's and
        /// side 2's clients hold the rock (it was made side-visible before the
        /// build, simulating an earlier discovery) but have no current eye on it
        /// and saw nothing of the build — native's disguise branch. Both sides'
        /// peers sit 30 km away along DIFFERENT axes (never co-located: two
        /// overlapping hulls grind each other down with collision damage). The
        /// signatures are tuned so nothing at 30 km is in detection range while
        /// the moved 2–3 km observer is: rock 0.05 → 565 u, station 0.5 → 5307 u
        /// (blind) or rock 1.0 → 10,065 u (zone-eye in range).</summary>
        public static World Build(float rockSignature = 0.05f, float stationSignature = 0.5f)
        {
            var w = new World();
            w.Server = new Server();
            w.Server.Boot(Config());
            var mission = w.Mission = w.Server.TestMission!;
            mission.Stage = MissionStage.Started;

            var core = mission.Core;
            var sector = w.Sector = mission.Sectors[0];
            foreach (var side in mission.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;
            var observerHull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First()
                with { ScannerRange = 10_000f, Length = 10f, Signature = 1f };

            var rockPos = new Vector3(511f, 823f, 604f);
            // A terminal station type (no successor): the build must use exactly
            // this type, at the tuned signature.
            var stationType = core.StationTypes.Values
                .First(st => st.SuccessorStationTypeId < 0) with { Signature = stationSignature };
            w.Rock = new Asteroid(MkRock(7701, rockPos, 60, sector.Id, stationType.AabmBuild, rockSignature));
            mission.PlaceAsteroid(w.Rock, sector);

            // Both blind sides' clients hold the rock from an earlier discovery
            // (side-visible, no current eye — the seed sweep below drops the eye
            // for the out-of-range observers and the predictable latch keeps the
            // rock known, exactly a side that saw the rock before the build).
            w.Rock.SideVisibility.SetSideVisibility(1, true);
            w.Rock.SideVisibility.SetSideVisibility(2, true);

            // A near-invisible builder: the blind observers (10,000 scanner
            // range) must NOT see the construction — native's pbseensides /
            // GetBuilderSeenSide legs would inform them and kill the disguise.
            var builderHull = core.HullTypes.Values.OrderBy(h => h.HullId).First() with { Signature = 0.001f };
            var builder = new Ship(5601, builderHull, rockPos + new Vector3(0f, 0f, 800f), Vector3.Zero, Orientation.Identity)
            { SideId = 0 };
            mission.PlaceShip(builder, sector);

            var ownerShip = SeatShip(mission, 9000, 0, rockPos + new Vector3(0f, 0f, 500f), observerHull);
            var blindSpot = ClearSpot(sector, rockPos, 15_000f, rockPos);
            var farSpot = ClearSpot(sector, rockPos, 15_000f, blindSpot);
            w.BlindObserver = SeatShip(mission, 9001, 2, blindSpot, observerHull);
            w.FarObserver = SeatShip(mission, 9002, 1, farSpot, observerHull);
            w.OwnerConn = new(900);
            w.BlindConn = new(901);
            w.FarConn = new(902);
            w.Server.TestSeedPeers([
                (w.OwnerConn, ownerShip, sector),
                (w.BlindConn, w.BlindObserver, sector),
                (w.FarConn, w.FarObserver, sector),
            ]);

            mission.SeedStartingVisibility();
            Assert.False(w.Rock.GetCurrentEye(1), "side 1 must have no current eye on the rock");
            Assert.False(w.Rock.GetCurrentEye(2), "side 2 must have no current eye on the rock");

            // The build completes on the SERVER clock: pumping TickSim keeps the
            // mission clock and the SimPass clock in lockstep, so the visibility
            // sweep and BroadcastStationsUpdate both run throughout (mixing a
            // direct mission.Update far ahead of _simNow would make every later
            // SimPass update throw "mission clock moved backwards").
            sector.StartBuildingEffect(builder, w.Rock, stationType, mission.LastUpdate);
            int guard = 0;
            while (w.Rock.ConstructedStation is null && guard++ < 1200) w.Server.TickSim();
            w.Station = w.Rock.ConstructedStation!;
            Assert.NotNull(w.Station);
            Assert.False(ownerShip.Dead, "the owner observer must survive the build");
            Assert.False(w.BlindObserver.Dead, "the side-2 observer must survive the build");
            Assert.False(w.FarObserver.Dead, "the side-1 observer must survive the build");
            Assert.Contains((short)1, w.Station.StaleRockSides);
            Assert.Contains((short)2, w.Station.StaleRockSides);
            Assert.False(w.Station.SeenBySide(1));
            Assert.False(w.Station.SeenBySide(2));
            return w;
        }
    }

    private const ushort FmidAsteroidDestroyed = AsteroidDestroyed.Fmid;
    private const ushort FmidExportStation = ExportStation.Fmid;
    private const ushort FmidObjectSpotted = ObjectSpotted.Fmid;

    private static bool Has(List<(ConnectionId Conn, ushort Fmid)> s, ConnectionId c, ushort fmid) =>
        s.Any(x => x.Conn == c && x.Fmid == fmid);

    /// <summary>⭐ SIM-0643 — the disguise holds: while a side that holds the
    /// rock cannot see the station and cannot eye the zone, the wire carries
    /// NOTHING to it — no ASTEROID_DESTROYED, no EXPORT_STATION — so its client
    /// keeps drawing the rock with the ORIGINAL data it received at discovery
    /// (native SetRoidSig/Pos/Radius/Caps + SetRoidSide, FedSrv.CPP:11449-11453:
    /// the disguise is the absence of any update, backed by the retained
    /// ConsumedAsteroid record). The other blind side is equally quiet.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_blind_side_that_holds_the_rock_receives_nothing_while_disguised()
    {
        Require.Artwork();
        var w = World.Build();
        using var __ = w.Server;
        var sent = Capture(w.Server);

        for (int i = 0; i < 30; i++) w.Server.TickSim();

        Assert.False(Has(sent, w.BlindConn, FmidAsteroidDestroyed), "the disguised side must NOT receive a rock kill");
        Assert.False(Has(sent, w.BlindConn, FmidExportStation), "the disguised side must NOT receive the station");
        Assert.False(Has(sent, w.BlindConn, FmidObjectSpotted), "the disguised side must NOT receive the spotted notice");
        Assert.False(Has(sent, w.FarConn, FmidAsteroidDestroyed), "the other blind side must NOT receive a rock kill");
        Assert.False(Has(sent, w.FarConn, FmidExportStation), "the other blind side must NOT receive the station");
        // The disguise is backed by the retained original rock record: the
        // station still knows the consumed asteroid (its signature/position/
        // radius are what the zone-eye and the retirement read), and both blind
        // sides are still latched.
        Assert.Same(w.Rock, w.Station.ConsumedAsteroid);
        Assert.Equal(0.05f, w.Station.ConsumedAsteroid!.Data.Signature);
        Assert.Equal(2, w.Station.StaleRockSides.Count);
    }

    /// <summary>⭐ SIM-0643 — on FIRST SIGHT of the station, a disguised side's
    /// silent rock pop rides the station EXPORT: ASTEROID_DESTROYED +
    /// EXPORT_STATION + OBJECT_SPOTTED reach that side (FedSrv.CPP:11035-11062
    /// via ExportDiscoveredStations), and only that side — the other blind side,
    /// which still cannot see the station, stays quiet and keeps its rock. The
    /// rock's 0.05 signature keeps the zone-eye out of range at 3 km, so the
    /// kill genuinely rides the show packet.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void First_sight_of_the_station_kills_the_rock_and_exports_the_station()
    {
        Require.Artwork();
        var w = World.Build();
        using var __ = w.Server;
        var sent = Capture(w.Server);

        // Side 2's observer flies to scanning range of the station: visibility
        // flips, the per-tick export reaches its side.
        w.BlindObserver.Position = ClearLineSpot(w.Sector, w.Station.Center, 3000f);
        for (int i = 0; i < 30; i++)
        {
            w.Server.TickSim();
        }

        Assert.True(w.Station.SeenBySide(2), "the 3 km observer must see the station");
        Assert.True(Has(sent, w.BlindConn, FmidAsteroidDestroyed), "first sight must retire the disguised rock");
        Assert.True(Has(sent, w.BlindConn, FmidExportStation), "first sight must export the station");
        Assert.True(Has(sent, w.BlindConn, FmidObjectSpotted), "first sight must announce the station (OBJECT_SPOTTED)");
        Assert.DoesNotContain((short)2, w.Station.StaleRockSides);   // the latch cleared after the send
        // The other blind side never saw anything: its rock stays.
        Assert.False(Has(sent, w.FarConn, FmidAsteroidDestroyed));
        Assert.False(Has(sent, w.FarConn, FmidExportStation));
        Assert.Contains((short)1, w.Station.StaleRockSides);
    }

    /// <summary>SIM-0643 — the per-tick zone-eye consumption
    /// (clusterIGC.cpp:141-155): a disguised side whose scanner eyes WHERE THE
    /// ROCK WOULD BE (the retained original signature/position/radius) gets a
    /// side-scoped ASTEROID_DESTROYED and nothing else — the rock is gone, the
    /// station is still not exported (SimPass's station export never ran in
    /// this test's mission.Update-only pumping). Wire-level, per side.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Zoning_the_rock_position_kills_only_that_sides_rock()
    {
        Require.Artwork();
        var w = World.Build(rockSignature: 1.0f);   // zone detection radius 10,065 u: 2 km eyed, 15 km not
        using var __ = w.Server;
        var sent = Capture(w.Server);

        w.BlindObserver.Position = ClearLineSpot(w.Sector, w.Station.Center, 2000f);
        // mission.Update only — no SimPass — so the station export (a SimPass
        // broadcast) cannot fire and the rock-kill is provably the zone-eye's.
        for (int i = 0; i < 10; i++) w.Mission.Update(w.Mission.LastUpdate + 0.05);

        Assert.True(Has(sent, w.BlindConn, FmidAsteroidDestroyed), "the zone-eye must retire the disguised rock");
        Assert.False(Has(sent, w.BlindConn, FmidExportStation), "the station export never ran in this test — rock-kill only");
        Assert.DoesNotContain((short)2, w.Station.StaleRockSides);
        // Side 1's observer is still 30 km away: its rock stays.
        Assert.False(Has(sent, w.FarConn, FmidAsteroidDestroyed));
        Assert.Contains((short)1, w.Station.StaleRockSides);
    }

    /// <summary>SIM-0643 — the station-death retirement (FedSrv.CPP:12384-12389):
    /// when the station dies, every still-disguised side's rock is retired with
    /// a side-scoped ASTEROID_DESTROYED — no ghost rock survives the station.
    /// Wire-level, per side.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Killing_the_station_retires_the_disguised_rocks()
    {
        Require.Artwork();
        var w = World.Build();
        using var __ = w.Server;
        var sent = Capture(w.Server);

        ((IIgcSite)w.Server.Host).KillStationEvent(w.Station, null!, 1f);

        Assert.True(Has(sent, w.BlindConn, FmidAsteroidDestroyed), "the disguised side's rock must be retired on station death");
        Assert.True(Has(sent, w.FarConn, FmidAsteroidDestroyed), "the other disguised side's rock must be retired too");
        Assert.Empty(w.Station.StaleRockSides);
    }
}
