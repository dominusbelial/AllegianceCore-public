namespace Allegiance.Core.Model;

using Allegiance.Core.Sim;

/// <summary>
/// A minimal <b>side (team)</b> of a static <see cref="Mission"/>: its id, its
/// name (when the host supplied one), its civilization resolved from the
/// core, and its accrued treasury. Buckets and tech masks are runtime concerns
/// built by the engine's <c>GenerateMission</c> over the same (id,
/// civilization) skeleton (<c>missionigc.cpp:3734-3899</c>); <see cref="Money"/>
/// is the one runtime piece the payday slice (P51) owns.
/// </summary>
/// <remarks>
/// The <see cref="ISideIgc"/> marker lets the tick report the side pass to the
/// observation hook (<see cref="IIgcSite.SideUpdated"/>). <see cref="Update"/>
/// is a recording stub: the C++ <c>CsideIGC::Update</c> (<c>sideigc.h:41</c>)
/// refreshes its buckets and advances <c>m_lastUpdate</c>; ours just counts the
/// tick and remembers the time. <see cref="Money"/> is the port of the C++
/// FedSrv accumulator <c>m_rgMoney[sideID]</c> (<c>fsmission.cpp:61,3504</c> —
/// initialised to 0 at mission start), fed by the tick's payday accrual
/// (<c>fsmission.cpp:2536</c>) and drained by the host's per-player payout
/// (<c>fsmission.cpp:2301-2309</c>).
/// </remarks>
public sealed class Side : ISideIgc
{
    /// <summary>The side id (<c>sideID</c>).</summary>
    public short Id { get; }

    /// <summary>The side name, if the supplying host provided one; else <c>null</c>.</summary>
    public string? Name { get; }

    /// <summary>The civilization backing this side, resolved from the core.</summary>
    public DataCivilizationIGC Civilization { get; }

    /// <summary>
    /// The side's construction buckets (P119) — the things the team can fund to
    /// build. Seeded by <see cref="Mission.Assemble"/> from the (synthetic,
    /// real-hull-backed) <see cref="BucketCatalog"/>; advanced for build time in
    /// <see cref="Update"/>. The port of the native per-side
    /// <c>BucketListIGC m_buckets</c> (sideigc.h:329).</summary>
    public IReadOnlyList<Bucket> Buckets { get; }

    /// <summary>Number of times <see cref="Update"/> has been called.</summary>
    public long UpdateCount { get; private set; }

    /// <summary>The time of the most recent <see cref="Update"/>, or 0 before the first.</summary>
    public double LastUpdate { get; private set; }

    /// <summary>
    /// The side's accrued treasury, the port of <c>m_rgMoney[sideID]</c>
    /// (<c>fsmission.cpp:61,3504</c>: starts 0; <c>fsmission.cpp:2536</c> adds
    /// income each payday; <c>fsmission.cpp:2309</c> subtracts <c>delta·np</c>
    /// when the host pays out). A <c>long</c> here (vs the native 32-bit
    /// <c>Money</c>) is a deliberate widening so the sim accumulator cannot
    /// overflow for unrealistically long sessions; the wire field stays
    /// native-width 32-bit (see <see cref="Allegiance.Core.Wire.Payday"/>).
    /// Owned by <see cref="Mission.Update"/>'s payday pass and drained by the
    /// host's <c>PaydayEvent</c> handler on the same (sim) thread.
    /// </summary>
    public long Money { get; set; }

    /// <summary>Construct a side from an already-resolved civilization and its
    /// construction-bucket catalog.</summary>
    public Side(short id, string? name, DataCivilizationIGC civilization, IReadOnlyList<Bucket>? buckets = null)
    {
        Id = id;
        Name = name;
        Civilization = civilization;
        Buckets = buckets ?? Array.Empty<Bucket>();
    }

    /// <summary>
    /// Once-per-<see cref="Mission.Update"/> side pass (<c>CsideIGC::Update</c>,
    /// <c>sideigc.h:41</c>): records the time and advances every construction
    /// bucket's invested-build-time accumulator (P119), so a funded bucket
    /// progresses toward completion and the completion seam can fire. Before
    /// P119 this was a counting stub; the bucket advance is the economy-slice
    /// work the native's <c>AdjustBuckets</c>/bucket <c>Update</c> performs.
    /// </summary>
    public void Update(double now)
    {
        UpdateCount++;
        LastUpdate = now;
        foreach (var bucket in Buckets)
            bucket.Update(now);
    }
}
