using Allegiance.ClientCore.Browser.Assets;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Allegiance.ClientCore.Browser.Pages;

public partial class Menu
{
    private BattleManifest? _battleManifest;
    private BattlePackStreamer? _battleStreamer;
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
        if (_battleManifest is not null)
        {
            var battleStager = new AssetStager(Http, new BrowserAssetCache(_platform, _assetMemoryFallback), request =>
            { request.SetBrowserResponseStreamingEnabled(true); request.SetBrowserRequestCache(BrowserRequestCache.Reload); }, SectorPackStreamer.VerifyCooperativelyAsync);
            _battleStreamer = new(_battleManifest, resident, battleStager, _assets);
            _game.RequestBattlePack = _battleStreamer.RequestPack;
        }
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
        // Stage current-sector missile visuals through the same verified path.
        // Their scene pass and effect texture upload belong to task B.
        if (_battleStreamer is { } battle && _session is { } session)
            foreach (var missile in session.World.Missiles.Values)
                if (missile.ClusterId == sector) battle.RequestPack("missile-" + missile.TypeId);
    }
    private async Task StopSectorStreamingAsync()
    {
        if (_game is not null) _game.RequestBattlePack = null;
        var battle = _battleStreamer; _battleStreamer = null;
        if (battle is not null) await battle.DisposeAsync();
        var streamer = _sectorStreamer; _sectorStreamer = null;
        if (streamer is not null) { streamer.Changed -= SectorProgressChanged; await streamer.DisposeAsync(); }
        _game?.EndSectorVisuals(); _prefetchSector = null; _entryUpgradeRequested = false;
    }
    private void SelectMapSector(ChangeEventArgs args)
    { if (short.TryParse(args.Value?.ToString(), out short id)) _game?.SelectMapSector(id); }
    private void SelectWarp(ChangeEventArgs args)
    { if (short.TryParse(args.Value?.ToString(), out short id)) _game?.SelectWarp(id); }
}
