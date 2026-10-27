namespace Allegiance.Core.Sim;

/// <summary>
/// P835 — the cross-side visibility sweep's last-spotter short-circuit switch
/// (<c>sim.visibilitySpotterCache</c>, <c>ALLEGIANCE_VIS_SPOTTER_CACHE</c>).
/// A static holder so <see cref="Allegiance.Core.Model.Sector"/> can consult it
/// without an <c>Allegiance.Server</c> dependency; set once at server boot from
/// <c>ServerConfig.Sim</c> (see <c>Server.Lifecycle</c>), and set directly by tests.
/// </summary>
/// <remarks>
/// <para><b>What it does.</b> When enabled, the per-10th-pass cross-side
/// visibility sweep (<see cref="Allegiance.Core.Model.Sector.ResolveCrossSideVisibility"/>)
/// first re-tests only the target's recorded last spotter for the side — exactly
/// the short-circuit native runs before its full scanner loop
/// (<c>FedSrv.CPP:10896-10903</c>, <c>sv.pLastSpotter() &amp;&amp;
/// sv.pLastSpotter()-&gt;InScannerRange(pmodel)</c>) — and skips the O(scanners)
/// loop when that spotter still sees the target. In a steady clash most
/// (target, side) pairs keep their spotter sweep after sweep, so the sweep's
/// dominant scanner×target scan collapses to one InScannerRange call per pair.</para>
/// <para><b>Behaviour.</b> The resulting <c>SeenBySide</c> flags are identical
/// to the full loop's for every input (if the last spotter still sees the
/// target, the full loop would also return seen; if not, the full loop runs
/// unchanged). The recorded <b>spotter identity</b>, however, is the native
/// sticky one: the side keeps its previous spotter while that spotter still
/// sees the target, instead of re-picking the first scanner in enumeration
/// order every sweep (<c>FedSrv.CPP:11026</c>, "Keep track of who spotted it").
/// The port's pre-P835 sweep deliberately omitted the cache (documented as a
/// pure performance optimization) and therefore re-picks; enabling this flag
/// restores the native spotter semantics — a wire-observable change in
/// <c>OBJECT_SPOTTED(6)</c>'s otSpotter/oidSpotter when several scanners see
/// the same target — so it ships behind this default-OFF flag pending a rig A/B.</para>
/// <para><b>Default OFF.</b> When <see cref="Enabled"/> is false the sweep runs
/// exactly the pre-P835 full scan, byte-for-byte.</para>
/// </remarks>
public static class VisibilitySpotterCache
{
    /// <summary>Master switch. <c>false</c> (the default) = the pre-P835 full
    /// scanner loop, byte-for-byte. <c>true</c> = the native last-spotter
    /// short-circuit (FedSrv.CPP:10896-10903).</summary>
    public static bool Enabled { get; set; }

    /// <summary>Diagnostic test seam — reset to the default (off) state.</summary>
    public static void Reset() => Enabled = false;
}
