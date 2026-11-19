using Allegiance.ClientCore.Browser.Assets;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Allegiance.ClientCore.Browser.Pages;

public partial class Menu
{
    private SectorManifest? _sectorManifest;
    private SectorPackStreamer? _sectorStreamer;
    private short? _prefetchSector;
    private bool _entryUpgradeRequested;
    private bool _showWaypoint;
    private float _waypointX, _waypointY, _waypointZ;
    private string SectorStatus => _sectorStreamer?.Status ?? "";
    private short? CurrentSector => _session?.World.MyShipId is { } id && _session.World.Ships.TryGetValue(id, out var own) ? own.SectorId : null;
    private void StartSectorStreaming(StagedManifest resident)
    {
        if (_game is null || _assets is null || _platform is null || _sectorManifest is null || _sectorStreamer is not null) return;
        _game.ConfigureSectorVisuals(_sectorManifest);
        var stager = new AssetStager(Http, new BrowserAssetCache(_platform, _assetMemoryFallback), request =>
        { request.SetBrowserResponseStreamingEnabled(true); request.SetBrowserRequestCache(BrowserRequestCache.Reload); }, SectorPackStreamer.VerifyCooperativelyAsync);
        _sectorStreamer = new(_sectorManifest, resident, stager, _game.PrepareSectorVisualsAsync, _game.SectorVisualsReady);
        _sectorStreamer.Changed += SectorProgressChanged;
        _game.PrefetchSector = sector =>
        {
            _prefetchSector = sector;
            if (CurrentSector is { } current)
            { _sectorStreamer.SetInterest(current, sector); _game.RetainSectorVisuals(current, sector); }
            _sectorStreamer.RequestSector(sector, 2);
        };
    }
    private void SectorProgressChanged() { if (!_disposed) StateHasChanged(); }
    private void RefreshSectorStreaming()
    {
        if (_game?.Flying != true || _sectorStreamer is null || CurrentSector is not { } sector) return;
        // Starts only after the first actual launch, never before the join ticket.
        int priority = _entryUpgradeRequested ? 1 : 0;
        _entryUpgradeRequested = true;
        _sectorStreamer.SetInterest(sector, _prefetchSector);
        _game.RetainSectorVisuals(sector, _prefetchSector);
        _sectorStreamer.RequestSector(sector, priority);
    }
    private async Task StopSectorStreamingAsync()
    {
        var streamer = _sectorStreamer; _sectorStreamer = null;
        if (streamer is not null) { streamer.Changed -= SectorProgressChanged; await streamer.DisposeAsync(); }
        _game?.EndSectorVisuals(); _prefetchSector = null; _entryUpgradeRequested = false;
    }
    private void SelectMapSector(ChangeEventArgs args)
    { if (short.TryParse(args.Value?.ToString(), out short id)) _game?.SelectMapSector(id); }
    private void SelectWarp(ChangeEventArgs args)
    { if (short.TryParse(args.Value?.ToString(), out short id)) _game?.SelectWarp(id); }
}
