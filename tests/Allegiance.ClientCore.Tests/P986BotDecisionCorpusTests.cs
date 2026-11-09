using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using H = Allegiance.Tools.McpSpawner.BotTargetClass;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P986 — bot decision golden corpus. Each scenario is a deterministic,
/// synthetic input/output pair for one bot decision family. The corpus files
/// live next to this test so they can be diffed and reviewed; running with
/// <c>UPDATE_CORPUS=1</c> regenerates them. The tests prove the current driver
/// produces exactly the recorded decisions and catch accidental behaviour drift.
/// </summary>
public sealed class P986BotDecisionCorpusTests : IDisposable
{
    private static readonly string CorpusDir = Path.Combine(
        Path.GetDirectoryName(typeof(P986BotDecisionCorpusTests).Assembly.Location)!,
        "..", "..", "..", "GoldenCorpus", "BotDecisions");

    private readonly StaticCore _core = Catalog.RepresentativeCore();
    private readonly List<string> _updated = new();

    public P986BotDecisionCorpusTests()
    {
        Directory.CreateDirectory(CorpusDir);
    }

    public void Dispose()
    {
        if (_updated.Count > 0)
            Console.WriteLine("P986 corpus updated: " + string.Join(", ", _updated));
    }

    private static bool UpdateMode => Environment.GetEnvironmentVariable("UPDATE_CORPUS") == "1";

    // ── corpus helpers ───────────────────────────────────────────────────

    private void AssertAgainstCorpus(string fileName, JsonArray scenarios)
    {
        string path = Path.Combine(CorpusDir, fileName);
        if (UpdateMode)
        {
            File.WriteAllText(path, scenarios.ToJsonString(_indented) + Environment.NewLine);
            _updated.Add(fileName);
            return;
        }

        Assert.True(File.Exists(path), $"Missing corpus: {path}. Run with UPDATE_CORPUS=1 to regenerate.");
        var expected = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        string expectedText = expected.ToJsonString(_indented);
        string actualText = scenarios.ToJsonString(_indented);
        if (expectedText != actualText)
            Assert.Fail($"Corpus mismatch in {fileName}. Diff:{Environment.NewLine}{BotDecisionRecorder.Diff(expectedText, actualText)}");
    }

    private static JsonArray RunAll(params (string Name, Func<JsonObject> Run)[] scenarios)
    {
        var arr = new JsonArray();
        foreach (var (name, run) in scenarios)
        {
            var obj = run();
            obj["name"] = name;
            arr.Add(obj);
        }
        return arr;
    }

    private static JsonObject Scenario(object inputs, object outputs)
        => new() { ["inputs"] = BotDecisionRecorder.ToJson(inputs), ["outputs"] = BotDecisionRecorder.ToJson(outputs) };

    private static readonly JsonSerializerOptions _indented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // ── 1. Combat target choice ──────────────────────────────────────────

