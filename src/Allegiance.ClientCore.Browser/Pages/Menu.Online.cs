using System.Net;
using System.Net.Http.Json;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Browser.Pages;

public partial class Menu
{
    private bool _online, _onlineBusy;
    private string? _onlineError;
    private IdentityAccount? _account;
    private BrowserServer[] _servers = [];
    private ConnectionSession? _session;
    private string _onlineState = "Login", _onlineName = "";
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
        _onlineBusy = true; _onlineError = null;
        try
        {
            if (_session is not null) await _session.DisposeAsync();
            var core = StaticCore.Load(IgcReader.Parse(Allegiance.ClientCore.MonoGame.Assets.AssetSources.ReadAllBytes(_assets, "PCore001.igc")));
            var session = _session = new ConnectionSession { WebSocketEndpoint = WebSocketEndpoint.FromServerListing(server.WebSocketUrl!) };
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
            if (refusal is not null) throw new InvalidOperationException("The server refused this mission join.");
            if (session.World.JoinError is { } error) throw new InvalidOperationException(error);
            _onlineName = mission?.Name ?? server.MissionName;
            _onlineState = "Sides";
        }
        catch (Exception ex)
        {
            _onlineError = ex.Message;
            if (_session is not null) { await _session.DisposeAsync(); _session = null; }
        }
        finally { _onlineBusy = false; StateHasChanged(); }
    }
    private async Task SelectSideAsync(short side)
    {
        if (_session is not { } session || _onlineBusy || session.World.SideRoster?.Sides.Any(s => s.SideId == side) != true) return;
        _onlineBusy = true; _onlineError = null;
        try
        {
            session.SendPositionReq(side);
            await WaitOnlineAsync(() => session.World.MySideId == side && session.World.MyShipId is not null);
            _onlineState = "Hangar";
        }
        catch (Exception ex) { _onlineError = ex.Message; }
        finally { _onlineBusy = false; StateHasChanged(); }
    }
    private async Task LaunchOnlineAsync()
    {
        if (_session is not { } session || _onlineBusy || session.World.MyShipId is not { } shipId) return;
        // This handler is a fresh user click after staging/join, so audio can be
        // activated here. Pointer capture and fullscreen remain explicit unsupported paths.
        EnableAudio();
        _onlineBusy = true; _onlineError = null;
        try
        {
            session.SendPlayerReady(true, shipId);
            await WaitOnlineAsync(() => session.World.Mission?.Stage == MissionStageMessage.StageStarted);
            if (!session.World.Ships.TryGetValue(shipId, out var ship)) throw new InvalidOperationException("No ship was assigned by the server.");
            long sectorGeneration = session.World.SectorGeneration;
            session.ResetBuyLoadoutAcks();
            session.SendBuyLoadout(ship.HullId, true);
            await WaitOnlineAsync(() => session.BuyLoadoutAcks.Any(ack => ack.FLaunch)
                && session.World.SectorGeneration > sectorGeneration
                && session.World.Ships.TryGetValue(shipId, out var own) && own.StationId < 0);
            _onlineState = "Flight";
        }
        catch (Exception ex) { _onlineError = ex.Message; }
        finally { _onlineBusy = false; StateHasChanged(); }
    }
    private async Task WaitOnlineAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (_session?.Lost != false) throw new IOException("The server connection was lost.");
            await Task.Delay(10, timeout.Token);
        }
    }
    private async Task CloseOnlineAsync()
    {
        if (_session is not null) { await _session.DisposeAsync(); _session = null; }
        _online = false; _onlineState = "Login"; _onlineError = null; _onlineBusy = false;
    }
    private void RefreshOnline()
    {
        if (!_online || Environment.TickCount64 - _lastOnlineRefresh < 200) return;
        _lastOnlineRefresh = Environment.TickCount64;
        if (_session is { } session)
        {
            if (session.Lost) _onlineError = "The server connection was lost. Return to the list to request a fresh join.";
            if (session.World.MyShipId is { } id && session.World.Ships.TryGetValue(id, out var ship))
                _pose = $"Ship {id} · sector {ship.SectorId} · position {ship.Latest?.Position} · {session.DecodedBytes:N0} bytes received";
        }
        StateHasChanged();
    }
}
