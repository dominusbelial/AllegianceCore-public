using System.Text;

namespace Allegiance.Core.Model;

public sealed record TechIdentity(string Kind, short Id, string Name, string Role);
public sealed record ResearchNode(string Kind, short Id, string Name, byte[] Required, byte[] Effects,
    int Price, uint Seconds, float[] Attributes, byte[] Local, short SuccessorId = -1,
    short AsteroidMask = 0, short ConstructorId = -1, byte? PilotType = null);

/// <summary>Loaded-core queries. Purchase math: Igc/stationIGC.h:383-391; sideigc.h:237-280.</summary>
public sealed class ResearchTree
{
    public StaticCore Core { get; }
    public DataCivilizationIGC Civ { get; }
    public ResearchSettings Settings { get; }
    public IReadOnlyList<ResearchNode> Nodes { get; }
    public IReadOnlyList<DataPartTypeIGC> Parts { get; }
    public ResearchTree(StaticCore core, short civId, ResearchSettings? settings = null)
    {
        Core = core;
        Civ = core.Civilizations.TryGetValue(civId, out var civ) ? civ
            : throw new InvalidDataException($"civilization {civId} absent from core");
        Settings = settings ?? new();
        Parts = core.PartTypes.Values.Concat(core.Launchers.Keys.Select(id =>
            core.TryBuildExpendableLauncher(id, out var p) ? (DataPartTypeIGC?)p : null)
            .Where(p => p.HasValue).Select(p => p!.Value)).OrderBy(p => p.PartId).ToArray();
        var ga = GlobalAttributeSet.FromArray(Civ.GasBaseAttributes);
        // Only development buckets scale, frozen at creation: Igc/bucketigc.cpp:66-74.
        Nodes = core.Developments.Values.Where(d => d.DevelopmentId != Mission.TeamMoneyDevelopmentId)
            .Select(d => new ResearchNode("develop", d.DevelopmentId, d.Name, d.TtbmRequired, d.TtbmEffects,
                (int)(d.Price * ga.Get(GlobalAttributeSet.DevelopmentCost)),
                (uint)(d.TimeToBuild * ga.Get(GlobalAttributeSet.DevelopmentTime)), d.Gas, new byte[50]))
            .Concat(core.StationTypes.Values.Select(s => new ResearchNode("station", s.StationTypeId, s.Name,
                s.TtbmRequired, s.TtbmEffects, s.Price, s.TimeToBuild, [], s.TtbmLocal,
                s.SuccessorStationTypeId, s.AabmBuild, s.ConstructionDroneTypeId)))
            .Concat(core.DroneTypes.Values.Select(d => new ResearchNode("drone", d.DroneTypeId, d.Name,
                d.TtbmRequired, d.TtbmEffects, d.Price, d.TimeToBuild, [], new byte[50], PilotType: d.PilotType)))
            .OrderBy(n => n.Kind).ThenBy(n => n.Id).ToArray();
    }

    /// <summary>Own-economy closure, retaining redundant alternatives before they become obsolete.
    /// Local masks are tested per station, never merged into a fictitious super-station.</summary>
    public IReadOnlyList<ResearchNode> ReachableNodes()
    {
        var dev = Settings.StartingTechs(Civ);
        var starting = dev;
        var stations = new HashSet<short> { Civ.InitialStationTypeId };
        var reached = new HashSet<ResearchNode>();
        if (!Settings.AllowDevelopments) return Nodes.Where(n => n.Kind == "station" && stations.Contains(n.Id)).ToArray();
        while (true)
        {
            var techs = stations.Aggregate(dev, (t, id) => t.Or(TechTreeBitMask.FromBytes(Core.StationTypes[id].TtbmEffects)));
            var available = Nodes.Where(n => !reached.Contains(n) && (n.Kind == "develop"
                ? !DevelopmentLogic.IsObsolete(Core.Developments[n.Id], starting)
                    && TechTreeBitMask.FromBytes(n.Required).IsSubsetOf(techs)
                : stations.Any(id => TechTreeBitMask.RequiredTechsBuyable(n.Required, Core.StationTypes[id].TtbmLocal, techs))))
                .ToArray();
            if (available.Length == 0) break;
            foreach (var n in available)
            {
                reached.Add(n);
                if (n.Kind == "develop") dev = dev.Or(TechTreeBitMask.FromBytes(n.Effects));
                if (n.Kind == "station" && n.AsteroidMask != 0 && Core.DroneTypes.ContainsKey(n.ConstructorId)) stations.Add(n.Id);
            }
        }
        return Nodes.Where(reached.Contains).ToArray();
    }

