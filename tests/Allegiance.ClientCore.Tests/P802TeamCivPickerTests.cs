using System.Collections.Concurrent;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P802 — the client half of the team-lobby civilization picker.
/// <list type="bullet">
/// <item><b>Client send</b> — <see cref="ConnectionSession.SendChangeTeamCiv"/>
/// puts the native <c>FM_CS_CHANGE_TEAM_CIV(51)</c> layout on the wire
/// (<c>Inc/Messages.h:407-411</c>: two shorts + the random bool + align-2 pad),
/// proven over real loopback TCP.</item>
/// <item><b>Receive</b> — the native client mirror of
/// <c>IsideIGC::SetCivilization</c> (<c>Igc/sideigc.h:76-102</c>): the side's
/// development techs become the new civ's base techs and its stations are
/// retyped to the civ's initial station type, on top of the roster civ id
/// (P718) — so the docked shop/research UI follows the pick live.</item>
/// <item><b>Picker state</b> — leader vs non-leader and before vs after the
/// start (native gate <c>FedSrv.CPP:7344-7366</c>), the option list (core civs
/// below 300 + RANDOM, <c>teamscreen.cpp:899-916</c>, <c>igc.h:501</c>) and the
/// request it sends.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P802TeamCivPickerTests
{
    private static void Feed(ConnectionSession session, params IFedMessage[] messages) =>
        ((ITransportSite)session).OnReceive(ConnectionId.Server,
            FedPacket.Pack(messages.Select(m => m.Encode()).ToArray()));

    private static StaticCore LoadCore()
    {
        string root = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        return StaticCore.Load(IgcReader.ParseFile(Path.Combine(root, "PCore001.igc")));
    }

    /// <summary>A client seated on side 0 as its leader, mission NotStarted.</summary>
    private static ConnectionSession JoinedSession(StaticCore core)
    {
        var session = new ConnectionSession();
        session.World.MissionCoreResolver = _ => core;
        Feed(session, new MissionDef { SzIGCStaticFile = "pcore001" },
            new JoinedMission { ShipId = 10 },
            new SideRoster { Sides = [new(0, 1, 1, "Alpha"), new(1, 3, 1, "Bravo")] },
            new PlayerInfo { ShipId = 10, ISide = 0, CharacterName = "Leader" },
            new SetTeamLeader { SideId = 0, ShipId = 10 });
        var payload = new byte[ServerShipUpdate.Width];
        payload[0] = 10;   // SET_CLUSTER assigns MyShipId from the pose payload
        var reader = new WireReader(payload);
        session.World.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.DecodeBody(ref reader) });
        return session;
    }

    private sealed class Site(ConnectionSession? session = null) : ITransportSite
    {
        public readonly TaskCompletionSource<ConnectionId> Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<byte[]> Received = new();
        public readonly SemaphoreSlim Arrived = new(0);
        public void OnPeerJoined(ConnectionId id, string name) => Joined.TrySetResult(id);
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId id, ReadOnlySpan<byte> bytes)
        {
            if (session is not null) ((ITransportSite)session).OnReceive(id, bytes);
            foreach (var message in FedPacket.Unpack(bytes)) Received.Enqueue(message);
            Arrived.Release();
        }
    }

    /// <summary>⭐ The client can now SEND the picker's choice: the native header
    /// + <c>SideID@4</c>, <c>CivID@6</c>, <c>random@8</c>, one align-2 pad byte =
    /// 10 B (<c>Inc/Messages.h:407-411</c>), crossing real TCP exactly once.</summary>
    [Fact]
    public async Task SendChangeTeamCiv_sends_the_native_layout_over_real_tcp()
    {
        var hostSite = new Site();
        var host = new TcpTransport(hostSite);
        host.Host(0);
        using var session = new ConnectionSession();
        session.Connect(host.BoundPort, "p802");
        await hostSite.Joined.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 200 && !session.Connected; i++) await Task.Delay(10);
        Assert.True(session.Connected, "the session transport must be connected before it can send");

        session.SendChangeTeamCiv(sideId: 0, civId: 5, random: true);
        var decoded = ChangeTeamCiv.Decode(await NextChangeTeamCivAsync(hostSite));
        Assert.Equal((short)0, decoded.SideId);
        Assert.Equal((short)5, decoded.CivId);
        Assert.True(decoded.Random);

        session.SendChangeTeamCiv(sideId: 1, civId: 25);
        var direct = ChangeTeamCiv.Decode(await NextChangeTeamCivAsync(hostSite));
        Assert.Equal((short)1, direct.SideId);
        Assert.Equal((short)25, direct.CivId);
        Assert.False(direct.Random);
    }

    /// <summary>The transport sends its own join handshake before session
    /// messages, so scan the host's unpacked frames for CHANGE_TEAM_CIV.</summary>
    private static async Task<byte[]> NextChangeTeamCivAsync(Site host)
    {
        while (await host.Arrived.WaitAsync(TimeSpan.FromSeconds(10)))
            if (host.Received.TryDequeue(out var message)
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(2)) == ChangeTeamCiv.Fmid)
                return message;
        throw new TimeoutException("no CHANGE_TEAM_CIV(51) packet reached the host");
    }

    /// <summary>⭐ Receiving the CHANGE_TEAM_CIV echo applies the native client's
    /// local <c>SetCivilization</c> mirror: dev techs = the new civ's base techs,
    /// stations retyped to the civ's initial station type, roster civ updated —
    /// so the shop, investment pane and station art follow the pick live.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Civ_echo_retargets_dev_techs_and_side_stations()
    {
        Require.Artwork();
        var core = LoadCore();
        short target = core.Civilizations.Values
            .Where(c => c.CivilizationId is >= 0 and < 300 && c.CivilizationId != 1)
            .OrderBy(c => c.CivilizationId).First().CivilizationId;
        var civ = core.Civilizations[target];
        using var session = JoinedSession(core);
        var world = session.World;
        world.Apply(new ExportStation { Station = new DataStationIGC(
            [0, 0, 0], [0, 1, 0], [1, 0, 0], [0, 0, 0, 1], 1, 0, 42, 15, 255, 255, "Home") });
        Assert.Equal((short)15, world.Stations[42].StationTypeId);

        Feed(session, new ChangeTeamCiv { SideId = 0, CivId = target, Random = false });

        Assert.Equal(target, TeamCivPicker.CurrentCivId(world));
        Assert.Equal(TechTreeBitMask.FromBytes(civ.TtbmBaseTechs), world.DevelopmentTechsFor(0));
        Assert.Equal(civ.InitialStationTypeId, world.Stations[42].StationTypeId);
        Assert.Equal(target, world.SideRoster!.Sides[0].CivId);
    }

    /// <summary>The picker's option list: the core's civilizations below the
    /// native "prevent special civs" cut of 300 (<c>teamscreen.cpp:897-916</c>,
    /// <c>:2667</c>), ascending, then the RANDOM entry
    /// (<c>RANDOM_ID = 9999</c>, <c>igc.h:501</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Options_are_the_legal_core_civs_plus_random_last()
    {
        Require.Artwork();
        var core = LoadCore();
        var options = TeamCivPicker.Options(core);
        Assert.True(options.Count >= 2);
        Assert.True(options[^1].IsRandom);
        Assert.Equal(TeamCivPicker.RandomCivId, options[^1].CivId);
        Assert.Single(options, o => o.IsRandom);
        Assert.All(options.Take(options.Count - 1), o => Assert.InRange(o.CivId, (short)0, (short)299));
        Assert.Equal(options.Take(options.Count - 1).Select(o => o.CivId).OrderBy(id => id),
            options.Take(options.Count - 1).Select(o => o.CivId));
        Assert.Equal(options.Select(o => o.CivId).Distinct().Count(), options.Count);
        Assert.Empty(TeamCivPicker.Options(null));
    }

    /// <summary>⭐ Leader vs non-leader and before vs after the start: only the
    /// side's current leader may open the picker, and only while the mission is
    /// <c>STAGE_NOTSTARTED</c> (the server's own gate, FedSrv.CPP:7348-7360).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Only_the_side_leader_before_the_start_may_pick()
    {
        Require.Artwork();
        using var session = JoinedSession(LoadCore());
        var world = session.World;
        Assert.True(TeamCivPicker.CanPick(world));                      // leader of side 0, NotStarted

        Feed(session, new SetTeamLeader { SideId = 0, ShipId = 11 });   // someone else leads
        Assert.False(TeamCivPicker.CanPick(world));

        Feed(session, new SetTeamLeader { SideId = 0, ShipId = 10 });
        Assert.True(TeamCivPicker.CanPick(world));
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        Assert.False(TeamCivPicker.CanPick(world));                    // the game started: read-only
    }

    /// <summary>The request the picker sends: a concrete civ goes direct; RANDOM
    /// sends the current civ id as a placeholder with <c>random=true</c> (the
    /// server resolves it — see <c>Server.TeamCiv.cs</c>). Cursor movement wraps
    /// and never indexes an empty list.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Request_and_cursor_semantics()
    {
        Require.Artwork();
        var options = TeamCivPicker.Options(LoadCore());
        var concrete = options[0];
        Assert.Equal((concrete.CivId, false), TeamCivPicker.Request(concrete, currentCivId: 7));
        Assert.Equal(((short)7, true), TeamCivPicker.Request(options[^1], currentCivId: 7));
        Assert.Equal(((short)0, true), TeamCivPicker.Request(options[^1], currentCivId: null));
        Assert.Equal(1, TeamCivPicker.Move(0, 1, 3));
        Assert.Equal(2, TeamCivPicker.Move(0, -1, 3));       // wraps backward
        Assert.Equal(1, TeamCivPicker.Move(1, 0, 3));
        Assert.Equal(0, TeamCivPicker.Move(-1, 0, 3));       // unknown index clamps to 0
        Assert.Equal(0, TeamCivPicker.Move(4, 1, 0));        // empty list never indexes
        Assert.Equal(options.Count - 1, TeamCivPicker.IndexOf(options, options[^1].CivId));
        Assert.Equal(-1, TeamCivPicker.IndexOf(options, (short)301));
    }
}
