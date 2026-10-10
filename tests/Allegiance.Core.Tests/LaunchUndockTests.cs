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
/// P84: the real native WinTrek client's Hangar screen "Launch" button never
/// sends <c>FM_C_DOCKED(48)</c> the way bots do (<see cref="DockDockingTests"/>)
/// -- it sends <c>FM_C_BUY_LOADOUT(4)</c> with <c>fLaunch=true</c>
/// (<c>HangarScreen::OnButtonLaunch</c> -> <c>BuyLoadout(ship, true)</c>,
/// hangarscreen.cpp:145-160) and nothing else. The native client's own
/// <c>BUY_LOADOUT_ACK</c> handler deliberately does NOT clear the Hangar
/// screen's launch lockdown when <c>fLaunch</c> is true
/// (<c>appmsg.cpp:3180</c>) -- it is waiting for <c>FM_S_SET_CLUSTER(7)</c>
/// instead (<c>appmsg.cpp:991-996</c>), the same message that clears
/// <c>GetStation()</c>/<c>GetCluster()</c> client-side. Before this fix,
/// <see cref="Server.ApplyLoadout"/> answered the ACK but never undocked the
/// ship, so a real player's Launch click did nothing observable and every
/// further click re-hit the client's own already-locked-down
/// <c>assert(false)</c> path (<c>hangarscreen.cpp:154-157</c>) -- confirmed
/// live on the VM (screenshots + repeated no-op clicks), not guessed. Fixed
/// by having <c>ApplyLoadout</c> call the same <c>PerformUndock</c> helper the
/// existing <c>FM_C_DOCKED</c> handler already used, whenever
/// <c>buy.FLaunch</c> is true and the ship is docked.
/// </summary>
[Collection("LiveNetwork")]
public class LaunchUndockTests
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
            new Gm { Name = "Launch Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 48u, DwCookie = 0x48u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 84000000000000000ul, DrmHash = "launchtest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    private static (Ship Ship, Sector Sector) SpawnDocked(Mission mission, short shipId, out DataStationIGC station)
    {
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
        var sector = mission.Sectors.First(s => s.Stations.Contains(st));
        var ship = new Ship(shipId, hull, StationPos(st) + new Vector3(20f, 0f, 0f), Vector3.Zero, Orientation.Identity)
        {
            SideId = st.SideId,
        };
        mission.PlaceShip(ship, sector);
        station = st;
        return (ship, sector);
    }

    /// <summary>⭐ The real regression: a docked ship's <c>BUY_LOADOUT</c> with
    /// <c>fLaunch=true</c> must undock it -- clearing <c>StationId</c> and
    /// producing a second <see cref="SetCluster"/> on the wire, exactly like a
    /// real <c>FM_C_DOCKED</c> undock does. Before the fix this assertion
    /// fails: the ACK arrives but the ship never leaves the station.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void BuyLoadout_with_fLaunch_undocks_a_docked_ship()
    {
        Require.Artwork();
        using var server = new Srv();
        server.ShipSpawner = (core, mission, shipId) => SpawnDocked(mission, shipId, out _);

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "launch_probe");
        bot.SendLogonRequest(Request("launch_probe"));

        PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        short shipId = bot.ShipAssignment!.Ship.ShipId;

        // Confirm the ship is actually docked server-side before testing the fix.
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is not null, 200);
        var live = server.Ships.First(s => s.Ship.Id == shipId);
        Assert.NotNull(live.Ship.StationId);

        // The real client's exact Launch-click message: BUY_LOADOUT, fLaunch=true,
        // hull-only (nParts=0) -- HangarScreen::OnButtonLaunch's own call shape.
        int assignmentsBefore = bot.Assignments.Count;
        bot.SendBuyLoadout(live.Ship.HullType.HullId, fLaunch: true);

        // (a) server-side proof: the ship actually undocks.
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is null, 4000);
        Assert.Null(server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId);

        // (b) wire proof: a SECOND SetCluster arrives -- the exact message the
        // native client's BUY_LOADOUT_ACK handler is waiting for
        // (appmsg.cpp:991-996) to clear both GetStation()/GetCluster() and the
        // Hangar screen's launch lockdown (appmsg.cpp:3180, hangarscreen.cpp:147).
        PumpUntil(server, () => bot.Assignments.Count > assignmentsBefore, 4000);
        Assert.True(bot.Assignments.Count > assignmentsBefore,
            "expected a second SET_CLUSTER after BUY_LOADOUT(fLaunch=true) on a docked ship");
    }

    /// <summary>Guardrail: <c>fLaunch=true</c> on an already-undocked ship must
    /// not misbehave (no station to clear, no spurious extra SetCluster) --
    /// <see cref="Server.ApplyLoadout"/> only undocks when
    /// <c>live.Ship.StationId != null</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void BuyLoadout_with_fLaunch_on_an_undocked_ship_sends_no_extra_SetCluster()
    {
        Require.Artwork();
        using var server = new Srv();
        server.ShipSpawner = (core, mission, shipId) => SpawnDocked(mission, shipId, out _);

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "launch_probe2");
        bot.SendLogonRequest(Request("launch_probe2"));

        PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        short shipId = bot.ShipAssignment!.Ship.ShipId;

        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is not null, 200);

        // Undock it first via the normal bot path, then confirm it's flying
        // AND that the resulting SetCluster has actually been received on the
        // wire (not just applied server-side) before capturing the baseline
        // count below -- otherwise that in-flight packet can land between the
        // baseline capture and the final assert, a test race, not a product bug.
        int assignmentsBeforeUndock = bot.Assignments.Count;
        bot.SendDocked();
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is null, 4000);
        Assert.Null(server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId);
        PumpUntil(server, () => bot.Assignments.Count > assignmentsBeforeUndock, 4000);

        int assignmentsBefore = bot.Assignments.Count;
        var live = server.Ships.First(s => s.Ship.Id == shipId);
        bot.SendBuyLoadout(live.Ship.HullType.HullId, fLaunch: true);

        // Give the server a few ticks to (not) react, then confirm no new
        // SetCluster arrived -- the ACK alone is the only expected traffic.
        PumpUntil(server, () => false, 100);
        Assert.Equal(assignmentsBefore, bot.Assignments.Count);
        Assert.Null(server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId);
    }
}