    /// <summary>Direct purchase dependencies; prerequisites still apply. Actual buyability is Unlocks.</summary>
    public IReadOnlyList<TechIdentity> PurchaseDependents(ResearchNode node)
    {
        bool Uses(byte[] required) => Enumerable.Range(0, 50).Any(i => (required[i] & (node.Effects[i] | node.Local[i])) != 0);
        return Core.HullTypes.Values.Where(h => Uses(h.TtbmRequired))
            .Select(h => new TechIdentity("hull", h.HullId, h.Name, $"capabilities=0x{(ushort)h.HabmCapabilities:X}; group={h.GroupId}"))
            .Concat(Parts.Where(p => Uses(p.TtbmRequired)).Select(p => new TechIdentity("part", p.PartId, p.Name, p.EquipmentType.ToString())))
            .OrderBy(i => i.Kind).ThenBy(i => i.Id).ToArray();
    }

    public ResearchNode Find(string kind, short id) => Nodes.SingleOrDefault(n => n.Kind == kind && n.Id == id)
        ?? throw new InvalidDataException($"unknown {kind} id {id}");
    public IReadOnlyList<TechIdentity> Unlocks(TechTreeBitMask techs, IEnumerable<short> stations)
    {
        var local = stations.Distinct().Select(id => Core.StationTypes[id].TtbmLocal).ToArray();
        bool Buy(byte[] need) => local.Any(l => TechTreeBitMask.RequiredTechsBuyable(need, l, techs));
        return Core.HullTypes.Values.Where(h => Buy(h.TtbmRequired))
            .Select(h => new TechIdentity("hull", h.HullId, h.Name, $"capabilities=0x{(ushort)h.HabmCapabilities:X}; group={h.GroupId}"))
            .Concat(Parts.Where(p => Buy(p.TtbmRequired)).Select(p =>
                new TechIdentity("part", p.PartId, p.Name, p.EquipmentType.ToString())))
            .OrderBy(i => i.Kind).ThenBy(i => i.Id).ToArray();
    }

    public ResearchPlan Generate(string coreName, string name = "generated", bool expensiveFirst = false)
    {
        var state = new ResearchState(this);
        var steps = new List<ResearchStep>();
        var pending = Nodes.ToList();
        while (true)
        {
            var eligible = pending.Where(state.CanApply);
            var node = (expensiveFirst ? eligible.OrderByDescending(n => n.Price) : eligible.OrderBy(n => n.Price))
                .ThenBy(n => n.Kind).ThenBy(n => n.Id).FirstOrDefault();
            if (node is null) break;
            var before = state.Unlocks;
            var step = new ResearchStep { Action = node.Kind, Id = node.Id, Name = node.Name,
                AsteroidClass = node.Kind == "station" ? (short)(node.AsteroidMask & -node.AsteroidMask) : null };
            state.Apply(step);
            step.ExpectedUnlocks = state.Unlocks.Except(before).ToList();
            steps.Add(step);
            pending.Remove(node);
        }
        return new ResearchPlan { Core = coreName, CoreVersion = Core.Version, CivId = Civ.CivilizationId,
            Name = name, Steps = steps };
    }

