using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P733ResearchTests(ITestOutputHelper output)
{
    internal static ServerConfig Config(double scale = 1, string mode = "research") => new()
    {
        Port = 0, Showcase = new() { TechMode = mode, IncomeMultiplier = 5000, ResearchTimeScale = scale },
        SideSelectAutoSeatSeconds = 30,   // P763: ConnectionSession peers in this suite never send POSITIONREQ
        LiveSettings = new() { TimeDilation = "off", AutoNextMatch = new() { IntermissionSeconds = 2 } },
        // P797 harness option: the harness bots cannot scout yet (P798).
        Games = [new() { ShowMap = true, Name = "P733", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"],
            MaxTeamImbalance = 32767, MapGen = new() { Type = "SingleRing", PlayerSectorAsteroids = 20, PlayerSectorSpecialAsteroids = 8 } }],
    };

    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Fields)!.SetValue(obj, value);
    private static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, Fields)!.GetValue(obj)!;

    [Trait("Tier", "Live")] // Expensive corpus/scenario or collision-work budget; docs/testing/TIERS.md.
    [SkippableFact]
    public void No_bomber_tech_finale_reaches_stage_over_and_automatic_next_match_twice()
    {
        Require.Artwork();
        using var server = new Srv();
        var config = Config(); config.Showcase.FinaleTimeoutSeconds = 8;
        server.Boot(config);
        using var a = P660NextMatchLiveTests.Connect(server, "P733-finale-A", 733101);
        using var b = P660NextMatchLiveTests.Connect(server, "P733-finale-B", 733102);
        var peers = new[] { a, b }.OrderBy(p => p.World.MySideId).ToArray();
        foreach (var peer in peers) peer.SendPlayerReady(true, peer.World.MyShipId!.Value);
        P660NextMatchLiveTests.Pump(server, () => server.Missions[0].Stage == MissionStage.Started);
        for (int match = 1; match <= 2; match++)
        {
            var mission = server.Missions[0];
            P660NextMatchLiveTests.Pump(server, () => mission.Stage == MissionStage.Started
                && peers.All(p => P660NextMatchLiveTests.Read(p, () => p.World.ResearchBriefing?.Epoch == mission.ResearchEpoch
                    && p.World.MyShipId is { } id && p.World.Ships.TryGetValue(id, out var me) && me.StationId >= 0)));
            var botType = typeof(BotManager).GetNestedType("ActiveBot", BindingFlags.NonPublic)!;
            var bots = peers.Select((peer, i) =>
            {
                var bot = Activator.CreateInstance(botType, Fields, null,
                    ["P733-finale-" + i, (short)15, "Fighter", (short)i, (short)i, "combat", 0.0], null)!;
                Set(bot, "Cs", peer); Set(bot, "ShipId", peer.World.MyShipId!.Value);
                Set(bot, "SpawnPending", false); Set(bot, "Role", "bomber");
                Set(bot, "TechLoadouts", true); Set(bot, "PodDocked", true); Set(bot, "IsPod", true);
                return bot;
            }).ToArray();
            var manager = (BotManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BotManager));
            Set(manager, "_gate", new object()); Set(manager, "_clock", System.Diagnostics.Stopwatch.StartNew());
            var roster = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(botType))!;
            foreach (var bot in bots) roster.Add(bot);
            Set(manager, "_bots", roster);
            DataStationIGC[] stations;
            lock (a.Gate) stations = a.World.Stations.Values.ToArray();
            manager.RegisterFinale(new(AfterMinutes: .01, Side: "0", MinRelaunchShare: 0, MinKillsLastFiveMinutes: 1, Bombers: 1),
                bots.Select(bot => Get<string>(bot, "Name")).ToArray(), stations);
            var run = Get<object>(bots[0], "Finale");
            double trigger = Get<double>(run, "Created") + 1;
            Get<Queue<(double, short)>>(run, "Kills").Enqueue((trigger, 0));
            typeof(BotManager).GetMethod("UpdateFinales", Fields)!.Invoke(manager, [trigger]);
            Assert.True(Get<bool>(bots[0], "FinaleBomberReserved"));
            P660NextMatchLiveTests.Pump(server, () => ((System.Collections.IDictionary)Get<object>(server, "_researchFinales")).Count == 1);
            // Repeated arm requests must preserve the first deadline.
            var deadlines = (System.Collections.IDictionary)Get<object>(server, "_researchFinales");
            var armed = deadlines[mission];
            peers[0].SendShowcaseFinale(Guid.ParseExact(mission.ResearchEpoch, "N"), 0);
            server.TickSim(); Thread.Sleep(2); server.TickSim();
            Assert.Equal(armed, deadlines[mission]);
            // Real purchase retry: no bomber or station-damaging starting weapon exists.
            var side = mission.Sides[0]; var target = mission.Sides[1].Civilization.InitialStationTypeId;
            Assert.Null(TechLoadoutSelector.Select(mission.Core, side.Civilization.CivilizationId, Showcase.HullRole.Bomber,
                side.Techs, side.Civilization.InitialStationTypeId, mission.Core.StationTypes[target]));
            Assert.Null(TechLoadoutSelector.SelectAvailableStrike(mission.Core, side.Civilization.CivilizationId,
                side.Techs, side.Civilization.InitialStationTypeId, mission.Core.StationTypes[target], 1_000_000));
            typeof(BotManager).GetMethod("RelaunchDockedPod", BindingFlags.Static | Fields)!
                .Invoke(null, [bots[0], peers[0], trigger + 1, null, true]);
            P660NextMatchLiveTests.Pump(server, () => peers[0].BuyLoadoutAcks.Any(ack => ack.FLaunch && ack.FBoughtEverything));
            Assert.True(Get<bool>(bots[0], "FinaleBomberActive"));
            Assert.Contains("research finale", Get<TechLoadoutSelector.Selection>(bots[0], "PendingTechLoadout").Reason);
            P660NextMatchLiveTests.Pump(server, () => !ReferenceEquals(mission, server.Missions[0]));
            Assert.Equal(MissionStage.Over, mission.Stage);
            P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(peers[0], () => peers[0].World.GameOverResult is not null));
            output.WriteLine($"match {match}: Stage Over, epoch {mission.ResearchEpoch}, sim={server.SimNow}");
            P660NextMatchLiveTests.Pump(server, () => server.Missions[0].Stage == MissionStage.Started);
        }
        Assert.Equal(0, server.SimPassStageFailures);
    }

    [SkippableTheory]
    [InlineData("research")] [InlineData("unlocked")]
    public void Finale_bound_rejects_stale_epoch_wrong_side_and_unlocked_mode(string mode)
    {
        Require.Artwork();
        using var server = new Srv(); var config = Config(mode: mode);
        config.Showcase.FinaleTimeoutSeconds = 1; server.Boot(config);
        using var peer = P660NextMatchLiveTests.Connect(server, "P733-guard", 733201);
        var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
        var deadlines = (System.Collections.IDictionary)Get<object>(server, "_researchFinales");
        void PumpRequest(Guid epoch, short winner)
        {
            peer.SendShowcaseFinale(epoch, winner);
            for (int i = 0; i < 40; i++) { server.TickSim(); Thread.Sleep(1); }
            Assert.Same(mission, server.Missions[0]); Assert.Empty(deadlines.Keys.Cast<object>());
        }
        PumpRequest(Guid.NewGuid(), peer.World.MySideId!.Value);
        PumpRequest(Guid.ParseExact(mission.ResearchEpoch, "N"), (short)(1 - peer.World.MySideId!.Value));
        if (mode == "unlocked") PumpRequest(Guid.ParseExact(mission.ResearchEpoch, "N"), peer.World.MySideId!.Value);
    }

    [SkippableFact]
    public void Available_strike_uses_native_gates_and_strongest_station_damage_without_bomber_hulls()
    {
        Require.Artwork();
        var file = IgcReader.ParseFile(Path.Combine(Catalog.ArtRoot, "PCore001.igc"));
        var core = StaticCore.Load(new IgcFile
        {
            Wrapper = file.Wrapper, Version = file.Version, Body = file.Body, BodyLength = file.BodyLength, BytesConsumed = file.BytesConsumed,
            Records = file.Records.Where(r => r.Type != IgcObjectType.HullType
                || Showcase.ClassifyHull(DataHullTypeIGC.Decode(file.Body.Slice(r.PayloadOffset, r.Size).Span)) != Showcase.HullRole.Bomber).ToArray(),
        });
        const short civ = 25;
        var techs = core.ComputeRequiredTechUnion(); var dock = core.Civilizations[civ].InitialStationTypeId;
        var target = core.StationTypes[core.Civilizations[3].InitialStationTypeId];
        Assert.Null(TechLoadoutSelector.Select(core, civ, Showcase.HullRole.Bomber, techs, dock, target));
        var selected = TechLoadoutSelector.SelectAvailableStrike(core, civ, techs, dock, target, 1_000_000);
        Assert.NotNull(selected); Assert.NotNull(selected.Strike); Assert.True(selected.Strike.Volley.Score(target) > 0);
        Assert.True(TechLoadoutSelector.Legal(core, civ, techs, dock, selected.HullId, selected.Parts));
        Assert.Null(TechLoadoutSelector.SelectAvailableStrike(core, civ, techs, dock, target, 0));
        Assert.Equal(selected.HullId, TechLoadoutSelector.SelectAvailableStrike(core, civ, techs, dock, target, 1_000_000)!.HullId);
        output.WriteLine(selected.Reason);
    }

    // Headless, ordinary 20 UPS. Two real purchasing peers plus 20 docked fleet peers.
    // Authoritative snapshots remove socket receipt latency from the pacing measurement;
    // donations and constructor orders still use the real server wire handlers.
    private int[] Measure(double scale, int simSeconds = 1800, int warmupSeconds = 0)
    {
        using var server = new Srv();
        var config = Config(scale); config.Games[0].MapGen = null; config.Games[0].Map = "m2xspiderw.igc";
        // Eleven wallets/side stand in for the rig's 105 pilots plus control peers.
        // Match the smaller rig payday conservatively (107 wallets on its probe side).
        config.Showcase.IncomeMultiplier = 5000d * 11 / 107;
        server.Boot(config);
        var peers = new List<ConnectionSession>();
        try
        {
            for (int i = 0; i < 22; i++) peers.Add(P660NextMatchLiveTests.Connect(server, "P733-" + i, (ulong)(733000 + i)));
            var mission = server.Missions[0];
            var researchers = peers.Take(2).OrderBy(p => p.World.MySideId).ToArray();
            Assert.Equal(new short?[] { 0, 1 }, researchers.Select(p => p.World.MySideId));
            foreach (var peer in peers) peer.SendAutoDonate(-1);
            foreach (var peer in peers) peer.SendPlayerReady(true, peer.World.MyShipId!.Value);
            P660NextMatchLiveTests.Pump(server, () => mission.Stage == MissionStage.Started);
            double fundedAt = server.SimNow + warmupSeconds;
            for (int tick = 0; server.SimNow < fundedAt; tick++)
            { server.TickSim(); if (tick % 5 == 0) Thread.Sleep(1); }
            var reservations = new Dictionary<short, short>();
            var commanders = mission.Sides.Select(s => new ResearchCommander(s.Id, mission.ResearchEpoch,
                new ResearchTree(mission.Core, s.Civilization.CivilizationId, mission.Research), mission.ResearchPlans[s.Id],
                s.InitialTechs, reservations, researchTimeScale: scale)).ToArray();
            double end = server.SimNow + simSeconds;
            for (int tick = 0; server.SimNow < end; tick++)
            {
                if (tick % 5 == 0)
                {
                    foreach (var side in mission.Sides)
                    {
                        var peer = researchers[side.Id];
                        var own = server.Ships.Single(l => l.Ship.Id == peer.World.MyShipId);
                        var stations = mission.Sectors.SelectMany(s => s.StationRuntimes).Where(s => s.SideId == side.Id).ToArray();
                        var homeSectors = stations.Select(st => st.ClusterId).ToHashSet();
                        var units = mission.Sectors.SelectMany(s => s.Models).OfType<Ship>().Where(u => u.IsNpc && u.SideId == side.Id).ToArray();
                        var snapshot = new ResearchCommander.Snapshot(server.SimNow, true, own.Wallet.Money, side.DevelopmentTechs,
                            stations.Select(s => s.Type.StationTypeId).ToArray(),
                            side.Buckets.Where(b => b.Completed && b.Development.HasValue).Select(b => b.Development!.Value.DevelopmentId).ToHashSet(),
                            units.Select(u => new ResearchCommander.Unit(u.Id, u.DroneTypeId, u.BuilderStationType?.StationTypeId ?? -1, !u.Dead, u.Sector?.Id ?? -1)).ToArray(),
                            mission.Sectors.SelectMany(s => s.Mineable)
                                .OrderByDescending(r => homeSectors.Contains(r.Data.ClusterId)).ThenBy(r => r.Data.AsteroidId)
                                .Select(r => new ResearchCommander.Rock(r.Data.AsteroidId, r.Data.AabmCapabilities,
                                !mission.Sectors.SelectMany(s => s.BuildingEffects).Any(e => e.Target == r),
                                mission.Sectors.SelectMany(s => s.BuildingEffects).Any(e => e.Target == r), r.Data.ClusterId)).ToArray(),
                            side.Buckets.ToDictionary(b => b.Id, b => new ResearchCommander.Funding(b.InvestedMoney, b.InvestedMilliseconds)));
                        foreach (var action in commanders[side.Id].Tick(snapshot))
                        {
                            if (action.Kind == "donate") peer.SendBucketDonate(action.Id, action.Amount);
                            if (action.Kind is "goto" or "build") peer.SendChat("", ChatTarget.Individual, oidRecipient: action.Id,
                                commandId: action.Kind == "goto" ? OrderChange.CidGoto : OrderChange.CidBuild,
                                otTarget: (short)IgcObjectType.Asteroid, oidTarget: action.Target);
                        }
                    }
                    Thread.Sleep(1);
                }
                server.TickSim();
            }
            var rows = commanders.Select(c => c.Telemetry!).ToArray();
            output.WriteLine(JsonSerializer.Serialize(new { scale, simSeconds, warmupSeconds, rows }));
            var proof = Environment.GetEnvironmentVariable("ALLEGIANCE_P733_PROOF_DIRECTORY");
            if (proof is not null) { Directory.CreateDirectory(proof); File.WriteAllText(Path.Combine(proof, $"scale-{scale}-{simSeconds}s.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true })); }
            Assert.Equal(0, server.SimPassStageFailures);
            return rows.Select(r => (int)Math.Floor(r.ProgressPercent * r.TotalSteps / 100 + 1e-6)).ToArray();
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
    }

    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Headless_10v10_scale_eight_completes_at_least_five_times_native_steps()
    {
        Require.Artwork();
        // A native payday first removes the opening cash bottleneck. Compare
        // 90 seconds of the bucket-heavy opening with equal 150-second match age;
        // later constructor flight is intentionally not accelerated.
        var native = Measure(1, 90, 60);
        var fast = Measure(8, 90, 60);
        Assert.All(native, steps => Assert.True(steps > 0));
        for (int side = 0; side < 2; side++) Assert.True(fast[side] >= native[side] * 5,
            $"side {side}: scale 1={native[side]}, scale 8={fast[side]}");
    }

    [SkippableTheory]
    [InlineData(1)] [InlineData(4)] [InlineData(8)] [InlineData(16)]
    [Trait("Tier", "Live")]
    public void Headless_30_minute_rig_matchup_measurement(double scale)
    { Require.Artwork(); Assert.All(Measure(scale), steps => Assert.True(steps > 0)); }
}
