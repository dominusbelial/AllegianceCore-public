using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

public sealed class SC3CloakPolicyTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Native_mount_energy_flight_and_action_gates_preserve_movement()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var pairs = (from h in core.HullTypes.Values
                     where Showcase.ClassifyHull(h) is Showcase.HullRole.Scout or Showcase.HullRole.Bomber
                     from p in core.PartTypes.Values
                     where p.Type is DataPartTypeIGC.Cloak { MaxCloaking: > 0 } && h.CanMountPart(p, 0)
                     select (Hull: h, Part: p)).ToArray();
        Assert.Contains(pairs, p => Showcase.ClassifyHull(p.Hull) == Showcase.HullRole.Bomber);
        Assert.Contains(pairs, p => Showcase.ClassifyHull(p.Hull) == Showcase.HullRole.Scout);
        foreach (var (h, p) in pairs)
        {
            var parts = new[] { new ExpandedPartData(p.PartId, 1, 0, ExpandedPartData.FullyMounted) };
            int Run(float energy = 1, bool flying = true, bool ripcord = false, bool approach = true,
                bool held = false, int state = ShipControlState.LeftButton, IReadOnlyList<ExpandedPartData>? inventory = null,
                short? hull = null) => CloakPolicy.Apply(core, hull ?? h.HullId, inventory ?? parts,
                    energy, flying, ripcord, approach, held, state);
            if (h.MaxEnergy <= Math.Max(h.MaxEnergy * .2f, ((DataPartTypeIGC.Cloak)p.Type).EnergyConsumption * 4)) continue;
            Assert.Equal(ShipControlState.LeftButton | ShipControlState.CloakActive, Run());
            Assert.Equal(ShipControlState.LeftButton, Run(energy: 0));
            Assert.Equal(ShipControlState.LeftButton, Run(energy: float.NaN));
            Assert.Equal(ShipControlState.LeftButton, Run(flying: false));
            Assert.Equal(ShipControlState.LeftButton, Run(ripcord: true));
            Assert.Equal(ShipControlState.LeftButton, Run(approach: false));
            Assert.Equal(ShipControlState.LeftButton, Run(inventory: []));
            Assert.Equal(ShipControlState.LeftButton, Run(inventory: [parts[0] with { MountId = 255 }]));
            Assert.Equal(ShipControlState.LeftButton, Run(inventory: [parts[0] with { FractionMounted = 239 }]));
            Assert.Equal(ShipControlState.LeftButton, Run(inventory: [parts[0] with { Amount = 0 }]));
            Assert.Equal(ShipControlState.LeftButton, Run(hull: -1));
            var incompatible = core.HullTypes.Values.FirstOrDefault(other =>
                Showcase.ClassifyHull(other) is Showcase.HullRole.Scout or Showcase.HullRole.Bomber
                && !other.CanMountPart(p, 0));
            if (incompatible.Name is not null)
                Assert.Equal(ShipControlState.LeftButton, Run(hull: incompatible.HullId));
            var fighter = core.HullTypes.Values.First(other => Showcase.ClassifyHull(other) == Showcase.HullRole.Fighter);
            Assert.Equal(ShipControlState.LeftButton, Run(hull: fighter.HullId));
            foreach (int action in new[] { ShipControlState.AllWeapons, ShipControlState.OneWeapon,
                         ShipControlState.MissileFire, ShipControlState.MineFire, ShipControlState.AfterburnerButton })
                Assert.Equal(action | ShipControlState.LeftButton, Run(held: true,
                    state: action | ShipControlState.LeftButton | ShipControlState.CloakActive));
            float middle = Math.Max(.1f, ((DataPartTypeIGC.Cloak)p.Type).EnergyConsumption * 2 / h.MaxEnergy);
            if (middle < .2f)
            {
                Assert.Equal(ShipControlState.LeftButton, Run(energy: middle));
                Assert.Equal(ShipControlState.LeftButton | ShipControlState.CloakActive, Run(energy: middle, held: true));
            }
        }
    }

    [SkippableFact]
    public void Research_loadouts_offer_real_legal_cloaks_without_unlock_bypass()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        int scouts = 0, bombers = 0;
        foreach (short civ in new short[] { 1, 35 })
        {
            var tree = new ResearchTree(core, civ);
            var state = new ResearchState(tree);
            foreach (var step in tree.Generate("PCore001").Steps) state.Apply(step);
            foreach (var dock in state.Stations)
            foreach (var role in new[] { Showcase.HullRole.Scout, Showcase.HullRole.Bomber })
            {
                var selected = TechLoadoutSelector.Select(core, civ, role, state.Techs, dock);
                if (selected is null || !selected.Parts.Any(p => TechLoadoutSelector.Part(core, p.PartId).Type is DataPartTypeIGC.Cloak)) continue;
                Assert.True(TechLoadoutSelector.Legal(core, civ, state.Techs, dock, selected.HullId, selected.Parts));
                if (role == Showcase.HullRole.Scout) scouts++; else bombers++;
                output.WriteLine($"civ={civ} dock={dock}: {selected.Reason}");
            }
        }
        Assert.True(scouts > 0 && bombers > 0);
    }
}
