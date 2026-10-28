using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

/// <summary>
/// P866 / SIM-0608 — the remaining <c>RequestPosition</c> leg. Native
/// <c>CFSMission::RequestPosition</c> removes the switcher from their CURRENT side
/// with <c>RemovePlayerFromSide(pfsPlayer, QSR_SwitchingSides)</c> BEFORE it seats
/// them or parks the request (<c>FedSrv/fsmission.cpp:4747-4751</c>), and
/// <c>AddPlayerToSide</c> does the same as its opening move
/// (<c>:562-563</c>) — so every switch runs the full removal body:
/// <list type="number">
/// <item>flag drop — respawn/jettison + <c>GAIN_FLAG(NA, ship)</c> (<c>:875-893</c>);</item>
/// <item>leader succession / mission-owner migration (<c>:917-1099</c>);</item>
/// <item><c>QUIT_SIDE(reason)</c> whole-mission (<c>:1130-1137</c>);</item>
/// <item>the leaver's money into the old side's treasury with an immediate payday
/// (<c>:1042-1068,1141-1145</c>).</item>
/// </list>
/// P852 closed the PENDING leg (the switcher was parked on SIDE_TEAMLOBBY via
/// <c>MoveToTeamLobby</c>). The remaining leg was the AUTO-ACCEPT branch: it called
/// <c>TryJoinSide</c> directly, which ran only leader succession + QUIT_SIDE —
/// no flag drop and no payday. <see cref="Srv"/> now routes every live switch
/// through the same <c>LeaveSideForSwitch</c> block, so this test drives the real
/// <c>FM_C_POSITIONREQ</c> auto-accept path on a started game and asserts every
/// removal effect.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P866RequestPositionSwitchTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games = [new Gm { Name = "P866 Switch", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 866u, DwCookie = 0x86Bu,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 86600000000000001ul, DrmHash = "p866switch",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        Assert.True(PumpUntil(server, () => bot.ShipAssignment is not null, 4000) < 4000, $"{name} did not seat");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    private static Srv.LiveShip LiveOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId);

    /// <summary>⭐ The auto-accept POSITIONREQ leg leaves the old side exactly like
    /// the pending leg / native <c>RemovePlayerFromSide(QSR_SwitchingSides)</c>:
    /// the carried flag is dropped (GAIN_FLAG to the mission), the switcher's money
    /// is credited to the old side and paid out (PAYDAY to the remaining side-mate),
    /// leader succession promotes the side-mate, QUIT_SIDE(SwitchingSides) goes out
    /// whole-mission, and the requester is then seated on the target side with a
    /// zeroed wallet.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Auto_accept_position_request_runs_the_full_remove_from_side_effects()
    {
        Require.Artwork();
        using var server = new Srv();
        var spawnSeq = 0;
        // Both players on side 0, in one sector; the first is the side leader and
        // the mission owner. The target side (1) stays empty = leaderless, so the
        // POSITIONREQ auto-accept fast path seats the requester immediately.
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.Where(h => h.GroupId >= 0 && h.Price > 0).OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Allegiance.Core.Collision.Vector3(300f, 780f, 880f),
                Allegiance.Core.Collision.Vector3.Zero, Orientation.Identity) { SideId = mission.Sides[0].Id };
            mission.PlaceShip(ship, mission.Sectors[0]);
            spawnSeq++;
            return (ship, mission.Sectors[0]);
        };
        server.Boot(Config());
        using var observer = new FltBot();
        using var switcher = new FltBot();
        short observerId = Seat(server, observer, "P866_OBSERVER");
        short switcherId = Seat(server, switcher, "P866_SWITCHER");
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;
        mission.MissionOwnerSideId = mission.Sides[0].Id;
        mission.Sides[0].LeaderShipId = switcherId;

        var switcherLive = LiveOf(server, switcherId);
        var switcherShip = switcherLive.Ship;
        switcherShip.Flag = 1;              // carrying side 1's flag
        switcherLive.Wallet.Money = 250;    // money that must flow to the old side
        observer.ResetPaydays();
        observer.ResetGainFlags();
        observer.ResetLeadershipSignals();   // clears the recorded QUIT_SIDE feed too

        var targetSide = mission.Sides.First(s => s.Id == 1);
        Assert.Null(targetSide.LeaderShipId);   // leaderless → auto-accept
        switcher.SendPositionReq(1);

        int pumped = PumpUntil(server, () =>
            switcherShip.SideId == 1
            && observer.QuitSides.Any(q => q.ShipId == switcherId),
            4000);
        Assert.True(pumped < 4000, "the auto-accept must seat the requester and announce QUIT_SIDE");

        // 1. Flag drop (fsmission.cpp:875-893).
        Assert.Null(switcherShip.Flag);
        Assert.Contains(observer.GainFlags, g => g.ShipidRecipient == switcherId && g.SideidFlag == GainFlag.NA);

        // 2. QUIT_SIDE with the native SwitchingSides reason.
        var quit = Assert.Single(observer.QuitSides, q => q.ShipId == switcherId);
        Assert.Equal(QuitSideReason.SwitchingSides, quit.Reason);

        // 3. Money to the old side, immediate payday to the remaining side-mate.
        Assert.Equal(0, switcherLive.Wallet.Money);          // pfsPlayer->SetMoney(0)
        Assert.Contains(observer.Paydays, p => p.DMoney == 250);

        // 4. Leader succession: the remaining player now leads the old side.
        Assert.Equal(observerId, mission.Sides.First(s => s.Id == 0).LeaderShipId);

        // 5. The requester is on the new side.
        Assert.Equal((short)1, switcherShip.SideId);

        Console.WriteLine($"P866-0608: ship {switcherId} auto-accepted onto side 1 — flag dropped, 250 to side 0 (PAYDAY), " +
                          $"QUIT_SIDE(SwitchingSides), leader succession → ship {observerId}, wallet zeroed");
    }
}
