using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    private sealed class DefenseLiveReads : IDisposable
    {
        private readonly bool _saved = ClientWorld.DecisionSnapshotEnabled;
        public DefenseLiveReads() => ClientWorld.DecisionSnapshotEnabled = false;
        public void Dispose() => ClientWorld.DecisionSnapshotEnabled = _saved;
    }
    private static void DefenseMap(ClientWorld world)
    {
        world.Apply(new ClusterInfo { Clusters = [new(1, true), new(2, false), new(3, false), new(4, false)],
            Warps = [new(1, 2), new(2, 3), new(3, 4)] });
        world.Apply(new ExportCluster { Cluster = new(0, [0, 1, 0], 0, 0, 0, 1, 0, 0, "Home", "", "", 0, 0, 0, true, false) });
        world.Apply(new ExportStation { Station = Station(100, 0, 1) });
    }
    private static void DefenseNoSocketSender(object bot)
    {
        var cs = Get<Allegiance.ClientCore.Session.ConnectionSession>(bot, "Cs");
        var transport = Get<TcpTransport>(cs, "_transport");
        var senderType = typeof(TcpTransport).GetNestedType("PeerSender", System.Reflection.BindingFlags.NonPublic)!;
        var sender = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(senderType);
        Set(sender, "_sync", new object()); Set(sender, "_disconnected", true);
        Set(sender, "_cts", new CancellationTokenSource());
        Set(transport, "_clientSender", sender); // command sends terminate at a closed queue; no socket exists
    }
    private static void DefenseAlert(ClientWorld world, bool critical = false) => world.Apply(new ChatMessage
    {
        ChatTarget = (int)ChatTarget.Team, VoiceOver = 1234,
        Message = critical ? "base in Home critically damaged and under attack by foe" : "base in Home under attack by foe",
    });
    private static void DefensePass(BotManager manager, double now, params object[] bots)
    {
        var array = Array.CreateInstance(BotType, bots.Length);
        for (int i = 0; i < bots.Length; i++) array.SetValue(bots[i], i);
        Call("UpdateDefense", manager, array, now);
    }
    private static void DefenseEnemy(ClientWorld world, short id = 9, short hull = 15, float x = 100) {
        world.Apply(new ExportShip { ShipId = id, SideId = 1, HullId = hull });
        world.Apply(new SingleShipUpdate { OidTarget = id, Ship = Pose(id, x) });
        world.Apply(new ShipStatusMessage { ShipId = id, HullId = hull, SectorId = 1, StationId = -1 });
    }

    [Fact]
    public void DefenseAlert_BoundedNearestWing_ResumesOriginalRouteAfterQuietAndMinimum()
    {
        using var reads = new DefenseLiveReads();
        var sessions = Enumerable.Range(1, 20).Select(_ => Session(true)).ToArray();
        try
        {
            var bots = sessions.Select((cs, i) => {
                cs.World.Reset();
                Seat(cs.World, (short)(i + 1), (short)(i < 2 ? 1 : i < 5 ? 2 : 3), i == 0 ? 500 : 100);
                DefenseMap(cs.World);
                var bot = Bot(cs, (short)(i + 1));
                DefenseNoSocketSender(bot);
                Set(bot, "TargetSector", (short?)4); Set(bot, "UseStaging", true);
                Set(bot, "RoutePath", new List<short> { 1, 2, 3, 4 });
                return bot;
            }).ToArray();
            Set(bots[1], "Role", "bomber"); // closest bomber must never be pulled
            Set(bots[3], "Role", "interceptor");
            var mgr = Manager(bots);
            DefenseAlert(sessions[0].World);
            DefensePass(mgr, 0, bots);
            var selected = bots.Where(b => Get<short?>(b, "DefenseSector") == 1).ToArray();
            Assert.Equal(3, selected.Length); // ceil(20 * .15)
            Assert.Equal(new short[] { 1, 3, 4 }, selected.Select(b => Get<short>(b, "ShipId")));
            Assert.Null(Get<short?>(bots[1], "DefenseSector"));
            Assert.Equal("defending", mgr.ActiveBots().Single(b => b.ShipId == 1).State);
            Assert.All(selected, b => { Assert.Equal((short?)4, Get<short?>(b, "TargetSector"));
                Assert.Equal(new short[] { 1, 2, 3, 4 }, Get<List<short>>(b, "RoutePath")); });
            DefensePass(mgr, 21, bots); // quiet has passed, minimum deployment has not
            Assert.All(selected, b => Assert.Equal((short?)1, Get<short?>(b, "DefenseSector")));
            DefensePass(mgr, 31, bots);
            Assert.All(bots, b => Assert.Null(Get<short?>(b, "DefenseSector")));
            Call("DriveCombat", mgr, bots[0], 31.0, null);
            Assert.False(Get<bool>(bots[0], "DefendingInPlace"));
            Assert.Equal((short)3, Get<short>(bots[0], "StagingSector")); // original march resumed
        }
        finally { foreach (var cs in sessions) cs.Dispose(); }
    }

    [Fact]
    public void DefenseRepeatedAlerts_ExtendSameWing_AndRespectSideCapAcrossBases()
    {
        using var reads = new DefenseLiveReads();
        var sessions = Enumerable.Range(1, 10).Select(_ => Session(true)).ToArray();
        try
        {
            var bots = sessions.Select((cs, i) => { cs.World.Reset(); Seat(cs.World, (short)(i + 1), 2); DefenseMap(cs.World); return Bot(cs, (short)(i + 1)); }).ToArray();
            var mgr = Manager(bots); Set(mgr, "_defenseSettings", new BotManager.DefenseSettings(true, .9, 2));
            DefenseAlert(sessions[0].World); DefensePass(mgr, 0, bots);
            var selected = bots.Where(b => Get<short?>(b, "DefenseSector") is not null).ToArray();
            Assert.Equal(2, selected.Length);
            foreach (double now in new[] { 10.0, 20, 30, 40 })
            {
                DefenseAlert(sessions[1].World, critical: true); DefensePass(mgr, now, bots);
                Assert.Equal(selected, bots.Where(b => Get<short?>(b, "DefenseSector") is not null));
            }
            sessions[0].World.Apply(new ExportStation { Station = Station(101, 0, 2) });
            sessions[0].World.Apply(new ExportCluster { Cluster = new(0, [0, 1, 0], 0, 0, 0, 2, 0, 0, "Other", "", "", 0, 0, 0, true, false) });
            sessions[0].World.Apply(new ChatMessage { ChatTarget = (int)ChatTarget.Team, Message = "base in Other under attack by foe" });
            DefensePass(mgr, 41, bots);
            Assert.Equal(2, bots.Count(b => Get<short?>(b, "DefenseSector") is not null));
            DefensePass(mgr, 59, bots); Assert.All(selected, b => Assert.NotNull(Get<short?>(b, "DefenseSector")));
            DefensePass(mgr, 72, bots); Assert.All(bots, b => Assert.Null(Get<short?>(b, "DefenseSector")));
        }
        finally { foreach (var cs in sessions) cs.Dispose(); }
    }

    [Fact]
    public void DefenseRelaunch_EngagesVisibleBaseAttackerBeforeLeaving_AndPrioritizesBomber()
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); var bot = Bot(cs); var mgr = Manager(bot);
        DefenseNoSocketSender(bot);
        Set(bot, "TargetSector", (short?)4);
        Call("ResetNewLife", null, bot, 10.0, true);
        // Resolve a real bomber from the core rather than assuming a hull id.
        short bomber = Catalog.RepresentativeCore().HullTypes.Values.First(h => CombatTargetSelector.ClassifyTarget(h) == BotTargetClass.Bomber).HullId;
        DefenseEnemy(cs.World, 9, 15, 50); DefenseEnemy(cs.World, 10, bomber, 500);
        DefenseAlert(cs.World); DefensePass(mgr, 10, bot);
        Call("DriveCombat", mgr, bot, 10.0, null);
        Assert.Equal((short?)10, Get<short?>(bot, "TargetId"));
        Assert.True(Get<bool>(bot, "DefendingInPlace"));
        Assert.Empty(Get<List<short>>(bot, "Path"));
        Assert.Equal(BotManager.StageReleasedPhase, Get<byte>(bot, "StagePhase"));
        Assert.Equal((short?)4, Get<short?>(bot, "TargetSector"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DefenseTravellingFighter_AnswersReceivedHitOnSelfOrFriendlyAsset_ThenResumes(bool asset)
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World);
        var bot = Bot(cs); var mgr = Manager(bot); Set(bot, "TargetSector", (short?)4);
        DefenseNoSocketSender(bot);
        DefenseEnemy(cs.World, x: 5000); // far from base; damage is the trigger
        if (asset) { cs.World.Apply(new ExportShip { ShipId = 8, SideId = 0, HullId = 15 }); }
        cs.World.Apply(new ProjectileHullHit { SourceId = 9, TargetId = asset ? (short)8 : (short)1, ClusterId = 1 });
        Call("DriveCombat", mgr, bot, 10.0, null);
        Assert.Equal((short?)9, Get<short?>(bot, "TargetId"));
        Assert.False(Get<bool>(bot, "DefendingInPlace")); Assert.Empty(Get<List<short>>(bot, "Path"));
        Call("DriveCombat", mgr, bot, 20.0, null);
        Assert.False(Get<bool>(bot, "DefendingInPlace")); Assert.Null(Get<short?>(bot, "TargetId"));
        Assert.Equal(new short[] { 1, 2, 3, 4 }, Get<List<short>>(bot, "Path"));
    }

    [Fact]
    public void DefenseDisableSwitch_RestoresTravelPastThreats_AndIgnoresAlerts()
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); DefenseAlert(cs.World); DefenseEnemy(cs.World);
        var bot = Bot(cs); Set(bot, "TargetSector", (short?)4); var mgr = Manager(bot);
        DefenseNoSocketSender(bot);
        Set(mgr, "_defenseSettings", BotManager.DefenseSettings.Read(name => name == "ALLEGIANCE_BOT_DEFEND" ? "0" : null));
        DefensePass(mgr, 0, bot); Call("DriveCombat", mgr, bot, 1.0, null);
        Assert.Null(Get<short?>(bot, "DefenseSector")); Assert.False(Get<bool>(bot, "DefendingInPlace"));
        Assert.Null(Get<short?>(bot, "TargetId")); Assert.NotEmpty(Get<List<short>>(bot, "Path"));
    }

    [Fact]
    public void DefenseAfterArrival_ReturnsToOriginalObjective_AndPreservesStaging()
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); var bot = Bot(cs); DefenseNoSocketSender(bot); var mgr = Manager(bot);
        Set(bot, "TargetSector", (short?)4); Set(bot, "Arrived", true); Set(bot, "UseStaging", true);
        Set(bot, "StagePhase", BotManager.StageReleasedPhase);
        Set(bot, "RoutePath", new List<short> { 1, 2, 3, 4 });
        DefenseAlert(cs.World); DefensePass(mgr, 0, bot);
        Call("DriveCombat", mgr, bot, 1.0, null);
        Assert.True(Get<bool>(bot, "DefendingInPlace"));
        DefensePass(mgr, 31, bot); Call("DriveCombat", mgr, bot, 31.0, null);
        Assert.Null(Get<short?>(bot, "DefenseSector"));
        Assert.Equal(new short[] { 1, 2, 3, 4 }, Get<List<short>>(bot, "Path"));
        Assert.True(Get<bool>(bot, "Arrived"));
        Assert.Equal(BotManager.StageReleasedPhase, Get<byte>(bot, "StagePhase"));
    }

    [Fact]
    public void DefenseDelayedWorldSync_GetsFullMinimumDeployment()
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); DefenseAlert(cs.World); var bot = Bot(cs); var mgr = Manager(bot);
        Set(bot, "ObjectivesPending", true); DefensePass(mgr, 0, bot);
        Assert.Null(Get<short?>(bot, "DefenseSector"));
        Set(bot, "ObjectivesPending", false); DefensePass(mgr, 25, bot);
        Assert.Equal((short?)1, Get<short?>(bot, "DefenseSector"));
        DefensePass(mgr, 31, bot); Assert.NotNull(Get<short?>(bot, "DefenseSector"));
        DefensePass(mgr, 56, bot); Assert.Null(Get<short?>(bot, "DefenseSector"));
    }

    [Fact]
    public void DefenseDetour_FliesHomeWarp_PreservesObjectivePath_AndRecordsArrival()
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        cs.World.Apply(new SetCluster { Ship = Pose(1, 1000), SectorId = 2 }); DefenseMap(cs.World);
        cs.World.Apply(new ExportWarp { Warp = new(20, 21, 60, "", "", "home", [500, 0, 0], [0, 0, -1], [0, 0, 0, 1], 0, 2) });
        cs.World.Apply(new ExportWarp { Warp = new(21, 20, 60, "", "", "outward", [500, 0, 0], [0, 0, -1], [0, 0, 0, 1], 0, 1) });
        var bot = Bot(cs); DefenseNoSocketSender(bot); var mgr = Manager(bot);
        Set(bot, "TargetSector", (short?)4); Set(bot, "Path", new List<short> { 2, 3, 4 });
        DefenseAlert(cs.World); DefensePass(mgr, 0, bot); Call("DriveCombat", mgr, bot, 1.0, null);
        Assert.Equal((short?)1, Get<short?>(bot, "DefenseSector"));
        Assert.Equal((short)1, Get<Dictionary<short, short>>(bot, "WarpDest")[20]);
        Assert.Equal(new short[] { 2, 3, 4 }, Get<List<short>>(bot, "Path"));
        cs.World.Apply(new SetCluster { Ship = Pose(1, 600), SectorId = 1 });
        Call("DriveCombat", mgr, bot, 2.0, null);
        var hop = Assert.Single(Get<List<(double At, short From, short To)>>(bot, "WarpHops"));
        Assert.Equal((short)2, hop.From); Assert.Equal((short)1, hop.To);
        Assert.Equal((short?)4, Get<short?>(bot, "TargetSector"));
    }

    [Theory]
    [InlineData("bomber")] [InlineData("scout")]
    public void DefenseAlert_DoesNotPullOtherRoles(string role)
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); DefenseAlert(cs.World); var bot = Bot(cs); Set(bot, "Role", role);
        DefensePass(Manager(bot), 0, bot); Assert.Null(Get<short?>(bot, "DefenseSector"));
    }

    [Fact]
    public void DefenseObservations_IgnoreHiddenEnemiesAndUnrelatedChat_AndExpireOnRotation()
    {
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); var bot = Bot(cs); var mgr = Manager(bot);
        cs.World.Apply(new ChatMessage { ChatTarget = (int)ChatTarget.Everyone, Message = "base in Home under attack by foe" });
        cs.World.Apply(new ChatMessage { ChatTarget = (int)ChatTarget.Team, Message = "enemy in Other under attack by foe" });
        DefenseEnemy(cs.World); cs.World.Apply(new ShipDelete { ShipId = 9, Sdr = ShipDelete.SdrLeftSector });
        DefensePass(mgr, 0, bot); Assert.Null(Get<short?>(bot, "DefenseSector"));
        DefenseAlert(cs.World); DefensePass(mgr, 1, bot); Assert.NotNull(Get<short?>(bot, "DefenseSector"));
        cs.World.BeginNextMission(); Seat(cs.World); DefenseMap(cs.World);
        DefensePass(mgr, 2, bot); Assert.Null(Get<short?>(bot, "DefenseSector"));
    }

    [Fact]
    public void DefenseSettings_DefaultsClampAndInvalidValues()
    {
        Assert.Equal(new BotManager.DefenseSettings(true, .15, 30), BotManager.DefenseSettings.Read(_ => null));
        Assert.Equal(new BotManager.DefenseSettings(true, .15, 30), BotManager.DefenseSettings.Read(_ => "NaN"));
        Assert.Equal(new BotManager.DefenseSettings(true, 1, 200), BotManager.DefenseSettings.Read(_ => "999"));
        Assert.Equal(new BotManager.DefenseSettings(true, 0, 0), BotManager.DefenseSettings.Read(_ => "-1"));
    }
}