    public string Markdown(string coreName)
    {
        var plan = Generate(coreName);
        var state = new ResearchState(this);
        var sb = new StringBuilder($"# {coreName} — civ {Civ.CivilizationId}: {Civ.Name}\n\n");
        sb.AppendLine($"Core version: {Core.Version}. Starting bits: {Bits(state.DevelopmentTechs.ToBytes())}.");
        var home = Core.StationTypes[Civ.InitialStationTypeId];
        sb.AppendLine($"Initial station {home.StationTypeId}: {home.Name}; required [{Bits(home.TtbmRequired)}]; effects [{Bits(home.TtbmEffects)}]; local [{Bits(home.TtbmLocal)}]; successor {home.SuccessorStationTypeId}; price {home.Price}; time {home.TimeToBuild}s.");
        sb.AppendLine("\nReachability assumes unlimited money and suitable unoccupied rocks; no capture. Each row is a valid completion order. Drone constructors are exercised by station steps. Prices/time are frozen bucket costs; only developments receive civ scaling. Unlocks use side + individual station local techs. Hull roles show core capability flags/group; parts show equipment role. IDs are core IDs.\n");
        sb.AppendLine("## Starting purchases\n");
        foreach (var u in state.Unlocks) sb.AppendLine($"- {u.Kind} {u.Id} {u.Name} ({u.Role})");
        sb.AppendLine("\n## Reachable completions\n");
        foreach (var step in plan.Steps)
        {
            var n = Find(step.Action, step.Id);
            state.Apply(step);
            sb.AppendLine($"### {n.Kind} {n.Id}: {n.Name}\n");
            sb.AppendLine($"Required bits: {Bits(n.Required)}; effects: {Bits(n.Effects)}; local: {Bits(n.Local)}. Price: {n.Price}; time: {n.Seconds}s. Successor: {n.SuccessorId}; asteroid mask: 0x{(ushort)n.AsteroidMask:X}; constructor: {n.ConstructorId}.");
            sb.AppendLine($"Attributes (GA index × factor): {string.Join(", ", n.Attributes.Select((v, i) => (v, i)).Where(x => x.v != 1).Select(x => $"{x.i} × {x.v.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))}.");
            sb.AppendLine("New purchases at this completion:");
            foreach (var u in step.ExpectedUnlocks) sb.AppendLine($"- {u.Kind} {u.Id} {u.Name} ({u.Role})");
            if (step.ExpectedUnlocks.Count == 0) sb.AppendLine("- None (attribute, construction, drone or prerequisite effect).");
            sb.AppendLine();
        }
        sb.AppendLine("## Reachable purchases\n");
        foreach (var u in state.Unlocks) sb.AppendLine($"- {u.Kind} {u.Id} {u.Name} ({u.Role})");
        sb.AppendLine("\n## Reachability and direct purchase dependencies\n\nThis closure retains redundant alternatives as well as the selected order. Constructor drone types are purchased through their station buckets. A dependency means the purchase uses a granted/local bit; its remaining prerequisites still apply. Use the completion rows for actual newly buyable purchases.\n");
        foreach (var n in ReachableNodes())
        {
            sb.AppendLine($"- {n.Kind} {n.Id} {n.Name}: {string.Join("; ", PurchaseDependents(n).Select(u => $"{u.Kind} {u.Id} {u.Name} ({u.Role})"))}");
        }
        sb.AppendLine("\n## Other core nodes\n\nThese are outside the selected completion order: they may be unreachable, initially known, constructors exercised through stations, or redundant/obsolete alternatives. Native bucket eligibility additionally assumes capture (Igc/sideigc.cpp:123-206).\n");
        var used = plan.Steps.Select(s => (s.Action, s.Id)).ToHashSet();
        foreach (var n in Nodes.Where(n => !used.Contains((n.Kind, n.Id))))
            sb.AppendLine($"- {n.Kind} {n.Id} {n.Name}: required [{Bits(n.Required)}], effects [{Bits(n.Effects)}], local [{Bits(n.Local)}], {n.Price} / {n.Seconds}s; successor {n.SuccessorId}; constructor {n.ConstructorId}; rock mask 0x{(ushort)n.AsteroidMask:X}; attributes [{string.Join(", ", n.Attributes.Select((v, i) => (v, i)).Where(x => x.v != 1).Select(x => $"{x.i} × {x.v.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))}].");
        return sb.ToString();
    }
    public static string Bits(byte[] mask) => string.Join(",", Enumerable.Range(0, 400)
        .Where(i => (mask[i / 8] & (0x80 >> (i % 8))) != 0));
}

