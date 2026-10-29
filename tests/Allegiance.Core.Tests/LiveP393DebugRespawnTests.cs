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
/// P393 — death/lifepod respawn was picking the REAL home station (Ganymede /
/// Organia) instead of the debug arena's synthetic garrison, stranding a
/// debug-mode player back in the real, otherwise-unused part of the map on
/// every death. Root cause: <c>FindFriendlyStation</c> (the port's stand-in
/// for the native nearest-friendly-station search, Server.Combat.cs) returns
/// "any" same-side station first-found in the mission's deterministic
/// sector-then-station order — a fine P105 simplification when a side has one
/// station, but the P379 debug topology gives side 0/1 TWO same-side stations
/// (the real Ganymede/Organia enumerated early, plus the synthetic
/// Omega/Plato garrison added at boot). "First in mission order" reliably picks
/// the real station. P393 gives FindFriendlyStation the same
/// debug-garrison-first preference the join path's ResolveSideHomeSector
/// already has (P390), via ONE shared helper (FindDebugGarrisonStation), so a
/// dead debug player respawns inside the self-contained arena.
///
/// Proofs over a real in-process debug-mode server + real loopback FltBot:
/// <list type="number">
/// <item><b>Side 0</b>: an explicitly side-0 peer is converted to its Lifepod
///      hull and suicides → the respawned lifepod docks at the OMEGA DEBUG
///      GARRISON (90001), not Ganymede — and its sector is Omega(1053).</item>
/// <item><b>Side 1</b>: same for an explicitly side-1 peer → PLATO DEBUG
///      GARRISON (90002), not Organia — sector Plato(1060).</item>
/// <item><b>Regression</b> (debugMode OFF on the real m2xspiderw map): a real
///      player death still respawns at the exact first-in-mission-order
///      same-side station the pre-P393 scan returned — never a synthetic
///      9000x garrison — proving the fallback path is byte-for-byte
///      unaffected.</item>
/// </list>
/// The existing P105 test
/// <c>Player_death_relocates_to_docked_lifepod_and_only_owner_gets_enter_lifepod</c>
/// continues to pass unchanged as a second regression net over the non-debug
/// respawn path.
/// </summary>
[Collection("LiveNetwork")]
public class LiveP393DebugRespawnTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private const short OmegaDebugStationId = unchecked((short)90001);
    private const short PlatoDebugStationId = unchecked((short)90002);

    private static Cfg DebugConfig() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = new Ls { DebugMode = true },
        Games =
        [
            new Gm { Name = "P393 Debug Topology", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static Cfg NormalConfig() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P393 Normal", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 948u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9580000000000000000ul, DrmHash = "p393",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    /// <summary>Seat <paramref name="bot"/> on <paramref name="sideId"/> via an
    /// explicit POSITIONREQ after logon (P390's proven debug-mode recipe), then
    /// force a real death on the Lifepod hull so it reaches the death/respawn
    /// branch (a non-lifepod hull would EJECT, not respawn — P107). Returns the
    /// respawned ship.</summary>
    private static Ship DieAndReturnRespawn(
        Srv server, FltBot bot, string name, short sideId)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int ok = PumpUntil(server, () => bot.Ack is { FValidated: true }, 4000);
        Assert.True(ok < 4000, $"{name}: never acked");
        bot.SendPositionReq(sideId);
        ok = PumpUntil(server,
            () => bot.ShipAssignment is not null &&
                  ShipOf(server, bot.ShipAssignment.Ship.ShipId).Sector is not null,
            4000);
        Assert.True(ok < 4000, $"{name}: never seated after explicit side {sideId}");

        var ship = ShipOf(server, bot.ShipAssignment!.Ship.ShipId);
        Assert.Equal(sideId, ship.SideId);
        // H-06: default seats are docked. Launch before the death scenario.
        if (ship.StationId is not null)
        {
            bot.SendBuyLoadout(ship.HullType.HullId, fLaunch: true);
            Assert.True(PumpUntil(server, () => ship.StationId is null, 4000) < 4000);
        }

        // Convert to the side's Lifepod hull so the death is a bKilled death
        // (native bLifepod → bKilled, FedSrv.CPP:11776-11780), not an eject.
        var mission = ship.Mission!;
        var lp = mission.Core.HullTypes[mission.Sides.First(s => s.Id == sideId).Civilization.Lifepod];
        ship.ChangeHull(lp);

        // H-26 / FedSrv.CPP:6338: privileged death-fixture connection.
        server.Config.LiveSettings.AdminCheats = new global::Allegiance.Server.AdminCheatsConfig
            { Enabled = true, AllowNames = [name] };
        bot.SendSuicide();
        int ticks = PumpUntil(server, () => ShipOf(server, ship.Id).StationId is not null, 2000);
        Assert.True(ticks < 2000, $"{name}: suicide on lifepod hull never respawned at a station");
        return ShipOf(server, ship.Id);
    }

    /// <summary>The station the PRE-P393 first-in-mission-order scan would have
    /// picked — replicates the old FindFriendlyStation exactly, to pin the
    /// "byte-for-byte unaffected" regression claim.</summary>
    private static DataStationIGC? PreP393FirstFriendlyStation(Mission mission, short sideId)
    {
        foreach (var sector in mission.Sectors)
            foreach (var station in sector.Stations)
                if (station.SideId == sideId)
                    return station;
        return null;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Side0_death_respawns_at_omega_debug_garrison_not_ganymede()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(DebugConfig());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // suicide + dock need Started (P180)
        short side0 = server.Missions[0].Sides[0].Id;
        Assert.Equal((short)0, side0);

        using var bot = new FltBot { AutoSelectSideAfterLogon = false };
        var respawned = DieAndReturnRespawn(server, bot, "p393_s0", side0);

        Assert.Equal(OmegaDebugStationId, respawned.StationId);
        Assert.Equal((short)1053, respawned.Sector!.Id);   // Omega, the arena home
        Assert.NotEqual((short)0, respawned.StationId!.Value);   // trivially not the real id
        Console.WriteLine($"[p393] side-0 player death respawned: station {respawned.StationId} '{respawned.HullType.Name}' in sector {respawned.Sector.Name} ({respawned.Sector.Id})");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Side1_death_respawns_at_plato_debug_garrison_not_organia()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(DebugConfig());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        short side1 = server.Missions[0].Sides[1].Id;
        Assert.Equal((short)1, side1);

        using var bot = new FltBot { AutoSelectSideAfterLogon = false };
        var respawned = DieAndReturnRespawn(server, bot, "p393_s1", side1);

        Assert.Equal(PlatoDebugStationId, respawned.StationId);
        Assert.Equal((short)1060, respawned.Sector!.Id);   // Plato, the arena home
        Console.WriteLine($"[p393] side-1 player death respawned: station {respawned.StationId} in sector {respawned.Sector.Name} ({respawned.Sector.Id})");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Normal_game_death_respawn_still_hits_the_pre_p393_real_station()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(NormalConfig());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var mission = server.Missions[0];
        short side0 = mission.Sides[0].Id;
        short side1 = mission.Sides[1].Id;

        // No synthetic garrison exists in normal mode (P379 topology is debug-gated).
        bool anySynthetic = mission.Sectors
            .SelectMany(s => s.Stations)
            .Any(st => st.StationId is OmegaDebugStationId or PlatoDebugStationId);
        Assert.False(anySynthetic, "normal mode must not contain synthetic 9000x garrisons");
        var expected0 = PreP393FirstFriendlyStation(mission, side0);
        Assert.NotNull(expected0);

        using var bot = new FltBot { AutoSelectSideAfterLogon = false };
        var respawned = DieAndReturnRespawn(server, bot, "p393_norm", side0);

        // The fallback must return EXACTLY the same station the old scan returned —
        // byte-for-byte the pre-P393 behavior — and never a synthetic garrison.
        Assert.Equal(expected0.Value.StationId, respawned.StationId);
        Assert.False(respawned.StationId is OmegaDebugStationId or PlatoDebugStationId);
        Assert.Equal(expected0.Value.ClusterId, respawned.Sector!.Id);
        Console.WriteLine($"[p393] normal-mode side-0 death respawned at real station {respawned.StationId} in sector {respawned.Sector.Name} ({respawned.Sector.Id})");
    }
}
