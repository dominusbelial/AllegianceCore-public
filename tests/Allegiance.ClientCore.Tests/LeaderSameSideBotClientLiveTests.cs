using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P366 — live proof of the CLIENT half of the leader-only bot dock/launch
/// command, end to end through a real <see cref="ConnectionSession"/> against a
/// real in-process server. Proves, over the REAL wire into a REAL
/// <see cref="ClientWorld"/>:
/// <list type="bullet">
/// <item>the leader peer's world tracks that it is the side's team leader
/// (<see cref="ClientWorld.IsTeamLeader"/> from the real
/// <c>SET_TEAM_LEADER(121)</c> broadcast);</item>
/// <item>the leader's world is told about a SAME-SIDE AI bot teammate — the
/// P366 same-side NPC EXPORT — so the leader's <see cref="LeaderBotCommandResolver"/>
/// offers the Dock/Launch command for it (the UI "appears");</item>
/// <item>a NON-leader peer on the same side gets NO such command
/// (<see cref="ClientWorld.IsTeamLeader"/> false → resolver null).</item>
/// </list>
/// The server-side leadership/order seams (<c>TryJoinSide</c>, <c>SpawnNpcShip</c>)
/// are internal, so they are reached via reflection (the same friend-gate bypass
/// HullSwapClientLiveTests uses for <c>ShipSpawner</c>).
/// </summary>
[Collection("LiveNetwork")]
public class LeaderSameSideBotClientLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Leader Bot Client Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 997u, DwCookie = 0x997u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99300000000000000ul, DrmHash = "leaderbotcli",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static readonly MethodInfo TryJoinSideMethod =
        typeof(Srv).GetMethod("TryJoinSide", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Server.TryJoinSide not found");

    private static readonly MethodInfo SpawnNpcShipMethod =
        typeof(Srv).GetMethod("SpawnNpcShip", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Server.SpawnNpcShip not found");

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models).OfType<Ship>()
              .First(sh => sh.Id == shipId);

    private sealed class Rig : IDisposable
    {
        public Srv Server = null!;
        public Mission Mission = null!;
        public Sector Sector = null!;
        public ConnectionSession Leader = null!;   // real client (ClientWorld)
        public ConnectionSession Mate = null!;     // real non-leader client
        public short Side0;
        public Ship LeaderShip = null!;

        public void Dispose()
        {
            Leader?.Dispose();
            Mate?.Dispose();
            Server?.Dispose();
        }
    }

    private static Rig Build()
    {
        var rig = new Rig();
        rig.Server = new Srv();
        // Use the server's DEFAULT spawner / auto-join flow — the real production
        // path: the first connecting peer auto-joins the lowest side and becomes
        // its team leader via TryJoinSide (P183), which also broadcasts
        // SET_TEAM_LEADER(121). We capture the mission/sector for the NPC spawn.
        rig.Server.Boot(Config());
        foreach (var m in rig.Server.Missions) m.Stage = MissionStage.Started;
        rig.Mission = rig.Server.Missions[0];
        rig.Sector = rig.Mission.Sectors[0];

        // Leader A (real client) — auto-joins the first side and becomes its leader.
        var a = new ConnectionSession();
        a.Connect(rig.Server.BoundPort, "ldr_a");
        a.SendLogonRequest(Request("ldr_a"));
        PumpUntil(rig.Server, () => a.World.MyShipId is not null && a.World.MySideId is not null, 4000);
        Assert.True(a.World.MyShipId is not null && a.World.MySideId is not null, "leader A did not seat");
        rig.Side0 = a.World.MySideId!.Value;
        rig.LeaderShip = ShipOf(rig.Server, a.World.MyShipId!.Value);
        // Promote A to leader of its (empty-of-members) side via the real
        // leadership mechanic (TryJoinSide first-on-empty-side → leader, P183),
        // which broadcasts SET_TEAM_LEADER(121) whole-mission.
        Assert.True((bool)(TryJoinSideMethod.Invoke(rig.Server, new object[] { rig.LeaderShip, rig.Side0 }) ?? false)!,
            "A must become the leader of side 0 (TryJoinSide)");
        rig.Leader = a;

        // Mate B (real client) — a second peer on the same mission. It auto-joins
        // the other side by default; that makes it a genuine NON-leader (it can
        // never be the leader of A's side), and the resolver's same-side gate
        // independently returns null for it (covering the "non-leader sees nothing"
        // half with real wire state too).
        var b = new ConnectionSession();
        b.Connect(rig.Server.BoundPort, "ldr_b");
        b.SendLogonRequest(Request("ldr_b"));
        PumpUntil(rig.Server, () => b.World.MyShipId is not null, 4000);
        Assert.True(b.World.MyShipId is not null, "mate B did not seat");
        rig.Mate = b;
        return rig;
    }

    [Fact]
    public void Leader_client_sees_same_side_bot_and_gets_command_non_leader_does_not_live()
    {
        using var rig = Build();

        // Establish real leadership propagation to the leader's ClientWorld.
        int lp = PumpUntil(rig.Server, () => rig.Leader.World.IsTeamLeader, 4000);
        string diag = $"MyShipId={rig.Leader.World.MyShipId} MySideId={rig.Leader.World.MySideId} " +
                      $"TeamLeaderOf({rig.Side0})={rig.Leader.World.TeamLeaderShipIdFor(rig.Side0)} IsTeamLeader={rig.Leader.World.IsTeamLeader} " +
                      $"serverLeader={rig.Mission.Sides.First(s => s.Id == rig.Side0).LeaderShipId}";
        Assert.True(lp < 4000, "leader's ClientWorld must learn IsTeamLeader (SET_TEAM_LEADER relay): " + diag);
        Assert.False(rig.Mate.World.IsTeamLeader, "non-leader B must NOT be team leader");

        // Spawn a real same-side AI bot teammate on the leader's side.
        var npc = (Ship)SpawnNpcShipMethod.Invoke(rig.Server,
            new object?[] { rig.Mission, rig.Sector, rig.Side0, new Vector3(700f, 720f, 680f), (short?)null })!;
        Assert.True(npc.IsNpc, "spawned bot must carry IsNpc");
        Assert.Equal(rig.Side0, npc.SideId);

        // The leader's real ClientWorld is told about the same-side bot (P366
        // same-side NPC EXPORT): it appears with its real side + the EXPORT's
        // identity fields (HullId + CharacterName — the heavy-kinematic update
        // never sets either, so these are the export-only discriminator that
        // proves the same-side NPC identity really reached the client) + no
        // PlayerInfo (the P366 bot discriminator), so the resolver OFFERS the command.
        int se = PumpUntil(rig.Server,
            () => rig.Leader.World.Ships.TryGetValue(npc.Id, out var se2)
                  && se2.SideId == rig.Side0
                  && se2.HullId == npc.HullType.HullId
                  && se2.CharacterName.Length > 0
                  && se2.Player is null && se2.Latest is not null,
            4000);
        Assert.True(se < 4000, "leader's ClientWorld must be shown the same-side bot (EXPORT with SideId + hull identity + pose)");

        var cmd = LeaderBotCommandResolver.Resolve(rig.Leader.World, npc.Id);
        Assert.NotNull(cmd);
        Assert.StartsWith("DOCK", cmd.Value.Label);          // the flying bot -> DOCK prompt
        Assert.Equal(npc.Id, cmd.Value.TargetShipId);

        // A NON-leader on the same side sees NO command for the same bot.
        var mateCmd = LeaderBotCommandResolver.Resolve(rig.Mate.World, npc.Id);
        Assert.Null(mateCmd);

        Console.WriteLine($"P366: leader client {rig.Leader.World.MyShipId}(side {rig.Side0}) sees same-side bot {npc.Id} -> " +
                          $"'{cmd.Value.Label}' offered; non-leader client {rig.Mate.World.MyShipId} sees nothing (resolver null)");
    }
}
