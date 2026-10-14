using System.Collections;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class P656ShowcaseFinaleTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(1799, 1, 1, 100, false)]
    [InlineData(1800, .49, 1, 100, false)]
    [InlineData(1800, 1, .49, 100, false)]
    [InlineData(1800, 1, 1, 4, false)]
    [InlineData(1800, .5, .5, 5, true)]
    public void P656_TriggerRequiresTimeBothSidesAndSustainedCombat(double seconds,
        double shareA, double shareB, int recentKills, bool expected)
    {
        var sides = new[] { new ShowcaseFinale.Maturity(0, 100, (int)(100 * shareA), 100),
            new ShowcaseFinale.Maturity(1, 100, (int)(100 * shareB), 200) };
        var result = ShowcaseFinale.TriggerSides(new(), seconds, sides, recentKills);
        Assert.Equal(expected ? new short[] { 1 } : [], result);
        Assert.Empty(ShowcaseFinale.TriggerSides(new(AfterMinutes: 0), 1e6, sides, 100));
    }

    [Theory]
    [InlineData("leader", 0)] [InlineData("0", 0)] [InlineData("1", 1)] [InlineData("both", 2)]
    public void P656_SideSelectionIsDeterministicIncludingTies(string selection, int expected)
    {
        var sides = new[] { new ShowcaseFinale.Maturity(1, 2, 1, 9), new ShowcaseFinale.Maturity(0, 2, 1, 9) };
        var chosen = ShowcaseFinale.TriggerSides(new(Side: selection), 1800, sides, 5);
        Assert.Equal(expected == 2 ? new short[] { 0, 1 } : [(short)expected], chosen);
        Assert.Equal(9, new ShowcaseFinale.Settings().EscortBudget(90));
        Assert.Equal(12, new ShowcaseFinale.Settings().EscortBudget(200));
        Assert.Equal(0, new ShowcaseFinale.Settings().EscortBudget(9));
    }

    [Fact]
    public void P656_SettingsSchemaAcceptsNumericSideAndRejectsMalformedValues()
    {
        var tools = McpTools.Definitions();
        var schema = JsonSerializer.SerializeToElement(tools.Single(t => t.Name == "spawn_scenario").InputSchema);
        foreach (var field in new[] { "finale_after_minutes", "finale_side", "finale_min_relaunch_share",
            "finale_min_kills_last_5_minutes", "finale_bombers", "finale_escort_share", "finale_escort_cap",
            "finale_escort_radius", "finale_escort_leash" })
            Assert.True(schema.GetProperty("properties").TryGetProperty(field, out _));
        Assert.Empty(schema.GetProperty("required").EnumerateArray()); // preset needs no homogeneous size
        var botSchema = JsonSerializer.SerializeToElement(tools.Single(t => t.Name == "spawn_bot").InputSchema);
        Assert.False(botSchema.GetProperty("properties").TryGetProperty("finale_after_minutes", out _));
        Dictionary<string, JsonElement> Parse(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        Assert.Equal(30, McpTools.ReadFinaleSettings(Parse("{}"), true).AfterMinutes);
        Assert.False(McpTools.ReadFinaleSettings(Parse("{}"), false).Enabled);
        Assert.Equal("1", McpTools.ReadFinaleSettings(Parse("{\"finale_side\":1}"), true).Side);
        Assert.False(McpTools.ReadFinaleSettings(Parse("{\"finale_after_minutes\":0}"), true).Enabled);
        foreach (string json in new[] { "{\"finale_after_minutes\":-1}", "{\"finale_after_minutes\":\"30\"}",
            "{\"finale_side\":2}", "{\"finale_bombers\":1.5}", "{\"finale_escort_leash\":100}" })
            Assert.Throws<ArgumentException>(() => McpTools.ReadFinaleSettings(Parse(json), true));
    }

    [SkippableFact]
    public void P656_RealCoreLoadoutBeatsDefaultBomberAndEveryMountIsLegal()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var target = core.StationTypes.Values.Where(s => s.Name.Contains("Garrison", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.StationTypeId).First();
        var bomber = Showcase.RepresentativeHullOfRole(core, Showcase.HullRole.Bomber)!.Value;
        var original = Catalog.NativePreferredLoadout(bomber.HullId, bomber.Name);
        var chosen = ShowcaseFinale.SelectLoadout(core, target);
        var baseline = ShowcaseFinale.Volley(core, original.Parts, target);
        Assert.True(chosen.Volley.Hull > baseline.Hull);
        Assert.True(chosen.Volley.Score(target) > baseline.Score(target));
        var hull = core.HullTypes[chosen.HullId];
        Assert.False(Allegiance.Core.Model.SuccessorUpgrade.HullIsObsolete(core, hull, null, core.ComputeRequiredTechUnion()));
        foreach (var part in chosen.Parts)
        {
            if (!core.PartTypes.TryGetValue(part.PartId, out var type)) Assert.True(core.TryBuildExpendableLauncher(part.PartId, out type));
            Assert.True(hull.CanMountPart(type));
            Assert.False(Allegiance.Core.Model.SuccessorUpgrade.PartIsObsolete(core, type, null, core.ComputeRequiredTechUnion()));
            Assert.NotEqual(byte.MaxValue, type.GroupId);
            if (type.EquipmentType == EquipmentType.Weapon)
            {
                Assert.True(hull.Hardpoints[part.MountId].Fixed);
                Assert.Equal(0, type.PartMask & ~hull.Hardpoints[part.MountId].PartMask);
            }
        }
        output.WriteLine(JsonSerializer.Serialize(new { baselineHull = bomber.Name, baselineHullId = bomber.HullId, target = new { target.Name, target.StationTypeId,
            target.DefenseTypeArmor, target.DefenseTypeShield, target.MaxArmorHitPoints, target.MaxShieldHitPoints,
            target.ArmorRegeneration, target.ShieldRegeneration }, baseline, chosen }, new JsonSerializerOptions { WriteIndented = true }));
        // Damage math also matches the actual station simulation, including shield overflow.
        var sim = new Allegiance.Core.Sim.Station(new([0, 0, 0], [0, 1, 0], [0, 0, -1], [0, 0, 0, 0],
            1, 1, 100, target.StationTypeId, 255, 255, "test"), target);
        var cluster = new DataClusterIGC(0, [0, 1, 0], 0, 0, 0, 1, 0, 0, "test", "", "", 0, 0, 0, true, false);
        var map = Allegiance.Core.Model.MissionMap.FromLists([cluster], [], [], []);
        var mission = Allegiance.Core.Model.Mission.Assemble(core, map, [], new Allegiance.Core.Sim.NullIgcSite());
        var sector = mission.Sectors[0];
        var shooter = new Allegiance.Core.Sim.Ship(1, hull, Vector3.Zero, Vector3.Zero, Allegiance.Core.Sim.Orientation.Identity);
        mission.PlaceShip(shooter, sector);
        shooter.SideId = 0;
        var gun = core.PartTypes[chosen.Parts.First(p => core.PartTypes.TryGetValue(p.PartId, out var t)
            && t.Type is DataPartTypeIGC.Weapon).PartId];
        var round = core.ProjectileTypes[((DataPartTypeIGC.Weapon)gun.Type).ProjectileTypeId];
        var damage = ShowcaseFinale.PartDamage(core, gun.PartId, target);
        sim.ReceiveDamage(round.DamageType, round.Power, 0, Vector3.Zero, Vector3.Zero, shooter);
        float expectedShield = target.MaxShieldHitPoints > 0 && damage.Shield > 0
            ? Math.Max(0, 1 - damage.Shield / target.MaxShieldHitPoints) : 1;
        Assert.Equal(expectedShield, sim.ShieldFraction, 4);
    }

    [Fact]
    public void P656_EscortEngagesBomberAttackerOverNearIdleAndHonorsLeash()
    {
        var attacker = new ShowcaseFinale.EscortEnemy(9, new(500, 0, 0), Vector3.Zero, true);
        var idle = new ShowcaseFinale.EscortEnemy(8, new(1, 0, 0), Vector3.Zero, false);
        Assert.Equal((short?)9, ShowcaseFinale.EscortTarget(Vector3.Zero, Vector3.Zero, [idle, attacker], 1500, 2500));
        Assert.Null(ShowcaseFinale.EscortTarget(new(2501, 0, 0), Vector3.Zero, [attacker], 1500, 2500));
        Assert.Null(ShowcaseFinale.EscortTarget(Vector3.Zero, Vector3.Zero, [attacker with { Position = new(2501, 0, 0) }], 1500, 2500));
        var approach = idle with { Velocity = new(-1, 0, 0) };
        Assert.Equal((short?)8, ShowcaseFinale.EscortTarget(Vector3.Zero, Vector3.Zero, [approach], 1500, 2500));
        Assert.Null(ShowcaseFinale.EscortTarget(Vector3.Zero, Vector3.Zero, [approach with { Velocity = new(1, 0, 0) }], 1500, 2500));
    }
}

