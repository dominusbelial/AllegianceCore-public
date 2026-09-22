using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P346 — live loopback proof of the <b>cross-side ship identity EXPORT</b>(26)
/// broadcast (Server.BroadcastCrossSideExport), the missing half of the already
/// built cross-side visibility foundation. The per-tick P132 sweep
/// (<see cref="Sector.ResolveCrossSideVisibility"/>) computes WHEN a ship
/// becomes visible to a non-own side; P139's HEAVY/LIGHT_SHIPS_UPDATE(176/175)
/// then carries that foreign ship's live telemetry, but neither the sweep nor
/// 176/175 carry a HullId/name — so a client that decodes them still could not
/// render the model. P346 adds the identity message: on a genuine false→true
/// visibility edge the sweep queues a (ship, side) pair that the host turns
/// into the same EXPORT/ExportShip wire shape the same-side [teamexport] path
/// already sends — to the newly-seeing SIDE's connections only (per-side, the
/// STATIONS_UPDATE P133 distinction; never whole-mission, never the ship's own
/// side).
/// <para>Construction mirrors P139's booted 2-bot harness (no Windows VM, no
/// native client). The distinguishing requirement is a REAL visibility
/// TRANSITION to observe — not both spawned already-visible. The cross-side
/// test therefore parks both ships 500 km apart (beyond every scanner AND every
/// real station scanner range, so no ambient station can muddy the edge),
/// confirms no cross-side EXPORT while they are mutually out of range, then
/// brings them within scanner range and confirms each receives a real EXPORT of
/// the OTHER with the correct HullId — a genuine unseen→seen edge driven by the
/// sweep, not a spawn-already-visible degenerate case. The same-side test pins
/// the existing [teamexport] path unchanged: exactly one EXPORT of the other
/// (no cross-side duplicate).</para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class CrossSideExportLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Cross-Side Export Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 993u, DwCookie = 0x993u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99000000000000002ul, DrmHash = "crosssideexport",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static bool HasExportAbout(FltBot bot, short shipId)
        => bot.Exports.Any(e => e.ShipId == shipId);

    /// <summary>A booted 2-bot live server. Both ships fly in
    /// <c>Sectors[0]</c> with the mission's largest-scanner-range hull (so a
    /// 300-unit separation is decisively mutually in-range while a 500 km one is
    /// decisively out). <paramref name="sameSide"/> forces both onto side 0 for
    /// the no-regression test; otherwise they round-robin onto different sides.</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship A = null!, B = null!;
        public FltBot ABot = null!, BBot = null!;
        public Sector Sector = null!;

        public void Dispose()
        {
            ABot?.Dispose();
            BBot?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatPair(bool sameSide)
    {
        var seat = new Seated();
        seat.Server = new Srv();

        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
            short side = sameSide
                ? mission.Sides[0].Id
                : mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B onto different sides
            ship.SideId = side;
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot() };
        string[] names = sameSide ? new[] { "p346a", "p346b" } : new[] { "p346_x_a", "p346_x_b" };
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
            // P180 pattern: the cross-side visibility sweep only runs once the
            // mission is Started/Starting (Mission.Update early-returns otherwise),
            // so bypass the ready-up lobby to make the ships' sectors live.
            foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;
            seat.A.Velocity = Vector3.Zero;
            seat.B.Velocity = Vector3.Zero;
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    /// <summary>⭐ Two ships on DIFFERENT sides, parked far apart (no cross
    /// visibility), receive NO cross-side EXPORT of each other; once brought
    /// within scanner range the P132 sweep's genuine false→true edge fires and
    /// each receives a real EXPORT of the OTHER with the correct HullId — sent to
    /// the newly-seeing side's connections only, never whole-mission.</summary>
    [Fact]
    public void Cross_side_ship_becoming_visible_triggers_export()
    {
        using var seat = SeatPair(sameSide: false);
        Assert.NotEqual(seat.A.SideId, seat.B.SideId);

        // 1) Park both far beyond every scanner range AND every real station
        //    scanner range (~3900 worst case) so neither is seen by ANY side.
        //    Discard any join-time exports so the "while far" window is clean.
        seat.A.Position = new Vector3(500_000f, 0f, 0f);
        seat.B.Position = new Vector3(1_000_000f, 0f, 0f);
        seat.ABot.ResetExports();
        seat.BBot.ResetExports();

        // Let the every-c_nPassesPerUpdate(10) sweep run several times with them
        // far apart (60 ticks = 6 cadence windows). No cross-side EXPORT may fire.
        PumpUntil(seat.Server, () => false, 60);
        Assert.False(HasExportAbout(seat.ABot, seat.B.Id),
            "A must NOT receive a cross-side EXPORT of B while B is out of scanner range");
        Assert.False(HasExportAbout(seat.BBot, seat.A.Id),
            "B must NOT receive a cross-side EXPORT of A while A is out of scanner range");

        // 2) Bring B within A's scanner range (300 u ≪ the max-hull detection
        //    radius). The next sweep sees the false→true edge → cross-side EXPORT
        //    to each newly-seeing side.
        seat.B.Position = seat.A.Position + new Vector3(300f, 0f, 0f);
        PumpUntil(seat.Server, () => HasExportAbout(seat.ABot, seat.B.Id) && HasExportAbout(seat.BBot, seat.A.Id), 1000);
        Assert.True(HasExportAbout(seat.ABot, seat.B.Id), "A must receive a cross-side EXPORT of B once B is in range");
        Assert.True(HasExportAbout(seat.BBot, seat.A.Id), "B must receive a cross-side EXPORT of A once A is in range");

        var aExp = seat.ABot.Exports.First(e => e.ShipId == seat.B.Id);
        var bExp = seat.BBot.Exports.First(e => e.ShipId == seat.A.Id);

        // Correct identity: real HullId of the exported ship, and the wire SideId
        // is the exported ship's OWN side (faction identity), not the seeing side.
        Assert.Equal(seat.B.HullType.HullId, aExp.HullId);
        Assert.Equal(seat.A.HullType.HullId, bExp.HullId);
        Assert.Equal(seat.B.SideId, aExp.SideId);
        Assert.Equal(seat.A.SideId, bExp.SideId);

        Console.WriteLine($"P346-XEXPORT: A({seat.A.Id}, side {seat.A.SideId}) <- EXPORT of B({seat.B.Id}, hull {aExp.HullId}) " +
                          $"and B({seat.B.Id}, side {seat.B.SideId}) <- EXPORT of A({seat.A.Id}, hull {bExp.HullId}) on the false->true visibility edge [PER SIDE]");
    }

    /// <summary>⭐ Regression pin for the existing same-side [teamexport] path:
    /// two teammates on the SAME side still get the existing EXPORT of each other
    /// (real hull id + name) — exactly once — with NO cross-side double-export
    /// (the cross-side sweep never fires for a ship's own side).</summary>
    [Fact]
    public void Same_side_teammate_still_gets_teamexport_no_cross_duplicate()
    {
        using var seat = SeatPair(sameSide: true);
        Assert.Equal(seat.A.SideId, seat.B.SideId);

        // NOTE: no ResetExports here — the same-side [teamexport] is a one-shot
        // per (recipient, subject) pair sent during join (gated by
        // _teammateExportSent in BroadcastShipStates), so clearing the bots'
        // accumulators after join would erase the only copy. The cross-side
        // export is edge-triggered, so the other test CAN reset and re-observe.
        PumpUntil(seat.Server, () => HasExportAbout(seat.ABot, seat.B.Id) && HasExportAbout(seat.BBot, seat.A.Id), 2000);
        Assert.True(HasExportAbout(seat.ABot, seat.B.Id), "A must receive the same-side [teamexport] of B (no regression)");
        Assert.True(HasExportAbout(seat.BBot, seat.A.Id), "B must receive the same-side [teamexport] of A (no regression)");

        // Exactly once each: the same-side teamexport, and no cross-side duplicate
        // (the P346 cross-side sweep provably never fires for a ship's own side,
        // and a loadout change that would re-export is not induced here).
        Assert.Equal(1, seat.ABot.Exports.Count(e => e.ShipId == seat.B.Id));
        Assert.Equal(1, seat.BBot.Exports.Count(e => e.ShipId == seat.A.Id));

        // Regression: the teamexport still carries the real hull id + name.
        var ab = seat.ABot.Exports.First(e => e.ShipId == seat.B.Id);
        Assert.Equal(seat.B.HullType.HullId, ab.HullId);
        Assert.Equal("p346b", ab.CharacterName);

        Console.WriteLine($"P346-TEAMEXPORT: same-side A({seat.A.Id})/B({seat.B.Id}, side {seat.A.SideId}) -> exactly one EXPORT of the other (hull {ab.HullId}), no cross duplicate [SAME-SIDE INTACT]");
    }
}
