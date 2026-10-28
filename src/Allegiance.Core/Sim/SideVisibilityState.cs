namespace Allegiance.Core.Sim;

/// <summary>
/// A model that participates in cross-side visibility — it hosts a
/// <see cref="SideVisibilityState"/> that the sector's per-tick sweep
/// (<c>Sector.ResolveCrossSideVisibility</c>, P132) writes, and exposes the
/// native model-level read surface (<c>ImodelIGC::SeenBySide/GetCurrentEye</c>,
/// <c>Igc/modelIGC.h:110-132</c>). Implemented by the three scanner-and-target
/// kinds — <see cref="Ship"/>, <see cref="Probe"/> (via the <see cref="Model"/>
/// base) and <see cref="Station"/> — exactly the set the native registers as
/// scanners for their own side (<c>Igc/clusterIGC.h:126,148,219</c>) and marks
/// <c>c_mtSeenBySide</c>.
/// </summary>
public interface ISideVisible
{
    /// <summary>The per-side visibility flags of this model — the port-owned
    /// holder the native keeps in the model's <c>ThingSite</c>
    /// (<c>ThingSiteImpl::m_rgSideVisibility</c>, FedSrv.CPP:11172).</summary>
    SideVisibilityState SideVisibility { get; }

    /// <summary>Whether <paramref name="sideId"/> can currently see this model
    /// — the port of <c>ImodelIGC::SeenBySide</c> (<c>modelIGC.h:110-114</c>).</summary>
    bool SeenBySide(short sideId);

    /// <summary>Whether <paramref name="sideId"/> is currently the eye on this
    /// model — the port of <c>ImodelIGC::GetCurrentEye</c> (<c>modelIGC.h:120-124</c>).</summary>
    bool GetCurrentEye(short sideId);
}

/// <summary>
/// Per-model, per-side visibility state — the port of the native
/// <c>ThingSiteImpl::SideVisibility m_rgSideVisibility[c_cSidesMax]</c> flags
/// (<c>FedSrv/FedSrv.CPP:10692-11172</c>, read in full; the array is declared
/// at <c>FedSrv.CPP:11172</c>), reshaped idiomatically as a dictionary keyed by
/// <see cref="short"/> <c>SideId</c> rather than a fixed-size side array
/// (the port has no <c>c_cSidesMax</c> bound — missions carry an unbounded,
/// data-driven set of sides, <c>Model/Side.cs</c>).
/// </summary>
/// <remarks>
/// Each side holds the two flags native <c>SideVisibility</c> packs together:
/// a <b>visible</b> flag (<c>SeenBySide</c>) and a <b>current-eyed</b> flag
/// (<c>GetCurrentEye</c>), exactly the pair the model accessors read
/// (<c>Igc/modelIGC.h:110-132</c>: <c>SeenBySide</c>/<c>GetCurrentEye</c> both
/// route to <c>m_pThingSite-&gt;GetSideVisibility/GetCurrentEye</c>). This is
/// the <b>state transition only</b> — the write reproduces the native
/// <c>SetSideVisibility</c> (<c>FedSrv.CPP:11153-11171</c>) flag updates but
/// deliberately fires <b>none</b> of its side effects: no <c>ShowObject</c>/
/// <c>HideObject</c> (<c>FedSrv.CPP:11163-11167</c>) and no territory refresh
/// for stations (<c>FedSrv.CPP:11170</c>). Those wire effects belong to the
/// future per-cluster detection sweep task; this class is the foundation it
/// builds on.
/// </remarks>
public sealed class SideVisibilityState
{
    private readonly Dictionary<short, bool> _visible = [];
    private readonly Dictionary<short, bool> _currentEye = [];
    private readonly HashSet<short> _revealedPredictable = [];

    /// <summary>
    /// Whether this model carries the native <c>c_mtPredictable</c> attribute
    /// (a static object: warp, asteroid, station, mine, probe, static treasure —
    /// <c>warpIGC.cpp:35</c>, <c>asteroidigc.cpp:36</c>, <c>stationIGC.cpp:244</c>,
    /// <c>mineigc.cpp:104</c>, <c>probeigc.cpp:115</c>, <c>treasureIGC.cpp:167</c>).
    /// The flag is what makes native's visibility <b>latch</b>: once a side has
    /// seen a predictable object it stays <c>SeenBySide</c> forever, because
    /// <c>ThingSiteImpl::UpdateSideVisibility</c> re-forces the visible flag back
    /// to true instead of hiding it —
    /// <c>if (m_bPredictable) //keep static objects visible (but not currentEyed)
    ///   sv.fVisible(true);</c> (<c>FedSrv/FedSrv.CPP:11124-11126</c>).
    /// Only the <c>CurrentEyed</c> flag keeps decaying, which is why the port can
    /// still tell "known" (static) from "currently watched" (ore updates, radar).
    /// Ships are the one non-predictable visibility kind, so their flag still
    /// clears when the last scanner loses them (<c>FedSrv.CPP:11126-11129</c>).
    /// </summary>
    public bool Predictable { get; set; }

