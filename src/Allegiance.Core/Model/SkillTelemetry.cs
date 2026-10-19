namespace Allegiance.Core.Model;

/// <summary>Server-enabled, simulation-thread-owned observation only. No per-event allocation or logging.</summary>
public enum ShowcaseSkill
{
    Flight,
    Warp,
    Autopilot,
    Targeting,
    Guns,
    Missiles,
    Chaff,
    Booster,
    Cloak,
    Probes,
    Scouting,
    Mines,
    ShipDamage,
    StationDamage,
    StationKills,
    NaniteShip,
    NaniteStation,
    OrderGoto,
    OrderAttack,
    OrderBuild,
    OrderRepair,
    DroneBuild,
    DroneMine,
    Eject,
    PodRescue,
    Dock,
    Relaunch,
    Purchase,
    Research,
    Chat,
    Voice,
    ShipKills,
    Count
}

public sealed class SkillTelemetry(long match, IEnumerable<short> sides)
{
    public static readonly (string Id, long Minimum)[] Catalogue =
    [
        ("flight", 50),
        ("warp", 20),
        ("autopilot", 5),
        ("targeting", 50),
        ("guns", 1000),
        ("missiles", 100),
        ("chaff", 10),
        ("booster", 20),
        ("cloak", 5),
        ("probes", 10),
        ("scouting", 10),
        ("mines", 5),
        ("ship-damage", 100),
        ("station-damage", 20),
        ("station-kills", 1),
        ("nanite-ship", 10),
        ("nanite-station", 10),
        ("order-goto", 5),
        ("order-attack", 5),
        ("order-build", 2),
        ("order-repair", 2),
        ("drone-build", 2),
        ("drone-mine", 10),
        ("eject", 10),
        ("pod-rescue", 2),
        ("dock", 20),
        ("relaunch", 20),
        ("purchase", 20),
        ("research", 5),
        ("chat", 5),
        ("voice", 5),
        ("ship-kills", 20),
    ];
    private sealed class SideCounters
    {
        public readonly long[] Counts = new long[(int)ShowcaseSkill.Count];
        public readonly long[,] Damage = new long[2, 256];
    }
    private readonly Dictionary<short, SideCounters> _sides = sides.ToDictionary(s => s, _ => new SideCounters());
    public long Match { get; } = match;
    public bool Completed { get; private set; }
    public void Record(short? side, ShowcaseSkill skill)
    {
        if (!Completed && side is { } id && _sides.TryGetValue(id, out var counters))
            counters.Counts[(int)skill]++;
    }
    public long Count(short side, ShowcaseSkill skill) => _sides[side].Counts[(int)skill];
    public void Damage(short? side, bool station, byte type)
    {
        if (!Completed && side is { } id && _sides.TryGetValue(id, out var counters))
            counters.Damage[station ? 1 : 0, type]++;
    }
    /// <summary>Repeated finalization is a no-op. Queries neither freeze nor reset counters.</summary>
    public void Write(TextWriter output, bool final = false)
    {
        if (final && Completed) return;
        if (final) Completed = true;
        foreach (var (side, counters) in _sides.OrderBy(s => s.Key))
        {
            string prefix = $"[skills] match {Match} side {side}";
            output.WriteLine($"{prefix} {(final ? "final" : "snapshot")}");
            output.WriteLine($"{prefix}: " + string.Join(" ", Catalogue.Select((s, i) => $"{s.Id} {counters.Counts[i]}")));
            int covered = Catalogue.Where((s, i) => counters.Counts[i] >= s.Minimum).Count();
            output.WriteLine($"{prefix} coverage {covered}/{Catalogue.Length}");
            output.WriteLine($"{prefix} damage-types: " + string.Join(" ",
                Enumerable.Range(0, 2).SelectMany(target => Enumerable.Range(0, 256)
                    .Where(type => counters.Damage[target, type] != 0)
                    .Select(type => $"{(target == 1 ? "station" : "ship")}-{type} {counters.Damage[target, type]}"))));
        }
    }
}
