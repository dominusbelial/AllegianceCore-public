using System.Net;
using Microsoft.JSInterop;
using System.Net.Http.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
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
    private string? _launchGuard;
    private long _readySends;
    private long _onlineGeneration;
    private int _pendingJoins;
    private BrowserServer? _pendingServer;
    private BrowserMission? _pendingMission;
    private string? _onlineError;
    private IdentityAccount? _account;
    private bool _guest, _guestPersistent, _nameConflict, _browserEnabled;
    private string _guestName = "", _guestEdit = "";
    private string IdentityLabel => _account is { } account ? "Signed in · " + account.Callsign
        : _guest ? "Guest · " + _guestName : "Not signed in";
    private void SyncBrowserIdentity() => _game?.SetBrowserIdentity(_account, _guest ? _guestName : null);
    private async Task PlayGuestAsync()
    {
        if (!_browserEnabled || _onlineBusy) return;
        try
        {
            _guestName = await JsRuntime.InvokeAsync<string>("guestIdentity.load");
            _guestEdit = _guestName;
            _guestPersistent = await JsRuntime.InvokeAsync<bool>("guestIdentity.persistent");
            _guest = true;
            await OpenOnlineAsync();
        }
        catch (Exception error) { _onlineError = error.Message; }
    }
    private async Task SaveGuestNameAsync(bool roll = false)
    {
        if (_onlineBusy || _session is not null) return;
        try
        {
            if (!roll && !JoinTokens.ValidCallsign(_guestEdit))
            { _onlineError = "Use 1–24 printable Latin-1 characters, without outer spaces or invisible characters."; return; }
            _guestName = roll ? await JsRuntime.InvokeAsync<string>("guestIdentity.roll")
                : await JsRuntime.InvokeAsync<string>("guestIdentity.save", _guestEdit);
            _guestEdit = _guestName;
            _guestPersistent = await JsRuntime.InvokeAsync<bool>("guestIdentity.persistent");
            _onlineError = null; _nameConflict = false;
            SyncBrowserIdentity();
        }
        catch (Exception error) { _onlineError = error.Message; }
    }
    private async Task ForgetGuestAsync()
    {
        if (_onlineBusy || _session is not null) return;
        bool forgotten = await JsRuntime.InvokeAsync<bool>("guestIdentity.forget");
        _guest = false; _guestName = _guestEdit = ""; _guestPersistent = false;
        _onlineState = "Login"; _nameConflict = false;
        _onlineError = forgotten ? null : "This page forgot the guest identity, but browser storage could not be cleared. Clear site data to remove the saved name.";
        SyncBrowserIdentity();
    }
    private async Task TryAnotherNameAsync()
    {
        if (!_guest || _pendingServer is not { } server || _onlineBusy) return;
        await SaveGuestNameAsync(roll: true);
        if (_onlineError is null) await JoinServerAsync(server, _pendingMission);
    }
    private BrowserServer[] _servers = [];
    private ConnectionSession? _session;
    private string _onlineState = "Login", _onlineName = "";
    private bool _showBriefing;
    private string _pose = "Waiting for authoritative updates";
    private long _lastOnlineRefresh;
    // P1062 — the browser client's own URL contract (?online=1, ?server=): parsed
    // once, the round trip opens the online panel by itself, and the listing entry
    // the lobby's "Play in browser" link named is shown first and marked.
    private BrowserEntryState? _entry;
    private bool _entryOpened;
    private string? _preselectServerId;
    // The published generation's build identity and the game password are held only
    // in memory: never in storage, a URL, a log or the asset cache.
    private string? _clientVersion;
    private string _joinPassword = "";
    private Uri LobbyUri(string path) => new(new Uri(Http.BaseAddress!, "/"), path);

    private BrowserEntryState Entry => _entry ??= BrowserEntry.Parse(new Uri(Nav.Uri).Query);
    /// <summary>P1062 — the identity page that brings the player back here already
    /// signed in. The target is this client's own path plus its entry flags; the
    /// lobby validates it again before redirecting.</summary>
    private string SignInHref() => BrowserEntry.SignInHref(BrowserEntry.ReturnTarget(new Uri(Nav.Uri).PathAndQuery, Entry.Server));
    /// <summary>P1062 — the lobby home page or identity page sends the player back
    /// with <c>?online=1</c>; open the online panel once, by itself, so the player
    /// lands on the signed-in server list with no extra click or refresh.</summary>
    private async Task OpenEntryAsync()
    {
        if (_entryOpened || !Entry.Online) return;
        _entryOpened = true;
        await OpenOnlineAsync();
        // The lobby's Play now link already expresses the guest choice. Keep it
        // one click to the disclosed server list; the ordinary menu button still
        // presents Play now beside Sign in before generating a guest identity.
        if (_account is null && _browserEnabled && _onlineState == "Login" && _onlineError is null)
            await PlayGuestAsync();
    }

    private async Task OpenOnlineAsync()
    {
        if (_onlineBusy || _assets is null) return;
        _online = true; _onlineBusy = true; _onlineError = null; _nameConflict = false;
        try
        {
            if (!Http.BaseAddress!.IsLoopback && Http.BaseAddress.Scheme != Uri.UriSchemeHttps)
                throw new NotSupportedException("Online play needs the HTTPS lobby page.");
            using var response = await Http.GetAsync(LobbyUri("api/identity/me"), _lifetime.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) _account = null;
            else
            {
                response.EnsureSuccessStatusCode();
                _account = await response.Content.ReadFromJsonAsync(IdentityJsonContext.Default.IdentityAccount, _lifetime.Token);
            }
            await ReadBrowserOfferAsync();
            if (_account is not null) _guest = false;
            SyncBrowserIdentity();
            if (_account is null && (!_guest || !_browserEnabled)) { _onlineState = "Login"; return; }
            await EnsureClientVersionAsync(_lifetime.Token);
            var listed = await Http.GetFromJsonAsync(LobbyUri("api/servers"), BrowserJoinJson.Default.BrowserServerArray, _lifetime.Token) ?? [];
            // P1062 — the entry the lobby's "Play in browser" link named is offered
            // first and marked; other listed games stay selectable.
            _preselectServerId = Entry.Server;
            _servers = _preselectServerId is { } preselect
                ? [.. listed.OrderBy(server => string.Equals(server.Id, preselect, StringComparison.Ordinal) ? 0 : 1)]
                : listed;
            _onlineState = "Servers";
        }
        catch (Exception ex) { _onlineError = ex.Message; }
        finally { _onlineBusy = false; StateHasChanged(); }
    }
    private async Task ReadBrowserOfferAsync()
    {
        // A failed/disabled offer never enables guest play. It does not block the
        // existing signed-in ticket flow.
        _browserEnabled = false;
        try
        {
            using var offer = await Http.GetAsync(LobbyUri("api/browser"), _lifetime.Token);
            if (!offer.IsSuccessStatusCode) return;
            using var json = System.Text.Json.JsonDocument.Parse(await offer.Content.ReadAsStringAsync(_lifetime.Token));
            _browserEnabled = json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && json.RootElement.TryGetProperty("enabled", out var enabled)
                && enabled.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or NotSupportedException) { }
    }
    // The online menu uses the directory rule for the page it is served from: a
    // loopback lobby may list its own loopback route; a public page needs the strict
    // public WSS route. BrowserJoinPolicy then requires that route to be this origin.
    private bool LoopbackDirectory => Http.BaseAddress?.IsLoopback == true;
    private string? Unavailable(BrowserServer server) =>
        Http.BaseAddress is { } page ? BrowserJoinPolicy.Refusal(page, server, _clientVersion is not null, guest: _account is null) : "Online play is unavailable.";
    /// <summary>Fetch the published generation's build identity once. Absent/unreadable
    /// means "not reported"; a server that enforces a minimum client version then refuses
    /// with that reason — the server check is never bypassed.</summary>
    private async Task EnsureClientVersionAsync(CancellationToken cancellationToken)
    {
        if (_clientVersion is not null) return;
        try
        {
            using var reply = await Http.GetAsync("client-version.json", cancellationToken);
            if (!reply.IsSuccessStatusCode) return;
            var info = await reply.Content.ReadFromJsonAsync(BrowserJoinJson.Default.BrowserClientVersion, cancellationToken);
            if (!string.IsNullOrWhiteSpace(info?.Version)) _clientVersion = info.Version.Trim();
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or NotSupportedException) { }
    }
    /// <summary>The advertised lock is early warning; the server's ordinary LOGONREQ
    /// password gate stays authoritative (native FedSrv.CPP:648-661).</summary>
    private static bool RequiresPassword(BrowserServer server, BrowserMission? mission) =>
        mission?.PasswordRequired == true || server.PasswordRequired;
    private async Task EnsureBriefingDecoderAsync(StagedManifest manifest)
    {
        // A cancelled join and its replacement share preparation; neither may
        // observe a half-linked decoder or reset the other's WASM modules.
        var preparation = _briefingPreparation ??= PrepareBriefingDecoderAsync(manifest);
        try { await preparation; }
        catch
        {
            if (ReferenceEquals(_briefingPreparation, preparation)) _briefingPreparation = null;
            throw;
        }
    }
    private async Task PrepareBriefingDecoderAsync(StagedManifest manifest)
    {
        string path = await JsRuntime.InvokeAsync<string>("preview.asset", _lifetime.Token, "./brotli.js");
        var decoder = (IJSInProcessObjectReference)await JsRuntime.InvokeAsync<IJSObjectReference>("import", _lifetime.Token, path);
        try
        {
            decoder.InvokeVoid("begin");
            foreach (var module in manifest.Assets.Where(a => a.Source.StartsWith("runtime/brotli/", StringComparison.Ordinal)))
                await decoder.InvokeVoidAsync("addModule", _lifetime.Token, Allegiance.ClientCore.MonoGame.Assets.AssetSources.ReadAllBytes(_assets!, module.Source));
            decoder.InvokeVoid("finish");
            _lifetime.Token.ThrowIfCancellationRequested();
            _briefingDecoder = decoder;
        }
        catch { await decoder.DisposeAsync(); throw; }
    }
    private async Task JoinServerAsync(BrowserServer server, BrowserMission? mission)
    {
        if (_onlineBusy || _assets is null || (_account is null && !_guest) || !_servers.Contains(server) || Unavailable(server) is not null) return;
        bool passwordRequired = RequiresPassword(server, mission);
        if (passwordRequired && _joinPassword.Length == 0)
        { _onlineError = "This game needs its password."; StateHasChanged(); return; }
        long generation = ++_onlineGeneration; ++_pendingJoins;
        ConnectionSession? ownedSession = null;
        _pendingServer = server; _pendingMission = mission;
        _onlineBusy = true; _onlineError = null; _nameConflict = false;
        try
        {
            if (_platform is null) throw new InvalidOperationException("Asset storage is not ready.");
            _onlineState = "Staging";
            var stager = new AssetStager(Http, new BrowserAssetCache(_platform, _assetMemoryFallback), request => request.SetBrowserResponseStreamingEnabled(true));
            var joinManifest = _joinManifest ?? await stager.ReadManifestAsync(_lifetime.Token, "join-manifest.json");
            if (generation != _onlineGeneration) return;
            _joinManifest = joinManifest;
            _sectorManifest ??= await stager.ReadSectorManifestAsync(joinManifest, _lifetime.Token);
            if (generation != _onlineGeneration) return;
            _battleManifest ??= await stager.ReadBattleManifestAsync(joinManifest, _lifetime.Token);
            if (generation != _onlineGeneration) return;
            stager.RetainedHashes = _sectorManifest.RetainedHashes(joinManifest).Concat(_battleManifest.RetainedHashes()).Distinct(StringComparer.Ordinal).ToArray();
            if (!_joinStaged)
            {
                var pack = await stager.StageAsync(joinManifest, (percent, status) =>
                { _progress = percent; _status = status; StateHasChanged(); }, _lifetime.Token);
                if (generation != _onlineGeneration) return;
                _assets.Merge(pack);
                _joinStaged = true;
            }
            if (generation != _onlineGeneration) return;
            StartSectorStreaming(joinManifest);
            await EnsureBriefingDecoderAsync(joinManifest);
            if (generation != _onlineGeneration) return;
            _game?.AttachOnlineSession(null);
            if (_session is not null) await _session.DisposeAsync();
            if (generation != _onlineGeneration) return;
            var core = StaticCore.Load(IgcReader.Parse(Allegiance.ClientCore.MonoGame.Assets.AssetSources.ReadAllBytes(_assets, "PCore001.igc")));
            var session = ownedSession = _session = new ConnectionSession { WebSocketEndpoint = WebSocketEndpoint.FromLoopbackDirectoryListing(server.Route(LoopbackDirectory)!) };
            // P894/P1061 — the resume logon reports the same build identity as the first one.
            session.ClientVersion = _clientVersion;
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
            string callsign = _account?.Callsign ?? _guestName;
            var refusal = await session.JoinAsync((ushort)server.Port, callsign,
                new LogonRequest { CharacterName = callsign, FedsrvVer = LogonRequest.MsgVer,
                    CrcFileList = -1, DwCookie = mission?.Cookie ?? uint.MaxValue, Time = 1,
                    ClientVersion = _clientVersion,
                    MissionPassword = passwordRequired ? _joinPassword : null },
                identityTicket: _account is null ? null : async ct =>
                {
                    using var reply = await Http.PostAsJsonAsync(LobbyUri("api/identity/join-token"),
                        new JoinTokenRequest(server.IdentityServerId!), IdentityJsonContext.Default.JoinTokenRequest, ct);
                    reply.EnsureSuccessStatusCode();
                    return (await reply.Content.ReadFromJsonAsync(IdentityJsonContext.Default.JoinTokenResponse, ct))!.Token;
                }, cancellationToken: _lifetime.Token);
            if (generation != _onlineGeneration) throw new OperationCanceledException("The join was retired.");
            if (refusal is not null) throw new InvalidOperationException(BrowserJoinFailure.From("The server refused this mission join: " + refusal.Reason, refusal.Reason).Message);
            if (session.World.JoinError is { } error) throw new InvalidOperationException(error);
            _onlineName = mission?.Name ?? server.MissionName;
            _onlineState = "Sides";
            _joinPassword = "";
        }
        catch (Exception ex)
        {
            if (generation == _onlineGeneration)
            {
                var failure = BrowserJoinFailure.From(ex.Message);
                _onlineError = failure.Message; _nameConflict = _account is null && failure.NameConflict;
                // A refusal from the server (password, version, mission) is answered on
                // the join screen, so the player can correct the password and retry.
                _onlineState = "Servers";
                if (ReferenceEquals(_session, ownedSession)) _session = null;
            }
            if (ownedSession is not null) { await ownedSession.LeaveBrowserAsync(); await ownedSession.DisposeAsync(); }
        }
        finally { --_pendingJoins; if (generation == _onlineGeneration) { _onlineBusy = false; StateHasChanged(); } }
    }
    private void ToggleContactView() => _game?.ToggleContactView();
    // P1067 — the parked director camera (desktop F2 / "Watch").
    private void ToggleWatch() => _game?.ToggleWatch();
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
        // Never silent, never stuck: every outcome a click can hit is visible.
        // The button is disabled in exactly these states (LaunchAffordance), so
        // this path only answers a click that raced a state change.
        if (_onlineBusy)
        {
            _launchGuard = "busy";
            _onlineError = "The server is still completing the previous launch request.";
            StateHasChanged();
            return;
        }
        var readiness = HangarLaunch.Of(_session);
        if (!readiness.CanLaunch)
        {
            _launchGuard = readiness.Blocker.ToString();
            _onlineError = LaunchBlockerText(readiness.Blocker);
            StateHasChanged();
            return;
        }
        var session = _session!;
        short shipId = readiness.ShipId!.Value;
        _launchGuard = "sent";
        // This handler is a fresh user click after staging/join, so audio can be
        // activated here. Page-level DOM handlers own pointer/fullscreen gestures.
        EnableAudio();
        _onlineBusy = true; _onlineError = null;
        try
        {
            session.SendPlayerReady(true, shipId); ++_readySends;
            await WaitOnlineForStartAsync(session);
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
    /// <summary>P1070 — the game window's hangar asked for the launch the page owns. The
    /// same <see cref="LaunchOnlineAsync"/> runs (ready → <c>STAGE_STARTED</c> →
    /// <c>DOCKED</c>, <c>Menu.Online.cs</c> above); the hangar is then told whether it
    /// happened, with the same blocker/refusal text the HTML control would have shown,
    /// so the game window's status line never disagrees with the page.</summary>
    private async Task LaunchFromGameWindowAsync()
    {
        try { await LaunchOnlineAsync(); }
        catch (Exception ex) { _onlineError = RequestError(ex); }
        bool launched = !_onlineBusy && _onlineState == "Flight";
        _game?.HangarLaunchFinished(launched, _onlineError ?? (launched ? null : LaunchBlockerText(HangarLaunch.Of(_session).Blocker)));
    }
    /// <summary>Longest silent stretch a ready click accepts without the mission
    /// stage or its countdown moving; every observed change re-arms it.</summary>
    private const long LaunchWaitMilliseconds = 60000;
    private static string LaunchBlockerText(HangarLaunch.Blocker blocker) => HangarLaunchText.For(blocker);
    /// <summary>The hangar button's live state and the reason it is disabled —
    /// one shared decision for Classic and Modern (they differ only in chrome).</summary>
    private (bool Enabled, string? Reason) LaunchAffordance()
    {
        var readiness = HangarLaunch.Of(_session);
        return (readiness.CanLaunch && !_onlineBusy, readiness.CanLaunch ? null : LaunchBlockerText(readiness.Blocker));
    }
    /// <summary>The busy line: the countdown while the mission starts, else what
    /// the click is actually waiting for.</summary>
    private string BusyStatus()
    {
        if (_onlineState == "Hangar")
        {
            if (_session?.World.CountdownRemainingSeconds() is { } seconds) return $"Next match starts in {seconds} seconds…";
            if (_session?.World.Mission?.Stage != MissionStageMessage.StageStarted)
                return "Ready. Waiting for the server to start the mission…";
        }
        return "Waiting for the server…";
    }
    /// <summary>Native keeps a ready-up click pending until the mission reaches
    /// STAGE_STARTED — the server ignores DOCKED before that (FedSrv.CPP:7031-7034)
    /// — so the wait is legitimate, but it must report rather than hang forever.</summary>
    private async Task WaitOnlineForStartAsync(ConnectionSession session)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        long deadline = Environment.TickCount64 + LaunchWaitMilliseconds;
        int? lastSeconds = session.World.CountdownRemainingSeconds();
        while (session.World.Mission?.Stage != MissionStageMessage.StageStarted)
        {
            if (session.Lost || session.IsDisposed) throw new IOException("The server connection was lost.");
            if (!ReferenceEquals(_session, session)) throw new OperationCanceledException("The join was retired.");
            int? seconds = session.World.CountdownRemainingSeconds();
            if (seconds != lastSeconds)
            {
                lastSeconds = seconds;
                deadline = Environment.TickCount64 + LaunchWaitMilliseconds;
            }
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("The server has not started the mission yet. Ready again to retry.");
            await Task.Delay(50, timeout.Token);
        }
    }
    private static string RequestError(Exception error) => error is OperationCanceledException
        ? "The server did not complete this request. Try again or return to the list." : error.Message;
    private async Task WaitOnlineAsync(ConnectionSession session, Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        // The dock acknowledgement keeps its existing bounded wait; the
        // readiness wait above owns the long, reported wait for the start.
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
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
        await StopSectorStreamingAsync();
        var session = _session; _session = null;
        try
        {
            if (session is not null) { await session.LeaveBrowserAsync(); await session.DisposeAsync(); }
        }
        finally
        {
            _online = false; _onlineState = "Login"; _onlineError = null; _onlineBusy = false; _onlineClosing = false;
            _joinPassword = ""; _nameConflict = false;
        }
    }
    private void RefreshOnline()
    {
        if (!_online || Environment.TickCount64 - _lastOnlineRefresh < 200) return;
        _lastOnlineRefresh = Environment.TickCount64;
        RefreshSectorStreaming();
        // P1070 — the hangar in the game window asked for the launch this page owns
        // (ready -> STAGE_STARTED -> DOCKED). One request per click.
        if (_game?.TakeHangarLaunchRequest() == true) _ = LaunchFromGameWindowAsync();
        if (_session is { } session)
        {
            if (_onlineState == "Flight" && session.World.MyShipId is { } ownId && session.World.Ships.TryGetValue(ownId, out var ownShip) && ownShip.StationId >= 0) _onlineState = "Hangar";
            // P1070 — the hangar launched from the game window with the desktop's own
            // BUY_LOADOUT(fLaunch) (the server's SET_CLUSTER undocks); the toolbar and
            // the flight input follow the same state change the HTML button caused.
            if (_onlineState is "Hangar" or "Sides" && session.World.MyShipId is { } flyingId
                && session.World.Ships.TryGetValue(flyingId, out var flying) && flying.StationId < 0 && flying.Latest is not null)
            {
                _onlineState = "Flight";
                _ = JsRuntime.InvokeVoidAsync("gameFlight.activate");
            }
            if (session.Lost) _onlineError = "The server connection was lost. Return to the list to request a fresh join.";
            if (session.World.MyShipId is { } id && session.World.Ships.TryGetValue(id, out var ship))
                _pose = $"Ship {id} · sector {ship.SectorId} · position {ship.Latest?.Position} · {session.DecodedBytes:N0} bytes received";
        }
        StateHasChanged();
    }
}
