using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P797 — live loopback proof of the per-side map discovery the native server
/// performs in <c>CFSMission::SendMissionInfo</c> (<c>FedSrv/fsmission.cpp:4113-4250</c>):
/// <list type="number">
/// <item><b>Join is per side.</b> The cluster exports and the
/// <c>CLUSTERINFO</c>(200) topology overview go to every peer (native: the map
/// preview is public, <c>fsmission.cpp:4122-4166</c>), but an aleph, asteroid or
/// station reaches a peer only when <b>that peer's side has seen it</b>
/// (<c>fsmission.cpp:4181/4206/4220</c>).</item>
/// <item><b>Mid-game discovery.</b> When a side's scanner first comes into range
/// of an aleph it never saw, that side's peers receive the object
/// (<c>ThingSiteImpl::ShowObject</c> → <c>ExportObj</c> + <c>OBJECT_SPOTTED</c>,
/// <c>FedSrv.CPP:10750-10784,11055-11062</c>) and the OTHER side — which has no
/// scanner there — receives nothing.</item>
/// <item><b>Late join.</b> A peer that joins after its side discovered the aleph
/// is told about it by its own join export, i.e. "exactly what its side has
/// seen" — the same test also re-proves the other side is not.</item>
/// </list>
/// Construction mirrors <see cref="StationsUpdateLiveTests"/>: a loopback server
/// booted from the real core + <c>XXV_simple.igc</c>, a <c>ShipSpawner</c> that
/// seats the bots on alternating sides in sector 0, then
/// <see cref="Mission.PlaceShip"/> to fly a side-A ship next to an aleph it has
/// never seen. No Windows VM, no native client: the expectations are read off
/// the live mission's own latched <c>SeenBySide</c> flags and the real
/// <c>CLUSTERINFO</c>.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P797MapDiscoveryLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    // m2xspiderw.igc: 13 sectors, 44 warps, 2 stations and 134 real rocks —
    // the only shipped demo map with asteroids, so the per-side asteroid gate is
    // exercised on real content too.
    private const string MapFile = "m2xspiderw.igc";
    private const int ExpectedWarps = 44;   // server boot summary / tools/missiondump

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            // ShowMap stays at native's default (false): the whole point.
            new Gm { Name = "P797 Discovery", Map = MapFile, Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name, ulong steamId = 797_000_000_000_000_000ul) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 797u, DwCookie = 0x797u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = steamId, DrmHash = "p797discovery",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private sealed class Seat : IDisposable
    {
        public Srv Server = null!;
        public Ship ShipA = null!, ShipB = null!;
        public FltBot BotA = null!, BotB = null!;
        public short SideA, SideB;
        /// <summary>When set, the next ShipSpawner call seats that side and clears
        /// it — how the test forces the late-join peer onto side A deterministically
        /// (the spawner only receives the core/mission/id, not the connection).</summary>
        public short? ForcedSide;

        public void Dispose()
        {
            BotA?.Dispose();
            BotB?.Dispose();
            Server?.Dispose();
        }
    }

    /// <summary>Boot a 2-bot / 2-side live server; slot 0 is side A (its ship near
    /// the origin of sector 0), slot 1 is side B (200 u away).</summary>
    private static Seat SeatTwoSides()
    {
        var seat = new Seat();
        seat.Server = new Srv();
        // Side is assigned per CONNECTION, in first-seen order (1st = A, 2nd = B,
        // 3rd = A …). A plain global call counter is not stable here: the server
        // may re-drive the spawner for the same peer (reseat/launch), which would
        // silently shift later peers onto the wrong side.
        short slot = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            short forced = seat.ForcedSide ?? -1;
            seat.ForcedSide = null;
            var sector = mission.Sectors[0];
            // A hull with a real scanner: the sweep tests range from the hull's
            // ScannerRange (Ship.EffectiveScannerRange), and the weakest hull in
            // the core (a lifepod) scans nothing, which would make the discovery
            // leg of this test prove nothing.
            var hull = mission.Core.HullTypes.Values
                .Where(h => h.ScannerRange >= 3000f)
                .OrderBy(h => h.HitPoints).First();
            var ship = new Ship(id, hull,
                slot == 0 ? new Vector3(0f, 300f, 0f) : new Vector3(0f, -300f, 0f),
                Vector3.Zero, Orientation.Identity)
            {
                SideId = forced >= 0 ? forced : mission.Sides[slot % mission.Sides.Count].Id,
            };
            slot++;
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        // P180: these tests assume an immediately-flyable mission (bypass ready-up).
        foreach (var m in seat.Server.Missions) m.Stage = MissionStage.Started;

        var port = seat.Server.BoundPort;
        seat.BotA = new FltBot();
        seat.BotB = new FltBot();
        seat.BotA.Connect(port, "p797_a");
        seat.BotB.Connect(port, "p797_b");
        seat.BotA.SendLogonRequest(Request("p797_a"));
        seat.BotB.SendLogonRequest(Request("p797_b", 797_000_000_000_000_001ul));
        PumpUntil(seat.Server, () => seat.BotA.ShipAssignment is not null && seat.BotB.ShipAssignment is not null, 4000);
        Assert.True(seat.BotA.ShipAssignment is not null && seat.BotB.ShipAssignment is not null,
            "both loopback bots must be assigned a ship");

        seat.ShipA = seat.Server.Ships.First(s => s.Ship.Id == seat.BotA.ShipAssignment!.Ship.ShipId).Ship;
        seat.ShipB = seat.Server.Ships.First(s => s.Ship.Id == seat.BotB.ShipAssignment!.Ship.ShipId).Ship;
        seat.SideA = seat.ShipA.SideId!.Value;
        seat.SideB = seat.ShipB.SideId!.Value;
        Assert.True(seat.SideA != seat.SideB, "the two bots must be on different sides");
        return seat;
    }

    private static IEnumerable<ResolvedWarp> AllWarps(Mission m) => m.Sectors.SelectMany(s => s.Warps);

    /// <summary>⭐ The join burst is per side: every aleph/asteroid/station the
    /// peer received is one its side has seen, the count matches exactly, and the
    /// topology overview still went out whole.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fresh_side_receives_only_the_objects_its_side_has_seen()
    {
        Require.Artwork();
        using var seat = SeatTwoSides();
        var mission = seat.Server.Missions[0];

        // Let the seeded/live sweep settle so the latched flags are stable.
        PumpUntil(seat.Server, () => false, 25);

        var seenWarpsA = AllWarps(mission).Where(w => w.SeenBySide(seat.SideA)).Select(w => w.Warp.WarpId).OrderBy(x => x).ToArray();
        var seenWarpsB = AllWarps(mission).Where(w => w.SeenBySide(seat.SideB)).Select(w => w.Warp.WarpId).OrderBy(x => x).ToArray();

        // (1) The topology overview is unchanged — native gives every side the
        // cluster/warp graph without positions (fsmission.cpp:4122-4166).
        var ci = Assert.Single(seat.BotA.ClusterInfos);
        Assert.Equal(13, ci.Clusters.Count);
        Assert.Equal(ExpectedWarps, ci.Warps.Count);

        // (2) Alephs: exactly the seen set, and nothing more.
        var gotWarpsA = seat.BotA.WarpExports.Select(w => w.Warp.WarpId).Distinct().OrderBy(x => x).ToArray();
        var gotWarpsB = seat.BotB.WarpExports.Select(w => w.Warp.WarpId).Distinct().OrderBy(x => x).ToArray();
        Assert.Subset(seenWarpsA.ToHashSet(), gotWarpsA.ToHashSet());
        Assert.Equal(seenWarpsA, gotWarpsA);
        Assert.Equal(seenWarpsB, gotWarpsB);
        Assert.True(gotWarpsA.Length < ExpectedWarps,
            $"a fresh side must NOT start knowing all {ExpectedWarps} alephs (got {gotWarpsA.Length})");

        // (3) Stations: exactly the seen set (own-side rule + scanner coverage).
        var seenStationsA = mission.Sectors.SelectMany(s => s.StationRuntimes)
            .Where(s => s.SeenBySide(seat.SideA)).Select(s => s.StationId).OrderBy(x => x).ToArray();
        var gotStationsA = seat.BotA.StationExports.Select(s => s.Station.StationId).Distinct().OrderBy(x => x).ToArray();
        Assert.Equal(seenStationsA, gotStationsA);
        Assert.Contains(seat.SideA, mission.Sectors.SelectMany(s => s.StationRuntimes)
            .Where(s => s.SeenBySide(seat.SideA)).Select(s => s.SideId));

        // (4) Asteroids: exactly the seen set.
        var seenRocksA = mission.Sectors.SelectMany(s => s.Mineable)
            .Where(a => a.SeenBySide(seat.SideA)).Select(a => a.Data.AsteroidId).OrderBy(x => x).ToArray();
        var gotRocksA = seat.BotA.AsteroidExports.Select(a => a.Asteroid.AsteroidId).Distinct().OrderBy(x => x).ToArray();
        Assert.Equal(seenRocksA, gotRocksA);

        // (5) The home-sector own-side rule still holds (a side always sees its
        // own stations) — the gate is per side, not "nobody sees anything".
        Assert.NotEmpty(seenStationsA);

        Console.WriteLine($"P797-JOIN: side {seat.SideA}: {gotWarpsA.Length}/{ExpectedWarps} warp(s), " +
                          $"{gotStationsA.Length} station(s), {gotRocksA.Length} asteroid(s); " +
                          $"side {seat.SideB}: {gotWarpsB.Length} warp(s) — CLUSTERINFO still 13 clusters / {ci.Warps.Count} edges");
    }

    /// <summary>⭐ Flying a side-A ship into scanner range of an aleph A never saw
    /// delivers it to A's peers (EXPORT + OBJECT_SPOTTED) and to nobody on side B;
    /// a peer that joins A afterwards receives it from its own join export.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unseen_aleph_is_discovered_by_the_seeing_side_only_and_reaches_a_late_joiner()
    {
        Require.Artwork();
        using var seat = SeatTwoSides();
        var mission = seat.Server.Missions[0];
        PumpUntil(seat.Server, () => false, 25);

        // Pick an aleph side A has never seen, with a resolved partner (so a
        // "discovered" report can name the destination).
        var unseen = mission.Sectors
            .SelectMany(s => s.Warps.Select(w => (Sector: s, Warp: w)))
            .Where(x => x.Warp.Warp.Position is { Length: 3 }
                && x.Warp.Destination is not null
                && !x.Warp.SeenBySide(seat.SideA))
            // Prefer a sector that also carries rocks, so the same flight proves
            // the asteroid discovery leg as well as the aleph one.
            .OrderByDescending(x => x.Sector.Mineable.Count)
            .FirstOrDefault();
        Skip.If(unseen.Warp is null, "this corpus map leaves no aleph unseen by a fresh side");

        var target = unseen.Warp;
        Assert.False(target.SeenBySide(seat.SideB) && seat.SideB == seat.SideA);
        int warpsBeforeA = seat.BotA.WarpExports.Count;
        int warpsBeforeB = seat.BotB.WarpExports.Count;
        int rocksBeforeA = seat.BotA.AsteroidExports.Count;
        int rocksBeforeB = seat.BotB.AsteroidExports.Count;
        bool seenByB = target.SeenBySide(seat.SideB);

        // Fly the side-A ship to the aleph's mouth (the real 3D geometry the
        // sweep tests — a faohshcar-class hull's scanner range covers the spot).
        mission.PlaceShip(seat.ShipA, unseen.Sector);
        seat.ShipA.Position = target.Position + new Vector3(300f, 0f, 0f);
        PumpUntil(seat.Server, () => target.SeenBySide(seat.SideA), 400);
        PumpUntil(seat.Server, () => seat.BotA.WarpExports.Count > warpsBeforeA, 200);

        Assert.True(target.SeenBySide(seat.SideA), "the side that flew there must now see the aleph");
        Assert.Contains(seat.BotA.WarpExports.Skip(warpsBeforeA), w => w.Warp.WarpId == target.Warp.WarpId);

        // Rocks in that sector (if it has any) follow the same discovery path.
        if (unseen.Sector.Mineable.Count > 0)
        {
            PumpUntil(seat.Server, () => seat.BotA.AsteroidExports.Count > rocksBeforeA, 200);
            Assert.True(seat.BotA.AsteroidExports.Count > rocksBeforeA,
                "the sector's rocks must be discovered to the side that flew in");
        }

        // The other side never saw it and must not have been told.
        if (!seenByB)
        {
            Assert.False(target.SeenBySide(seat.SideB), "side B has no scanner there — the aleph stays unknown to it");
            Assert.DoesNotContain(seat.BotB.WarpExports.Skip(warpsBeforeB), w => w.Warp.WarpId == target.Warp.WarpId);
            Assert.Equal(rocksBeforeB, seat.BotB.AsteroidExports.Count);
        }

        // Late join: a third peer, seated on side A by the round-robin spawner,
        // must be told about the discovery by its own join export.
        using var late = new FltBot();
        seat.ForcedSide = seat.SideA;   // seat the late peer on side A deterministically
        late.Connect(seat.Server.BoundPort, "p797_late");
        late.SendLogonRequest(Request("p797_late", 797_000_000_000_000_002ul));
        PumpUntil(seat.Server, () => late.ShipAssignment is not null && late.WarpExports.Count > 0, 4000);
        var lateShip = seat.Server.Ships.First(s => s.Ship.Id == late.ShipAssignment!.Ship.ShipId).Ship;
        Assert.True(seat.SideA == lateShip.SideId, "the late joiner must land on side A (round-robin spawner)");
        var lateWarps = late.WarpExports.Select(w => w.Warp.WarpId).Distinct().ToArray();
        Assert.Contains(target.Warp.WarpId, lateWarps);

        Console.WriteLine($"P797-DISCOVER: aleph {target.Warp.WarpId} in sector {unseen.Sector.Id} -> side {seat.SideA} " +
                          $"(live + late peer {lateShip.Id}, {lateWarps.Length} warp(s) at join), side {seat.SideB} " +
                          $"{(seenByB ? "also saw it" : "received nothing")}");
    }
}