    [SkippableFact]
    public void CombatTarget_CorpusMatches()
    {
        Require.Artwork();
        var empty = new Dictionary<short, int>();

        var scenarios = RunAll(
            ("lone-miner-first-bot", () =>
            {
                var miner = Cand(9001, H.MinerDrone, dist: 100f);
                var ctx = Ctx(empty);
                var choice = CombatTargetSelector.Select(new[] { miner }, ctx);
                return Scenario(
                    new { self = ctx.SelfShipId, cluster = ctx.MyCluster, candidates = new[] { new { miner.Id, miner.Class, miner.IsThreat, miner.Distance } } },
                    new { choice.TargetId, choice.Hold, choice.ChosenClass });
            }),
            ("lone-miner-at-cap-holds", () =>
            {
                var miner = Cand(9001, H.MinerDrone, dist: 100f);
                var ctx = Ctx(new Dictionary<short, int> { [9001] = CombatTargetSelector.CapMinerDrone });
                var choice = CombatTargetSelector.Select(new[] { miner }, ctx);
                return Scenario(
                    new { self = ctx.SelfShipId, cluster = ctx.MyCluster, candidates = new[] { new { miner.Id, miner.Class, miner.IsThreat, miner.Distance } }, ctx.EngagerCounts },
                    new { choice.TargetId, choice.Hold, choice.ChosenClass });
            }),
            ("threat-beats-nearer-fighter", () =>
            {
                var threat = Cand(9101, H.Fighter, threat: true, dist: 999f);
                var idle = Cand(9102, H.Fighter, dist: 50f);
                var miner = Cand(9001, H.MinerDrone, dist: 10f);
                var all = new[] { miner, idle, threat };
                var ctx = Ctx(empty);
                var choice = CombatTargetSelector.Select(all, ctx);
                return Scenario(
                    new { self = ctx.SelfShipId, cluster = ctx.MyCluster, candidates = all.Select(c => new { c.Id, c.Class, c.IsThreat, c.Distance }).ToArray() },
                    new { choice.TargetId, choice.Hold, choice.ChosenClass });
            }),
            ("combat-present-miner-ignored", () =>
            {
                var fighter = Cand(9102, H.Fighter, dist: 200f);
                var miner = Cand(9001, H.MinerDrone, dist: 5f);
                var all = new[] { miner, fighter };
                var ctx = Ctx(empty);
                var choice = CombatTargetSelector.Select(all, ctx);
                return Scenario(
                    new { self = ctx.SelfShipId, cluster = ctx.MyCluster, candidates = all.Select(c => new { c.Id, c.Class, c.IsThreat, c.Distance }).ToArray() },
                    new { choice.TargetId, choice.Hold, choice.ChosenClass });
            }),
            ("focus-fire-second-bot-fills", () =>
            {
                var f1 = Cand(9102, H.Fighter, dist: 100f);
                var f2 = Cand(9103, H.Fighter, dist: 120f);
                var all = new[] { f1, f2 };
                var ctx = Ctx(new Dictionary<short, int> { [9102] = 1 });
                var choice = CombatTargetSelector.Select(all, ctx);
                return Scenario(
                    new { self = ctx.SelfShipId, cluster = ctx.MyCluster, candidates = all.Select(c => new { c.Id, c.Class, c.IsThreat, c.Distance }).ToArray(), ctx.EngagerCounts },
                    new { choice.TargetId, choice.Hold, choice.ChosenClass });
            }),
            ("two-side-fighter-spread", () =>
            {
                // Side A (self=9900) vs Side B and Side C in the same cluster.
                var bFighter = Cand(1001, H.Fighter, dist: 300f);
                var cFighter = Cand(2001, H.Fighter, dist: 400f);
                var bMiner = Cand(1002, H.MinerDrone, dist: 150f);
                var all = new[] { bFighter, cFighter, bMiner };
                var ctx = Ctx(empty, teammates: 10);
                var choice = CombatTargetSelector.Select(all, ctx);
                return Scenario(
                    new { self = ctx.SelfShipId, cluster = ctx.MyCluster, teammates = ctx.TeammatesInCluster, candidates = all.Select(c => new { c.Id, c.Class, c.IsThreat, c.Distance }).ToArray() },
                    new { choice.TargetId, choice.Hold, choice.ChosenClass });
            }),
            ("three-side-capital-threat", () =>
            {
                var bCapital = Cand(1001, H.Capital, dist: 800f);
                var cBomber = Cand(2001, H.Bomber, dist: 600f);
                var bFighter = Cand(1002, H.Fighter, dist: 200f);
                var all = new[] { bCapital, cBomber, bFighter };
                var ctx = Ctx(empty, teammates: 20);
                var choice = CombatTargetSelector.Select(all, ctx);
                return Scenario(
                    new { self = ctx.SelfShipId, cluster = ctx.MyCluster, teammates = ctx.TeammatesInCluster, candidates = all.Select(c => new { c.Id, c.Class, c.IsThreat, c.Distance }).ToArray() },
                    new { choice.TargetId, choice.Hold, choice.ChosenClass });
            }),
            ("empty-sector-holds", () =>
            {
                var ctx = Ctx(empty);
                var choice = CombatTargetSelector.Select([], ctx);
                return Scenario(
                    new { self = ctx.SelfShipId, cluster = ctx.MyCluster, candidates = Array.Empty<object>() },
                    new { choice.TargetId, choice.Hold, choice.ChosenClass });
            })
        );

        AssertAgainstCorpus("combat-target.json", scenarios);
    }

