using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Strategy;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P1001CivFleetTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Identical_portfolio_in_a_new_epoch_restores_duties_and_clears_the_old_coverage_sortie()
    {
        var (_, _, plan) = P1001CivCommanderTests.For(25);
        using var peer = new Allegiance.ClientCore.Session.ConnectionSession();
        var briefing = new Allegiance.Core.Wire.ResearchBriefing { Epoch = "first", Plan = plan };
        typeof(Allegiance.ClientCore.State.ClientWorld).GetProperty("ResearchBriefing")!.SetValue(peer.World, briefing);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var manager = (BotManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BotManager));
        var cache = typeof(BotManager).GetField("_civDutyPlans", flags)!;
        cache.SetValue(manager, Activator.CreateInstance(cache.FieldType));
        typeof(BotManager).GetField("_civSupportShares", flags)!.SetValue(manager, new Dictionary<short, double> { [0] = .15 });
        Assert.False(manager.CivSupportSlot(0, 6, 8, Showcase.HullRole.Bomber));
        Assert.True(manager.CivSupportSlot(0, 0, 8, Showcase.HullRole.Fighter));
        Assert.True(manager.CivSupportSlot(0, 1, 8, Showcase.HullRole.Fighter));
        Assert.False(manager.CivSupportSlot(0, 2, 8, Showcase.HullRole.Fighter));
        Assert.False(manager.CivSupportSlot(0, 0, 8, Showcase.HullRole.Scout));
        Assert.False(manager.CivSupportSlot(0, 0, 8, Showcase.HullRole.Interceptor));
        Assert.False(manager.CivSupportSlot(0, 0, 1, Showcase.HullRole.Fighter));
        var type = typeof(BotManager).GetNestedType("ActiveBot", flags)!;
        var bot = Activator.CreateInstance(type, flags, null, ["epoch-test", (short)15, "Fighter", (short)0, (short)0, "combat", 0d], null)!;
        void Set(string name, object? value) => type.GetField(name, flags)!.SetValue(bot, value);
        object? Get(string name) => type.GetField(name, flags)!.GetValue(bot);
        var refresh = typeof(BotManager).GetMethod("RefreshCivDuty", flags)!;
        Set("CivFleetOrdinal", 0); Set("CivFleetSize", 6);
        refresh.Invoke(manager, [bot, peer]); var duty = Get("Role");
        Set("CivCoverageDuty", "repair"); Set("CivOriginalRole", duty); Set("Role", "support");
        refresh.Invoke(manager, [bot, peer]);
        Assert.Equal("repair", Get("CivCoverageDuty")); // an ordinary refresh keeps this sortie
        briefing.Epoch = "second";
        refresh.Invoke(manager, [bot, peer]);
        Assert.Null(Get("CivCoverageDuty")); Assert.Null(Get("CivOriginalRole")); Assert.Equal(duty, Get("Role"));
        Assert.StartsWith("second/", Assert.IsType<string>(Get("CivDutyPlan")));
    }

    [SkippableTheory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(18)] [InlineData(25)] [InlineData(35)] [InlineData(101)] [InlineData(201)]
    public void Every_civ_has_a_deterministic_legal_fit_and_small_fleet_duties(short civ)
    {
        Require.Artwork(); var (tree, choice, _) = P1001CivCommanderTests.For(civ);
        var profile = CivProfile.Get(tree.Core, civ);
        var state = new ResearchState(tree);
        var input = new CivLoadoutPolicy.Input(tree.Core, profile,
            new[] { CivProfile.Get(tree.Core, civ == 4 ? (short)18 : (short)4) }, state.Techs,
            GlobalAttributeSet.FromArray(tree.Civ.GasBaseAttributes), tree.Civ.InitialStationTypeId, 30000);
        var a = CivLoadoutPolicy.Candidates(input, CivLoadoutPolicy.Duty.Fighter);
        var b = CivLoadoutPolicy.Candidates(input, CivLoadoutPolicy.Duty.Fighter);
        Assert.NotEmpty(a);
        Assert.Equal(JsonSerializer.Serialize(a[0]), JsonSerializer.Serialize(b[0]));
        Assert.All(a, candidate =>
        {
            Assert.True(TechLoadoutSelector.Legal(tree.Core, civ, state.Techs, input.Dock,
                candidate.Loadout.HullId, candidate.Loadout.Parts));
            Assert.InRange(candidate.Loadout.Price, 0, input.Budget);
            Assert.True(candidate.Score > 0 && double.IsFinite(candidate.Score));
            Assert.InRange(candidate.Sustain, 0, 1); Assert.True(candidate.Acceleration > 0);
        });
        var mix = BotManager.ComposeCivFleet(tree.Core, profile, choice.Primary!.Spikes, 6);
        Assert.Equal(6, mix.Sum(e => e.Count));
        Assert.Contains(mix, e => Showcase.ClassifyHull(tree.Core.HullTypes[e.HullId]) == Showcase.HullRole.Bomber);
        Assert.Contains(mix, e => Showcase.ClassifyHull(tree.Core.HullTypes[e.HullId]) == Showcase.HullRole.Scout);
        if (choice.Primary.Spikes.Any(s => s.Duty == "anti-base" && s.Power > 0))
            Assert.Equal(ResearchTactics.StrikeGroupSize, mix.Where(e => Showcase.ClassifyHull(tree.Core.HullTypes[e.HullId]) == Showcase.HullRole.Bomber).Sum(e => e.Count));
        Assert.Equal(7, BotManager.ComposeCivFleet(tree.Core, profile, choice.Primary.Spikes, 7).Sum(e => e.Count));
        output.WriteLine($"civ={civ} hull={a[0].Loadout.HullId} score={a[0].Score:F3} dps={a[0].Damage:F3} candidates={a.Count}");
    }

    [SkippableFact]
    public void Live_attributes_and_opponent_defenses_change_complete_fit_power()
    {
        var (tree, _, _) = P1001CivCommanderTests.For(4); var profile = CivProfile.Get(tree.Core, 4);
        var state = new ResearchState(tree); var ga = (float[])tree.Civ.GasBaseAttributes.Clone();
        var input = new CivLoadoutPolicy.Input(tree.Core, profile, [CivProfile.Get(tree.Core, 18)], state.Techs,
            GlobalAttributeSet.FromArray(ga), tree.Civ.InitialStationTypeId, 30000);
        var baseline = CivLoadoutPolicy.Candidates(input, CivLoadoutPolicy.Duty.Fighter)[0];
        ga[GlobalAttributeSet.DamageGuns] *= 2; ga[GlobalAttributeSet.Thrust] *= 2;
        var improved = CivLoadoutPolicy.Candidates(input with { Attributes = GlobalAttributeSet.FromArray(ga) }, CivLoadoutPolicy.Duty.Fighter)[0];
        Assert.True(improved.Score > baseline.Score);
        Assert.True(improved.Damage > baseline.Damage);
        var other = CivLoadoutPolicy.Candidates(input with { Opponents = [CivProfile.Get(tree.Core, 5)] }, CivLoadoutPolicy.Duty.Fighter)[0];
        Assert.NotEqual(baseline.Score, other.Score);

    }

    [SkippableFact]
    public void Coverage_is_bounded_by_both_slots_and_money_and_resets_on_rotation()
    {
        var (tree, _, plan) = P1001CivCommanderTests.For(4); var state = new ResearchState(tree);
        var input = new CivLoadoutPolicy.Input(tree.Core, CivProfile.Get(tree.Core, 4), [CivProfile.Get(tree.Core, 18)],
            state.Techs, GlobalAttributeSet.FromArray(tree.Civ.GasBaseAttributes), tree.Civ.InitialStationTypeId, 30000);
        var candidates = CivLoadoutPolicy.Candidates(input, CivLoadoutPolicy.Duty.Fighter);
        var shop = new BotManager.LoadoutShop(4, input.Dock, input.Techs, input.Budget, Side: 0, Plan: plan, Epoch: "one");
        CivCoverage.Reset(); var saved = Console.Out; using var log = new StringWriter(); Console.SetOut(log);
        try
        {
            for (int i = 0; i < 40; i++)
            {
                var fit = CivCoverage.Select(shop, .15, candidates, Showcase.HullRole.Fighter);
                CivCoverage.ObserveAccepted(shop.Side, shop.Epoch, shop.Pilot, fit.HullId, fit.Parts, true);
            }
            CivCoverage.Select(shop with { Epoch = "two" }, .15, candidates, Showcase.HullRole.Fighter);
        }
        finally { Console.SetOut(saved); }
        var lines = log.ToString().Split('\n').Where(l => l.Contains("stage=selected")).ToArray();
        Assert.Equal(41, lines.Length);
        Assert.InRange(lines.Take(40).Count(l => l.Contains("coverage=true ")), 1, 6);
        Assert.Contains("selected=1 covered=0", lines[^1]);
        foreach (var line in lines)
        {
            var fields = line.Split(' ').Where(f => f.Contains('=')).Select(f => f.Split('=', 2)).ToDictionary(f => f[0], f => f[1]);
            Assert.True(long.Parse(fields["coverageMoney"]) + long.Parse(fields["pendingCoverageMoney"])
                <= long.Parse(fields["strategyMoney"]) * .15 / .85);
        }
    }

    [SkippableFact]
    public void Strike_and_repair_duties_keep_real_hardware_and_never_mix_repair_with_damage()
    {
        var (tree, _, plan) = P1001CivCommanderTests.For(4);
        var state = new ResearchState(tree);
        foreach (var step in plan.Steps) state.Apply(step);
        var ga = (float[])tree.Civ.GasBaseAttributes.Clone();
        foreach (var s in plan.Steps.Where(s => s.Action == "develop"))
            for (int i = 0; i < ga.Length; i++) ga[i] *= tree.Core.Developments[s.Id].Gas[i];
        var input = new CivLoadoutPolicy.Input(tree.Core, CivProfile.Get(tree.Core, 4), [CivProfile.Get(tree.Core, 18)], state.Techs,
            GlobalAttributeSet.FromArray(ga), SuccessorUpgrade.StationSuccessor(tree.Core, tree.Core.StationTypes[state.Stations.Order().First()], state.DevelopmentTechs).StationTypeId, 1_000_000, tree.Core.StationTypes[tree.Civ.InitialStationTypeId]);
        short strikeDock = state.Stations.Select(id => SuccessorUpgrade.StationSuccessor(tree.Core,
            tree.Core.StationTypes[id], state.DevelopmentTechs).StationTypeId).First(id => TechLoadoutSelector.Select(tree.Core, 4,
                Showcase.HullRole.Bomber, state.Techs, id, input.Target, 1_000_000) is not null);
        input = input with { Dock = strikeDock };
        var strike = CivLoadoutPolicy.Candidates(input, CivLoadoutPolicy.Duty.Strike);
        Assert.NotEmpty(strike);
        Assert.All(strike, s => Assert.True(tree.Core.HullTypes[s.Loadout.HullId].HasCapability(DataHullTypeIGC.HabmThreatToStation)));
        var carrier = tree.Core.HullTypes[strike[0].Loadout.HullId];
        carrier = carrier with { HabmCapabilities = (short)(carrier.HabmCapabilities | DataHullTypeIGC.HabmCarrier) };
        Assert.Equal(Showcase.HullRole.Heavy, Showcase.ClassifyHull(carrier));
        Assert.True(BotManager.CivStationDamageHull(plan, carrier, carrier.HullId, strike[0].Loadout.Parts, input.Target!.Value));
        Assert.False(BotManager.CivStationDamageHull(null, carrier, carrier.HullId, strike[0].Loadout.Parts, input.Target.Value));
        Assert.False(BotManager.CivStationDamageHull(plan, carrier, -1, strike[0].Loadout.Parts, input.Target.Value));
        Assert.False(BotManager.CivStationDamageHull(plan, carrier, carrier.HullId, [], input.Target.Value));
        var shop = new BotManager.LoadoutShop(4, input.Dock, input.Techs, input.Budget, Target: input.Target,
            Plan: plan, Attributes: input.Attributes);
        var ordinary = BotManager.PickTechLoadout(shop, Showcase.HullRole.Bomber, input.Target, preferTurretBomber: true);
        Assert.NotNull(ordinary);
        Assert.StartsWith("civ-strategy duty=Strike", ordinary.Reason);
        var finale = BotManager.PickTechLoadout(shop, Showcase.HullRole.Bomber, input.Target, preserveFinaleFit: true);
        Assert.NotNull(finale);
        Assert.DoesNotContain("civ-strategy duty=", finale.Reason);
        var support = CivLoadoutPolicy.Candidates(input, CivLoadoutPolicy.Duty.Support);
        Assert.NotEmpty(support);
        foreach (var p in support[0].Loadout.Parts)
            if (TechLoadoutSelector.Part(tree.Core, p.PartId).Type is DataPartTypeIGC.Weapon w)
                Assert.True(tree.Core.ProjectileTypes[w.ProjectileTypeId].Power < 0);
    }
    [SkippableTheory, Trait("Tier", "Live")]
    [InlineData(4)] [InlineData(25)]
    public void Routine_coverage_uses_real_fitting_hardware_with_a_joint_money_and_slot_budget(short civ)
    {
        var (tree, _, plan) = P1001CivCommanderTests.For(civ); var state = new ResearchState(tree);
        foreach (var step in plan.Steps) state.Apply(step);
        var dock = state.Stations.Select(id => SuccessorUpgrade.StationSuccessor(tree.Core,
            tree.Core.StationTypes[id], state.DevelopmentTechs).StationTypeId).First(id =>
                TechLoadoutSelector.Select(tree.Core, civ, Showcase.HullRole.Fighter, state.Techs, id) is not null);
        var input = new CivLoadoutPolicy.Input(tree.Core, CivProfile.Get(tree.Core, civ), [CivProfile.Get(tree.Core, 18)],
            state.Techs, GlobalAttributeSet.FromArray(tree.Civ.GasBaseAttributes), dock, 1_000_000);
        var candidates = CivLoadoutPolicy.Candidates(input, CivLoadoutPolicy.Duty.Fighter);
        var shop = new BotManager.LoadoutShop(civ, dock, state.Techs, 1_000_000, Side: 0, Plan: plan,
            Epoch: "routines", FleetSize: 30, FriendlyTurret: true, RemoteAnchor: true, Conquest: true);
        CivCoverage.Reset(); var saved = Console.Out; using var log = new StringWriter(); Console.SetOut(log);
        var duties = new HashSet<string>();
        try
        {
            var investment = candidates.MaxBy(c => c.Loadout.Price)!.Loadout;
            for (short pilot = -100; pilot > -100 - shop.FleetSize; pilot--)
                CivCoverage.ObserveAccepted(shop.Side, shop.Epoch, pilot, investment.HullId, investment.Parts, true);
            output.WriteLine($"initial fleet grants={shop.FleetSize} strategic footprint={investment.Price * shop.FleetSize}");
            for (int i = 0; i < 140; i++)
            {
                var fit = CivCoverage.Select(shop, .15, candidates, Showcase.HullRole.Fighter);
                Assert.True(TechLoadoutSelector.Legal(tree.Core, civ, shop.Techs, dock, fit.HullId, fit.Parts,
                    boarding: fit.CoverageDuty == "boarding"));
                CivCoverage.ObserveAccepted(shop.Side, shop.Epoch, shop.Pilot, fit.HullId, fit.Parts, true);
                if (fit.CoverageDuty is { } duty)
                {
                    duties.Add(duty);
                    if (duty == "repair") Assert.Contains(fit.Parts, p => TechLoadoutSelector.Part(tree.Core, p.PartId).Type is DataPartTypeIGC.Weapon w
                        && tree.Core.ProjectileTypes[w.ProjectileTypeId].Power < 0);
                    if (duty == "mines") Assert.Contains(fit.Parts, p => Showcase.IsMineLauncher(tree.Core, p.PartId));
                    if (duty == "turret") Assert.Contains(fit.Parts, p => p.MountId >= tree.Core.HullTypes[fit.HullId].MaxFixedWeapons
                        && TechLoadoutSelector.Part(tree.Core, p.PartId).EquipmentType == EquipmentType.Weapon);
                }
            }
        }
        finally { Console.SetOut(saved); }
        Assert.Contains("repair", duties); Assert.Contains("ripcord", duties); Assert.Contains("chaff", duties);
        if (TechLoadoutSelector.PickRescueProbeLauncher(tree.Core, shop.Techs) is not null) Assert.Contains("rescue", duties);
        else Assert.DoesNotContain("rescue", duties);
        Assert.Equal(0, CivCoverage.CrewSlots(6, .15)); Assert.Equal(2, CivCoverage.CrewSlots(15, .15));
        output.WriteLine("reachable selected routines: " + string.Join(',', duties.Order()));
    }

}