public sealed partial class BotFixRegressionTests
{
    private static JsonElement FinaleJson(BotManager manager) => JsonSerializer.SerializeToElement(Assert.Single(manager.FinaleStatus()));

    private sealed class FinaleRig : IDisposable
    {
        internal readonly Allegiance.ClientCore.Session.ConnectionSession[] Sessions;
        internal readonly object[] Bots;
        internal readonly BotManager Manager;
        internal readonly DataStationIGC[] Stations;
        internal readonly double Started;
        internal FinaleRig(ShowcaseFinale.Settings settings, int perSide = 12)
        {
            var core = Catalog.RepresentativeCore();
            var station = core.StationTypes.Values.OrderBy(s => s.StationTypeId)
                .First(s => s.Name.Contains("Garrison", StringComparison.OrdinalIgnoreCase));
            Stations = [Station(100, 0, 1) with { StationTypeId = station.StationTypeId },
                Station(101, 1, 4) with { StationTypeId = station.StationTypeId }];
            Sessions = Enumerable.Range(0, perSide * 2).Select(_ => Session(true)).ToArray();
            Bots = Sessions.Select((cs, i) =>
            {
                cs.World.Reset(); Seat(cs.World, (short)(i + 1), 1);
                DefenseMap(cs.World);
                foreach (var st in Stations) cs.World.Apply(new ExportStation { Station = st });
                var bot = Bot(cs, (short)(i + 1));
                Set(bot, "Name", $"finale-{i}"); Set(bot, "Side", (short)(i / perSide));
                Set(bot, "TargetSector", (short?)3); Set(bot, "UseStaging", true);
                Set(bot, "ObjectiveName", "Alderan");
                Set(bot, "Parts", Catalog.NativePreferredLoadout(15, "fighter").Parts);
                Set(bot, "CompletedPodCycles", 1);
                DefenseNoSocketSender(bot);
                if (i % perSide < 2) Set(bot, "Role", "bomber");
                return bot;
            }).ToArray();
            Manager = BotFixRegressionTests.Manager(Bots);
            Manager.RegisterFinale(settings, Bots.Select(b => Get<string>(b, "Name")).ToArray(), Stations);
            Started = Get<double>(Get<object>(Bots[0], "Finale"), "Created");
        }
        internal void Tick(double elapsed) => Call("UpdateFinales", Manager, Started + elapsed);
        internal void Kill(short side, double elapsed)
        {
            var bot = Bots.First(b => Get<short>(b, "Side") == side);
            Set(bot, "Kills", Get<long>(bot, "Kills") + 1);
            var run = Get<object>(bot, "Finale");
            Get<Queue<(double, short)>>(run, "Kills").Enqueue((Started + elapsed, side));
        }
        internal void Mature() { for (int i = 0; i < 5; i++) Kill(0, 1800); Tick(1800); }
        public void Dispose() { foreach (var cs in Sessions) cs.Dispose(); }
    }

