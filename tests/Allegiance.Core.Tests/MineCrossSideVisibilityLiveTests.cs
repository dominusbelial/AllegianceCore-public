using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P453 — live loopback proof that an ENEMY side can now see a minefield the
/// way native intends: the P132 cross-side visibility sweep scores mines as
/// side-visible targets, and once a side's scanner net spots a foreign field it
/// receives the <c>EXPORT(26)/OT_mine</c> exactly once — then, on expiry, the
/// <c>MINE_DESTROYED(84)</c> so no phantom field lingers. This is the inverse
/// of <see cref="MineBroadcastTests"/>, which pinned the OLD same-side-only
/// behaviour (a cross-side enemy received neither message).
/// <para>The two loopback clients are on DIFFERENT sides in the SAME sector. A
/// (side 0) lays a mine; B (side 1) starts far out of scanner range, so its
/// mine table is empty; B is then flown into scanner range of the field and
/// receives the EXPORT once (the loopback client's <c>MineExports</c> — the raw
/// wire record that feeds <c>ClientWorld.Mines</c>, which the
/// <c>ClientWorldMineTests</c> unit tests pin at the table layer). The mine is
/// then pumped past its lifespan and B receives the destroy.</para>
/// <para>Before this fix the in-range EXPORT step fails: B never receives the
/// mine (same-side-only export), so the assertion that it appears exactly once
/// in B's world is red on the unfixed code (recorded in the report).</para>
/// </summary>
[Collection("LiveNetwork")]
public class MineCrossSideVisibilityLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Mine Cross-Side Visibility", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 853u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 88000000000000001ul, DrmHash = "minexvis",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A real mine type with a lifespan in [2 s, 8 s) — long enough to
    /// pump the creation/detection, then pump past expiry, without racing.</summary>
    private static DataMineTypeIGC RealMineType(StaticCore core)
    {
        var t = core.MineTypes.Values
            .Where(m => m.Lifespan >= 2.0f && m.Lifespan < 8.0f)
            .OrderBy(m => m.Lifespan)
            .FirstOrDefault();
        if (t.ExpendableTypeId == 0 && core.MineTypes.Count > 0)
            throw new InvalidOperationException("representative core has no mine with 2<=lifespan<8");
        return t;
    }

    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship A = null!, B = null!;
        public FltBot ABot = null!, BBot = null!;
        public Sector Sector = null!;
        public Vector3 Base;                 // a clean base point far from occluders/scanners
        public bool BaseComputed;
        public CoreConstantOverride? UniverseRadius;

        public void Dispose()
        {
            ABot?.Dispose();
            BBot?.Dispose();
            UniverseRadius?.Dispose();
            Server?.Dispose();
        }

        /// <summary>A clean base 10000 units beyond the sector's max asteroid /
        /// station X, so no ambient occluder or real scanner can skew detection
        /// and the scanner→mine ray at Base+300 is unobstructed.</summary>
        public Vector3 ComputeBase(Mission mission)
        {
            var sector = mission.Sectors[0];
            var content = new List<Vector3>();
            foreach (var a in sector.Asteroids)
                if (a.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));
            foreach (var st in sector.Stations)
                if (st.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));
            float cy = content.Count == 0 ? 0f : content.Average(v => v.Y);
            float cz = content.Count == 0 ? 0f : content.Average(v => v.Z);
            float maxX = content.Count == 0 ? 0f : content.Max(v => v.X);
            return new Vector3(maxX + 10000f, cy, cz);
        }
    }

    private static Seated SeatPair()
    {
        var seat = new Seated();
        seat.Server = new Srv();

        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            if (!seat.BaseComputed) { seat.Base = seat.ComputeBase(mission); seat.BaseComputed = true; }
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            // A on side 0 at the clean base; B on side 1 far away (same sector),
            // so initially no cross-side visibility exists.
            var pos = placed == 0 ? seat.Base : seat.Base + new Vector3(900_000f, 0f, 0f);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity)
            {
                SideId = mission.Sides[placed % mission.Sides.Count].Id,
            };
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };

        seat.Server.Boot(Config());
        // This visibility fixture deliberately separates peers by 900,000 units.
        // Expand the loaded universe instead of exposing them to OOB damage.
        seat.UniverseRadius = new CoreConstantOverride(seat.Server.Missions[0], 1, 1_000_000f);
        foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;  // sweep only runs Started/Starting

        var port = seat.Server.BoundPort;
        var bots = new[] { new FltBot(), new FltBot() };
        string[] names = { "mxv_a", "mxv_b" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "both bots must be assigned a ship");

            for (int i = 0; i < bots.Length; i++)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bots[i].ShipAssignment!.Ship.ShipId).Ship;
                if (i == 0) { seat.A = ship; seat.ABot = bots[i]; }
                else { seat.B = ship; seat.BBot = bots[i]; }
            }
            Assert.NotNull(seat.A); Assert.NotNull(seat.B);
            Assert.NotEqual(seat.A.SideId, seat.B.SideId);
            // Concurrent joins need not follow bot-array order. Establish the
            // named roles' geometry after resolving their real assignments.
            seat.A.Position = seat.Base;
            seat.B.Position = seat.Base + new Vector3(900_000f, 0f, 0f);
            seat.A.Velocity = Vector3.Zero;
            seat.B.Velocity = Vector3.Zero;
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.UniverseRadius?.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    /// <summary>⭐ The full P453 wire story, end to end:
    /// <list type="number">
    /// <item>With B far out of scanner range, A's freshly-laid mine does NOT
    /// reach B — <c>BBot.MineExports</c> is empty (the enemy starts unaware).</item>
    /// <item>Fly B into scanner range: B receives the EXPORT(26)/OT_mine exactly
    /// once, with the real mine id, the OWNER side (A's side) on the wire, the
    /// right cluster, and the real mine type — i.e. the field appears in B's
    /// world (the loopback record that feeds <c>ClientWorld.Mines</c>). The
    /// per-(peer, mine) latch keeps it at exactly one as ticks continue.</item>
    /// <item>Pump past the mine's lifespan: B receives the MINE_DESTROYED(84)
    /// naming that same mine — the field leaves B's world (no phantom).</item>
    /// </list></summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Enemy_ship_spots_a_laid_mine_once_in_range_and_learns_its_destruction()
    {
        Require.Artwork();
        using var seat = SeatPair();

        var mission = seat.A.Mission ?? throw new InvalidOperationException("layer must have a mission");
        var mType = RealMineType(mission.Core);

        // A lays a mine at A's position (the clean base).
        double now = mission.LastUpdate;
        var mine = seat.A.DeployMine(mType, now);
        short mineId = mine.Id;

        // (1) B is far out of scanner range -> B has NOT seen the mine. Pump a
        //     little so any erroneous broadcast would have arrived.
        PumpUntil(seat.Server, () => false, 60);
        Assert.DoesNotContain(seat.BBot.MineExports, e => e.Mine.MineId == mineId);
        Assert.True(seat.BBot.MineExports.Count(e => e.Mine.MineId == mineId) == 0,
            "B, out of scanner range, must not yet hold the mine");

        // (2) Fly B into scanner range of the field (300 u, within the large
        //     scanner hull's detection radius at the clean base, clear LOS).
        seat.B.Velocity = Vector3.Zero;
        seat.B.Position = seat.Base + new Vector3(300f, 0f, 0f);
        PumpUntil(seat.Server, () => seat.BBot.MineExports.Any(e => e.Mine.MineId == mineId), 20000);
        var inRange = seat.BBot.MineExports.Where(e => e.Mine.MineId == mineId).ToList();
        Assert.True(inRange.Count >= 1, "B must receive the mine's EXPORT once its scanner net spots it");

        var exp = inRange.First().Mine;
        Assert.Equal(mineId, exp.MineId);
        Assert.Equal(seat.A.SideId, exp.SideId);            // wire side = the OWNER side (the faction that laid the field)
        Assert.Equal(mine.OwnerSector.Id, exp.ClusterId);
        Assert.Equal(mType.ExpendableTypeId, exp.MinetypeId);
        Assert.True(exp.CreateNow, "the spotted mine exports as a fresh creation (createNow)");
        Assert.True((exp.P0 - mine.Position).Length() < 1e-3f, "the spotted mine is at its real position");

        // Exactly once: pump well beyond detection so any per-tick re-export
        // (a latch violation) would surface, then confirm still exactly one.
        PumpUntil(seat.Server, () => false, 200);
        Assert.Equal(1, seat.BBot.MineExports.Count(e => e.Mine.MineId == mineId));

        // (3) Pump past the mine's lifespan -> B learns the destruction.
        PumpUntil(seat.Server, () => seat.BBot.MineDestroyeds.Any(d => d.MineId == mineId), 5000);
        Assert.Contains(seat.BBot.MineDestroyeds, d => d.MineId == mineId);
        Assert.Equal(mine.OwnerSector.Id, seat.BBot.MineDestroyeds.First(d => d.MineId == mineId).ClusterId);

        Console.WriteLine($"P453-MINEVIS: A(side {seat.A.SideId}) laid mine {mineId} at {mine.Position}; B(side {seat.B.SideId}) " +
                          $"received EXPORT/OT_mine exactly once when in range (owner side {exp.SideId}) and MINE_DESTROYED(84) on expiry -> enemy now sees the field");
    }
}