    // ── 2. Loadout selection ─────────────────────────────────────────────

    [SkippableFact]
    public void Loadout_CorpusMatches()
    {
        Require.Artwork();
        var techs = TechTreeBitMask.Empty;
        var dock = _core.Civilizations.Values.First().InitialStationTypeId;

        var scenarios = RunAll(
            ("fighter-default", () =>
            {
                var sel = TechLoadoutSelector.Select(_core, 1, Showcase.HullRole.Fighter, techs, dock);
                return Scenario(
                    new { civId = 1, role = Showcase.HullRole.Fighter, dockTypeId = dock, budget = long.MaxValue },
                    new { sel?.HullId, sel?.HullName, sel?.Price, parts = sel?.Parts.Select(p => new { p.PartId, p.Amount, p.MountId }).ToArray() });
            }),
            ("scout-prefers-cloak", () =>
            {
                var sel = TechLoadoutSelector.Select(_core, 1, Showcase.HullRole.Scout, techs, dock);
                return Scenario(
                    new { civId = 1, role = Showcase.HullRole.Scout, dockTypeId = dock },
                    new { sel?.HullId, sel?.HullName, hasCloak = sel?.Parts.Any(p => p.MountId == 0 && p.Amount > 0) });
            }),
            ("bomber-strike-loadout", () =>
            {
                var target = _core.StationTypes[dock];
                var sel = TechLoadoutSelector.Select(_core, 1, Showcase.HullRole.Bomber, techs, dock, target);
                return Scenario(
                    new { civId = 1, role = Showcase.HullRole.Bomber, dockTypeId = dock, targetStationTypeId = target.StationTypeId },
                    new { sel?.HullId, sel?.HullName, sel?.Price, parts = sel?.Parts.Select(p => new { p.PartId, p.Amount, p.MountId }).ToArray() });
            }),
            ("support-nanite", () =>
            {
                var sel = TechLoadoutSelector.SelectSupport(_core, 1, techs, dock, long.MaxValue);
                return Scenario(
                    new { civId = 1, dockTypeId = dock, budget = long.MaxValue },
                    new { sel?.HullId, sel?.HullName, sel?.Price, parts = sel?.Parts.Select(p => new { p.PartId, p.Amount, p.MountId }).ToArray() });
            }),
            ("rescue-probe-scout", () =>
            {
                var sel = TechLoadoutSelector.Select(_core, 1, Showcase.HullRole.Scout, techs, dock, rescueProbe: true);
                return Scenario(
                    new { civId = 1, role = Showcase.HullRole.Scout, dockTypeId = dock, rescueProbe = true },
                    new { sel?.HullId, sel?.HullName, hasRescueProbe = sel?.Parts.Any(p => _core.TryBuildExpendableLauncher(p.PartId, out var ep) && ep.Type is DataPartTypeIGC.ProbeLauncher).ToString() });
            })
        );

        AssertAgainstCorpus("loadout.json", scenarios);
    }

    // ── 3. Research commander ────────────────────────────────────────────

