using System.Net;
using Microsoft.JSInterop;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.Browser.Assets;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Allegiance.Core;
using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Browser.Pages;

public partial class Menu
{
    private bool _online, _onlineBusy, _onlineClosing;
    private long _onlineGeneration;
    private BrowserServer? _pendingServer;
    private BrowserMission? _pendingMission;
    private string? _onlineError;
    private IdentityAccount? _account;
    private BrowserServer[] _servers = [];
    private ConnectionSession? _session;
    private string _onlineState = "Login", _onlineName = "";
    private bool _showBriefing;
    private string _pose = "Waiting for authoritative updates";
    private long _lastOnlineRefresh;
    private Uri LobbyUri(string path) => new(new Uri(Http.BaseAddress!, "/"), path);

    private async Task OpenOnlineAsync()
    {
        if (_onlineBusy || _assets is null) return;
        _online = true; _onlineBusy = true; _onlineError = null;
        try
        {
            if (!Http.BaseAddress!.IsLoopback) throw new NotSupportedException("Online play outside loopback is not available in the browser.");
            using var response = await Http.GetAsync(LobbyUri("api/identity/me"), _lifetime.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) { _account = null; _onlineState = "Login"; return; }
            response.EnsureSuccessStatusCode();
            _account = await response.Content.ReadFromJsonAsync(IdentityJsonContext.Default.IdentityAccount, _lifetime.Token);
            _servers = await Http.GetFromJsonAsync(LobbyUri("api/servers"), BrowserJoinJson.Default.BrowserServerArray, _lifetime.Token) ?? [];
            _onlineState = "Servers";
        }
        catch (Exception ex) { _onlineError = ex.Message; }
        finally { _onlineBusy = false; StateHasChanged(); }
    }
    private static string? Unavailable(BrowserServer server)
    {
        if (server.WebSocketUrl is null) return "Joining this server is not available in the browser (no web route).";
        try { if (!WebSocketEndpoint.FromServerListing(server.WebSocketUrl).Uri.IsLoopback) return "Public joining is not available in the browser."; }
        catch (ArgumentException) { return "Joining this server is not available in the browser (invalid web route)."; }
        if (!server.Verified || string.IsNullOrEmpty(server.IdentityServerId)) return "Unverified identity servers are not available in the browser.";
        if (server.PasswordRequired) return "Password games are not available in the browser.";
        if (!string.Equals(server.CoreFile, "PCore001.igc", StringComparison.OrdinalIgnoreCase)) return "This core is not available in the browser starter pack.";
        if (!string.IsNullOrEmpty(server.MinClientVersion)) return "Client update qualification is not available in the browser.";
        return null;
    }
    private async Task JoinServerAsync(BrowserServer server, BrowserMission? mission)
    {
        if (_onlineBusy || _assets is null || _account is null || !_servers.Contains(server) || Unavailable(server) is not null) return;
        if (mission?.PasswordRequired == true) { _onlineError = "Password games are not available in the browser."; return; }
        long generation = ++_onlineGeneration;
        ConnectionSession? ownedSession = null;
        _pendingServer = server; _pendingMission = mission;
        _onlineBusy = true; _onlineError = null;
        try
        {
            if (_platform is null) throw new InvalidOperationException("Asset storage is not ready.");
            _onlineState = "Staging";
            var stager = new AssetStager(Http, new BrowserAssetCache(_platform, _assetMemoryFallback), request => request.SetBrowserResponseStreamingEnabled(true));
            var joinManifest = _joinManifest ?? await stager.ReadManifestAsync(_lifetime.Token, "join-manifest.json");
            var pack = await stager.StageAsync(joinManifest, (percent, status) =>
            { _progress = percent; _status = status; StateHasChanged(); }, _lifetime.Token);
            if (generation != _onlineGeneration) return;
            _assets.Merge(pack);
            _game?.AttachOnlineSession(null);
            if (_session is not null) await _session.DisposeAsync();
            var core = StaticCore.Load(IgcReader.Parse(Allegiance.ClientCore.MonoGame.Assets.AssetSources.ReadAllBytes(_assets, "PCore001.igc")));
            var session = ownedSession = _session = new ConnectionSession { WebSocketEndpoint = WebSocketEndpoint.FromServerListing(server.WebSocketUrl!) };
            session.BrowserMissionBriefingDecoder = message =>
            {
                var header = FedMessageHeader.Read(message);
                if (header.Fmid != MissionBriefingInfo.Fmid || header.CbMsg != message.Length) throw new InvalidDataException("Invalid briefing header");
                byte[] json = _briefingDecoder!.Invoke<byte[]>("decode", message[4..]);
                return System.Text.Json.JsonSerializer.Deserialize(json, MissionBriefingInfoJsonContext.Default.MissionBriefingInfo)
                    ?? throw new InvalidDataException("Empty briefing");
            };
            session.EnableAutoReconnect();
            session.World.MissionCoreResolver = name => string.Equals(Path.GetFileNameWithoutExtension(name), Path.GetFileNameWithoutExtension(server.CoreFile), StringComparison.OrdinalIgnoreCase) ? core : null;
            // The complete verified starter snapshot already exists. Fetch a fresh
            // ticket now; keep it only inside the owned LOGONREQ, never in storage/URLs.
            var refusal = await session.JoinAsync((ushort)server.Port, _account.Callsign,
                new LogonRequest { CharacterName = _account.Callsign, FedsrvVer = LogonRequest.MsgVer,
                    CrcFileList = -1, DwCookie = mission?.Cookie ?? uint.MaxValue, Time = 1 },
                identityTicket: async ct =>
                {
                    using var reply = await Http.PostAsJsonAsync(LobbyUri("api/identity/join-token"),
                        new JoinTokenRequest(server.IdentityServerId!), IdentityJsonContext.Default.JoinTokenRequest, ct);
                    reply.EnsureSuccessStatusCode();
                    return (await reply.Content.ReadFromJsonAsync(IdentityJsonContext.Default.JoinTokenResponse, ct))!.Token;
                }, cancellationToken: _lifetime.Token);
            if (generation != _onlineGeneration) return;
            if (refusal is not null) throw new InvalidOperationException("The server refused this mission join.");
            if (session.World.JoinError is { } error) throw new InvalidOperationException(error);
            _onlineName = mission?.Name ?? server.MissionName;
            _onlineState = "Sides";
        }
        catch (Exception ex)
        {
            if (generation == _onlineGeneration)
            {
                _onlineError = ex.Message;
                if (ReferenceEquals(_session, ownedSession)) _session = null;
            }
            if (ownedSession is not null) { await ownedSession.LeaveBrowserAsync(); await ownedSession.DisposeAsync(); }
        }
        finally { if (generation == _onlineGeneration) { _onlineBusy = false; StateHasChanged(); } }
    }
    private void ToggleContactView() => _game?.ToggleContactView();
    private void DockHome() => _game?.DockHome();
    private Task RetryStagingAsync() => _pendingServer is { } server ? JoinServerAsync(server, _pendingMission) : Task.CompletedTask;
    private (string Title, string Objective, string Body)? OnlineBriefing()
    {
        if (_session?.World.MissionBriefing is not { } info || _assets is null) return null;
        var bytes = Allegiance.ClientCore.MonoGame.Assets.AssetSources.ReadAllBytes(_assets, "missionbrief.mdl");
        var strings = Allegiance.ClientCore.MonoGame.Rendering.MissionBriefingStrings.Parse(System.Text.Encoding.Latin1.GetString(bytes));
        return Allegiance.ClientCore.MonoGame.Rendering.MissionBriefingGenerator.Build(info, tipNumber: 1, strings: strings);
    }
    private async Task SelectSideAsync(short side)
    {
        if (_session is not { } session || _onlineBusy || session.World.SideRoster?.Sides.Any(s => s.SideId == side) != true) return;
        _onlineBusy = true; _onlineError = null;
        try
        {
            session.SendPositionReq(side);
            await WaitOnlineAsync(session, () => session.World.MySideId == side && session.World.MyShipId is not null);
            if (!ReferenceEquals(_session, session)) return;
            session.SendAutoAccept(side, true);
            _game?.AttachOnlineSession(session);
            _onlineState = "Hangar";
        }
        catch (Exception ex) { if (ReferenceEquals(_session, session)) _onlineError = RequestError(ex); }
        finally { if (ReferenceEquals(_session, session)) { _onlineBusy = false; StateHasChanged(); } }
    }
    private async Task LaunchOnlineAsync()
    {
        if (_session is not { } session || _onlineBusy || session.World.MyShipId is not { } shipId) return;
        // This handler is a fresh user click after staging/join, so audio can be
        // activated here. Page-level DOM handlers own pointer/fullscreen gestures.
        EnableAudio();
        _onlineBusy = true; _onlineError = null;
        try
        {
            session.SendPlayerReady(true, shipId);
            await WaitOnlineAsync(session, () => session.World.Mission?.Stage == MissionStageMessage.StageStarted, waitForStart: true);
            if (!session.World.Ships.TryGetValue(shipId, out var ship)) throw new InvalidOperationException("No ship was assigned by the server.");
            long sectorGeneration = session.World.SectorGeneration;
            // Same no-purchase undock as desktop U (Game1.Panes.cs). Keep the
            // actual server-assigned hardware instead of re-buying unoffered parts.
            _game?.BeginFlight();
            session.SendDocked();
            await WaitOnlineAsync(session, () => session.World.SectorGeneration > sectorGeneration
                && session.World.Ships.TryGetValue(shipId, out var own) && own.StationId < 0);
            if (!ReferenceEquals(_session, session)) return;
            _onlineState = "Flight";
            await JsRuntime.InvokeVoidAsync("gameFlight.activate");
        }
        catch (Exception ex) { if (ReferenceEquals(_session, session)) _onlineError = RequestError(ex); }
        finally { if (ReferenceEquals(_session, session)) { _onlineBusy = false; StateHasChanged(); } }
    }
    private static string RequestError(Exception error) => error is OperationCanceledException
        ? "The server did not complete this request. Try again or return to the list." : error.Message;
    private async Task WaitOnlineAsync(ConnectionSession session, Func<bool> condition, bool waitForStart = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        // Native readiness/intermission can legitimately outlast a request timeout.
        // Keep this wait asynchronous and cancellable by leaving the owned session.
        if (!waitForStart) timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (session.Lost || session.IsDisposed) throw new IOException("The server connection was lost.");
            await Task.Delay(10, timeout.Token);
        }
    }
    private async Task CloseOnlineAsync()
    {
        if (_onlineClosing) return;
        _onlineClosing = true; ++_onlineGeneration;
        _game?.AttachOnlineSession(null);
        var session = _session; _session = null;
        try
        {
            if (session is not null) { await session.LeaveBrowserAsync(); await session.DisposeAsync(); }
        }
        finally
        {
            _online = false; _onlineState = "Login"; _onlineError = null; _onlineBusy = false; _onlineClosing = false;
        }
    }
    private void RefreshOnline()
    {
        if (!_online || Environment.TickCount64 - _lastOnlineRefresh < 200) return;
        _lastOnlineRefresh = Environment.TickCount64;
        if (_session is { } session)
        {
            if (_onlineState == "Flight" && session.World.MyShipId is { } ownId && session.World.Ships.TryGetValue(ownId, out var ownShip) && ownShip.StationId >= 0) _onlineState = "Hangar";
            if (session.Lost) _onlineError = "The server connection was lost. Return to the list to request a fresh join.";
            if (session.World.MyShipId is { } id && session.World.Ships.TryGetValue(id, out var ship))
                _pose = $"Ship {id} · sector {ship.SectorId} · position {ship.Latest?.Position} · {session.DecodedBytes:N0} bytes received";
        }
        StateHasChanged();
    }
}

// Deliberately only the public discovery fields required by this slice.
public sealed record BrowserServer(string Id, string Name, int Port, string MissionName, string CoreFile,
    bool Verified, string? IdentityServerId, string? WebSocketUrl, bool PasswordRequired = false,
    string? MinClientVersion = null, BrowserMission[]? Missions = null);
public sealed record BrowserMission(uint Cookie, string Name, bool PasswordRequired = false);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BrowserServer[]))]
internal partial class BrowserJoinJson : JsonSerializerContext;
