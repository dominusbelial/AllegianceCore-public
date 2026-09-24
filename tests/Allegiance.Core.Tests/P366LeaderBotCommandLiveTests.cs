using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using FltBot = global::Allegiance.Bot.Bot;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P366 — live proof of the server half of the leader-only bot dock/launch
/// command. Sets up a REAL in-process server, two REAL connected peers (leader
/// <c>A</c> + non-leader <c>B</c> on the SAME side, established through the real
/// <c>TryJoinSide</c> leadership mechanic), and a real same-side AI bot teammate
/// (<c>SpawnNpcShip</c>), then proves over the REAL wire:
/// <list type="bullet">
/// <item>the leader peer is that side's team leader (<c>Side.LeaderShipId</c>)
/// and received the <c>SET_TEAM_LEADER(121)</c> whole-mission broadcast;</item>
/// <item>the non-leader peer is NOT the leader;</item>
/// <item>the leader's <c>ORDER_CHANGE(93)</c> targeting the same-side bot is an
/// EFFECTIVE command — a flying bot is commanded to return-to-base
/// (<c>StayDocked</c> set), and a docked bot is commanded to LAUNCH
/// (<c>StayDocked</c> cleared + undocked).</item>
/// </list>
/// The CLIENT-side gate (only the leader's HUD shows the Dock/Launch prompt,
/// via <c>LeaderBotCommandResolver</c>) is covered separately in the pure
/// <c>Allegiance.ClientCore.Tests</c> <c>LeaderBotCommandTests</c> — native
/// gates the menu on the client only (WinTrek.cpp:4349-4368), and the server's
/// ORDER_CHANGE handler has no leadership check (FedSrv.CPP:7264+, faithful).
/// </summary>
public class P366LeaderBotCommandLiveTests
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
            new Gm { Name = "Leader Bot Cmd Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 996u, DwCookie = 0x996u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99200000000000000ul, DrmHash = "leaderbot",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

    /// <summary>A booted two-peer live server with the peer side = side 0, the
    /// same mission/sector captured for spawning the NPC, and real leadership
    /// established via TryJoinSide (P183).</summary>
    private sealed class Rig : IDisposable
    {
        public Srv Server = null!;
        public Mission Mission = null!;
        public Sector Sector = null!;
        public FltBot BotA = null!;      // the side-0 team leader
        public FltBot BotB = null!;      // the side-0 non-leader
        public Ship Leader = null!;
        public Ship Mate = null!;
        public short Side0;

        public void Dispose()
        {
            BotB?.Dispose();
            BotA?.Dispose();
            Server?.Dispose();
        }
    }

    private static Rig Build()
    {
        var rig = new Rig();
        rig.Server = new Srv();
        rig.Server.ShipSpawner = (_, mission, id) =>
        {
            rig.Sector = mission.Sectors[0];
            rig.Mission = mission;
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            // Unseated spawn (P195 trick): the test seats each peer via TryJoinSide,
            // so seating fully controls who is the side leader.
            var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, rig.Sector);
            return (ship, rig.Sector);
        };
        rig.Server.Boot(Config());
        foreach (var m in rig.Server.Missions) m.Stage = MissionStage.Started;   // immediately-flyable (P180)
        var port = rig.Server.BoundPort;

        // Leader A: connect, seat, then join the EMPTY side 0 first -> becomes its leader (P183).
        var a = new FltBot();
        a.Connect(port, "ldr_a");
        a.SendLogonRequest(Request("ldr_a"));
        PumpUntil(rig.Server, () => a.ShipAssignment is not null, 4000);
        Assert.True(a.ShipAssignment is not null, "leader A did not seat");
        var aShip = rig.Server.Ships.First(s => s.Ship.Id == a.ShipAssignment!.Ship.ShipId).Ship;
        rig.Side0 = rig.Mission.Sides[0].Id;
        Assert.True(rig.Server.TryJoinSide(aShip, rig.Side0), "A must join side 0 (becoming its leader)");

        // Mate B: connect, seat, join side 0 AFTER A -> member, NOT the leader.
        var b = new FltBot();
        b.Connect(port, "ldr_b");
        b.SendLogonRequest(Request("ldr_b"));
        PumpUntil(rig.Server, () => b.ShipAssignment is not null, 4000);
        Assert.True(b.ShipAssignment is not null, "mate B did not seat");
        var bShip = rig.Server.Ships.First(s => s.Ship.Id == b.ShipAssignment!.Ship.ShipId).Ship;
        Assert.True(rig.Server.TryJoinSide(bShip, rig.Side0), "B must join side 0 as a member");

        rig.BotA = a;
        rig.BotB = b;
        rig.Leader = aShip;
        rig.Mate = bShip;
        Flush(rig.Server);   // let the SET_TEAM_LEADER broadcast land on A's receive thread
        return rig;
    }

    [Fact]
    public void Leader_is_side_leader_and_receives_set_team_leader_but_mate_is_not()
    {
        using var rig = Build();

        var side0 = rig.Mission.Sides.First(s => s.Id == rig.Side0);
        Assert.Equal(rig.Leader.Id, side0.LeaderShipId);          // A leads side 0
        Assert.NotEqual(rig.Leader.Id, rig.Mate.Id);
        Assert.NotEqual(side0.LeaderShipId, rig.Mate.Id);          // B is NOT the leader

        // A (the leader's client) observed the real whole-mission SET_TEAM_LEADER(121).
        Assert.Contains(rig.BotA.SetTeamLeaders, tl => tl.SideId == rig.Side0 && tl.ShipId == rig.Leader.Id);

        // The non-leader B is not announced as side-0's leader.
        Assert.DoesNotContain(rig.BotB.SetTeamLeaders, tl => tl.SideId == rig.Side0 && tl.ShipId == rig.Mate.Id);

        Console.WriteLine($"P366: side {rig.Side0} leader = ship {side0.LeaderShipId} (A={rig.Leader.Id}); B={rig.Mate.Id} is a non-leader teammate");
    }

    [Fact]
    public void Leader_commands_same_side_bot_to_return_to_base_effectively_live()
    {
        using var rig = Build();

        // A real same-side AI bot teammate on the leader's side.
        var npc = rig.Server.SpawnNpcShip(rig.Mission, rig.Sector, rig.Side0, new Vector3(700f, 720f, 680f));
        Assert.True(npc.IsNpc, "spawned bot must carry IsNpc");
        Assert.Equal(rig.Side0, npc.SideId);
        Assert.False(npc.StayDocked, "flying bot starts StayDocked=false");

        // The LEADER issues the real ORDER_CHANGE (Dock: c_cidGoto + c_cmdAccepted,
        // the exact shape WinTrek.cpp contextDockDrone sends, objectID=NA).
        rig.BotA.SendOrderChange(npc.Id, OrderChange.CidGoto, OrderChange.CmdAccepted);

        // The server's P150 return-to-base arm sets StayDocked=true (flying NPC) —
        // the bot is now commanded to dock. (Assigning an AcceptedTarget is gated
        // on a friendly repair-capable station existing in the bot's sector,
        // FedSrv.CPP:7323; StayDocked is the unconditional, faithful proof — the
        // same standard the P148 stay-docked live test uses.)
        var pumped = PumpUntil(rig.Server, () => npc.StayDocked, 8000);
        Assert.True(npc.StayDocked,
            $"leader's ORDER_CHANGE must make the flying same-side bot return to base (StayDocked=true); pumped {pumped} ticks");

        Console.WriteLine($"P366: leader {rig.Leader.Id}(side {rig.Side0}) ORDER_CHANGE(93) c_cidGoto/c_cmdAccepted -> same-side bot {npc.Id} (flying) => StayDocked=true (return to base)");
    }

    [Fact]
    public void Leader_commands_docked_same_side_bot_to_launch_effectively_live()
    {
        using var rig = Build();

        var npc = rig.Server.SpawnNpcShip(rig.Mission, rig.Sector, rig.Side0, new Vector3(911f, 901f, 887f));
        Assert.True(npc.IsNpc);
        Assert.Equal(rig.Side0, npc.SideId);
        var allStations = rig.Mission.Sectors.SelectMany(s => s.Stations).ToList();
        Assert.True(allStations.Count > 0, "mission must expose a real station to dock the bot at");
        npc.StationId = allStations[0].StationId;   // dock the bot at a real station
        npc.StayDocked = true;                       // a stay-docked bot in the hangar

        // The LEADER's real ORDER_CHANGE (Launch: c_cidGoto != c_cidDoNothing)
        // drives the P152 launch arm: StayDocked=false + undock.
        rig.BotA.SendOrderChange(npc.Id, OrderChange.CidGoto, OrderChange.CmdAccepted);
        var pumped = PumpUntil(rig.Server, () => !npc.StayDocked && npc.StationId is null, 8000);
        Assert.True(pumped < 8000,
            $"leader's Launch command must undock the bot (StayDocked=false, StationId cleared); pumped {pumped} ticks");

        Console.WriteLine($"P366: leader {rig.Leader.Id}(side {rig.Side0}) ORDER_CHANGE(93) c_cidGoto/c_cmdAccepted -> docked bot {npc.Id} => LAUNCH (StayDocked=false, undocked)");
    }
}