    [SkippableFact]
    public void ResearchCommander_CorpusMatches()
    {
        Require.Artwork();
        var tree = new ResearchTree(_core, 1);
        var plan = BuildPlan(tree);
        var reservations = new Dictionary<short, short>();
        var initialTechs = tree.Settings.StartingTechs(tree.Civ);
        var commander = new ResearchCommander(0, "p986", tree, plan, initialTechs, reservations);

        var scenarios = RunAll(
            ("waiting-for-start", () =>
            {
                var world = NewWorld(tree, started: false);
                var cmds = commander.Tick(world);
                return Scenario(
                    new { side = commander.Side, plan = plan.Name, started = false, wallet = world.Wallet },
                    new { commandCount = cmds.Count, status = commander.Telemetry!.Status });
            }),
            ("first-donation", () =>
            {
                var world = NewWorld(tree, started: true, wallet: 7);
                var cmds = commander.Tick(world);
                return Scenario(
                    new { side = commander.Side, plan = plan.Name, started = true, wallet = world.Wallet },
                    new { commandCount = cmds.Count, firstKind = cmds.FirstOrDefault()?.Kind ?? "(none)", firstAmount = cmds.FirstOrDefault()?.Amount ?? 0, status = commander.Telemetry!.Status });
            }),
            ("insufficient-wallet", () =>
            {
                var world = NewWorld(tree, started: true, wallet: 0);
                var cmds = commander.Tick(world);
                return Scenario(
                    new { side = commander.Side, plan = plan.Name, started = true, wallet = 0 },
                    new { commandCount = cmds.Count, status = commander.Telemetry!.Status });
            }),
            ("drone-cap-blocks", () =>
            {
                var station = plan.Steps.First(s => s.Action == "station");
                var drone = plan.Steps.First(s => s.Action == "drone").Id;
                var world = NewWorld(tree, started: true, wallet: 1_000_000,
                    stations: new[] { tree.Civ.InitialStationTypeId, station.Id },
                    units: Enumerable.Range(100, 4).Select(id => new ResearchCommander.Unit((short)id, drone, -1, true)).ToArray());
                var cmds = commander.Tick(world);
                return Scenario(
                    new { side = commander.Side, plan = plan.Name, wallet = world.Wallet, unitCount = world.Units.Length },
                    new { commandCount = cmds.Count, status = commander.Telemetry!.Status, action = commander.Telemetry.Action });
            })
        );

        AssertAgainstCorpus("research-commander.json", scenarios);
    }

    // ── 4. Medevac scout selection ───────────────────────────────────────

    [SkippableFact]
    public void Medevac_CorpusMatches()
    {
        var scenarios = RunAll(
            ("two-side-one-each", () =>
            {
                var bots = new[]
                {
                    new BotManager.MedevacCandidate(1, 1, "scout", true),
                    new BotManager.MedevacCandidate(2, 2, "scout", true),
                    new BotManager.MedevacCandidate(3, 1, "fighter", true),
                };
                var chosen = BotManager.PickMedevacScouts(bots);
                return Scenario(
                    new { candidates = bots.Select(b => new { b.BotId, b.Side, b.Role, b.CanBuyRescueProbe }).ToArray() },
                    new { chosen = chosen.OrderBy(i => i).ToArray() });
            }),
            ("three-side-mixed-roles", () =>
            {
                var bots = new[]
                {
                    new BotManager.MedevacCandidate(1, 1, "scout", true),
                    new BotManager.MedevacCandidate(2, 1, "scout", true),
                    new BotManager.MedevacCandidate(3, 2, "scout", false),
                    new BotManager.MedevacCandidate(4, 3, "scout", true),
                    new BotManager.MedevacCandidate(5, 3, "scout", true),
                };
                var chosen = BotManager.PickMedevacScouts(bots);
                return Scenario(
                    new { candidates = bots.Select(b => new { b.BotId, b.Side, b.Role, b.CanBuyRescueProbe }).ToArray() },
                    new { chosen = chosen.OrderBy(i => i).ToArray() });
            }),
            ("no-scouts", () =>
            {
                var bots = new[]
                {
                    new BotManager.MedevacCandidate(1, 1, "fighter", true),
                    new BotManager.MedevacCandidate(2, 2, "bomber", true),
                };
                var chosen = BotManager.PickMedevacScouts(bots);
                return Scenario(
                    new { candidates = bots.Select(b => new { b.BotId, b.Side, b.Role, b.CanBuyRescueProbe }).ToArray() },
                    new { chosen = chosen.OrderBy(i => i).ToArray() });
            }),
            ("drop-gate-yes", () =>
            {
                bool drop = BotManager.ShouldDropMedevacProbe(
                    ownStationInSector: false, ownPodsInSector: 3, recentOwnEjects: 0,
                    ownRescueProbeNearby: false, now: 100, lastRescueProbeAt: 0, loadTime: 10);
                return Scenario(
                    new { ownStationInSector = false, ownPodsInSector = 3, recentOwnEjects = 0, ownRescueProbeNearby = false, now = 100, lastRescueProbeAt = 0, loadTime = 10 },
                    new { drop });
            }),
            ("drop-gate-no-probe-nearby", () =>
            {
                bool drop = BotManager.ShouldDropMedevacProbe(
                    ownStationInSector: false, ownPodsInSector: 3, recentOwnEjects: 0,
                    ownRescueProbeNearby: true, now: 100, lastRescueProbeAt: 0, loadTime: 10);
                return Scenario(
                    new { ownStationInSector = false, ownPodsInSector = 3, recentOwnEjects = 0, ownRescueProbeNearby = true, now = 100, lastRescueProbeAt = 0, loadTime = 10 },
                    new { drop });
            })
        );

        AssertAgainstCorpus("medevac.json", scenarios);
    }