    /// <summary>
    /// The last scanner that established a side's visibility — the port of
    /// native <c>SideVisibility::pLastSpotter()</c> (<c>FedSrv.CPP:10944</c> sets
    /// it on every successful scan; <c>:11119</c> clears it when the eye is lost),
    /// recorded as the native <c>(ObjectType, ObjectID)</c> pair the
    /// <c>OBJECT_SPOTTED</c>(6) announcement carries (<c>FedSrv.CPP:11055-11062</c>).
    /// <c>(-1, -1)</c> = no spotter yet (native NULL pointer / <c>NA</c>).
    /// <para><b>Per side (P865b).</b> Native's <c>sv</c> is each side's own
    /// visibility state (ThingSiteImpl keeps one per side), so
    /// <c>sv.pLastSpotter()</c> reads the seeing side's own spotter. A single
    /// model-level slot leaked one side's spotter to every other side through
    /// the last-spotter short-circuit (<c>FedSrv.CPP:10896-10903</c> reads it
    /// before the scanner loop): once side A's scanner spotted a target, side
    /// B's short-circuit passed on A's spotter geometry and B "saw" the target
    /// too — which broke the SIM-0643 disguise (one side's discovery revealed
    /// the station to every other side). Kept per side, exactly native's
    /// per-<c>sv</c> slot.</para>
    /// </summary>
    private readonly Dictionary<short, (short ObjectType, short ObjectId)> _lastSpotter = [];

    public (short ObjectType, short ObjectId) LastSpotterFor(short sideId) =>
        _lastSpotter.TryGetValue(sideId, out var spotter) ? spotter : ((short)-1, (short)-1);

    /// <summary>Record the scanner that just established visibility for a side
    /// (native <c>sv.pLastSpotter(s)</c>, <c>FedSrv.CPP:10944</c>).</summary>
    public void SetLastSpotter(short sideId, short objectType, short objectId) =>
        _lastSpotter[sideId] = (objectType, objectId);

    /// <summary>Clear the last spotter — native <c>sv.pLastSpotter(NULL)</c> when the
    /// eye is lost while the object stays known (<c>FedSrv.CPP:11119-11120</c>).</summary>
    public void ClearLastSpotter(short sideId) => _lastSpotter.Remove(sideId);

    /// <summary>
    /// Force this model visible to a side <b>without</b> touching its current-eye
    /// flag — the port of the native aleph-pair hack that writes
    /// <c>sv.fVisible(true)</c> directly on the partner warp
    /// (<c>FedSrv/FedSrv.CPP:10795-10799</c>, "force the visibility of this warp
    /// wrt the side to true without going through SetSideVisibility"). Because a
    /// warp is <c>c_mtPredictable</c>, the next sweep's plain false write keeps
    /// the forced true (the latch above), so the partner stays known exactly as
    /// natively.
    /// </summary>
    public void RevealToSide(short sideId) => _visible[sideId] = true;

    // Probe firing explicitly reveals a predictable static object. Native
    // FedSrv/FedSrv.CPP:11120-11126 retains its known position after scanners
    // lose the current eye. Other visibility participants retain their policy.
    internal void RetainPredictableToSide(short sideId) => _revealedPredictable.Add(sideId);

    internal void RevealPredictableToSide(short sideId)
    {
        RetainPredictableToSide(sideId);
        SetSideVisibility(sideId, true);
    }

    /// <summary>Whether <paramref name="sideId"/> can currently see this model —
    /// the port of <c>ThingSiteImpl::GetSideVisibility</c>
    /// (<c>FedSrv.CPP:11133-11137</c>). Untouched sides read <c>false</c>,
    /// matching a fresh native array entry (zero-initialised).</summary>
    internal bool HasState(short sideId) => _visible.ContainsKey(sideId);

    public bool SeenBySide(short sideId)
        => _visible.TryGetValue(sideId, out var visible) && visible;

    /// <summary>Whether <paramref name="sideId"/> is currently the eye on this
    /// model — the port of <c>ThingSiteImpl::GetCurrentEye</c>
    /// (<c>FedSrv.CPP:11139-11143</c>). Untouched sides read <c>false</c>.</summary>
    public bool GetCurrentEye(short sideId)
        => _currentEye.TryGetValue(sideId, out var eye) && eye;

    /// <summary>
    /// Set the visibility of this model to <paramref name="sideId"/> to
    /// <paramref name="visible"/>. Faithfully mirrors the native flag
    /// transitions of <c>SetSideVisibility</c> (<c>FedSrv.CPP:11153-11171</c>):
    /// the visible flag is assigned the new value, and the current-eyed flag is
    /// synced to the same value in the same call. A predictable probe contact
    /// revealed by firing keeps its known position when the eye is lost
    /// (FedSrv.CPP:11120-11126). Native line
    /// <c>FedSrv.CPP:11168</c>. Sides are independent — updating one side
    /// never touches another's flags. No <c>ShowObject</c>/<c>HideObject</c>
    /// side effects and no station-territory refresh are fired; see class doc.
    /// </summary>
    public void SetSideVisibility(short sideId, bool visible)
    {
        // The predictable latch (native FedSrv.CPP:11124-11126): a static object
        // that was ever seen stays visible to that side; only the current-eye
        // flag drops. Non-predictable models (ships) keep the plain assignment.
        _visible[sideId] = visible || _revealedPredictable.Contains(sideId)
            || (Predictable && SeenBySide(sideId));
        _currentEye[sideId] = visible;
    }
}
