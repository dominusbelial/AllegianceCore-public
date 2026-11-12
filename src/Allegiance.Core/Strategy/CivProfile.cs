using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Allegiance.Core.Model;

namespace Allegiance.Core.Strategy;

public readonly record struct StrategyNode(string Kind, short Id);
public sealed record ProfileSettings(bool Developments, bool Shipyard, bool Expansion, bool Tactical,
    bool Supremacy, int Miners)
{
    public static ProfileSettings From(ResearchSettings s) => new(s.AllowDevelopments, s.AllowShipyardPath,
        s.AllowExpansionPath, s.AllowTacticalPath, s.AllowSupremacyPath, s.InitialMinersPerTeam);
    public ResearchSettings Research() => new() { AllowDevelopments = Developments, AllowShipyardPath = Shipyard,
        AllowExpansionPath = Expansion, AllowTacticalPath = Tactical, AllowSupremacyPath = Supremacy, InitialMinersPerTeam = Miners };
}
public sealed record Prerequisite(StrategyNode Key, string Name, ImmutableArray<byte> Required,
    ImmutableArray<byte> Effects, ImmutableArray<byte> Local, long Price, double Seconds,
    ImmutableArray<float> Attributes, short Successor, short AsteroidMask, short Constructor,
    ImmutableArray<StrategyNode> Providers, bool Reachable);
public sealed record ProfileRoute(StrategyNode Target, ImmutableArray<StrategyNode> Steps,
    long Funding, double CriticalSeconds, double SerialSeconds, ImmutableArray<float> Attributes);
public sealed record HullProfile(short Id, string Name, ImmutableArray<byte> Required, short Successor,
    int Price, float Mass, float Speed, float Thrust, ImmutableArray<float> TurnRates, float Armor,
    byte Defense, float Energy, float Recharge, short Ammo, short Magazine, short Dispenser, short Chaff,
    int CargoSlots, int FixedGuns, int Crew, float Scan, float Signature, short Capabilities,
    ImmutableArray<short> EquipmentMasks, ImmutableArray<MountProfile> Mounts, JsonElement Specs);
public sealed record MountProfile(int Index, bool Fixed, short Mask, ImmutableArray<short> Parts);
public sealed record PartProfile(short Id, string Name, EquipmentType Equipment, ImmutableArray<byte> Required,
    short Successor, int Price, float Mass, short Mask, JsonElement Specs,
    ImmutableArray<double> DamageAgainstDefense, ImmutableArray<double> BlastDamageAgainstDefense);
public sealed record StationProfile(short Id, string Name, ImmutableArray<byte> Required,
    ImmutableArray<byte> Effects, ImmutableArray<byte> Local, byte ArmorDefense, byte ShieldDefense,
    float Income, short Constructor, short Successor, JsonElement Specs);
public sealed record EconomyProfile(double PaydayFactor, double StartingMoneyAddition, double PaydayInterval,
    double BasePayday, double OreCapacity, double OreValue, double MiningRate, double MiningYield,
    double MiningCapacity, double DevelopmentCost, double DevelopmentTime,
    ImmutableArray<short> Miners, ImmutableArray<short> Constructors);

/// <summary>Immutable, core-derived input shared by bot and commander policy. The graph retains
/// every provider alternative; Routes are exact-replayed candidate witnesses, not all permutations.
/// No live mission, enemy observations, mutable native arrays or random generator is retained.</summary>
public sealed class CivProfile
{
    public string CoreIdentity { get; }
    public int CoreVersion { get; }
    public short Civ { get; }
    public string Name { get; }
    public ProfileSettings Settings { get; }
    public short StartingStation { get; }
    public ImmutableArray<byte> BaseTechs { get; }
    public ImmutableArray<byte> NoDevelopmentTechs { get; }
    public ImmutableArray<byte> StartingTechs { get; }
    public ImmutableArray<float> Attributes { get; }
    public ImmutableArray<TechIdentity> StartingPurchases { get; }
    public ImmutableArray<Prerequisite> Prerequisites { get; }
    public ImmutableArray<ProfileRoute> Routes { get; }
    public ImmutableArray<HullProfile> Hulls { get; }
    public ImmutableArray<PartProfile> Parts { get; }
    public ImmutableArray<StationProfile> Stations { get; }
    public ImmutableArray<JsonElement> Projectiles { get; }
    public ImmutableArray<JsonElement> Missiles { get; }
    public ImmutableArray<JsonElement> Drones { get; }
    public ImmutableArray<ImmutableArray<float>> DamageMatrix { get; }
    public EconomyProfile Economy { get; }

