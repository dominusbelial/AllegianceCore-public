namespace Allegiance.Core.Model;

/// <summary>
/// A minimal <b>side (team)</b> of a static <see cref="Mission"/>: its id, its
/// name (when the host supplied one), and its civilization resolved from the
/// core. Purely descriptive — no buckets, no tech masks, no ships. Those are
/// runtime concerns built by the engine's <c>GenerateMission</c> over the same
/// (id, civilization) skeleton (<c>missionigc.cpp:3734-3899</c>).
/// </summary>
public sealed class Side
{
    /// <summary>The side id (<c>sideID</c>).</summary>
    public short Id { get; }

    /// <summary>The side name, if the supplying host provided one; else <c>null</c>.</summary>
    public string? Name { get; }

    /// <summary>The civilization backing this side, resolved from the core.</summary>
    public DataCivilizationIGC Civilization { get; }

    /// <summary>Construct a side from an already-resolved civilization.</summary>
    public Side(short id, string? name, DataCivilizationIGC civilization)
    {
        Id = id;
        Name = name;
        Civilization = civilization;
    }
}
