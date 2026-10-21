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
using Allegiance.TestInfra;

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
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: some test clients never send POSITIONREQ
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
        public ConnectionSession OtherLeader = null!; // balances the other team
        public short Side0;
        public Ship LeaderShip = null!;

        public void Dispose()
        {
            Leader?.Dispose();
            Mate?.Dispose();
            OtherLeader?.Dispose();
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
        // rig.Sector is set from the leader's REAL spawn sector below (P368: a
        // side 0 peer now spawns in its own home sector, not mission.Sectors[0],
        // so the NPC must be spawned where the leader actually is).

        // Leader A (real client) — auto-joins the first side and becomes its leader.
        var a = new ConnectionSession();
        a.Connect(rig.Server.BoundPort, "ldr_a");
        a.SendLogonRequest(Request("ldr_a"));
        PumpUntil(rig.Server, () => a.World.MyShipId is not null && a.World.MySideId is not null, 4000);
        Assert.True(a.World.MyShipId is not null && a.World.MySideId is not null, "leader A did not seat");
        rig.Side0 = a.World.MySideId!.Value;
        rig.LeaderShip = ShipOf(rig.Server, a.World.MyShipId!.Value);
        rig.Sector = rig.LeaderShip.Sector!;   // P368: the leader sits in its side's real home sector — spawn the NPC there.
        // Promote A to leader of its (empty-of-members) side via the real
        // leadership mechanic (TryJoinSide first-on-empty-side → leader, P183),
        // which broadcasts SET_TEAM_LEADER(121) whole-mission.
        Assert.True((bool)(TryJoinSideMethod.Invoke(rig.Server, new object[] { rig.LeaderShip, rig.Side0 }) ?? false)!,
            "A must become the leader of side 0 (TryJoinSide)");
        rig.Leader = a;

        // Native elects the first player on EVERY empty team (fsmission.cpp:
        // 601-613). Seed the other team so the real Started-stage balance rule
        // permits B to join A as an ordinary member; keep the default imbalance.
        short otherSide = rig.Mission.Sides.First(s => s.Id != rig.Side0).Id;
        var other = new ConnectionSession();
        other.Connect(rig.Server.BoundPort, "ldr_other");
        other.SendLogonRequest(Request("ldr_other"));
        other.SendPositionReq(otherSide);
        PumpUntil(rig.Server, () => other.World.MyShipId is not null && other.World.MySideId == otherSide, 4000);
        Assert.True(other.World.MyShipId is not null && other.World.MySideId == otherSide,
            "other team's player must seat before B, preserving team balance");
        rig.OtherLeader = other;

        // B is a real same-side NON-leader. The old fixture placed B first on
        // the other team and incorrectly expected it not to lead that team.
        var b = new ConnectionSession();
        b.Connect(rig.Server.BoundPort, "ldr_b");
        b.SendLogonRequest(Request("ldr_b"));
        b.SendPositionReq(rig.Side0);
        bool MemberStateArrived() => b.World.MyShipId is not null && b.World.MySideId == rig.Side0
            && b.World.TeamLeaderShipIdFor(rig.Side0) == rig.LeaderShip.Id;
        PumpUntil(rig.Server, MemberStateArrived, 4000);
        Assert.True(MemberStateArrived(), "B must seat on A's side and learn A's leadership");
        rig.Mate = b;
        return rig;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Leader_client_sees_same_side_bot_and_gets_command_non_leader_does_not_live()
    {
        Require.Artwork();
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
        // proves the same-side NPC identity really reached the client). HUD-20
        // gives a drone a PLAYERINFO too (native seeds every mission ship,
        // FedSrv.CPP:2129-2134), so the bot discriminator is PLAYERINFO's
        // dtidDrone != NA (FedSrv.CPP:2088-2091), not "no PlayerInfo".
        int se = PumpUntil(rig.Server,
            () => rig.Leader.World.Ships.TryGetValue(npc.Id, out var se2)
                  && se2.SideId == rig.Side0
                  && se2.HullId == npc.HullType.HullId
                  && se2.CharacterName.Length > 0
                  && se2.Player is { DtidDrone: not PlayerInfo.NA } && se2.Latest is not null,
            4000);
        Assert.True(se < 4000, "leader's ClientWorld must be shown the same-side bot (EXPORT with SideId + hull identity + pose)");

        var cmd = LeaderBotCommandResolver.Resolve(rig.Leader.World, npc.Id);
        Assert.NotNull(cmd);
        Assert.StartsWith("DOCK", cmd.Value.Label);          // the flying bot -> DOCK prompt
        Assert.Equal(npc.Id, cmd.Value.TargetShipId);

        // Both clients must know the same real NPC before testing authority;
        // a missing target would otherwise make the negative result vacuous.
        int mateSeen = PumpUntil(rig.Server,
            () => rig.Mate.World.Ships.TryGetValue(npc.Id, out var memberBot)
                  && memberBot.SideId == rig.Side0 && memberBot.HullId == npc.HullType.HullId
                  && memberBot.CharacterName.Length > 0
                  && memberBot.Player is { DtidDrone: not PlayerInfo.NA } && memberBot.Latest is not null,
            4000);
        Assert.True(mateSeen < 4000, "non-leader B must receive the same-side NPC identity and pose");
        // A NON-leader on the same side sees NO command for the same bot.
        var mateCmd = LeaderBotCommandResolver.Resolve(rig.Mate.World, npc.Id);
        Assert.Null(mateCmd);

        Console.WriteLine($"P366: leader client {rig.Leader.World.MyShipId}(side {rig.Side0}) sees same-side bot {npc.Id} -> " +
                          $"'{cmd.Value.Label}' offered; non-leader client {rig.Mate.World.MyShipId} sees nothing (resolver null)");
    }
}
