using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

// Probes exercise the production controller and wire-applied worlds without TCP.
[Collection("DecisionSnapshotFlag")]
public sealed partial class BotFixRegressionTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type BotType = typeof(BotManager).GetNestedType("ActiveBot", BindingFlags.NonPublic)!;
    private static void Set(object obj, string field, object? value) => obj.GetType().GetField(field, Fields)!.SetValue(obj, value);
    private static T Get<T>(object obj, string field) => (T)obj.GetType().GetField(field, Fields)!.GetValue(obj)!;
    private static object? Call(string method, object? instance, params object?[] args)
    {
        var m = typeof(BotManager).GetMethod(method, BindingFlags.Static | Fields)!;
        var parameters = m.GetParameters();
        return m.Invoke(instance, args.Concat(parameters.Skip(args.Length).Select(p => p.DefaultValue)).ToArray());
    }
    private static BotManager Manager(params object[] bots)
    {
        var manager = (BotManager)RuntimeHelpers.GetUninitializedObject(typeof(BotManager));
        Set(manager, "_gate", new object());
        var roster = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(BotType))!;
        foreach (var bot in bots) roster.Add(bot);
        Set(manager, "_bots", roster);
        Set(manager, "_removedBots", new List<BotManager.RemovedBotView>());
        Set(manager, "_tickEngagers", new Dictionary<short, int>());
        Set(manager, "_clock", System.Diagnostics.Stopwatch.StartNew());
        Set(manager, "_defenseSettings", BotManager.DefenseSettings.Read(_ => null));
        return manager;
    }
    private static object Bot(ConnectionSession? cs = null, short id = 1)
    {
        var bot = Activator.CreateInstance(BotType, Fields, null,
            ["probe-bot", (short)15, "fighter", (short)0, (short)0, "combat", 0.0], null)!;
        Set(bot, "Cs", cs);
        Set(bot, "ShipId", id);
        Set(bot, "SpawnPending", false);
        return bot;
    }
    private static ServerShipUpdate Pose(short id, float x = 0)
    {
        // Identity compact orientation is decoded from the same wire bytes as a peer.
        var bytes = new byte[ServerShipUpdate.Width];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, id);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), x);
        var reader = new WireReader(bytes);
        return ServerShipUpdate.DecodeBody(ref reader);
    }
    private static void Seat(ClientWorld world, short id = 1, short sector = 1, float x = 0)
    {
        world.Apply(new ExportShip { ShipId = id, HullId = 15, SideId = 0, CharacterName = "probe" });
        world.Apply(new SetCluster { Ship = Pose(id, x), SectorId = sector });
    }
    private static ConnectionSession Session(bool connected = false)
    {
        var cs = new ConnectionSession();
        Seat(cs.World);
        if (connected)
        {
            var transport = new TcpTransport(cs);
            typeof(TcpTransport).GetProperty(nameof(TcpTransport.IsConnected))!.SetValue(transport, true);
            Set(cs, "_transport", transport); // marks connectivity without opening a socket
        }
        return cs;
    }
    private static DataStationIGC Station(short id, short side, short sector, float x = 0) =>
        new([x, 0, 0], [0, 1, 0], [0, 0, -1], [0, 0, 0, 0], sector, side, id, 1, 255, 255, "base");

    [Fact]
    public void DockedPod_BuysPlayerLoadoutAndLaunches_WithBoundedRetries()
    {
        using var cs = Session();
        cs.World.Apply(new DockedNotify { StationId = 23 });
        var bot = Bot(cs);
        var parts = new List<ExpandedPartData>();
        Set(bot, "Parts", parts);
        Set(bot, "PodDocked", true);
        var requests = new List<(short Hull, bool Launch, IReadOnlyList<ExpandedPartData> Parts)>();
        Action<short, bool, IReadOnlyList<ExpandedPartData>> send = (h, l, p) => requests.Add((h, l, p));
        foreach (double now in new[] { 0.0, 0.1, 4.9, 5, 10, 15, 100 })
            Call("RelaunchDockedPod", null, bot, cs, now, send);
        Assert.Equal(3, requests.Count);
        Assert.All(requests, r => { Assert.Equal((short)15, r.Hull); Assert.True(r.Launch); Assert.Same(parts, r.Parts); });
        // In space a stale PodDocked flag cannot buy a ship.
        cs.World.Apply(new SetCluster { Ship = Pose(1), SectorId = 1 });
        Call("ResetNewLife", null, bot, 101.0, true);
        Call("RelaunchDockedPod", null, bot, cs, 102.0, send);
        Assert.Equal(3, requests.Count);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PodLife_AckUpdatesGrant_LaunchConfirmsNewLife_Twice()
    {
        Allegiance.TestInfra.Require.Artwork();
        var core = Catalog.RepresentativeCore();
        short podHull = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod)).HullId;
        short fighter = core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod) && h.MaxWeapons > 0).HullId;
        using var cs = Session(); var bot = Bot(cs); Set(bot, "HullId", fighter);
        for (int life = 0; life < 2; life++)
        {
            cs.World.Apply(new BuyLoadoutAck { HullId = podHull, Parts = [] });
            cs.World.Apply(new DockedNotify { StationId = 23 });
            Assert.True((bool)Call("UpdatePodState", null, bot, cs, 10.0 * life)!);
            Action<short, bool, IReadOnlyList<ExpandedPartData>> send = (_, _, _) => { };
            Call("RelaunchDockedPod", null, bot, cs, 10.0 * life + 1, send);
            ((ITransportSite)cs).OnReceive(ConnectionId.Server, FedPacket.Pack([new BuyLoadoutAck { HullId = fighter, Parts = [], FLaunch = true }.Encode()]));
            Assert.True((bool)Call("UpdatePodState", null, bot, cs, 10.0 * life + 2)!);
            Assert.Equal(fighter, Get<BotManager.LoadoutGrant>(bot, "Granted").HullId);
            cs.World.Apply(new SetCluster { Ship = Pose(1), SectorId = 1 });
            Assert.False((bool)Call("UpdatePodState", null, bot, cs, 10.0 * life + 3)!);
            Assert.False(Get<bool>(bot, "PodDocked"));
            Assert.Equal(BotManager.StageReleasedPhase, Get<byte>(bot, "StagePhase"));
        }
        Assert.Equal(2, Get<int>(bot, "Deaths")); Assert.Equal(2, Get<int>(bot, "PodsDockedTotal"));
        Assert.Equal(2, Manager(bot).GetCombatSummary().PodsDocked);
    }

    [Fact]
    public void NewLife_ClearsAllPodTravelAndTargetLatches_ForTwoDeaths()
    {
        var bot = Bot();
        for (int life = 0; life < 2; life++)
        {
            Set(bot, "IsPod", true); Set(bot, "PodDocked", true);
            Set(bot, "CloakHeld", true);
            Set(bot, "PodStationId", (short)23); Set(bot, "PodDockStationId", (short)23);
            Set(bot, "PodStationSector", (short)2); Set(bot, "Arrived", true);
            Set(bot, "TargetId", (short?)99); Set(bot, "OrderedTargetId", (short?)99);
            Set(bot, "WarpDest", new Dictionary<short, short> { [1] = 2 });
            Set(bot, "ExitClearFrom", (Vector3?)new Vector3(2, 3, 4));
            Get<List<short>>(bot, "Path").Add(1);
            Call("ResetNewLife", null, bot, 10.0 + life, true);
            Assert.False(Get<bool>(bot, "IsPod")); Assert.False(Get<bool>(bot, "PodDocked"));
            Assert.False(Get<bool>(bot, "CloakHeld"));
            Assert.Equal((short)-1, Get<short>(bot, "PodStationId"));
            Assert.Equal((short)-1, Get<short>(bot, "PodDockStationId"));
            Assert.Equal((short)-1, Get<short>(bot, "PodStationSector"));
            Assert.False(Get<bool>(bot, "Arrived")); Assert.Empty(Get<List<short>>(bot, "Path"));
            Assert.Null(Get<short?>(bot, "TargetId")); Assert.Null(Get<short?>(bot, "OrderedTargetId"));
            Assert.Null(Get<Dictionary<short, short>?>(bot, "WarpDest"));
            Assert.Null(Get<Vector3?>(bot, "ExitClearFrom"));
            Assert.Equal(BotManager.StageReleasedPhase, Get<byte>(bot, "StagePhase"));
        }
    }

    [Fact]
    public void LostBot_CannotDriveOrReserveEngagers_AndIsReportedOnRemoval()
    {
        using var cs = Session(true);
        var bot = Bot(cs);
        var mgr = Manager(bot);
        ((ITransportSite)cs).OnSessionLost();
        Assert.False((bool)Call("IsParticipating", null, bot)!);
        Set(bot, "Arrived", true);
        Call("DriveCombat", mgr, bot, 10.0, null);
        Assert.Empty(Get<Dictionary<short, int>>(mgr, "_tickEngagers"));
        Call("RemoveInactiveBot", mgr, bot, "connection lost");
        Assert.Empty(Get<IList>(mgr, "_bots"));
        Assert.Equal("connection lost", Assert.Single(mgr.RemovedBots()).Reason);
    }

    /// <summary>P813c — a bot whose session is inside its reconnect window is NOT
    /// gone: the server is holding its seat, so the roster must not reap it as
    /// "connection lost" (that would make the grace pointless for the 390-client
    /// rig). The pre-P813c contract — a lost session is not participating — is
    /// preserved when auto-reconnect is off (the test above).</summary>
    [Fact]
    public void ReconnectingBot_IsStillParticipating()
    {
        using var cs = Session(true);
        cs.EnableAutoReconnect("127.0.0.1", 1, "p813c-reconnect-bot", windowSeconds: 5);   // port 1: every dial fails
        var bot = Bot(cs);
        ((ITransportSite)cs).OnSessionLost();
        Assert.True(cs.Reconnecting, "a loss with auto-reconnect armed must enter the reconnect window, not latch Lost");
        Assert.False(cs.Lost);
        Assert.True((bool)Call("IsParticipating", null, bot)!, "a reconnecting bot stays on the roster");
    }

    [Fact]
    public void LostBot_DoesNotHoldLivingWing()
    {
        using var live = Session(true);
        using var dead = Session();
        var staged = Bot(live); var lost = Bot(dead);
        foreach (var b in new[] { staged, lost }) { Set(b, "UseStaging", true); Set(b, "TargetSector", (short?)4); }
        Set(staged, "StagePhase", BotManager.StageHoldPhase);
        Assert.True((bool)Call("StageReleaseRequired", Manager(staged, lost), (short)0, (short)4, -1, 1.0, true)!);
    }

    [Fact]
    public void MissionRotation_ResolvesNamedObjectiveAndClearsOldArrival_BeforeDriving()
    {
        using var cs = Session(); var bot = Bot(cs); var mgr = Manager(bot);
        Call("RefreshLife", mgr, bot, cs, 0.0);
        Set(bot, "Arrived", true); Set(bot, "TargetSector", (short?)4);
        Set(bot, "ObjectiveName", "Alderan"); Set(bot, "RoutePath", new List<short> { 1, 4 });
        cs.World.BeginNextMission(); Seat(cs.World, 2);
        Assert.False((bool)Call("RefreshLife", mgr, bot, cs, 1.0)!); // map is not ready yet
        Assert.False(Get<bool>(bot, "Arrived")); Assert.Null(Get<List<short>?>(bot, "RoutePath"));
        Assert.True(Get<bool>(bot, "MissionLaunchPending"));
        cs.World.Apply(new ClusterInfo { Clusters = [new(1, true), new(44, false)], Warps = [new(1, 44)] });
        cs.World.Apply(new ExportCluster { Cluster = new(0, [0, 1, 0], 0, 0, 0, 44, 0, 0, "Alderan", "", "", 0, 0, 0, true, false) });
        Assert.True((bool)Call("RefreshLife", mgr, bot, cs, 2.0)!);
        Assert.Equal((short?)44, Get<short?>(bot, "TargetSector"));
        Assert.Equal((short)2, Get<short>(bot, "ShipId"));
        Assert.Equal(BotManager.StageTransitPhase, Get<byte>(bot, "StagePhase"));
    }

    [Fact]
    public void SameShipId_NewMissionGenerationStillResetsLife()
    {
        using var cs = Session(); var bot = Bot(cs); var mgr = Manager(bot);
        Call("RefreshLife", mgr, bot, cs, 0.0);
        Set(bot, "Arrived", true);
        cs.World.BeginNextMission(); Seat(cs.World);
        Call("RefreshLife", mgr, bot, cs, 1.0);
        Assert.False(Get<bool>(bot, "Arrived")); Assert.True(Get<bool>(bot, "ObjectivesPending"));
    }

    [Theory]
    [InlineData("cookie")][InlineData("session")]
    public void ChangedIdentity_WithSameShipId_ResetsArrival(string identity)
    {
        using var first = Session(); using var second = Session(); var bot = Bot(first); var manager = Manager(bot);
        first.World.Apply(new MissionDef { DwCookie = 1 });
        Call("RefreshLife", manager, bot, first, 0.0); Set(bot, "Arrived", true);
        var current = first;
        if (identity == "cookie") first.World.Apply(new MissionDef { DwCookie = 2 });
        else { current = second; Set(bot, "Cs", current); }
        Call("RefreshLife", manager, bot, current, 1.0);
        Assert.False(Get<bool>(bot, "Arrived")); Assert.True(Get<bool>(bot, "ObjectivesPending"));
    }

    [Trait("Requires", "Artwork")]   // loads PCore001.igc (artwork corpus)
    [Fact]
    public void Bomber_SelectsOnlyEnemyStationsInDestination_AndReactsOnlyToVisibleAttackers()
    {
        Assert.Equal((short?)3, BotManager.PickEnemyStation(
            [Station(1, 0, 4), Station(2, 1, 9), Station(3, 1, 4, 100)], 0, 4, Vector3.Zero));
        Assert.Null(BotManager.PickEnemyStation([Station(1, 0, 4)], 0, 4, Vector3.Zero));
        using var cs = Session(); var bot = Bot(cs);
        cs.World.Apply(new ExportShip { ShipId = 9, SideId = 1, HullId = 15 });
        cs.World.Apply(new SingleShipUpdate { OidTarget = 9, Ship = Pose(9) });
        cs.World.Apply(new ShipStatusMessage { ShipId = 9, HullId = 15, SectorId = 1, StationId = -1 });
        Assert.False((bool)Call("HasVisibleAttacker", null, bot, cs, (short)1, 10.0)!);
        Get<Dictionary<short, double>>(bot, "RecentAttackers")[9] = 9;
        Assert.True((bool)Call("HasVisibleAttacker", null, bot, cs, (short)1, 10.0)!);
        Assert.False((bool)Call("HasVisibleAttacker", null, bot, cs, (short)1, 20.0)!);
    }

    [Fact]
    public void HeadlessProjectiles_StorageStaysEmpty_BeforeAndAfterDrains()
    {
        var world = new ClientWorld { CountOnlyProjectiles = true }; Seat(world);
        for (int i = 0; i < 10000; i++)
            world.Apply(new ProjectileInstance { LauncherId = (short)(i % 2 == 0 ? 1 : 2), Lifespan = 1 });
        Assert.Empty(world.Projectiles); Assert.Equal(5000, world.TakeOwnProjectileCount());
        Assert.Equal(0, world.TakeOwnProjectileCount()); Assert.Empty(world.TakeNewProjectiles());
        world.Apply(new ProjectileInstance { LauncherId = 1 });
        world.BeginNextMission(); Assert.Equal(0, world.TakeOwnProjectileCount());
        // Rendered worlds retain their original entity/drain behavior.
        var rendered = new ClientWorld(); rendered.Apply(new ProjectileInstance { LauncherId = 1 });
        Assert.Single(rendered.Projectiles); Assert.Single(rendered.TakeNewProjectiles()); Assert.Empty(rendered.TakeNewProjectiles());
    }

    [Fact]
    public void PlainBot_DisablesUnusedRawAccumulators()
    {
        using var bot = new global::Allegiance.Bot.Bot { RetainRawCombatUpdates = false };
        var shot = FedPacket.Pack([new ProjectileInstance { Lifespan = 1 }.Encode()]);
        var heavy = FedPacket.Pack([new HeavyShipsUpdate { TimeReference = 1, PositionReference = Vector3.Zero, HeavyShipEntries = [] }.Encode()]);
        for (int i = 0; i < 1000; i++) { ((ITransportSite)bot).OnReceive(ConnectionId.Server, shot); ((ITransportSite)bot).OnReceive(ConnectionId.Server, heavy); }
        Assert.Empty(bot.ProjectileInstances); Assert.Empty(bot.HeavyShipsUpdates);
    }

    [Fact]
    public void SpatialIndices_AreSharedWithinSideAndSector_AndExcludeOtherSides()
    {
        using var cs = Session(true); using var mate = Session(true); using var enemy = Session(true);
        cs.World.Apply(new ExportShip { ShipId = 2, SideId = 0, HullId = 15 });
        cs.World.Apply(new SingleShipUpdate { OidTarget = 2, Ship = Pose(2, 100) });
        cs.World.Apply(new ShipStatusMessage { ShipId = 2, HullId = 15, SectorId = 1, StationId = -1 });
        var a = Bot(cs); var b = Bot(mate); var c = Bot(enemy); Set(c, "Side", (short)1);
        var array = Array.CreateInstance(BotType, 3); array.SetValue(a, 0); array.SetValue(b, 1); array.SetValue(c, 2);
        foreach (var peer in new[] { cs, mate, enemy })
        {
            peer.World.RequestDecisionRefresh(); peer.World.PublishDecisionSnapshot();
        }
        Call("BuildPassSpatialIndices", null, array);
        Assert.Same(Get<Dictionary<short, BotSpatialIndex>>(a, "FriendlyGrids"), Get<Dictionary<short, BotSpatialIndex>>(b, "FriendlyGrids"));
        Assert.Empty(Get<Dictionary<short, BotSpatialIndex>>(c, "FriendlyGrids"));
        Assert.Equal(2, Get<Dictionary<short, BotSpatialIndex>>(a, "FriendlyGrids")[1].QueryCluster(1).Count);
    }

    [Fact]
    public void Selector_DoesNotDependOnUnusedTeammateCount()
    {
        BotTargetCandidate[] targets = [new(4, Vector3.Zero, BotTargetClass.Fighter, false)];
        var counts = new Dictionary<short, int>();
        Assert.Equal(CombatTargetSelector.Select(targets, new(1, 1, 0, counts)),
            CombatTargetSelector.Select(targets, new(1, 1, 390, counts)));
    }

    [Fact]
    public void Scheduler_DropsDebtAfterHitch_AndRecordsOverrun()
    {
        var cadence = new DecisionMetrics.LoopCadence(50); cadence.Begin(0);
        Assert.Equal(50, cadence.SleepForRemainder(341));
        Assert.Equal(1, cadence.OverrunCount);
        Assert.Equal(49, cadence.SleepForRemainder(392));
        Assert.Equal(49, cadence.SleepForRemainder(442));
        Assert.Equal(1, cadence.OverrunCount);
    }

    [Fact]
    public void KillAndChatSequences_SurviveRolloverAndReset_WithoutRecount()
    {
        var world = new ClientWorld(); long cursor = 0; int counted = 0;
        for (int batch = 0; batch < 8; batch++)
        {
            for (int i = 0; i < 200; i++) world.Apply(KillShip.FromVictim((short)i, IgcObjectType.Ship, 1));
            counted += world.Events.KillsAfter(cursor).Count(); cursor = world.Events.KillSequence;
            Assert.Empty(world.Events.KillsAfter(cursor));
        }
        Assert.Equal(1600, counted); Assert.Equal(EventLog.Bound, world.Events.Kills.Count);
        world.Reset(); world.Apply(KillShip.FromVictim(1, IgcObjectType.Ship, 2));
        Assert.Single(world.Events.KillsAfter(cursor));
        for (int i = 0; i < 510; i++) world.Apply(new ChatMessage { Message = i.ToString() });
        Assert.Equal(10, world.Events.ChatsAfter(500).Count());
    }

    private sealed class Peer : IDisposable { public bool Disposed; public void Dispose() => Disposed = true; }
    [Theory]
    [InlineData("combat")][InlineData("plain")][InlineData("commander")][InlineData("probe")]
    public void PendingOwnership_DisposesOnFailure_TransfersOnlyOnSuccess(string kind)
    {
        _ = kind; var peer = new Peer(); bool rollback = false;
        Assert.Throws<InvalidOperationException>((Action)(() => {
            using var pending = new PendingOwnership(); pending.Own(peer);
            pending.OnFailure(() => rollback = true); throw new InvalidOperationException("preflight failed");
        }));
        Assert.True(peer.Disposed); Assert.True(rollback);
        var accepted = new Peer(); using (var pending = new PendingOwnership()) { pending.Own(accepted); pending.Transfer(); }
        Assert.False(accepted.Disposed);
    }

    [Theory]
    [InlineData("Bomber")][InlineData("Fighter")]
    public void MissingTarget_EngagesInPlace(string role) =>
        Assert.Null(BotManager.ResolveScenarioTarget(Enum.Parse<Showcase.HullRole>(role), null, 9));

    [Theory]
    [InlineData("dead")][InlineData("gone")][InlineData("missing")]
    public void DeadOrderedTarget_IsClearedBeforeAutonomousCombat(string state)
    {
        using var cs = Session(true); var bot = Bot(cs);
        Set(bot, "OrderedTargetId", (short?)9);
        if (state != "missing") cs.World.Apply(new ExportShip { ShipId = 9, HullId = 15, SideId = 1 });
        if (state == "dead") cs.World.Apply(KillShip.FromVictim(9, IgcObjectType.Ship, 2));
        if (state == "gone") cs.World.Apply(new ShipDelete { ShipId = 9, Sdr = ShipDelete.SdrLeftSector });
        Call("ClearInvalidOrderedTarget", null, bot, cs);
        Assert.Null(Get<short?>(bot, "OrderedTargetId"));
        var alternative = new PreparedEngagement([new(10, Vector3.Zero, BotTargetClass.Fighter, false)], false);
        var choice = (BotTargetChoice)Call("ChooseCombatTarget", Manager(bot), bot, cs, Vector3.Zero, (short)1, alternative)!;
        Assert.Equal((short?)10, choice.TargetId);

    }

    [Fact]
    public void SyncBarrier_ExaminesOtherWingsOfSameGoal_AndTimeoutStaggerWorks()
    {
        using var a = Session(true); using var b = Session(true); var wing0 = Bot(a); var wing1 = Bot(b);
        foreach (var bot in new[] { wing0, wing1 }) { Set(bot, "UseStaging", true); Set(bot, "TargetSector", (short?)4); Set(bot, "FirstStagingAt", (double?)0); }
        Set(wing0, "RouteIndex", 0); Set(wing0, "StagePhase", BotManager.StageHoldPhase);
        Set(wing0, "RoutePath", new List<short> { 1, 2, 4 });
        Set(wing1, "RouteIndex", 1); Set(wing1, "RoutePath", new List<short> { 1, 2, 3, 4 });
        var mgr = Manager(wing0, wing1);
        Assert.False((bool)Call("StageReleaseRequired", mgr, (short)0, (short)4, 0, 1.0, true)!);
        Assert.True((bool)Call("StageReleaseRequired", mgr, (short)0, (short)4, 0, 1.0, false)!);
        Assert.False((bool)Call("StageReleaseRequired", mgr, (short)0, (short)4, 0, BotManager.StagingHoldSeconds + 0.1, true)!);
        Assert.True((bool)Call("StageReleaseRequired", mgr, (short)0, (short)4, 0, BotManager.StagingHoldSeconds + BotManager.RouteTransitSecondsPerHop + 0.1, true)!);
        Set(wing1, "StagePhase", BotManager.StageHoldPhase);
        Assert.True((bool)Call("StageReleaseRequired", mgr, (short)0, (short)4, 0, 1.0, true)!);
    }

    [Fact]
    public void Staging_UsesAssignedPrefix_AndFallbackAvoidsRendezvous()
    {
        using var cs = Session(); var bot = Bot(cs);
        cs.World.Apply(new ClusterInfo { Clusters = [], Warps = [new(1, 2), new(2, 3), new(3, 5), new(5, 6), new(6, 4), new(1, 4)] });
        Set(bot, "UseStaging", true); Set(bot, "TargetSector", (short?)4); Set(bot, "StagingSector", (short)6);
        Set(bot, "RoutePath", new List<short> { 1, 2, 3, 5, 6, 4 });
        Call("EnsureCombatPath", null, bot, (short)1, (short)6, 0.0);
        Assert.Equal(new short[] { 1, 2, 3, 5, 6 }, Get<List<short>>(bot, "Path"));
        Set(bot, "RoutePath", null); Get<List<short>>(bot, "Path").Clear();
        Call("EnsureCombatPath", null, bot, (short)1, (short)6, 1.0);
        Assert.DoesNotContain((short)4, Get<List<short>>(bot, "Path"));
    }

    [Fact]
    public void SpatialGrid_EnlargesCellsToCap_WithoutDroppingOccupants()
    {
        BotSpatialIndex.ShipPoint[] points = [new(1, new(-1000000, 0, -1000000)), new(2, new(1000000, 0, 1000000)), new(3, Vector3.Zero)];
        var grid = BotSpatialIndex.Build(1, points, 1);
        Assert.Equal(3, grid.QueryCluster(1).Count);
        foreach (var p in points) Assert.Equal(p.Id, Assert.Single(grid.QueryRadius(p.Position, 1)).Id);
    }

    [Fact]
    public void ActualHull_UsesWorldGrant_IncludingPod_AheadOfRequestedHull()
    {
        using var cs = Session(); var bot = Bot(cs);
        cs.World.Apply(new BuyLoadoutAck { HullId = 113, Parts = [] });
        Assert.Equal((short)113, (short)Call("ActualHullId", null, bot)!);
        Assert.Equal((short)15, Get<short>(bot, "HullId"));
    }

    [Fact]
    public void SnapshotAttitude_ExtrapolatesWithNativeAxisSigns()
    {
        var ship = new DecisionShip { R00 = 1, R11 = 1, R22 = 1, TurnYaw = 3.75f, TurnPitch = 1, TurnRoll = 0.5f };
        var expected = Orientation.Identity; expected.Yaw(0.15f); expected.Pitch(-0.04f); expected.Roll(0.02f); expected.Renormalize();
        var actual = DecisionSnapshot.DeadReckonOrientation(ship, 40);
        Assert.True((expected.GetForward() - actual.GetForward()).Length() < 0.00001f);
        Assert.True((actual.GetForward() - ship.MaterializeOrientation().GetForward()).Length() > 0.1f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecisionReadMode_FollowsWorldFlag_AfterBotManagerInitialization(bool initialMode)
    {
        bool saved = ClientWorld.DecisionSnapshotEnabled;
        try
        {
            ClientWorld.DecisionSnapshotEnabled = initialMode;
            RuntimeHelpers.RunClassConstructor(typeof(BotManager).TypeHandle);
            using var cs = Session();
            ClientWorld.DecisionSnapshotEnabled = true;
            cs.World.RequestDecisionRefresh();
            cs.World.PublishDecisionSnapshot();
            Assert.Equal((short)1, (short)Call("CsSector", null, cs)!);

            // Keep the published frame at sector 1 while live wire state moves.
            cs.World.Apply(new SetCluster { Ship = Pose(1, 200), SectorId = 2 });
            var held = cs.World.Decision;
            foreach (bool enabled in new[] { false, true, false, true })
            {
                ClientWorld.DecisionSnapshotEnabled = enabled;
                Assert.Equal(enabled ? (short)1 : (short)2, (short)Call("CsSector", null, cs)!);
                object?[] poseArgs = [cs, (short)1, null, null];
                var poseMethod = typeof(BotManager).GetMethod("TryCsPose", BindingFlags.Static | Fields)!;
                Assert.True((bool)poseMethod.Invoke(null, poseArgs)!);
                Assert.Equal(enabled ? 0f : 200f, ((Vector3)poseArgs[2]!).X);
                if (!enabled)
                {
                    cs.World.RequestDecisionRefresh();
                    Assert.Same(held, cs.World.PublishDecisionSnapshot());
                }
            }
        }
        finally { ClientWorld.DecisionSnapshotEnabled = saved; }
    }

    [Fact]
    [Trait("Tier", "Perf")]
    public void FreshSnapshotPublish_AllocationStaysBoundedAt390Ships()
    {
        bool saved = ClientWorld.DecisionSnapshotEnabled;
        try
        {
            ClientWorld.DecisionSnapshotEnabled = true;
            var world = new ClientWorld { ReckonRemotes = false }; Seat(world);
            for (short i = 2; i <= 390; i++)
            {
                world.Apply(new ExportShip { ShipId = i, HullId = 15, SideId = 0 });
                world.Apply(new SingleShipUpdate { OidTarget = i, Ship = Pose(i) });
            }
            world.RequestDecisionRefresh();
            long allocated = 0;
            for (int round = 0; round < 20; round++)
            {
                world.Apply(new SetCluster { Ship = Pose(1, round), SectorId = 1 });
                Set(world, "_lastDecisionSnapPublishTick", -100L);
                long before = GC.GetAllocatedBytesForCurrentThread();
                var snapshot = world.PublishDecisionSnapshot();
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.Equal(390, snapshot.Ships.Length);
            }
            double perShip = allocated / (20.0 * 390);
            Console.WriteLine($"[botfix] fresh snapshot allocation: {perShip:F0} B/ship, 390 ships, 20 publishes");
            Assert.True(perShip < 1024, $"snapshot publish allocated {perShip:F0} B/ship");
        }
        finally { ClientWorld.DecisionSnapshotEnabled = saved; }
    }

    [Fact]
    public void SnapshotFrame_IsOneGeneration_EvenWhenWorldPublishesDuringDecision()
    {
        using var cs = Session();
        var captured = new DecisionSnapshot { Version = 1, MyCluster = 1 };
        var scopeType = typeof(BotManager).GetNestedType("DecisionFrameScope", BindingFlags.NonPublic)!;
        using var scope = (IDisposable)Activator.CreateInstance(scopeType, Fields, null, [cs, captured], null)!;
        Set(cs.World, "_publishedDecision", new DecisionSnapshot { Version = 2, MyCluster = 2 });
        Assert.Same(captured, Call("SnapshotFor", null, cs));
        Assert.Same(captured, Call("SnapshotFor", null, cs));
    }

    [Fact]
    public void HeldSnapshot_IsStableBeyondEightPublishes_AndAgeSamplesAreBounded()
    {
        bool saved = ClientWorld.DecisionSnapshotEnabled;
        try
        {
            ClientWorld.DecisionSnapshotEnabled = true;
            var world = new ClientWorld(); Seat(world); world.RequestDecisionRefresh();
            Set(world, "_lastDecisionSnapPublishTick", -100L); var held = world.PublishDecisionSnapshot();
            var initial = held.Ships[0].Position;
            for (int i = 1; i <= 20; i++)
            {
                world.Apply(new SetCluster { Ship = Pose(1, i), SectorId = 1 });
                Set(world, "_lastDecisionSnapPublishTick", -100L); world.PublishDecisionSnapshot();
                Assert.Equal(initial, held.Ships[0].Position);
            }
            Assert.NotSame(held, world.Decision); Assert.Equal(1, held.Version);
        }
        finally { ClientWorld.DecisionSnapshotEnabled = saved; }
        var ageType = typeof(BotManager);
        var ages = (double[])ageType.GetField("_snapAgesMs", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        ageType.GetField("_snapAgeNext", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, 0);
        ageType.GetField("_snapAgeCount", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, 0);
        for (int i = 0; i < ages.Length + 10; i++) Call("RecordSnapshotAgeSample", null, (double)i);
        Assert.Equal((double)ages.Length, ages[0]); Assert.Equal(10.0, ages[10]);
        Assert.Equal(ages.Length, ageType.GetField("_snapAgeCount", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
        var summary = ((double Mean, double P95, double Max))Call("SnapAgeSummary", null)!;
        Assert.Equal(ages.Length + 9, summary.Max);
        Assert.Equal((10.0 + ages.Length + 9) / 2, summary.Mean);
        // Instrumentation uses an array ring, never an ever-shifting list.
        Assert.IsType<double[]>(typeof(BotManager).GetField("_snapAgesMs", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
    }
}
