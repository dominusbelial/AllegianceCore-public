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

    /// <summary>The civilization backing this side, resolved from the core.
    /// Settable since P186: <c>CHANGE_TEAM_CIV(51)</c> swaps it mid-mission
    /// (<c>fsmission.cpp:5268-5284 SetSideCiv</c>). Making it writable is safe
    /// because nothing on this side is keyed to the civilization — the
    /// construction-bucket catalog (<see cref="BucketCatalog.ForSide"/>) is a
    /// single fixed synthetic bucket that does not depend on the civ (it
    /// references hull 504 directly, Bucket.cs:242-258), so no bucket
    /// recomputation is needed on a civ swap; the only consumers read
    /// <c>Lifepod</c>/<c>IncomeMoney</c>/<c>CivilizationId</c> live.</summary>
    public DataCivilizationIGC Civilization { get; set; }

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

    /// <summary>
    /// Whether this side is currently marked aggregate-ready (P180) — the
    /// port of the native per-side <c>m_misdef.rgfReady[iSide]</c> flag that
    /// the server recomputes on every <c>PLAYER_READY</c> change and reports
    /// to the whole mission via <c>TEAM_READY(46)</c> when it changes
    /// (<c>fsmission.cpp:5449-5471</c>). Starts <c>false</c>; only the ready-up
    /// recompute (Server-side, <c>CheckForSideAllReady</c> analogue) writes
    /// it. Follows the same direct-field-on-Side shape as <see cref="Money"/> —
    /// this port stores per-side state on the side itself rather than in a
    /// side-keyed dictionary on <see cref="Mission"/>.
    /// </summary>
    public bool Ready { get; set; }

    /// <summary>
    /// Whether this side's readiness is being <b>forced</b> by its team leader
    /// (P184) — the port of the native per-side <c>m_misdef.rgfForceReady[iSide]</c>
    /// flag (<c>fsmission.cpp</c>) that <c>FORCE_TEAM_READY(47)</c> toggles. A
    /// persistent per-side flag, not a one-shot action: while <c>true</c>, the
    /// side's aggregate readiness is reported ready regardless of individual
    /// <c>PLAYER_READY</c> states (bypassing the per-player check in
    /// <c>Server.RecomputeSideReady</c>, matching native
    /// <c>CheckForSideAllReady</c>'s force-ready branch, <c>fsmission.cpp:5508-5509</c>);
    /// turning it off re-runs the normal per-player check. Starts <c>false</c>.
    /// Follows the same direct-field-on-Side shape as <see cref="Ready"/>.
    /// </summary>
    public bool ForceReady { get; set; }

    /// <summary>
    /// Whether this side auto-accepts pending side-join requests without
    /// leader approval (P186) — the port of the native per-side
    /// <c>m_misdef.rgfAutoAccept[sideID]</c> flag (<c>fsmission.cpp</c>), toggled
    /// by <c>AUTO_ACCEPT(52)</c> (<c>fsmission.cpp:4974-5011 SetAutoAccept</c>).
    /// While <c>true</c>, a <c>POSITIONREQ</c> for this side seats the requester
    /// immediately instead of routing it to the leader, and turning it <b>on</b>
    /// instantly seats any player with a currently-pending request on the side.
    /// Starts <c>false</c>. Follows the same direct-field-on-Side shape as
    /// <see cref="Ready"/>.</summary>
    public bool AutoAccept { get; set; }

    /// <summary>
    /// The <see cref="Ship.Id"/> of this side's current team leader, or
    /// <c>null</c> while the side has no leader (P183). The port of the native
    /// per-side <c>m_misdef.rgShipIDLeaders[sideID]</c> slot
    /// (<c>fsmission.cpp</c>): computed only at the two real event sites
    /// <c>AddPlayerToSide</c> (<c>fsmission.cpp:539-661</c> — the first player
    /// onto an empty side becomes its leader) and <c>RemovePlayerFromSide</c>
    /// (<c>fsmission.cpp:848-1099</c> — succession when the leader leaves);
    /// never looked up on demand. Starts <c>null</c>. Follows the same
    /// direct-field-on-Side shape as <see cref="Ready"/> — this port stores
    /// per-side state on the side itself rather than in a side-keyed
    /// dictionary on <see cref="Mission"/>. Written by the Server's
    /// side-switch capability and its disconnect leader-succession.
    /// </summary>
    public short? LeaderShipId { get; set; }

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
