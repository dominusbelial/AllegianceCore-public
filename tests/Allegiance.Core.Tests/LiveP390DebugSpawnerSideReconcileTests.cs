using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P390 — the P379 debug topology's ShipSpawner ignores the connecting
/// player's explicit side choice, because its closure has no side parameter
/// and places purely by its own round-robin counter (<c>spawned++ % 2</c>).
/// AssignShip now reconciles an explicit choice: when the spawner dropped the
/// ship on a different side than the one the player picked, the ship is
/// repositioned into the requested side's actual home sector (reusing the
/// generic <c>HomeSectorForSide</c>) so it is not stranded at an enemy station
/// where docking is refused.
///
/// Three complementary proofs over a real in-process debug-mode server and real
/// loopback FltBot peers:
/// <list type="number">
/// <item><b>Explicit side 1</b>: the first peer (spawner counter n=0 would put
///      it on side 0/Omega) explicitly chooses side 1 → the ship must end up in
///      Plato(1060) on side 1, and must dock at Plato's garrison.</item>
/// <item><b>Explicit side 0</b>: the second peer (spawner counter n=1 would put
///      it on side 1/Plato) explicitly chooses side 0 → the ship must end up in
///      Omega(1053) on side 0, and must dock at Omega's garrison.</item>
/// <item><b>Auto-assign</b> (<c>POSITIONREQ(-1)</c>): the spawner's own
///      round-robin side and its placement always agree (nothing overrides the
///      spawner), so the seated side must equal the sector the ship actually
///      occupies — the reverse case confirmed, not assumed.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public class LiveP390DebugSpawnerSideReconcileTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static readonly short OmegaDebugStationId = unchecked((short)90001);
    private static readonly short PlatoDebugStationId = unchecked((short)90002);

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: keep the pre-flag 30 s auto-seat this suite relies on
        LiveSettings = new Ls { DebugMode = true },
        Games =
        [
            new Gm { MaxTeamImbalance = 32767, Name = "P390 Debug Topology", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 949u, DwCookie = 0x949u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9590000000000000000ul, DrmHash = "p390",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    private static Sector Sec(Srv server, short id) =>
        server.Missions[0].Sectors.First(s => s.Id == id);

    private static Vector3 StationCenter(Sector sector, short stationId) =>
        new(
            sector.Stations.First(st => st.StationId == stationId).Position[0],
            sector.Stations.First(st => st.StationId == stationId).Position[1],
            sector.Stations.First(st => st.StationId == stationId).Position[2]);

    /// <summary>Seat <paramref name="bot"/> via an EXPLICIT POSITIONREQ side
    /// choice after logon (AutoSelectSideAfterLogon disabled so it does not
    /// auto-pick -1). Returns the seated ship's sector id and side id.</summary>
    private static (short SectorId, short SideId) SeatExplicit(
        Srv server, FltBot bot, string name, short sideId)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int ok = PumpUntil(server, () => bot.Ack is { FValidated: true }, 4000);
        Assert.True(ok < 4000, $"{name}: never acked (pending side-select not created)");
        bot.SendPositionReq(sideId);   // explicit human-style choice
        ok = PumpUntil(server,
            () => bot.ShipAssignment is not null &&
                  ShipOf(server, bot.ShipAssignment.Ship.ShipId).Sector is not null,
            4000);
        Assert.True(ok < 4000, $"{name}: never seated after explicit side {sideId}");
        var ship = ShipOf(server, bot.ShipAssignment!.Ship.ShipId);
        return (ship.Sector!.Id, ship.SideId!.Value);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Explicit_side_one_relocates_first_peer_to_plato_and_it_docks_there()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // dock pass needs a started mission (P180)
        var plato = Sec(server, 1060);
        short side1 = server.Missions[0].Sides[1].Id;

        using var bot = new FltBot { AutoSelectSideAfterLogon = false };
        var (sectorId, side) = SeatExplicit(server, bot, "p390_s1", side1);
        // Spawner counter n=0 would place this peer on side 0/Omega; the explicit
        // side-1 choice must relocate it to Plato.
        Assert.Equal((short)1, side);
        Assert.Equal((short)1060, sectorId);
        Assert.Equal(PlatoDebugStationId, plato.Stations.Single(st => st.SideId == side1).StationId);

        // And it can actually dock at its (new) home garrison — the whole point
        // of the relocation (a side-1 ship sitting at side-0's Omega is refused).
        var ship = ShipOf(server, bot.ShipAssignment!.Ship.ShipId);
        ship.Position = StationCenter(plato, PlatoDebugStationId);
        int d = PumpUntil(server, () => ShipOf(server, ship.Id).StationId is not null, 400);
        Assert.True(d < 400, "relocated side-1 peer never docked at Plato's garrison");
        Assert.Equal(PlatoDebugStationId, ShipOf(server, ship.Id).StationId!.Value);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Explicit_side_zero_relocates_second_peer_to_omega_and_it_docks_there()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var omega = Sec(server, 1053);
        short side0 = server.Missions[0].Sides[0].Id;

        // Peer 0 auto-assigns first (spawner counter n=0 -> side 0 / Omega).
        using var first = new FltBot();   // AutoSelectSideAfterLogon=true -> POSITIONREQ(-1)
        first.Connect(server.BoundPort, "p390_s0_first");
        first.SendLogonRequest(Request("p390_s0_first"));
        int ok = PumpUntil(server,
            () => first.Ack is { FValidated: true } && first.ShipAssignment is not null &&
                  ShipOf(server, first.ShipAssignment.Ship.ShipId).Sector is not null,
            4000);
        Assert.True(ok < 4000, "peer 0 never auto-seated");
        Assert.Equal((short)1053, ShipOf(server, first.ShipAssignment!.Ship.ShipId).Sector!.Id);

        // Peer 1 is the spawner's n=1 -> side 1 / Plato placement, but explicitly
        // chooses side 0. It must be relocated to Omega (side 0's arena home).
        using var bot = new FltBot { AutoSelectSideAfterLogon = false };
        var (sectorId, side) = SeatExplicit(server, bot, "p390_s0", side0);
        Assert.Equal((short)0, side);
        Assert.Equal((short)1053, sectorId);
        Assert.Equal(OmegaDebugStationId, omega.Stations.Single(st => st.SideId == side0).StationId);

        var ship = ShipOf(server, bot.ShipAssignment!.Ship.ShipId);
        ship.Position = StationCenter(omega, OmegaDebugStationId);
        int d = PumpUntil(server, () => ShipOf(server, ship.Id).StationId is not null, 400);
        Assert.True(d < 400, "relocated side-0 peer never docked at Omega's garrison");
        Assert.Equal(OmegaDebugStationId, ShipOf(server, ship.Id).StationId!.Value);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Auto_assign_side_matches_the_sector_the_spawner_actually_used()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        var omega = Sec(server, 1053);
        var plato = Sec(server, 1060);

        // Default AutoSelectSideAfterLogon=true → POSITIONREQ(-1), the debug
        // auto path. The spawner's own round-robin side must equal the sector it
        // placed the ship in (nothing overrides it), so no relocation occurs.
        for (int k = 0; k < 4; k++)
        {
            using var bot = new FltBot();
            bot.Connect(server.BoundPort, $"p390_auto{k}");
            bot.SendLogonRequest(Request($"p390_auto{k}"));
            int ok = PumpUntil(server,
                () => bot.Ack is { FValidated: true } && bot.ShipAssignment is not null &&
                      ShipOf(server, bot.ShipAssignment.Ship.ShipId).Sector is not null,
                4000);
            Assert.True(ok < 4000, $"auto peer {k} never seated");

            var ship = ShipOf(server, bot.ShipAssignment!.Ship.ShipId);
            var side = ship.SideId!.Value;
            short want = (short)(k % 2);
            var wantSector = want == 0 ? omega : plato;
            Assert.Equal(want, side);                                  // spawner round-robin
            Assert.Equal(wantSector.Id, ship.Sector!.Id);              // sector agrees with the side
        }
    }
}
