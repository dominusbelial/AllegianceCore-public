using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    // A real transport send queue, with no socket or writer. Inspect the encoded
    // commands, not just TargetId (the original defence tests stopped there).
    private static object P700Sender(object bot)
    {
        var cs = Get<Allegiance.ClientCore.Session.ConnectionSession>(bot, "Cs");
        var transport = Get<TcpTransport>(cs, "_transport");
        var type = typeof(TcpTransport).GetNestedType("PeerSender", BindingFlags.NonPublic)!;
        var sender = RuntimeHelpers.GetUninitializedObject(type);
        foreach (string name in new[] { "_orderedQueue", "_streamQueue", "_freeNodes", "_coalesceNodes" })
            Set(sender, name, Activator.CreateInstance(type.GetField(name, Fields)!.FieldType));
        Set(sender, "_sync", new object());
        Set(sender, "_cts", new CancellationTokenSource());
        Set(sender, "_framePool", ArrayPool<byte>.Shared);
        Set(transport, "_clientSender", sender);
        return sender;
    }

    private static ShipUpdate P700LastControls(object sender)
    {
        var messages = new List<byte[]>();
        foreach (var frame in Get<IEnumerable>(sender, "_orderedQueue"))
        {
            byte[] bytes = Get<byte[]>(frame, "Frame");
            int length = Get<int>(frame, "Length");
            messages.AddRange(FedPacket.Unpack(bytes.AsSpan(5, length - 5)));
        }
        return ShipUpdate.Decode(messages.Last(m => FedMessageHeader.Read(m).Fmid == ShipUpdate.Fmid));
    }

    private static ServerShipUpdate P700Pose(short id, Vector3 position)
    {
        var bytes = new byte[ServerShipUpdate.Width];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, id);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), position.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16), position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(20), position.Z);
        var reader = new WireReader(bytes);
        return ServerShipUpdate.DecodeBody(ref reader);
    }

    private static void P700Move(ClientWorld world, short id, Vector3 position) =>
        world.Apply(new SingleShipUpdate { OidTarget = id, Ship = P700Pose(id, position) });

    private static void P700Weapon(object bot)
    {
        var loadout = Catalog.NativePreferredLoadout(15, "Fighter");
        Set(bot, "WeaponPartId", Catalog.FirstWeaponPartId(loadout.Parts));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void P700_ShipHitsInBattleSectorNeverBecomeStationDefense(bool friendlyAsset)
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World);
        cs.World.Apply(new SetCluster { Ship = Pose(1), SectorId = 2 });
        DefenseEnemy(cs.World);
        cs.World.Apply(new ShipStatusMessage { ShipId = 9, HullId = 15, SectorId = 2, StationId = -1 });
        cs.World.Apply(new ExportShip { ShipId = 8, SideId = 0, HullId = 15 });
        var bot = Bot(cs); DefenseNoSocketSender(bot); var mgr = Manager(bot);
        Set(bot, "TargetSector", (short?)2);
        Set(mgr, "_engagersSnapshot", new Dictionary<short, int>());
        cs.World.Apply(new ProjectileHullHit { SourceId = 9, TargetId = friendlyAsset ? (short)8 : (short)1, ClusterId = 2 });
        Call("DriveCombat", mgr, bot, 10.0, null);
        DefensePass(mgr, 10, bot);
        Assert.Equal("active", Assert.Single(mgr.ActiveBots()).State);
        Assert.Null(Get<short?>(bot, "DefenseSector"));
        Assert.Equal((short?)9, Get<short?>(bot, "TargetId"));
    }

    [Fact]
    public void P700_PassiveVisibleEnemyDoesNotStartOrRefreshDefense()
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); DefenseEnemy(cs.World);
        var bot = Bot(cs); DefenseNoSocketSender(bot); var mgr = Manager(bot);
        Set(bot, "TargetSector", (short?)4);
        DefensePass(mgr, 0, bot); Call("DriveCombat", mgr, bot, 0.0, null);
        Assert.False(Get<bool>(bot, "DefendingInPlace"));
        Assert.Null(Get<short?>(bot, "DefenseSector"));
        DefenseAlert(cs.World); DefensePass(mgr, 1, bot);
        Assert.NotNull(Get<short?>(bot, "DefenseSector"));
        DefensePass(mgr, 32, bot); Call("DriveCombat", mgr, bot, 32.0, null);
        Assert.Null(Get<short?>(bot, "DefenseSector"));
        Assert.False(Get<bool>(bot, "DefendingInPlace"));
        Assert.Equal(new short[] { 1, 2, 3, 4 }, Get<List<short>>(bot, "Path"));
    }

    [Fact]
    public void P700_DefenderApproachesThenFires_AndDoesNotParkAtMatchedZeroSpeed()
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); DefenseAlert(cs.World); DefenseEnemy(cs.World);
        var bot = Bot(cs); P700Weapon(bot); var sender = P700Sender(bot); var mgr = Manager(bot);
        // Keep the station out of the approach path, and the target on our nose.
        cs.World.Apply(new ExportStation { Station = Station(100, 0, 1, 10000) });
        P700Move(cs.World, 1, Vector3.Zero);
        P700Move(cs.World, 9, new Vector3(0, 0, -4000));
        DefensePass(mgr, 0, bot); Call("DriveCombat", mgr, bot, 1.0, null);
        var approach = P700LastControls(sender).Payload;
        Assert.True(approach.Controls.Throttle > 0 || (approach.StateM & ShipControlState.ForwardButton) != 0);
        Assert.Equal((short?)9, Get<short?>(bot, "TargetId"));
        P700Move(cs.World, 9, new Vector3(0, 0, -100));
        Call("DriveCombat", mgr, bot, 10.0, null);
        var firing = P700LastControls(sender).Payload;
        Assert.NotEqual(0, firing.StateM & ShipControlState.AllWeapons);
        Assert.NotEqual(0, firing.StateM & (ShipControlState.LeftButton | ShipControlState.RightButton | ShipControlState.ForwardButton));
        Assert.Equal(2, Get<Dictionary<short, int>>(mgr, "_tickEngagers")[9]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void P700_FriendlyObstruction_Repositions_AndFriendsBehindTargetDoNotBlock(bool behindTarget)
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        var bot = Bot(cs); P700Weapon(bot); var sender = P700Sender(bot); var mgr = Manager(bot);
        DefenseEnemy(cs.World); P700Move(cs.World, 9, new Vector3(0, 0, -100));
        cs.World.Apply(new ExportShip { ShipId = 8, HullId = 15, SideId = 0 });
        cs.World.Apply(new ShipStatusMessage { ShipId = 8, HullId = 15, SectorId = 1, StationId = -1 });
        P700Move(cs.World, 8, new Vector3(0, 0, behindTarget ? -150 : -50));
        Call("EngageShipAt", null, bot, cs, (short)9, 10.0, 1, 1, false);
        var controls = P700LastControls(sender).Payload;
        Assert.Equal(behindTarget, (controls.StateM & ShipControlState.AllWeapons) != 0);
        Assert.NotEqual(0, controls.StateM & (ShipControlState.LeftButton | ShipControlState.RightButton | ShipControlState.ForwardButton));
    }

    [Fact]
    public void P700_WarpExitClearancePrecedesImmediateDefense()
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); DefenseAlert(cs.World); DefenseEnemy(cs.World);
        var bot = Bot(cs); P700Weapon(bot); var sender = P700Sender(bot); var mgr = Manager(bot);
        Set(bot, "TargetSector", (short?)1); Set(bot, "ExitClearFrom", (Vector3?)Vector3.Zero);
        Set(bot, "ExitClearRadius", 60f);
        cs.World.Apply(new ExportStation { Station = Station(100, 0, 1, 10000) });
        P700Move(cs.World, 1, new Vector3(0, 0, -10));
        DefensePass(mgr, 0, bot); Call("DriveCombat", mgr, bot, 1.0, null);
        var controls = P700LastControls(sender).Payload;
        Assert.True(controls.Controls.Throttle > .9f);
        Assert.Equal(0, controls.StateM & ShipControlState.AllWeapons);
        Assert.Null(Get<short?>(bot, "TargetId"));
    }

    [Fact]
    public void P700_StationHitStartsDefense_OriginalHitTimeBoundsAllClear()
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); DefenseEnemy(cs.World);
        var bot = Bot(cs); DefenseNoSocketSender(bot); var mgr = Manager(bot);
        Set(bot, "TargetSector", (short?)4);
        cs.World.Apply(new ProjectileHullHit { SourceId = 9, TargetId = 100, ClusterId = 1 });
        Call("DriveCombat", mgr, bot, 0.0, null);
        DefensePass(mgr, 0, bot);
        Assert.True(Get<bool>(bot, "DefendingInPlace"));
        Assert.Equal((short?)1, Get<short?>(bot, "DefenseSector"));
        // Seeing the same hit over several ticks must not move its timestamp.
        DefensePass(mgr, 7, bot);
        DefensePass(mgr, 30, bot); Call("DriveCombat", mgr, bot, 30.0, null);
        Assert.Null(Get<short?>(bot, "DefenseSector"));
        Assert.False(Get<bool>(bot, "DefendingInPlace"));
        Assert.Equal(new short[] { 1, 2, 3, 4 }, Get<List<short>>(bot, "Path"));
    }

    [Fact]
    public void P700_TargetDirectlyBehindCommandsTurn()
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        var bot = Bot(cs); P700Weapon(bot); var sender = P700Sender(bot);
        DefenseEnemy(cs.World); P700Move(cs.World, 9, new Vector3(0, 0, 100));
        Call("EngageShipAt", null, bot, cs, (short)9, 10.0, 1, 1, false);
        var controls = P700LastControls(sender).Payload;
        Assert.True(Math.Abs(controls.Controls.Yaw) > .9f);
        Assert.Equal(0, controls.StateM & ShipControlState.AllWeapons);
    }

    [Fact]
    public void P700_GlobalRecallCannotDefendSectorWithoutFriendlyStation()
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); var bot = Bot(cs); var mgr = Manager(bot);
        var badKey = new DefenseKey(null, cs.World.MissionGeneration, 0, 2);
        Set(mgr, "_coordinationDecisions", new CoordinationDecisions([], [new(badKey, [Get<string>(bot, "Name")])]));
        Set(mgr, "_coordinationReceived", Environment.TickCount64);
        Call("ApplyGlobalDefense", mgr, ArrayOf(bot), 0.0, new HashSet<DefenseKey> { new(null, cs.World.MissionGeneration, 0, 1) });
        Assert.Null(Get<short?>(bot, "DefenseSector"));
    }

    private static Array ArrayOf(params object[] bots)
    {
        var array = Array.CreateInstance(BotType, bots.Length);
        for (int i = 0; i < bots.Length; i++) array.SetValue(bots[i], i);
        return array;
    }

    [Fact]
    public void P700_IdleMetricSamplesActualBots_ExcludesDockedLostAndTargetless()
    {
        using var reads = new DefenseLiveReads();
        var sessions = Enumerable.Range(0, 5).Select(_ => Session(true)).ToArray();
        try
        {
            var bots = sessions.Select((cs, i) => {
                var bot = Bot(cs); Set(bot, "Name", $"idle-{i}"); Set(bot, "TargetId", (short?)9);
                DefenseNoSocketSender(bot); return bot;
            }).ToArray();
            Set(bots[1], "TargetId", null);
            sessions[2].World.Apply(new DockedNotify { StationId = 100 });
            Set(bots[3], "Inactive", true);
            Set(bots[4], "LastMoveAt", 9.5);
            var mgr = Manager(bots); var metric = new IdleTargetMetrics();
            Set(mgr, "_idleTargetMetrics", metric);
            Call("SampleIdleTargets", mgr, ArrayOf(bots), 10.0);
            Assert.Equal(new IdleTargetReport(1, 1, 0), metric.Snapshot(10));
            Set(bots[0], "TargetId", null);
            Set(bots[4], "TargetId", null);
            Call("SampleIdleTargets", mgr, ArrayOf(bots), 11.0);
            Assert.Equal(new IdleTargetReport(0, 1, 0), metric.Snapshot(11));
        }
        finally { foreach (var cs in sessions) cs.Dispose(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void P700_AmbiguousOrWrongSectorShipHitDoesNotInventStationAttack(bool ambiguous)
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); DefenseEnemy(cs.World);
        if (ambiguous) cs.World.Apply(new ExportShip { ShipId = 100, HullId = 15, SideId = 0 });
        var bot = Bot(cs); DefenseNoSocketSender(bot); var mgr = Manager(bot);
        Set(bot, "TargetSector", (short?)4);
        cs.World.Apply(new ProjectileHullHit { SourceId = 9, TargetId = 100, ClusterId = ambiguous ? (short)1 : (short)2 });
        Call("DriveCombat", mgr, bot, 0.0, null); DefensePass(mgr, 0, bot);
        Assert.False(Get<bool>(bot, "DefendingInPlace"));
        Assert.Null(Get<short?>(bot, "DefenseSector"));
    }
}
