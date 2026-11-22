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
    private long _streamingStartedAt;
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
        if (_battleManifest is not null)
        {
            _battleStreamer = new(_battleManifest, resident, stager, _assets);
            _game.RequestBattlePack = _battleStreamer.RequestPack;
            _game.PrioritizeBattlePack = _battleStreamer.RequestPack;
            _game.BattlePackReady = _battleStreamer.IsReady;
            _game.BattlePackExists = _battleStreamer.HasPack;
        }
        _sectorStreamer = new(_sectorManifest, resident, stager, _game.PrepareSectorVisualsAsync, _game.SectorVisualsReady);
        _sectorStreamer.Changed += SectorProgressChanged;
        _game.PrefetchSector = sector =>
        {
            _prefetchSector = sector;
            if (CurrentSector is { } current)
            { _sectorStreamer.SetInterest(current, sector); _game.RetainSectorVisuals(current, sector); }
            if (_entryUpgradeRequested && Environment.TickCount64 - _streamingStartedAt >= 30000)
                _sectorStreamer.RequestSector(sector, 2);
        };
    }
    private void SectorProgressChanged() { if (!_disposed) StateHasChanged(); }
    private void RefreshSectorStreaming()
    {
        if (_game?.Flying != true || _sectorStreamer is null || CurrentSector is not { } sector) return;
        // Starts only after the first actual launch, never before the join ticket.
        int priority = _entryUpgradeRequested ? 1 : 0;
        if (!_entryUpgradeRequested) _streamingStartedAt = Environment.TickCount64;
        _entryUpgradeRequested = true;
        _sectorStreamer.SetInterest(sector, _prefetchSector);
        _game.RetainSectorVisuals(sector, _prefetchSector);
        _sectorStreamer.RequestSector(sector, priority);
        if (_prefetchSector is { } next && next != sector && Environment.TickCount64 - _streamingStartedAt >= 30000)
            _sectorStreamer.RequestSector(next, 2);
        // Stage current-sector missile visuals through the same verified path.
        // Their scene pass and effect texture upload belong to task B.
        if (_battleStreamer is { } battle && _session is { } session)
        {
            battle.RequestPack("scene-b1");
            // Decorative libraries follow the first view's native geometry and sky.
            // Priority 2 keeps the verified originals queued for the 30 s grace.
            if (battle.IsReady("scene-b1")) battle.RequestPack("scene-paintings", 2);
            if (_battleManifest?.SceneGiantSectors is { } giantSectors && Array.IndexOf(giantSectors, sector) >= 0)
                battle.RequestPack("scene-giants");
            foreach (var effect in (Dictionary<short, Allegiance.ClientCore.State.BuildingEffectEntity>)session.World.BuildingEffects)
                if (effect.Value.ClusterId == sector) { battle.RequestPack("scene-build"); break; }
            foreach (var missile in session.World.Missiles.Values)
                if (missile.ClusterId == sector) battle.RequestPack("missile-" + missile.TypeId);
            // P1073 — generated maps have no P1054 sector recording: request the
            // station, asteroid, warp and sky closures for this sector (and the
            // selected/prefetched one) through the same verified on-demand path.
            _game!.RequestSectorArt(sector, _prefetchSector);
        }
    }
    private async Task StopSectorStreamingAsync()
    {
        if (_game is not null) { _game.RequestBattlePack = null; _game.PrioritizeBattlePack = null; _game.BattlePackReady = null; _game.BattlePackExists = null; }
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