    [SkippableFact]
    public void P656_ControllerReservesOnlyBombersAndBuysOnNextPodLaunch_EscortsAreFreshAndCapped()
    {
        Require.Artwork(); using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(Bombers: 1, EscortCap: 1));
        Assert.Equal(2, FinaleJson(rig.Manager).GetProperty("maturity").GetArrayLength());
        rig.Mature();
        Assert.Single(rig.Bots, b => Get<bool>(b, "FinaleBomberReserved"));
        Assert.All(rig.Bots, b => { Assert.False(Get<bool>(b, "FinaleBomberActive")); Assert.False(Get<bool>(b, "FinaleEscort")); });
        var bomber = rig.Bots[0]; var cs = rig.Sessions[0];
        Assert.Equal((short)15, Get<short>(bomber, "HullId"));
        cs.World.Apply(new DockedNotify { StationId = 100 });
        Set(bomber, "IsPod", true); Set(bomber, "PodDocked", true);
        var buys = new List<(short Hull, bool Launch, IReadOnlyList<ExpandedPartData> Parts)>();
        Action<short, bool, IReadOnlyList<ExpandedPartData>> send = (h, l, p) => buys.Add((h, l, p));
        Call("RelaunchDockedPod", null, bomber, cs, rig.Started + 1801, send);
        var buy = Assert.Single(buys);
        Assert.True(buy.Launch); Assert.True(Get<bool>(bomber, "FinaleBomberActive"));
        Assert.Equal(Get<short>(bomber, "HullId"), buy.Hull);
        Assert.Equal(Get<IReadOnlyList<ExpandedPartData>>(bomber, "Parts"), buy.Parts);
        ((Allegiance.Core.Transport.ITransportSite)cs).OnReceive(Allegiance.Core.Transport.ConnectionId.Server,
            FedPacket.Pack([new BuyLoadoutAck { HullId = buy.Hull, Parts = buy.Parts, FBoughtEverything = true, FLaunch = true }.Encode()]));
        Assert.True((bool)Call("UpdatePodState", null, bomber, cs, rig.Started + 1801.5)!);
        Assert.True(Get<bool>(bomber, "FinaleGrantVerified"));
        cs.World.Apply(new SetCluster { Ship = Pose(1), SectorId = 1 });
        Assert.False((bool)Call("UpdatePodState", null, bomber, cs, rig.Started + 1802)!);
        Assert.Equal(1802, FinaleJson(rig.Manager).GetProperty("strikeStartedAt").GetDouble(), 3);
        var pod = Catalog.RepresentativeCore().HullTypes.Values.First(h => Catalog.IsLifepodHull(h.HullId)).HullId;
        cs.World.Apply(new BuyLoadoutAck { HullId = pod, Parts = [] });
        cs.World.Apply(new DockedNotify { StationId = 100 });
        Call("UpdatePodState", null, bomber, cs, rig.Started + 1803);
        Call("RelaunchDockedPod", null, bomber, cs, rig.Started + 1804, send);
        Assert.Equal(2, buys.Count);
        Assert.Equal(buy, buys[1]); // normal second death buys the same strike again
        Assert.False(Get<bool>(bomber, "FinaleGrantVerified"));
        ((Allegiance.Core.Transport.ITransportSite)cs).OnReceive(Allegiance.Core.Transport.ConnectionId.Server,
            FedPacket.Pack([new BuyLoadoutAck { HullId = buy.Hull,
                Parts = buy.Parts.Where(p => p.MountId != 1).ToArray(), FBoughtEverything = false, FLaunch = true }.Encode()]));
        Call("HandleRelaunchAck", null, bomber, cs);
        Assert.False(Get<bool>(bomber, "FinaleGrantVerified"));
        Assert.Contains("not fully granted", Get<string>(bomber, "GrantWarning"));
        for (int i = 2; i < 4; i++)
        {
            var fighter = rig.Bots[i];
            Call("PrepareFinaleRelaunch", null, fighter, rig.Started + 1801);
            Assert.False(Get<bool>(fighter, "FinaleEscort")); // engaged fighter cannot be recruited
            Set(fighter, "PodDocked", true);
            Call("PrepareFinaleRelaunch", null, fighter, rig.Started + 1802);
        }
        Assert.Single(rig.Bots, b => Get<bool>(b, "FinaleEscort"));
        Assert.Equal((short?)4, Get<short?>(rig.Bots[2], "TargetSector"));
        Assert.Equal((short?)3, Get<short?>(rig.Bots[3], "TargetSector"));
    }

    [SkippableFact]
    public void P656_RollingKillWindowExpiresAndCountsEachCreditedKillOnceAcrossClients()
    {
        Require.Artwork(); using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(AfterMinutes: 40, MinKillsLastFiveMinutes: 1), 2);
        rig.Kill(1, 0); rig.Tick(2400);
        Assert.All(rig.Bots, b => Assert.False(Get<bool>(b, "FinaleBomberReserved")));
        var kill = new KillShip { ShipId = 90, IdCredit = 3, TypeCredit = 0, SideIdKiller = 1 };
        for (int i = 0; i < rig.Bots.Length; i++)
        {
            var cs = rig.Sessions[i]; cs.World.Apply(kill);
            Call("SampleCombatStats", null, rig.Bots[i], cs, Get<short>(rig.Bots[i], "ShipId"), rig.Started + 2401);
        }
        rig.Tick(2401);
        var status = FinaleJson(rig.Manager);
        Assert.Equal(1, status.GetProperty("killsLastFiveMinutes").GetInt32());
        Assert.Equal(1, status.GetProperty("selectedSides")[0].GetInt32());
        Assert.All(rig.Bots.Take(2), b => Assert.False(Get<bool>(b, "FinaleBomberReserved")));
        Assert.All(rig.Bots.Skip(2), b => Assert.True(Get<bool>(b, "FinaleBomberReserved")));
    }

    [SkippableTheory]
    [InlineData(1799, true)] [InlineData(1801, false)]
    public void P656_BeforeTriggerControllerLeavesOpeningDecisionsIdenticalToDisabled(double elapsed, bool mature)
    {
        Require.Artwork(); using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(), 2);
        for (int i = 0; i < 5; i++) rig.Kill(0, elapsed);
        if (!mature) foreach (var b in rig.Bots) Set(b, "CompletedPodCycles", 0);
        rig.Tick(elapsed);
        var bot = rig.Bots[0];
        object[] Decision() => [Get<short?>(bot, "TargetId")!, Get<List<short>>(bot, "Path").ToArray(),
            Get<byte>(bot, "StagePhase"), Get<short?>(bot, "TargetSector")!, Get<short>(bot, "HullId"),
            Get<IReadOnlyList<ExpandedPartData>>(bot, "Parts")];
        Call("DriveCombat", rig.Manager, bot, rig.Started + elapsed, null);
        string armedDecision = JsonSerializer.Serialize(Decision());
        Set(bot, "Finale", null);
        Call("ResetNewLife", null, bot, rig.Started + elapsed, false);
        Call("DriveCombat", rig.Manager, bot, rig.Started + elapsed, null);
        Assert.Equal(armedDecision, JsonSerializer.Serialize(Decision()));
        Assert.All(rig.Bots, b => { Assert.False(Get<bool>(b, "FinaleBomberReserved")); Assert.False(Get<bool>(b, "FinaleEscort")); });
    }

    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void P656_DefenseBomberPriorityRunsBeforeEitherP622Mode(bool legacy)
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); var bot = Bot(cs); DefenseNoSocketSender(bot);
        short bomber = Catalog.RepresentativeCore().HullTypes.Values.First(h => CombatTargetSelector.ClassifyTarget(h) == BotTargetClass.Bomber).HullId;
        DefenseEnemy(cs.World, 9, 15, 50); DefenseEnemy(cs.World, 10, bomber, 500);
        Set(bot, "DefendingInPlace", true); Set(bot, "DefenseTargets", new HashSet<short> { 9, 10 });
        Call("EngageCombat", Manager(bot), bot, 10.0, null, legacy);
        Assert.Equal((short?)10, Get<short?>(bot, "TargetId"));
    }

    [SkippableFact]
    public void P656_EscortControllerUsesBomberOwnHitEventsAndReturnsWhenOutsideLeash()
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var rig = new FinaleRig(new(), 12);
        rig.Mature();
        var bomber = rig.Bots[0]; var escort = rig.Bots[2];
        Set(bomber, "PodDocked", true); Set(escort, "PodDocked", true);
        Call("PrepareFinaleRelaunch", null, bomber, rig.Started + 1801);
        Call("PrepareFinaleRelaunch", null, escort, rig.Started + 1801);
        Set(bomber, "PodDocked", false); Set(escort, "PodDocked", false);
        var bomberCs = rig.Sessions[0]; var escortCs = rig.Sessions[2];
        DefenseEnemy(bomberCs.World, 90, 15, 500);
        bomberCs.World.Apply(new ProjectileHullHit { SourceId = 90, TargetId = 1, ClusterId = 1 });
        Call("SampleCombatStats", null, bomber, bomberCs, (short)1, rig.Started + 1802);
        Assert.True(Get<Dictionary<short, double>>(bomber, "RecentAttackers").ContainsKey(90));
        escortCs.World.Apply(new ExportShip { ShipId = 1, SideId = 0, HullId = Get<short>(bomber, "HullId") });
        escortCs.World.Apply(new SingleShipUpdate { OidTarget = 1, Ship = Pose(1) });
        escortCs.World.Apply(new ShipStatusMessage { ShipId = 1, SectorId = 1, StationId = -1 });
        DefenseEnemy(escortCs.World, 90, 15, 500); DefenseEnemy(escortCs.World, 91, 15, 1);
        Assert.True((bool)Call("TryDriveFinaleEscort", rig.Manager, escort, rig.Started + 1802)!);
        Assert.Equal((short?)90, Get<short?>(escort, "TargetId"));
        rig.Tick(1803);
        var status = FinaleJson(rig.Manager);
        Assert.True(status.GetProperty("escortCohesionSamples").GetInt64() > 0);
        // Outside the leash the production controller clears its chase and flies home to the bomber.
        escortCs.World.Apply(new SetCluster { Ship = Pose(3, 3000), SectorId = 1 });
        Call("TryDriveFinaleEscort", rig.Manager, escort, rig.Started + 1804);
        Assert.Null(Get<short?>(escort, "TargetId"));
        rig.Tick(1815);
        Assert.Equal(1, FinaleJson(rig.Manager).GetProperty("bomberProtected").GetInt64());
        var pod = Catalog.RepresentativeCore().HullTypes.Values.First(h => Catalog.IsLifepodHull(h.HullId)).HullId;
        bomberCs.World.Apply(new BuyLoadoutAck { HullId = pod, Parts = [] });
        Call("UpdatePodState", null, bomber, bomberCs, rig.Started + 1816);
        Assert.Equal(1, FinaleJson(rig.Manager).GetProperty("bomberKilled").GetInt64());
    }

    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void P656_StrikeKeepsAttackingItsGarrisonUnderFire_InBothP622Modes(bool legacy)
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var rig = new FinaleRig(new(), 2);
        rig.Mature(); var bomber = rig.Bots[0]; var cs = rig.Sessions[0];
        Set(bomber, "PodDocked", true); Call("PrepareFinaleRelaunch", null, bomber, rig.Started + 1801);
        Set(bomber, "PodDocked", false);
        cs.World.Apply(new SetCluster { Ship = Pose(1, 1000), SectorId = 4 });
        cs.World.Apply(new ExportStation { Station = rig.Stations[1] with { Position = [0, 0, 0] } });
        cs.World.Apply(new ExportStation { Station = rig.Stations[1] with { StationId = 102, Position = [900, 0, 0] } });
        DefenseEnemy(cs.World, 90, 15, 999);
        cs.World.Apply(new ShipStatusMessage { ShipId = 90, HullId = 15, SectorId = 4, StationId = -1 });
        Get<Dictionary<short, double>>(bomber, "RecentAttackers")[90] = rig.Started + 1802;
        Call("EngageCombat", rig.Manager, bomber, rig.Started + 1802, null, legacy);
        Assert.Equal((short?)101, Get<short?>(bomber, "TargetId"));
    }

    [SkippableFact]
    public void P656_StatusIncludesGatesOutcomeAndNextMissionReset()
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var rig = new FinaleRig(new(), 2);
        rig.Mature();
        var bot = rig.Bots[0]; var cs = rig.Sessions[0];
        Call("RefreshLife", rig.Manager, bot, cs, rig.Started);
        Set(bot, "PodDocked", true); Call("PrepareFinaleRelaunch", null, bot, rig.Started + 1801);
        var run = Get<object>(bot, "Finale"); Set(run, "StrikeStartedAt", (double?)(rig.Started + 1801));
        cs.World.Apply(new StationDestroyed { StationId = 101, Launcher = 1 });
        cs.World.Apply(new GameOver { SideWinner = 0, GameoverMessage = "conquest" });
        rig.Tick(1810);
        var status = FinaleJson(rig.Manager);
        Assert.Equal("game over", status.GetProperty("state").GetString());
        Assert.True(status.GetProperty("combatSustained").GetBoolean());
        Assert.Equal(1810, status.GetProperty("gameOverAt").GetDouble(), 3);
        Assert.Equal(9, status.GetProperty("wings")[0].GetProperty("timeToKillSeconds").GetDouble(), 3);
        Assert.True(status.TryGetProperty("escortCohesionAverageDistance", out _));
        Assert.True(status.TryGetProperty("bomberProtected", out _)); Assert.True(status.TryGetProperty("bomberKilled", out _));
        cs.World.BeginNextMission(); Seat(cs.World, 40);
        Call("RefreshLife", rig.Manager, bot, cs, rig.Started + 1811);
        Assert.False(Get<bool>(bot, "FinaleBomberActive")); Assert.False(Get<bool>(bot, "FinaleEscort"));
        Assert.Equal((short)15, Get<short>(bot, "HullId"));
        Assert.False(Get<bool>(bot, "Arrived")); Assert.Equal((short)-1, Get<short>(bot, "ReadyShipId"));
        Assert.Equal("next mission", FinaleJson(rig.Manager).GetProperty("state").GetString());
        Assert.Equal(1, FinaleJson(rig.Manager).GetProperty("nextMission").GetProperty("resetBots").GetInt32());
        DefenseMap(cs.World);
        foreach (var station in rig.Stations) cs.World.Apply(new ExportStation { Station = station });
        Call("ReReadyBot", rig.Manager, bot);
        Assert.Equal((short)40, Get<short>(bot, "ReadyShipId"));
        Assert.False(Get<bool>(bot, "ObjectivesPending"));
        Set(bot, "Launched", true);
        Call("DriveCombat", rig.Manager, bot, rig.Started + 1812, null);
        Assert.NotEmpty(Get<List<short>>(bot, "Path")); // resumes a real route into the new map
    }

    [SkippableFact]
    public void P656_OnlyCompletedDeathDockAndRelaunchCountsAsMature_OffRegistersNothing()
    {
        Require.Artwork(); using var cs = Session(); var bot = Bot(cs); var manager = Manager(bot);
        foreach (var state in new[] { (false, false), (true, false), (true, true) })
        {
            Set(bot, "IsPod", state.Item1); Set(bot, "PodDocked", state.Item2);
            Call("ResetNewLife", null, bot, 1.0, true);
        }
        Assert.Equal(1, Get<int>(bot, "CompletedPodCycles"));
        manager.ConfigureFinale(new(AfterMinutes: 0), ["probe-bot"]);
        Assert.Empty(manager.FinaleStatus()); Assert.Null(Get<object?>(bot, "Finale"));
    }
}
