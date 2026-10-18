using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

public sealed class P704TechLoadoutTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData((short)25)] [InlineData((short)3)] [InlineData((short)1)] [InlineData((short)2)]
    [InlineData((short)4)] [InlineData((short)35)] [InlineData((short)101)]
    public void Roles_select_legal_deterministic_loadouts_at_start_middle_and_final_research(short civ)
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var tree = new ResearchTree(core, civ);
        var state = new ResearchState(tree);
        var plan = tree.Generate("PCore001");
        var initial = new Dictionary<Showcase.HullRole, short>();
        int checks = 0;
        for (int step = 0; step <= plan.Steps.Count; step++)
        {
            if (step > 0) state.Apply(plan.Steps[step - 1]);
            if (step != 0 && step != plan.Steps.Count / 2 && step != plan.Steps.Count) continue;
            foreach (short dock in state.Stations.Order())
            foreach (var role in Enum.GetValues<Showcase.HullRole>())
            {
                var selected = TechLoadoutSelector.Select(core, civ, role, state.Techs, dock);
                if (selected is null) continue; // locked/nonexistent roles are tested by the fallback below
                checks++;
                Assert.Equal(role, Showcase.ClassifyHull(core.HullTypes[selected.HullId]));
                Assert.True(TechLoadoutSelector.Legal(core, civ, state.Techs, dock, selected.HullId, selected.Parts), selected.Reason);
                var repeat = TechLoadoutSelector.Select(core, civ, role, state.Techs, dock)!;
                Assert.Equal(selected.HullId, repeat.HullId); Assert.Equal(selected.Parts, repeat.Parts); Assert.Equal(selected.Reason, repeat.Reason);
                Assert.Equal(role, Showcase.ClassifyHull(core.HullTypes[selected.HullId] with { Name = "unrelated translated name" }));
                Assert.Contains("stable ID ties", selected.Reason);
                var h = core.HullTypes[selected.HullId];
                var guns = selected.Parts.Select(p => TechLoadoutSelector.Part(core, p.PartId).Type).OfType<DataPartTypeIGC.Weapon>().ToArray();
                Assert.True(guns.Sum(w => w.EnergyPerShot) <= h.MaxEnergy);
                Assert.True(guns.Sum(w => w.CAmmoPerShot) <= h.MaxAmmo);
                if (step == 0) initial[role] = selected.HullId;
                output.WriteLine($"civ={civ} step={step} dock={dock}: {selected.Reason}");
            }
        }
        Assert.True(checks > 15);
        var finalScout = TechLoadoutSelector.Select(core, civ, Showcase.HullRole.Scout, state.Techs, tree.Civ.InitialStationTypeId);
        Assert.NotNull(finalScout); Assert.NotEqual(initial[Showcase.HullRole.Scout], finalScout.HullId);
        var fallback = BotManager.PickTechLoadout(new(civ, tree.Civ.InitialStationTypeId,
            new ResearchState(tree).Techs, 1_000_000), Showcase.HullRole.Heavy);
        Assert.NotNull(fallback); Assert.Contains("temporary", fallback.Reason);
        Assert.Null(TechLoadoutSelector.Select(core, civ, Showcase.HullRole.Fighter, state.Techs, tree.Civ.InitialStationTypeId, budget: -1));
    }

    [SkippableFact]
    public void Dock_local_masks_are_never_combined_and_finale_uses_the_same_bomber_selection()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var state = new ResearchState(new(core, 1));
        var target = core.StationTypes.Values.First();
        foreach (var step in state.Tree.Generate("PCore001").Steps) state.Apply(step);
        foreach (short dock in state.Stations)
        {
            var selector = TechLoadoutSelector.Select(core, 1, Showcase.HullRole.Bomber, state.Techs, dock, target);
            var finale = ShowcaseFinale.SelectLoadout(core, target, state.Techs, core.StationTypes[dock].TtbmLocal);
            Assert.Equal(selector?.HullId, finale?.HullId);
            Assert.NotNull(selector); Assert.NotNull(finale);
            Assert.Equal(selector.Parts, finale.Parts);
            Assert.True(TechLoadoutSelector.Legal(core, 1, state.Techs, dock, selector.HullId, selector.Parts));
        }
        // Isolate one real local prerequisite while satisfying its other bits.
        // Empty global techs alone cannot buy civ-specific hulls in this core.
        bool distinguished = false;
        var purchases = core.HullTypes.Values.Select(h => (Kind: "hull", Id: h.HullId, Required: h.TtbmRequired))
            .Concat(state.Tree.Parts.Select(p => (Kind: "part", Id: p.PartId, Required: p.TtbmRequired))).ToArray();
        foreach (var dock in core.StationTypes.Values)
        foreach (var other in core.StationTypes.Values.Where(s => s.StationTypeId != dock.StationTypeId))
        {
            var purchase = purchases.FirstOrDefault(p => Enumerable.Range(0, 50).Any(i =>
                (p.Required[i] & dock.TtbmLocal[i] & ~other.TtbmLocal[i]) != 0));
            if (purchase.Required is null) continue;
            var global = (byte[])purchase.Required.Clone();
            for (int i = 0; i < 50; i++) global[i] &= (byte)~(dock.TtbmLocal[i] & ~other.TtbmLocal[i]);
            var techs = TechTreeBitMask.FromBytes(global);
            Assert.Contains(state.Tree.Unlocks(techs, [dock.StationTypeId]), p => p.Kind == purchase.Kind && p.Id == purchase.Id);
            Assert.DoesNotContain(state.Tree.Unlocks(techs, [other.StationTypeId]), p => p.Kind == purchase.Kind && p.Id == purchase.Id);
            distinguished = true; break;
        }
        Assert.True(distinguished);
    }
}

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P704TechLoadoutLiveTests(ITestOutputHelper output)
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type BotType = typeof(BotManager).GetNestedType("ActiveBot", BindingFlags.NonPublic)!;
    private static void Set(object obj, string name, object? value) => obj.GetType().GetField(name, Fields)!.SetValue(obj, value);
    private static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, Fields)!.GetValue(obj)!;
    private static object? Call(string method, params object?[] args) => typeof(BotManager)
        .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);

    private sealed class Rig : IDisposable
    {
        internal readonly Srv Server = new();
        internal readonly List<(ConnectionSession Client, object Bot)> Bots = [];
        internal readonly List<(double At, short Hull, short Ship)> Requests = [];
        internal readonly HashSet<BuyLoadoutAck> Acks = new(ReferenceEqualityComparer.Instance);
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal Action<ConnectionSession>? BeforeSend;
        private readonly Station _expansion;
        internal Mission Mission => Server.Missions[0];
        internal Rig(int count)
        {
            Server.SpawnInitialMinersAtBoot = false;
            Server.Boot(new()
            {
                ArtPath = Catalog.ArtRoot, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
                LiveSettings = new() { TimeDilation = "off", TestHarness = new() { Enabled = true, Money = 1_000_000, IgnoreTechGate = false } },
                Games = [new() { Name = "P704", Map = "XXV_simple.igc", Teams = 2,
                    CivIds = [1, 4], SideNames = ["Alpha", "Bravo"] }],
            });
            // A real neutral Expansion type becomes newly owned after research.
            // This independently exercises the client's changing owned-station effects.
            var home = Mission.Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == 0);
            var expansionType = Mission.Core.StationTypes[20];
            _expansion = new Station(home.Data with { StationId = 7040, StationTypeId = expansionType.StationTypeId,
                SideId = -1, Name = "P704 neutral expansion", Position = [12000, 0, 0] }, expansionType);
            Mission.PlaceStation(_expansion, Mission.Sectors.Single(s => s.Id == home.ClusterId));
            var pacer = new BotManager.LoadoutPurchasePacer();
            for (int i = 0; i < count; i++)
            {
                var cs = P660NextMatchLiveTests.Connect(Server, $"mcp-p704-{i}", (ulong)(70400 + i));
                short id = cs.World.MyShipId!.Value, side = cs.World.MySideId!.Value;
                var bot = Activator.CreateInstance(BotType, Fields, null,
                    [$"p704-{i}", (short)15, "fighter", side, side, "combat", 0.0], null)!;
                Set(bot, "ShipId", id); Set(bot, "Cs", cs); Set(bot, "TechLoadouts", true);
                Set(bot, "PurchasePacer", pacer); Set(bot, "Role", "bomber");
                Set(bot, "IsPod", true); Set(bot, "PodDocked", true);
                Bots.Add((cs, bot));
                Pump(() => { lock (cs.Gate) return BotManager.CurrentLoadoutShop(cs, side, id) is not null; });
            }
            Mission.Stage = MissionStage.Started;
            foreach (var (cs, _) in Bots) cs.ResetBuyLoadoutAcks();
            Acks.Clear();
        }
        internal void Tick()
        {
            Server.TickSim();
            foreach (var (cs, _) in Bots)
                foreach (var ack in cs.BuyLoadoutAcks) Acks.Add(ack);
            foreach (var live in Server.Ships.Where(l => l.Ship.StationId is null))
            {
                // Keep fixture ships apart while pumping research time. Purchase,
                // ejection, rescue and all notifications still use the real server.
                live.Ship.Position = new(10000 + live.Ship.Id % 64 * 150, 5000, 0);
                live.Ship.Velocity = Vector3.Zero;
            }
            Thread.Sleep(1);
        }
        internal void Pump(Func<bool> done, int seconds = 30)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed.TotalSeconds < seconds) Tick();
            Assert.True(done(), "P704 loopback condition timed out: " + string.Join("; ", Bots.Take(10).Select(p =>
            {
                lock (p.Client.Gate)
                {
                    var me = p.Client.World.Ships[Get<short>(p.Bot, "ShipId")];
                    var ack = p.Client.BuyLoadoutAcks.LastOrDefault();
                    return $"{me.ShipId}: dock={me.StationId}, hull={me.HullId}, attempts={Get<int>(p.Bot, "RelaunchAttempts")}, ack={ack?.HullId}/{ack?.FLaunch}/{ack?.FBoughtEverything}, warning={Get<string?>(p.Bot, "GrantWarning")}";
                }
            })));
        }
        internal void LaunchAll()
        {
            Pump(() =>
            {
                foreach (var (cs, bot) in Bots)
                {
                    Action<short, bool, IReadOnlyList<ExpandedPartData>> send = (h, launch, parts) =>
                    {
                        Requests.Add((Clock.Elapsed.TotalSeconds, h, Get<short>(bot, "ShipId")));
                        BeforeSend?.Invoke(cs);
                        cs.SendBuyLoadout(h, launch, parts);
                    };
                    Call("RelaunchDockedPod", bot, cs, Clock.Elapsed.TotalSeconds, send, false);
                    Call("HandleRelaunchAck", bot, cs);
                }
                return Bots.All(p =>
                {
                    lock (p.Client.Gate) return p.Client.World.Ships.TryGetValue(Get<short>(p.Bot, "ShipId"), out var me)
                        && me.StationId < 0 && !Catalog.IsLifepodHull(me.HullId)
                        && p.Client.BuyLoadoutAcks.LastOrDefault() is { FLaunch: true };
                });
            });
            foreach (var (cs, bot) in Bots)
            {
                Call("HandleRelaunchAck", bot, cs);
                Call("UpdatePodState", bot, cs, Clock.Elapsed.TotalSeconds);
            }
            Tick();
        }
        internal void ResearchBomber()
        {
            var side = Mission.Sides.Single(s => s.Id == 0);
            var bucket = side.Buckets.Single(b => b.Development?.DevelopmentId == 37);
            Assert.True(side.CanBuy(bucket));
            var donor = Bots.First(p => Get<short>(p.Bot, "Side") == 0).Client;
            donor.SendBucketDonate(bucket.Id, bucket.Price);
            Pump(() => bucket.Completed, 45);
            var ship = Server.Ships.Single(l => l.Ship.Id == donor.World.MyShipId).Ship;
            Mission.Site.CaptureStationEvent(ship, _expansion);
            _expansion.Capture(0);
            Pump(() => Bots.All(p => { lock (p.Client.Gate) return p.Client.World.Stations.TryGetValue(7040, out var st) && st.SideId == 0; }));
            Pump(() => Bots.Where(p => Get<short>(p.Bot, "Side") == 0).All(p =>
            { lock (p.Client.Gate) return p.Client.World.DevelopmentTechsFor(0) == side.DevelopmentTechs; }));
        }
        internal void KillAndRescueAll()
        {
            foreach (var (cs, bot) in Bots)
            {
                var ship = Server.Ships.Single(l => l.Ship.Id == Get<short>(bot, "ShipId")).Ship;
                ship.ReceiveDamage(0, 1_000_000, Server.SimNow, ship.Position, ship.Position, null);
            }
            Pump(() => Bots.All(p => { lock (p.Client.Gate) return Catalog.IsLifepodHull(p.Client.World.Ships[Get<short>(p.Bot, "ShipId")].HullId); }));
            foreach (var (cs, bot) in Bots)
            {
                Call("UpdatePodState", bot, cs, Clock.Elapsed.TotalSeconds);
                var ship = Server.Ships.Single(l => l.Ship.Id == Get<short>(bot, "ShipId")).Ship;
                var home = Mission.Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == ship.SideId);
                typeof(Srv).GetMethod("PerformStationRescue", Fields)!.Invoke(Server, [ship, home]);
            }
            Pump(() => Bots.All(p => { lock (p.Client.Gate) return p.Client.World.Ships[Get<short>(p.Bot, "ShipId")].StationId >= 0; }));
            foreach (var (cs, bot) in Bots) { Call("UpdatePodState", bot, cs, Clock.Elapsed.TotalSeconds); cs.ResetBuyLoadoutAcks(); }
        }
        public void Dispose() { foreach (var p in Bots) p.Client.Dispose(); Server.Dispose(); }
    }

    [SkippableTheory]
    [InlineData(1)] [InlineData(60)]
    public void Real_research_repick_and_simultaneous_death_relaunch_have_no_server_refusals(int count)
    {
        Require.Artwork();
        using var rig = new Rig(count);
        rig.LaunchAll();
        var old = rig.Bots.ToDictionary(p => Get<short>(p.Bot, "ShipId"), p => Get<short>(p.Bot, "HullId"));
        Assert.All(rig.Bots, p => Assert.NotEqual(Showcase.HullRole.Bomber,
            Showcase.ClassifyHull(Catalog.RepresentativeCore().HullTypes[Get<short>(p.Bot, "HullId")])));
        rig.ResearchBomber();
        Assert.Equal(count, rig.Requests.Count); // no purchases caused by SIDE_TECH_CHANGE
        rig.KillAndRescueAll();
        rig.LaunchAll();
        Assert.Equal(count * 2, rig.Requests.Count);
        Assert.Equal(count * 2, rig.Acks.Count);
        Assert.All(rig.Acks, a => { Assert.True(a.FLaunch); Assert.True(a.FBoughtEverything); });
        foreach (var (cs, bot) in rig.Bots.Where(p => Get<short>(p.Bot, "Side") == 0))
        {
            var hull = Get<short>(bot, "HullId");
            Assert.True(old[Get<short>(bot, "ShipId")] != hull, Get<TechLoadoutSelector.Selection?>(bot, "PreviousLegalLoadout")?.Reason);
            Assert.Equal(Showcase.HullRole.Bomber, Showcase.ClassifyHull(Catalog.RepresentativeCore().HullTypes[hull]));
            Assert.Equal(hull, rig.Server.Ships.Single(l => l.Ship.Id == Get<short>(bot, "ShipId")).Ship.HullType.HullId);
            Assert.NotNull(Get<TechLoadoutSelector.Selection?>(bot, "PreviousLegalLoadout"));
        }
        if (count == 60)
        {
            var burst = rig.Requests.Skip(count).ToArray();
            Assert.True(burst[^1].At - burst[0].At >= 2.8);
            Assert.True(burst.GroupBy(r => (int)Math.Floor(r.At)).Max(g => g.Count()) <= 21);
        }
        Assert.Equal(0, rig.Server.SimPassStageFailures);
        output.WriteLine($"{count} peers: {rig.Requests.Count} purchases, {rig.Acks.Count} full launch grants, 0 refusals; research through real BUCKET_DONATE + completion + SIDE_TECH_CHANGE; wall={rig.Clock.Elapsed.TotalSeconds:F2}s");
    }

    [SkippableFact]
    public void Refused_upgrade_falls_back_to_previous_revalidated_legal_loadout()
    {
        Require.Artwork();
        using var rig = new Rig(1);
        rig.LaunchAll();
        var (cs, bot) = rig.Bots.Single();
        var previous = Get<TechLoadoutSelector.Selection>(bot, "PreviousLegalLoadout");
        rig.ResearchBomber();
        rig.KillAndRescueAll();
        int sends = 0;
        rig.BeforeSend = peer =>
        {
            var live = rig.Server.Ships.Single(l => l.Ship.Id == Get<short>(bot, "ShipId"));
            // A wallet change after the client snapshot is an unavoidable refusal
            // race. This intentionally injects one, then restores available funds.
            live.Wallet.Money = ++sends == 1 ? 0 : 1_000_000;
        };
        rig.LaunchAll();
        Assert.Equal(2, sends);
        Assert.Single(rig.Acks, a => !a.FLaunch);
        Assert.Equal(3, rig.Requests.Count);
        Assert.NotEqual(previous.HullId, rig.Requests[1].Hull);
        Assert.Equal(previous.HullId, rig.Requests[2].Hull);
        Assert.True(cs.BuyLoadoutAcks.Last().FBoughtEverything);
        Assert.Contains("refusal fallback", Get<TechLoadoutSelector.Selection>(bot, "PreviousLegalLoadout").Reason);
        output.WriteLine("One intentionally stale-wallet refusal; previous loadout revalidated and fully granted on the next paced retry.");
    }
}


public sealed partial class BotFixRegressionTests
{
    [SkippableTheory]
    [InlineData((short)14, false)]
    [InlineData((short)23, true)]
    public void P704_Requested_bomber_role_attacks_stations_only_after_a_real_bomber_is_granted(short flyingHull, bool attacksStation)
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World);
        cs.World.Apply(new ExportShip { ShipId = 1, SideId = 0, HullId = flyingHull });
        cs.World.Apply(new ExportStation { Station = Station(101, 1, 1, 1000) });
        DefenseEnemy(cs.World, 9, 15, 500);
        var bot = Bot(cs);
        Set(bot, "Role", "bomber"); Set(bot, "HullId", flyingHull);
        Set(bot, "Parts", Catalog.NativePreferredLoadout(flyingHull, Catalog.RepresentativeCore().HullTypes[flyingHull].Name).Parts);
        Set(bot, "TargetSector", (short?)1);
        DefenseNoSocketSender(bot);
        Call("EngageCombat", Manager(bot), bot, 10.0, null);
        Assert.Equal((short?)(attacksStation ? 101 : 9), Get<short?>(bot, "TargetId"));
        Assert.Equal("bomber", Get<string>(bot, "Role")); // future lives retain the intended role
    }
}