    // ── 5. Defense budget ────────────────────────────────────────────────

    [SkippableFact]
    public void DefenseBudget_CorpusMatches()
    {
        var scenarios = RunAll(
            ("small-population", () =>
            {
                int budget = BotManager.DefenseBudgetFor(population: 10, alreadyAssigned: 0, cap: 30, share: .15);
                return Scenario(new { population = 10, alreadyAssigned = 0, cap = 30, share = .15 }, new { budget });
            }),
            ("large-population-capped", () =>
            {
                int budget = BotManager.DefenseBudgetFor(population: 200, alreadyAssigned: 5, cap: 30, share: .15);
                return Scenario(new { population = 200, alreadyAssigned = 5, cap = 30, share = .15 }, new { budget });
            }),
            ("already-assigned-exhausts", () =>
            {
                int budget = BotManager.DefenseBudgetFor(population: 100, alreadyAssigned: 15, cap: 15, share: .15);
                return Scenario(new { population = 100, alreadyAssigned = 15, cap = 15, share = .15 }, new { budget });
            }),
            ("zero-share", () =>
            {
                int budget = BotManager.DefenseBudgetFor(population: 100, alreadyAssigned: 0, cap: 30, share: 0);
                return Scenario(new { population = 100, alreadyAssigned = 0, cap = 30, share = 0 }, new { budget });
            })
        );

        AssertAgainstCorpus("defense-budget.json", scenarios);
    }

    // ── 6. Route / travel ────────────────────────────────────────────────

