namespace Allegiance.Core.Sim;

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

    /// <summary>Whether <paramref name="sideId"/> can currently see this model —
    /// the port of <c>ThingSiteImpl::GetSideVisibility</c>
    /// (<c>FedSrv.CPP:11133-11137</c>). Untouched sides read <c>false</c>,
    /// matching a fresh native array entry (zero-initialised).</summary>
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
    /// synced to the same value in the same call (native line
    /// <c>FedSrv.CPP:11168</c>). Sides are independent — updating one side
    /// never touches another's flags. No <c>ShowObject</c>/<c>HideObject</c>
    /// side effects and no station-territory refresh are fired; see class doc.
    /// </summary>
    public void SetSideVisibility(short sideId, bool visible)
    {
        _visible[sideId] = visible;
        _currentEye[sideId] = visible;
    }
}
