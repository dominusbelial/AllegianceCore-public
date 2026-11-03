using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

public sealed partial class Ship
{
    private long _nativeWarpNavViewVersion = -1;
    private Mission? _warpViewMission;
    private Sector? _warpViewSector;
    private short? _warpViewSide;
    private long _warpViewDiscoveryRevision = -1;
    private long _warpViewVersion;
    private readonly List<bool> _warpViewLocalExits = [];

    internal void PrepareWarpNavigationSight()
    {
        if (Sector is { } sector) FillNativeSightRocks(sector);
    }

    // Warps have no owning side. Reuse native personal scanning, including rear
    // attenuation, parent/cluster gates and asteroid LOS (shipIGC.h:341-389).
    internal bool CanSeeNavigationWarp(ResolvedWarp warp) => NativeCanSee(warp, null,
        warp.Position, warp.Radius, warp.Warp.Signature, warp.Warp.ClusterId);

    // O(local exits), no graph walk or per-tick allocation. The mission revision
    // advances only on a side's false->true warp visibility transition, including
    // RevealToSide's partner reveal (FedSrv/FedSrv.CPP:10783-10799). Personal
    // scanner eligibility can change before the periodic side sweep: compare
    // the exact local exit set too, so a cached miss can recover immediately.
    private long WarpNavigationViewVersion()
    {
        if (Mission is not { SeenWarpRoutingEnabled: true } mission || Sector is not { } sector) return 0;
        long discovery = SideId is { } side ? mission.WarpDiscoveryRevision(side) : 0;
        bool changed = _warpViewMission != mission || _warpViewSector != sector || _warpViewSide != SideId
            || _warpViewDiscoveryRevision != discovery;
        PrepareWarpNavigationSight();
        for (int i = 0; i < sector.Warps.Count; i++)
        {
            bool visible = CanSeeNavigationWarp(sector.Warps[i]);
            if (i == _warpViewLocalExits.Count) { _warpViewLocalExits.Add(visible); changed = true; }
            else if (_warpViewLocalExits[i] != visible) { _warpViewLocalExits[i] = visible; changed = true; }
        }
        if (_warpViewLocalExits.Count > sector.Warps.Count)
        {
            _warpViewLocalExits.RemoveRange(sector.Warps.Count, _warpViewLocalExits.Count - sector.Warps.Count);
            changed = true;
        }
        _warpViewMission = mission; _warpViewSector = sector; _warpViewSide = SideId;
        _warpViewDiscoveryRevision = discovery;
        if (changed) _warpViewVersion++;
        return _warpViewVersion;
    }
}
