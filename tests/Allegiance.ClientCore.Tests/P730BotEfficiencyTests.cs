using Allegiance.ClientCore.Replay;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

[Collection("DecisionSnapshotFlag")]
public sealed class P730BotEfficiencyTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static void Feed(ConnectionSession session, params IFedMessage[] messages) =>
        ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack(messages.Select(m => m.Encode()).ToArray()));
    private static SetCluster Seat(short id, short sector = 1, uint time = 100) => new()
    {
        SectorId = sector, Cookie = (byte)id,
        Ship = ServerShipUpdate.FromValues(id, 0, new(0, 0, 0, 1), time, new(id, 0, 0), Orientation.Identity,
            Vector3.Zero, new float[4], 1, 1, 1, 1, 1, 0, 0)
    };
    private static void Init(ConnectionSession session, short id)
    {
        Feed(session, new MissionDef { DwCookie = 730 }, Seat(id), new ExportShip { ShipId = -1, SideId = 0 });
    }

    [Fact]
    public void Side_tables_share_remote_entities_but_keep_own_status_loadout_wallet_and_orders_private()
    {
        var pool = new SharedBotWorlds();
        using var a = new ConnectionSession(pool); using var b = new ConnectionSession(pool);
        Init(a, 10); Init(b, 11);
        Feed(a, new ExportShip { ShipId = 30, HullId = 7, SideId = 1, CharacterName = "enemy" });
        Assert.Same(a.Gate, b.Gate);
        Assert.Same(a.World.Stations, b.World.Stations);
        Assert.Same(a.World.Asteroids, b.World.Asteroids);
        Assert.Same(a.World.Ships[30], b.World.Ships[30]);
        Feed(a, new BuyLoadoutAck { HullId = 7, Parts = [] }, new SetMoney { ShipId = 10, Money = 100 },
            new QueuedCommandUpdate { CommandId = OrderChange.CidAttack, TargetType = IgcObjectType.Ship, TargetId = 30 });
        Feed(b, new BuyLoadoutAck { HullId = 8, Parts = [] }, new SetMoney { ShipId = 11, Money = 200 },
            new ExportShip { ShipId = 10, HullId = 99, SideId = 0 });
        Assert.Equal(7, a.World.Ships[10].HullId); Assert.Equal(8, b.World.Ships[11].HullId);
        Assert.NotSame(a.World.Ships[10], b.World.Ships[10]);
        Assert.Equal(100, a.World.MyMoney); Assert.Equal(200, b.World.MyMoney);
        Assert.NotNull(a.World.MyQueuedCommand); Assert.Null(b.World.MyQueuedCommand);
        Assert.Equal((short?)10, a.World.MyShipId); Assert.Equal((short?)11, b.World.MyShipId);
        Assert.Equal(10, a.World.Ships[10].Cookie); Assert.Equal(11, b.World.Ships[11].Cookie);
    }

    [Fact]
    public void Identical_wire_is_decoded_once_and_repeated_states_still_apply()
    {
        var pool = new SharedBotWorlds();
        using var a = new ConnectionSession(pool); using var b = new ConnectionSession(pool);
        Init(a, 10); Init(b, 11);
        var first = new ExportShip { ShipId = 30, HullId = 7, SideId = 1, CharacterName = "enemy" };
        long decoded = pool.DecodedMessages, reused = pool.ReusedMessages;
        Feed(a, first); Feed(b, first);
        Assert.Equal(1, pool.DecodedMessages - decoded); Assert.Equal(1, pool.ReusedMessages - reused);
        Feed(a, new ExportShip { ShipId = 30, HullId = 8, SideId = 1 });
        Assert.Equal(8, b.World.Ships[30].HullId);
        Feed(b, first);
        Assert.Equal(7, a.World.Ships[30].HullId);
    }

    [Fact]
    public void Auto_seating_on_another_side_cannot_read_or_pollute_the_requested_sides_world()
    {
        var pool = new SharedBotWorlds(0);
        using var a = new ConnectionSession(pool); using var b = new ConnectionSession(pool);
        Init(a, 10);
        Feed(a, new ExportShip { ShipId = 30, SideId = 1, CharacterName = "side-zero-only" });
        Feed(b, new MissionDef { DwCookie = 730 }, Seat(11), new ExportShip { ShipId = -1, SideId = 1 });
        Assert.False(b.World.Ships.ContainsKey(30));
        Assert.NotSame(a.World.Stations, b.World.Stations);
        Feed(b, new ExportShip { ShipId = 40, SideId = 0, CharacterName = "side-one-only" });
        Assert.False(a.World.Ships.ContainsKey(40));
        Assert.Equal((short?)1, b.World.MySideId);
        Assert.Equal(11, b.World.Ships[11].Cookie);
        using var same = new ConnectionSession(pool); Init(same, 12);
        Assert.Same(a.World.Stations, same.World.Stations);
        Assert.Equal(12, same.World.Ships[12].Cookie);
    }

    [Fact]
    public void Opposing_sides_and_missions_never_share_knowledge()
    {
        var pool = new SharedBotWorlds();
        using var a = new ConnectionSession(pool); using var b = new ConnectionSession(new SharedBotWorlds());
        Init(a, 10); Init(b, 11);
        Feed(a, new ExportShip { ShipId = 30, SideId = 1 });
        Assert.False(b.World.Ships.ContainsKey(30));
        using var other = new ConnectionSession(pool);
        Feed(other, new MissionDef { DwCookie = 731 }, Seat(12));
        Assert.False(other.World.Ships.ContainsKey(30));
        Feed(a, new MissionDef { DwCookie = 731 });
        Assert.False(a.World.Ships.ContainsKey(30));
        Assert.Same(other.World.Stations, a.World.Stations);
    }

    [Fact]
    public void Rotation_reconnect_and_late_join_cannot_clear_another_pilots_world()
    {
        var pool = new SharedBotWorlds();
        using var a = new ConnectionSession(pool); using var b = new ConnectionSession(pool);
        Init(a, 10); Init(b, 11);
        var old = b.World.Stations;
        a.World.BeginNextMission();
        Init(a, 20);
        Assert.NotSame(old, a.World.Stations);
        b.World.BeginNextMission();
        Init(b, 21);
        Assert.Same(a.World.Stations, b.World.Stations);
        Feed(a, new ExportShip { ShipId = 40, SideId = 1 });
        using var late = new ConnectionSession(pool); Init(late, 22);
        Assert.Same(a.World.Stations, late.World.Stations);
        Assert.Same(a.World.Ships[40], late.World.Ships[40]);
        b.World.Reset();
        Assert.True(a.World.Ships.ContainsKey(40));
        Init(b, 23);
        Assert.Same(a.World.Stations, b.World.Stations);
        b.Dispose(); Assert.True(a.World.Ships.ContainsKey(40));
    }

    [Fact]
    public void Sector_change_preserves_side_knowledge_and_private_pose()
    {
        var pool = new SharedBotWorlds();
        using var a = new ConnectionSession(pool); using var b = new ConnectionSession(pool);
        Init(a, 10); Init(b, 11);
        Feed(a, new ExportShip { ShipId = 30, SideId = 1 },
            new ShipStatusMessage { ShipId = 30, SectorId = 1, StationId = -1, ParentId = -1, State = ShipStatusMessage.StateFlying }, new SingleShipUpdate { OidTarget = 30, Ship = Seat(30).Ship });
        Feed(b, Seat(11, 2));
        Assert.NotNull(a.World.Ships[30].Latest);
        Assert.False(a.World.Ships[30].IsGone);
        Assert.Equal(1, a.World.Ships[10].SectorId); Assert.Equal(2, b.World.Ships[11].SectorId);
    }

    [Fact]
    public void Snapshot_arrays_are_shared_immutable_and_keep_per_pilot_identity()
    {
        bool saved = ClientWorld.DecisionSnapshotEnabled; ClientWorld.DecisionSnapshotEnabled = true;
        try
        {
            var pool = new SharedBotWorlds();
            using var a = new ConnectionSession(pool); using var b = new ConnectionSession(pool);
            Init(a, 10); Init(b, 11);
            Feed(b, new ShipStatusMessage { ShipId = 11, SectorId = 1, StationId = 42, State = ShipStatusMessage.StateDocked });
            a.World.NowSeconds = b.World.NowSeconds = () => 1;
            a.World.RequestDecisionRefresh(); b.World.RequestDecisionRefresh();
            var x = a.World.PublishDecisionSnapshot(); var y = b.World.PublishDecisionSnapshot();
            Assert.Same(x.Ships, y.Ships); Assert.Same(x.Warps, y.Warps);
            Assert.Equal((short?)10, x.MyShipId); Assert.Equal((short?)11, y.MyShipId);
            Assert.Equal(new short[] { 10, 11 }, x.Ships.Select(s => s.ShipId));
            Assert.DoesNotContain(x.VisibleShips, ship => ship.ShipId == 11);
            Assert.Equal(42, b.World.Ships[11].StationId);
            a.World.NowSeconds = () => 2;
            Feed(a, Seat(10, 2, 200));
            var fresh = a.World.PublishDecisionSnapshot();
            Assert.NotSame(x.Ships, fresh.Ships);
            Assert.Equal(1, x.Ships[0].SectorId); Assert.Equal(2, fresh.Ships[0].SectorId);
        }
        finally { ClientWorld.DecisionSnapshotEnabled = saved; }
    }

    private static string BattlePath()
    {
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
        {
            string path = Path.Combine(root.FullName, "data", "benchmarks", "battle.alr");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Bundled P729 battle missing");
    }

    [SkippableFact]
    public void Recorded_battle_produces_identical_target_and_steering_decisions_with_sharing()
    {
        Require.Artwork();
        var replay = new BattleReplay(BattlePath());
        using var original = new ConnectionSession(); using var shared = new ConnectionSession(new SharedBotWorlds());
        int compared = 0, engaged = 0;
        foreach (var packet in replay.Packets.Where(p => p.Payload.Length > 0))
        {
            original.ReplayPacket(packet.Payload, packet.Microseconds / 1000);
            shared.ReplayPacket(packet.Payload, packet.Microseconds / 1000);
            if (original.World.MyShipId is not { } me || !original.World.Ships.TryGetValue(me, out var own) || own.Latest is not { } pose) continue;
            // Same production candidate predicate and native steer law, evaluated
            // after every recorded delivery (including warps, kills and relaunches).
            var expected = Choose(original.World, me, own.SectorId, pose.Position);
            var actual = Choose(shared.World, me, own.SectorId, pose.Position);
            Assert.Equal(expected, actual);
            compared++;
            if (expected.Choice.TargetId is not null) engaged++;
        }
        Assert.True(compared > 1000, $"Compared only {compared} recorded decisions");
        Assert.True(engaged > 100, $"Only {engaged} combat decisions");
        output.WriteLine($"recorded deliveries compared={compared}, combat decisions={engaged}");
    }
    private static (BotTargetChoice Choice, float Yaw, float Pitch, float Throttle) Choose(ClientWorld world, short me, short sector, Vector3 position)
    {
        var own = world.Ships[me];
        var candidates = world.Ships.Values.Where(s => s.ShipId != me && s.SideId != own.SideId && !s.IsNotVisible
                && s.SectorId == sector && s.Latest is not null)
            .Select(s => new BotTargetCandidate(s.ShipId, s.Latest!.Position, CombatTargetSelector.ClassifyTargetId(s.HullId), false)
                { Distance = (s.Latest.Position - position).Length() })
            .Where(s => s.Class != BotTargetClass.MinerDrone).ToList();
        var choice = CombatTargetSelector.Select(candidates, new(me, sector, 1, new Dictionary<short, int>()));
        var target = choice.TargetId is { } id ? world.Ships[id].Latest!.Position : position;
        var control = Allegiance.ClientCore.Flight.AutopilotSteering.SteerToward(position, own.Latest!.Orientation, target, own.Latest.Velocity);
        return (choice, control.Yaw, control.Pitch, control.Throttle);
    }

    [Theory]
    [InlineData(1, 10, 1)] [InlineData(4, 30, 1)] [InlineData(8, 60, 1)] [InlineData(32, 120, 2)]
    public void Auto_workers_use_core_and_fleet_budgets(int cores, int bots, int workers)
    {
        var budget = SkirmishBudget.ForCores(cores, bots);
        Assert.Equal(workers, budget.Workers);
        Assert.True(budget.Workers * budget.CoresPerWorker <= Math.Max(1, cores - 2));
    }

    [Fact]
    public void Distant_lod_is_two_hz_near_humans_and_disabled_modes_keep_every_pass()
    {
        Assert.False(BotLodPolicy.Due(true, false, .49, 0, .5));
        Assert.True(BotLodPolicy.Due(true, false, .5, 0, .5));
        Assert.True(BotLodPolicy.Due(true, true, .01, 0, .5));
        Assert.True(BotLodPolicy.Due(false, false, .01, 0, .5));
        Assert.True(BotLodPolicy.Due(true, false, 0, 10, .5));
        Assert.Equal(.5, BotLodPolicy.ReadInterval("NaN"));
        Assert.Equal(.25, BotLodPolicy.ReadInterval("4"));
        Assert.False(BotLodPolicy.IsHumanName("research-cmd0-1-1234"));
        Assert.False(BotLodPolicy.IsHumanName("mcp-w1-b001"));
        Assert.True(BotLodPolicy.IsHumanName("local-pilot"));
    }
}