    [SkippableFact]
    public void Route_CorpusMatches()
    {
        // A tiny deterministic graph: 1-2-3 and 1-4-3 (two distinct entry routes).
        var warps = new Dictionary<short, DataWarpIGC>
        {
            [10] = new(10, 20, 100, "", "", "", [0f, 0f, 0f], [0f, 0f, 0f], [0f, 0f, 0f], 0f, 1),
            [20] = new(20, 10, 100, "", "", "", [0f, 0f, 0f], [0f, 0f, 0f], [0f, 0f, 0f], 0f, 2),
            [21] = new(21, 30, 100, "", "", "", [0f, 0f, 0f], [0f, 0f, 0f], [0f, 0f, 0f], 0f, 2),
            [30] = new(30, 21, 100, "", "", "", [0f, 0f, 0f], [0f, 0f, 0f], [0f, 0f, 0f], 0f, 3),
            [40] = new(40, 41, 100, "", "", "", [0f, 0f, 0f], [0f, 0f, 0f], [0f, 0f, 0f], 0f, 1),
            [41] = new(41, 40, 100, "", "", "", [0f, 0f, 0f], [0f, 0f, 0f], [0f, 0f, 0f], 0f, 4),
            [42] = new(42, 43, 100, "", "", "", [0f, 0f, 0f], [0f, 0f, 0f], [0f, 0f, 0f], 0f, 4),
            [43] = new(43, 42, 100, "", "", "", [0f, 0f, 0f], [0f, 0f, 0f], [0f, 0f, 0f], 0f, 3),
        };

        var scenarios = RunAll(
            ("distinct-entry-routes", () =>
            {
                var routes = BotManager.RouteEnumeration(warps, from: 1, to: 3, maxRoutes: 4);
                return Scenario(
                    new { fromCluster = 1, toCluster = 3, maxRoutes = 4, warpCount = warps.Count },
                    new { routeCount = routes.Count, routes = routes.Select(r => r.ToArray()).ToArray() });
            }),
            ("pick-least-loaded-route", () =>
            {
                int pick = BotManager.PickLeastLoadedRoute(new[] { 5, 2, 8 });
                return Scenario(
                    new { counts = new[] { 5, 2, 8 } },
                    new { pick });
            }),
            ("tied-least-loaded-goes-to-lowest-index", () =>
            {
                // A real tie at the minimum (review fix: the earlier {3,3,1} had no
                // tie at the argmin, so flipping the tie rule went unnoticed).
                int pick = BotManager.PickLeastLoadedRoute(new[] { 2, 1, 1 });
                return Scenario(
                    new { counts = new[] { 2, 1, 1 } },
                    new { pick });
            })
        );

        AssertAgainstCorpus("route.json", scenarios);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static BotTargetCandidate Cand(short id, H cls, bool threat = false, float dist = 100f)
        => new(id, new Vector3(dist, 0f, 0f), cls, threat) { Distance = dist };

    private static BotSelectionContext Ctx(Dictionary<short, int> engagers, int teammates = 30)
        => new(SelfShipId: 9900, MyCluster: 1052, teammates, engagers);

    private static ResearchPlan BuildPlan(ResearchTree tree)
    {
        var state = new ResearchState(tree);
        var dev = tree.Nodes.First(n => n.Kind == "develop" && state.CanApply(n));
        var develop = new ResearchStep { Action = "develop", Id = dev.Id, Name = dev.Name };
        state.Apply(develop);
        var station = tree.Nodes.First(n => n.Kind == "station" && n.Id != tree.Civ.InitialStationTypeId && state.CanApply(n));
        var build = new ResearchStep
        {
            Action = "station",
            Id = station.Id,
            Name = station.Name,
            AsteroidClass = (short)(station.AsteroidMask & -station.AsteroidMask),
        };
        state.Apply(build);
        var drone = tree.Nodes.First(n => n.Kind == "drone" && state.CanApply(n));
        return new()
        {
            Core = "PCore001",
            CoreVersion = tree.Core.Version,
            CivId = 1,
            Name = "p986",
            Steps = [develop, build, new() { Action = "drone", Id = drone.Id, Name = drone.Name }],
        };
    }

    private static ResearchCommander.Snapshot NewWorld(
        ResearchTree tree, bool started, int wallet = 1_000_000,
        short[]? stations = null, ResearchCommander.Unit[]? units = null)
    {
        var state = new ResearchState(tree);
        var stationId = tree.Nodes.First(n => n.Kind == "station" && n.Id != tree.Civ.InitialStationTypeId && state.CanApply(n)).Id;
        var asteroidMask = tree.Core.StationTypes[stationId].AabmBuild;
        return new(
            Time: started ? 1 : 0,
            Started: started,
            Wallet: wallet,
            Developments: tree.Settings.StartingTechs(tree.Civ),
            Stations: stations ?? [tree.Civ.InitialStationTypeId],
            CompletedDevelopments: new HashSet<short>(),
            Units: units ?? [],
            Rocks: [new(10, asteroidMask, true), new(11, asteroidMask, true)],
            FundingByBucket: new Dictionary<short, ResearchCommander.Funding>());
    }
}