/// <summary>Pure prerequisite state for P703. No money is spent and no ships are spawned.</summary>
public sealed class ResearchState
{
    public ResearchTree Tree { get; }
    public TechTreeBitMask DevelopmentTechs { get; private set; }
    public HashSet<short> Stations { get; } = new();
    private readonly HashSet<(string, short)> _completed = new();
    public TechTreeBitMask Techs => Stations.Aggregate(DevelopmentTechs,
        (t, id) => t.Or(TechTreeBitMask.FromBytes(Tree.Core.StationTypes[id].TtbmEffects)));
    public IReadOnlyList<TechIdentity> Unlocks => Tree.Unlocks(Techs, Stations);
    public ResearchState(ResearchTree tree)
    {
        Tree = tree;
        DevelopmentTechs = tree.Settings.StartingTechs(tree.Civ);
        Stations.Add(tree.Civ.InitialStationTypeId);
    }
    public bool CanApply(ResearchNode n)
    {
        if (!Tree.Settings.AllowDevelopments || _completed.Contains((n.Kind, n.Id))) return false;
        if (n.Kind == "develop")
        {
            var dev = Tree.Core.Developments[n.Id];
            return !DevelopmentLogic.IsObsolete(dev, DevelopmentTechs)
                && TechTreeBitMask.FromBytes(n.Required).IsSubsetOf(Techs);
        }
        if (n.Kind == "drone" && n.PilotType == Sim.Ship.PilotTypeBuilder) return false;
        if (n.Kind == "station" && (Stations.Contains(n.Id) || n.AsteroidMask == 0
            || !Tree.Core.DroneTypes.ContainsKey(n.ConstructorId)
            || SuccessorUpgrade.StationSuccessor(Tree.Core, Tree.Core.StationTypes[n.Id], DevelopmentTechs).StationTypeId != n.Id)) return false;
        return Stations.Any(id => TechTreeBitMask.RequiredTechsBuyable(n.Required,
            Tree.Core.StationTypes[id].TtbmLocal, Techs));
    }
    public void Apply(ResearchStep step)
    {
        if (step.Action == "wait")
        {
            if (!double.IsFinite(step.WaitSeconds) || step.WaitSeconds < 0) throw new InvalidDataException("invalid wait duration");
            if (!TechTreeBitMask.FromBytes(step.RequiredTechs).IsSubsetOf(Techs)) throw new InvalidDataException("wait tech condition unsatisfied");
            return;
        }
        var n = Tree.Find(step.Action, step.Id);
        if (n.Name != step.Name) throw new InvalidDataException($"{n.Kind} {n.Id} name changed: expected '{step.Name}', loaded '{n.Name}'");
        if (!CanApply(n)) throw new InvalidDataException($"{n.Kind} {n.Id} '{n.Name}' prerequisites unsatisfied or obsolete");
        if (n.Kind == "station" && (step.AsteroidClass is not { } rock || rock <= 0
            || (rock & (rock - 1)) != 0 || (rock & n.AsteroidMask) == 0))
            throw new InvalidDataException($"station {n.Id} requires a single asteroid class in mask {n.AsteroidMask}");
        if (n.Kind == "develop") DevelopmentTechs = DevelopmentTechs.Or(TechTreeBitMask.FromBytes(n.Effects));
        if (n.Kind == "station") Stations.Add(n.Id);
        _completed.Add((n.Kind, n.Id));
        var unlocks = Unlocks;
        foreach (var expected in step.ExpectedUnlocks)
            if (!unlocks.Contains(expected)) throw new InvalidDataException($"expected unlock missing: {expected}");
    }
}
