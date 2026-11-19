using Allegiance.ClientCore.Browser.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Wire;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private SectorTexturePool? _sectorTextures;
    private SectorManifest? _sectorVisualManifest;
    private long _skyTextureRevision = -1;
    private StarmapRenderer? _browserStarmap;
    private bool _viewRock;
    private short _waypointId = 30000;
    private readonly Dictionary<string, int[]> _drawnRockSurfaces = new(StringComparer.Ordinal);
    public void ToggleRockView() { _viewRock = !_viewRock; }
    public bool StarmapOpen { get; private set; }
    public short? SelectedMapSector { get; private set; }
    public Action<short>? PrefetchSector { get; set; }
    public void ConfigureSectorVisuals(SectorManifest manifest)
    {
        _sectorTextures?.Dispose();
        _sectorVisualManifest = manifest;
        _sectorTextures = new(manifest, (StagedAssetSource)_assets);
    }
    public void EndSectorVisuals()
    { _sectorTextures?.Dispose(); _sectorTextures = null; PrefetchSector = null; StarmapOpen = false; SelectedMapSector = null; _viewRock = false; }
    public Task PrepareSectorVisualsAsync(SectorVisualPack batch, StagedAssetSource staged, CancellationToken token)
        => _sectorTextures?.PrepareAsync(batch, staged, token) ?? Task.FromCanceled(new CancellationToken(true));
    public bool SectorVisualsReady(short sector) => _sectorTextures?.IsResident(sector) == true;
    public void RetainSectorVisuals(short sector, short? prefetch) => _sectorTextures?.Retain(sector, prefetch);
    public object? SectorTextureMetrics => _sectorTextures?.Metrics;
    public object? SectorSkyMetrics
    {
        get
        {
            if (_flightSession?.World.MyShipId is not { } id || !_session.World.Ships.TryGetValue(id, out var ship) ||
                !_session.World.Clusters.TryGetValue(ship.SectorId, out var cluster) || _sectorTextures is null) return null;
            string digits = new(cluster.PosterName.Where(char.IsAsciiDigit).ToArray());
            return new { stars = cluster.Stars,
                environmentReady = string.IsNullOrEmpty(digits) || _sectorTextures.IsSourceReady($"environment{digits}bmp.mdl"),
                planetReady = string.IsNullOrEmpty(cluster.PlanetName) || _sectorTextures.IsSourceReady(cluster.PlanetName + ".mdl") };
        }
    }
    public void ToggleStarmap()
    {
        StarmapOpen = !StarmapOpen;
        if (StarmapOpen && _flightSession?.World.MyShipId is { } id && _session.World.Ships.TryGetValue(id, out var ship))
            SelectMapSector(SelectedMapSector ?? ship.SectorId);
    }
    public void SelectMapSector(short sector)
    {
        if (_flightSession?.World.Clusters.ContainsKey(sector) != true) return;
        SelectedMapSector = sector; PrefetchSector?.Invoke(sector);
    }
    public void SelectWarp(short warpId)
    {
        if (!Flying || !_session.World.Warps.TryGetValue(warpId, out var warp) ||
            !_session.World.Ships.TryGetValue(_session.World.MyShipId!.Value, out var ship) || warp.ClusterId != ship.SectorId) return;
        if (_session.World.Warps.TryGetValue(warp.DestinationId, out var destination)) PrefetchSector?.Invoke(destination.ClusterId);
        else if (_sectorVisualManifest is { } manifest)
        {
            // Until the partner mouth is discovered, the visible authored
            // aleph label can hint a unique recorded sector. This speculative
            // visual prefetch never supplies a navigation/physics value.
            var matches = manifest.Sectors.Where(s => string.Equals(s.Name, warp.Name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length == 1) PrefetchSector?.Invoke(matches[0].SectorId);
        }
        EngageAutopilotToward(new(TargetKind.Warp, warpId, warp.Name), "Autopilot to aleph " + warp.Name);
        StarmapOpen = false;
    }
    public void FlyToWaypoint(float x, float y, float z)
    {
        if (!Flying || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) ||
            !_session.World.Ships.TryGetValue(_session.World.MyShipId!.Value, out var ship)) return;
        while (_session.World.CommandBuoys.ContainsKey(_waypointId)) _waypointId--;
        // Same self-addressed native position order as desktop mouse picking:
        // WinTrek/console.cpp:1940-1972; clintlib/clintlib.cpp:2460-2469.
        var buoy = new DataBuoyIGC([x, y, z], ship.SectorId, DataBuoyIGC.CBuoyWaypoint, _waypointId--, true);
        _session.World.RegisterCommandBuoy(buoy);
        _session.SendChatBuoy("", Allegiance.Core.Sim.ChatTarget.Individual, ship.ShipId, buoy, commandId: OrderChange.CidGoto);
        EngageAutopilotToward(new(TargetKind.Buoy, buoy.BuoyId, "Waypoint"), "Autopilot to waypoint");
        StarmapOpen = false;
    }
    private void DrawBrowserStarmap(short sector)
    {
        if (!StarmapOpen) return;
        var sites = _session.World.Clusters.Values.Select(c => new StarmapLayout.ClusterSite(c.ClusterId, c.Name, c.ScreenX, c.ScreenY)).ToArray();
        _browserStarmap ??= new(_spriteBatch, _whitePixel, _hudSmall, GraphicsDevice, _assets);
        var vp = GraphicsDevice.Viewport;
        _browserStarmap.Draw(sites, _session.World.Graph?.Warps ?? [], sector, SelectedMapSector, null, false, vp,
            Math.Min(vp.Width * .9f, 1040), Math.Min(vp.Height * .78f, 640));
    }
}