    private sealed class CoreProfiles
    {
        internal readonly string Identity;
        internal readonly ConcurrentDictionary<(ProfileSettings, short), Lazy<CivProfile>> Profiles = new();
        internal CoreProfiles(StaticCore core) => Identity = Fingerprint(core);
    }
    private static readonly ConditionalWeakTable<StaticCore, CoreProfiles> Cache = new();
    public static CivProfile Get(StaticCore core, short civ, ResearchSettings? settings = null)
    {
        var key = ProfileSettings.From(settings ?? new());
        key.Research().Validate();
        var cache = Cache.GetValue(core, static c => new(c));
        return cache.Profiles.GetOrAdd((key, civ), k => new(() => new CivProfile(core, cache.Identity, k.Item2, k.Item1))).Value;
    }
    // Canonical ordering makes identity independent of dictionary insertion / record order.
    // Runtime subtype serialization includes weapon/shield/etc tails, even with the same core version.
    private static string Fingerprint(StaticCore c)
    {
        var data = new CoreProfileIdentity(c.Version, c.Constants,
            c.Civilizations.OrderBy(p => p.Key).Select(p => p.Value).ToArray(), c.HullTypes.OrderBy(p => p.Key).Select(p => p.Value).ToArray(),
            c.PartTypes.OrderBy(p => p.Key).Select(p => new IdentityPart(p.Value, Tail(p.Value))).ToArray(),
            c.Launchers.OrderBy(p => p.Key).Select(p => p.Value).ToArray(), c.StationTypes.OrderBy(p => p.Key).Select(p => p.Value).ToArray(),
            c.Developments.OrderBy(p => p.Key).Select(p => p.Value).ToArray(), c.DroneTypes.OrderBy(p => p.Key).Select(p => p.Value).ToArray(),
            c.ProjectileTypes.OrderBy(p => p.Key).Select(p => p.Value).ToArray(), c.MissileTypes.OrderBy(p => p.Key).Select(p => p.Value).ToArray(),
            c.MineTypes.OrderBy(p => p.Key).Select(p => p.Value).ToArray(), c.ProbeTypes.OrderBy(p => p.Key).Select(p => p.Value).ToArray(),
            c.ChaffTypes.OrderBy(p => p.Key).Select(p => p.Value).ToArray(),
            c.TreasureSets.OrderBy(p => p.Key).Select(p => p.Value).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, StrategyJsonContext.Default.CoreProfileIdentity);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
    private CivProfile(StaticCore core, string identity, short civ, ProfileSettings settings)
    {
        var tree = new ResearchTree(core, civ, settings.Research());
        var c = tree.Civ;
        CoreIdentity = identity; CoreVersion = core.Version; Civ = civ; Name = c.Name; Settings = settings;
        StartingStation = c.InitialStationTypeId;
        BaseTechs = [.. c.TtbmBaseTechs]; NoDevelopmentTechs = [.. c.TtbmNoDevTechs];
        StartingTechs = [.. tree.Settings.StartingTechs(c).ToBytes()]; Attributes = [.. c.GasBaseAttributes];
        var starting = new ResearchState(tree);
        StartingPurchases = [.. starting.Unlocks];
        bool InitialBuy(byte[] required) => TechTreeBitMask.RequiredTechsBuyable(required,
            core.StationTypes[c.InitialStationTypeId].TtbmLocal, starting.Techs);
        var reachable = tree.ReachableNodes().Select(Key).ToHashSet();
        Prerequisites = [.. tree.Nodes.Select(n => new Prerequisite(Key(n), n.Name, [.. n.Required], [.. n.Effects],
            [.. n.Local], n.Price, n.Seconds, [.. n.Attributes], n.SuccessorId, n.AsteroidMask, n.ConstructorId,
            [.. tree.Nodes.Where(p => Enumerable.Range(0, 50).Any(b => (n.Required[b] & (p.Effects[b] | p.Local[b])) != 0)).Select(Key)],
            reachable.Contains(Key(n))))];
        Routes = BuildRoutes(tree);
        Hulls = [.. core.HullTypes.Values.OrderBy(h => h.HullId).Select(h => new HullProfile(h.HullId, h.Name,
            [.. h.TtbmRequired], h.SuccessorHullId, h.Price, h.Mass, h.Speed, h.Thrust, [.. h.MaxTurnRates], h.HitPoints,
            h.DefenseType, h.MaxEnergy, h.RechargeRate, h.MaxAmmo, h.CapacityMagazine, h.CapacityDispenser,
            h.CapacityChaffLauncher, 5, h.MaxFixedWeapons, 1 + h.Hardpoints.Count(p => !p.Fixed), h.ScannerRange,
            h.Signature, h.HabmCapabilities, [.. h.PmEquipment], [.. h.Hardpoints.Select((p, i) =>
                new MountProfile(i, p.Fixed, p.PartMask, [.. tree.Parts.Where(part => h.CanMountPart(part, i)).Select(part => part.PartId)]))],
            JsonSerializer.SerializeToElement(h, StrategyJsonContext.Default.DataHullTypeIGC)))];
        Parts = [.. tree.Parts.Select(p => new PartProfile(p.PartId, p.Name, p.EquipmentType, [.. p.TtbmRequired],
            p.SuccessorPartId, p.Price, p.Mass, p.PartMask, Tail(p),
            [.. Damage(p, false)], [.. Damage(p, true)]))];
        Stations = [.. core.StationTypes.Values.OrderBy(s => s.StationTypeId).Select(s => new StationProfile(s.StationTypeId,
            s.Name, [.. s.TtbmRequired], [.. s.TtbmEffects], [.. s.TtbmLocal], s.DefenseTypeArmor,
            s.DefenseTypeShield, s.Income, s.ConstructionDroneTypeId, s.SuccessorStationTypeId,
            JsonSerializer.SerializeToElement(s, StrategyJsonContext.Default.DataStationTypeIGC)))];
        Projectiles = [.. core.ProjectileTypes.OrderBy(p => p.Key).Select(p => JsonSerializer.SerializeToElement(p.Value, StrategyJsonContext.Default.DataProjectileTypeIGC))];
        Missiles = [.. core.MissileTypes.OrderBy(p => p.Key).Select(p => JsonSerializer.SerializeToElement(p.Value, StrategyJsonContext.Default.DataMissileTypeIGC))];
        Drones = [.. core.DroneTypes.OrderBy(p => p.Key).Select(p => JsonSerializer.SerializeToElement(p.Value, StrategyJsonContext.Default.DataDroneTypeIGC))];
        DamageMatrix = [.. core.Constants.DamageConstants.Select(row => row.ToImmutableArray())];
        Economy = new(1 + c.IncomeMoney, c.BonusMoney, core.Constants.FloatConstants[6], core.Constants.FloatConstants[30],
            core.Constants.FloatConstants[7], core.Constants.FloatConstants[8], Attributes[GlobalAttributeSet.MiningRate],
            Attributes[GlobalAttributeSet.MiningYield], Attributes[GlobalAttributeSet.MiningCapacity],
            Attributes[GlobalAttributeSet.DevelopmentCost], Attributes[GlobalAttributeSet.DevelopmentTime],
            [.. core.DroneTypes.Values.Where(d => d.PilotType == Sim.Ship.PilotTypeMiner && (InitialBuy(d.TtbmRequired) || reachable.Contains(new("drone", d.DroneTypeId)))).OrderBy(d => d.DroneTypeId).Select(d => d.DroneTypeId)],
            [.. core.DroneTypes.Values.Where(d => d.PilotType == Sim.Ship.PilotTypeBuilder && (InitialBuy(d.TtbmRequired) || tree.Nodes.Any(n => n.Kind == "station" && n.ConstructorId == d.DroneTypeId && reachable.Contains(Key(n))))).OrderBy(d => d.DroneTypeId).Select(d => d.DroneTypeId)]);
        IEnumerable<double> Damage(DataPartTypeIGC p, bool blast)
        {
            byte damage; float power;
            if (p.Type is DataPartTypeIGC.Weapon w && core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var projectile))
            { damage = projectile.DamageType; power = blast ? projectile.BlastPower : projectile.Power; }
            else if (p.Type is DataPartTypeIGC.MissileLauncher m && core.MissileTypes.TryGetValue(m.ExpendableTypeId, out var missile))
            { damage = missile.DamageType; power = blast ? missile.BlastPower : missile.Power; }
            else return Enumerable.Repeat(0.0, DataConstantsIGC.DefidMax);
            int row = damage & Sim.BlastDamage.Mask; // Igc/missionigc.h:149-155
            return row < DataConstantsIGC.DmgidMax ? core.Constants.DamageConstants[row].Select(f => (double)power * f)
                : Enumerable.Repeat((double)power, DataConstantsIGC.DefidMax);
        }
    }
    private static JsonElement Tail(DataPartTypeIGC p) => JsonSerializer.SerializeToElement(p.Type,
        StrategyJsonContext.Default.GetTypeInfo(p.Type.GetType()) ?? throw new InvalidDataException("unknown part subtype"));
    internal static StrategyNode Key(ResearchNode n) => new(n.Kind, n.Id);
    internal static ResearchStep Step(ResearchTree tree, StrategyNode key)
    {
        var n = tree.Find(key.Kind, key.Id);
        return new() { Action = key.Kind, Id = key.Id, Name = n.Name,
            AsteroidClass = n.Kind == "station" ? (short)(n.AsteroidMask & -n.AsteroidMask) : null };
    }
    internal static ResearchState Replay(ResearchTree tree, IEnumerable<StrategyNode> steps)
    {
        var state = new ResearchState(tree);
        foreach (var key in steps) state.Apply(Step(tree, key));
        return state;
    }
    private static ImmutableArray<ProfileRoute> BuildRoutes(ResearchTree tree)
    {
        var result = new Dictionary<string, ProfileRoute>(StringComparer.Ordinal);
        var possible = tree.ReachableNodes().Where(n => n.Kind != "drone" || n.PilotType != Sim.Ship.PilotTypeBuilder).ToArray();
        var initial = tree.Settings.StartingTechs(tree.Civ).Or(TechTreeBitMask.FromBytes(tree.Core.StationTypes[tree.Civ.InitialStationTypeId].TtbmEffects)).ToBytes();
        // Search each target directly, including each alternative provider of its missing
        // bits. No single exhaustive generated schedule limits the opportunities. The
        // AND/OR graph retains recursive alternatives compactly rather than enumerating
        // exponentially many equivalent interleavings of independent developments.
        foreach (var target in possible)
        {
            Add(FindRoute(target, null), target);
            foreach (var provider in possible.Where(p => p != target && Enumerable.Range(0, 50)
                .Any(b => (target.Required[b] & ~initial[b] & (p.Effects[b] | p.Local[b])) != 0)))
                Add(FindRoute(target, provider), target);
        }
        return [.. result.Values.OrderBy(r => r.Target.Kind, StringComparer.Ordinal).ThenBy(r => r.Target.Id)
            .ThenBy(r => r.Funding).ThenBy(r => r.CriticalSeconds)];

        List<StrategyNode>? FindRoute(ResearchNode target, ResearchNode? first)
        {
            var steps = new List<StrategyNode>(); var state = new ResearchState(tree);
            var active = new HashSet<StrategyNode>();
            if (first is not null && (!Complete(first) || steps.Contains(Key(target)))) return null;
            if (!Complete(target)) return null;
            // Remove incidental providers. Exact replay preserves station-local and
            // successor conditions; the final target is never removed.
            for (int i = steps.Count - 2; i >= 0; i--)
            {
                var reduced = steps.Where((_, index) => index != i).ToList();
                try { Replay(tree, reduced); steps = reduced; }
                catch (InvalidDataException) { }
            }
            return steps;

            bool Complete(ResearchNode node)
            {
                var key = Key(node);
                if (steps.Contains(key)) return true;
                if (!active.Add(key)) return false;
                try { return Satisfy(); }
                finally { active.Remove(key); }

                bool Satisfy()
                {
                    if (state.CanApply(node))
                    { state.Apply(Step(tree, key)); steps.Add(key); return true; }
                    // Structural exclusions cannot be repaired by adding unrelated techs.
                    if (!tree.Settings.AllowDevelopments || node.Kind == "station" && (node.AsteroidMask == 0
                        || !tree.Core.DroneTypes.ContainsKey(node.ConstructorId) || state.Stations.Contains(node.Id))) return false;
                    var techs = state.Techs.ToBytes();
                    var missing = node.Kind == "develop" ? Missing(techs, new byte[50]) : state.Stations
                        .Select(id => Missing(techs, tree.Core.StationTypes[id].TtbmLocal)).OrderBy(m => m.Length).First();
                    if (missing.Length == 0) return false; // obsolete/successor, not a missing prerequisite
                    // The most constrained missing bit avoids exploring irrelevant providers.
                    var choices = missing.Select(bit => possible.Where(p => !active.Contains(Key(p)) && !steps.Contains(Key(p))
                        && ((p.Effects[bit / 8] | (p.Kind == "station" ? p.Local[bit / 8] : 0)) & (0x80 >> (bit % 8))) != 0).ToArray())
                        .OrderBy(providers => providers.Length).First();
                    foreach (var provider in choices.OrderBy(p => p.Price + p.Seconds * 20).ThenBy(p => p.Kind, StringComparer.Ordinal).ThenBy(p => p.Id))
                    {
                        int checkpoint = steps.Count;
                        if (Complete(provider) && Satisfy()) return true;
                        steps.RemoveRange(checkpoint, steps.Count - checkpoint);
                        state = Replay(tree, steps);
                    }
                    return false;
                }
                int[] Missing(byte[] global, byte[] local) => Enumerable.Range(0, 400).Where(bit =>
                    (node.Required[bit / 8] & ~(global[bit / 8] | local[bit / 8]) & (0x80 >> (bit % 8))) != 0).ToArray();
            }
        }
        void Add(List<StrategyNode>? route, ResearchNode target)
        {
            if (route is null || route.Count == 0) return;
            string signature = string.Join(',', route);
            if (result.ContainsKey(signature)) return;
            var ga = (float[])tree.Civ.GasBaseAttributes.Clone();
            var ends = new List<(ResearchNode Node, double End)>(); long cost = 0; double serial = 0;
            foreach (var key in route)
            {
                var n = tree.Find(key.Kind, key.Id); cost += n.Price; serial += n.Seconds;
                double start = ends.Where(p => Enumerable.Range(0, 50).Any(b =>
                    (n.Required[b] & (p.Node.Effects[b] | p.Node.Local[b])) != 0)).Select(p => p.End).DefaultIfEmpty().Max();
                double travel = n.Kind == "station" && tree.Core.DroneTypes.TryGetValue(n.ConstructorId, out var drone)
                    && tree.Core.HullTypes.TryGetValue(drone.HullTypeId, out var hull) ? 1000 / Math.Max(1, hull.Speed * ga[GlobalAttributeSet.MaxSpeed]) : 0;
                ends.Add((n, start + n.Seconds + travel));
                if (key.Kind == "develop") for (int i = 0; i < ga.Length; i++) ga[i] *= n.Attributes[i];
            }
            result.Add(signature, new(Key(target), [.. route], cost, ends.Select(p => p.End).DefaultIfEmpty().Max(), serial, [.. ga]));
        }
    }
}
