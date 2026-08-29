namespace Allegiance.Core.Model;

using Allegiance.Core.Sim;

/// <summary>
/// A minimal <b>side (team)</b> of a static <see cref="Mission"/>: its id, its
/// name (when the host supplied one), and its civilization resolved from the
/// core. Purely descriptive — no buckets, no tech masks, no ships. Those are
/// runtime concerns built by the engine's <c>GenerateMission</c> over the same
/// (id, civilization) skeleton (<c>missionigc.cpp:3734-3899</c>).
/// </summary>
/// <remarks>
/// The <see cref="ISideIgc"/> marker lets the tick report the side pass to the
/// observation hook (<see cref="IIgcSite.SideUpdated"/>). <see cref="Update"/>
/// is a recording stub: the C++ <c>CsideIGC::Update</c> (<c>sideigc.h:41</c>)
/// refreshes its buckets and advances <c>m_lastUpdate</c>; ours just counts the
/// tick and remembers the time.
/// </remarks>
public sealed class Side : ISideIgc
{
    /// <summary>The side id (<c>sideID</c>).</summary>
    public short Id { get; }

    /// <summary>The side name, if the supplying host provided one; else <c>null</c>.</summary>
    public string? Name { get; }

    /// <summary>The civilization backing this side, resolved from the core.</summary>
    public DataCivilizationIGC Civilization { get; }

    /// <summary>Number of times <see cref="Update"/> has been called.</summary>
    public long UpdateCount { get; private set; }

    /// <summary>The time of the most recent <see cref="Update"/>, or 0 before the first.</summary>
    public double LastUpdate { get; private set; }

    /// <summary>Construct a side from an already-resolved civilization.</summary>
    public Side(short id, string? name, DataCivilizationIGC civilization)
    {
        Id = id;
        Name = name;
        Civilization = civilization;
    }

    /// <summary>
    /// Recording stub for the once-per-<see cref="Mission.Update"/> side pass
    /// (<c>CsideIGC::Update</c>, <c>sideigc.h:41</c>). It only counts and
    /// records the time; bucket/tech work arrives with the economy task.
    /// </summary>
    public void Update(double now)
    {
        UpdateCount++;
        LastUpdate = now;
    }
}
