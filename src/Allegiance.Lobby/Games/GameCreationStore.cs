using Allegiance.Core.Wire;

namespace Allegiance.Lobby.Games;

/// <summary>
/// P871 / lobby::Create game — the in-memory new-game wizard store: the port of
/// native's <c>CREATE_MISSION_REQ</c> → <c>CREATE_MISSION_ACK</c> flow
/// (<c>lobby/messageslc.h:54-66</c>, <c>lobby/client.cpp:390-465</c>).
///
/// <para><b>Native shape.</b> The client sends the lobby a request naming the
/// server, its address, the IGC static file (core) and the game name; the lobby
/// finds a matching, unpaused server, records the mission and acknowledges with
/// a cookie — or NACKs when no server can host it. Native then relays the game
/// name and core to the game server, whose mission becomes joinable once it
/// publishes its lobby mission info.</para>
///
/// <para><b>Port shape.</b> The HTTP directory plays the lobby role. It
/// authenticates the request against the caller's lobby identity (native's
/// creator is the lobby-session client), validates the server/core/map selection
/// against the registered server and the configured supported catalogs, and
/// stores a request with an ACK id. The owner may keep editing the mission
/// parameters through the SAME authoritative <see cref="MissionParamsMsg.Invalid"/>
/// validation the game server uses (native's <c>NewGameScreen::OnButtonCreate</c>
/// ran the identical client-side check, <c>WinTrek/newgamescreen.cpp:1034-1041</c>).
/// The selected game server picks the request up from the relay feedback
/// (<c>GET /api/games</c>) and claims it with its registration token; params
/// travel to the mission through the existing MISSIONPARAMS path.</para>
/// </summary>
public sealed class GameCreationStore
{
    /// <summary>The directory-wide cap on live requests (native kept one cookie-space
    /// mission list per lobby; this port keeps the relay feedback bounded).</summary>
    public const int MaxRequests = 128;
    public const int MaxNameLength = 32;

    /// <summary>
    /// P1024 (R1 F10) — the small per-account cap on requests still awaiting an
    /// action (queued for a server, or claimed by one). Native's UI let a lobby
    /// client create one game at a time through CREATE_MISSION_REQ; four
    /// in-flight requests leave room for a player re-creating a game after a
    /// mistake without letting one account fill the directory. Terminal rows
    /// (a ready or failed game) do not count: they are showcase feedback, not an
    /// outstanding request.
    /// </summary>
    public const int MaxLiveRequestsPerAccount = 4;

    /// <summary>P1024 (R1 F10) — a queued request nobody claimed within ten
    /// minutes is stale: the wizard's create step is a few seconds of UI, and the
    /// native relay fired the CREATE_MISSION_REQ at once (<c>lobby/client.cpp:445-459</c>).
    /// Native's own gap here is explicit — “TODO: check up on this mission later to
    /// make sure that we got a lobbymissioninfo from the server”
    /// (<c>lobby/client.cpp:463</c>).</summary>
    public static readonly TimeSpan QueuedMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>P1024 (R1 F10) — a claimed request whose mission never reported
    /// ready within five minutes is dropped: native's mission became joinable on
    /// its LOBBYMISSIONINFO (<c>lobby/server.cpp:169-195</c>), which never arrived.</summary>
    public static readonly TimeSpan ClaimedMaxAge = TimeSpan.FromMinutes(5);

    /// <summary>P1024 (R1 F10) — a finished request (ready or failed) stays in the
    /// relay feedback for one hour, then expires. Native forgot a mission when the
    /// server reported MISSION_GONE (<c>lobby/server.cpp:197-205</c>,
    /// <c>lobby/mission.cpp:34-45</c>), not on a timer; the port has no mission-gone
    /// event, so a generous age bound stands in for it.</summary>
    public static readonly TimeSpan FinishedMaxAge = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly List<GameCreationRequest> _requests = new();
    private readonly TimeProvider _time;
    private readonly ServerRegistry? _registry;
    private long _nextId;

    /// <param name="time">Injected clock, so expiry is deterministic in tests.</param>
    /// <param name="registry">P1024 (R1 F10) — the directory. A request whose listing
    /// has disappeared (expired, deregistered) is dropped: native deleted the mission
    /// when it went away and never advertised a mission for a disconnected server
    /// (<c>lobby/server.cpp:197-205</c>). Null in store-only tests = no listing check.</param>
    public GameCreationStore(TimeProvider time, ServerRegistry? registry = null)
    {
        _time = time;
        _registry = registry;
    }

    public enum State { Queued, Claimed, Ready, Failed, Cancelled }

    public sealed record GameCreationRequest(
        string Id,
        string OwnerAccountId,
        string OwnerCallsign,
        string ServerId,
        string ServerName,
        string Address,
        int Port,
        string Core,
        string Map,
        string GameName,
        MissionParamsMsg Params,
        State RequestState,
        DateTimeOffset CreatedUtc,
        DateTimeOffset UpdatedUtc, uint? Cookie = null);

    /// <summary>The catalogs the lobby advertises as authoritative: the cores and
    /// maps this release supports. Registered servers are filtered against them
    /// when the lists are non-empty.</summary>
    public sealed record Catalog(IReadOnlyList<string> Cores, IReadOnlyList<string> Maps);

    /// <summary>Create a request. Returns the stored request, or null with the
    /// validation reason.</summary>
    public GameCreationRequest? Create(string accountId, string callsign, ServerListingDto server,
        string core, string map, string gameName, MissionParamsMsg? parameters, Catalog catalog,
        out string? error)
    {
        error = null;
        string name = (gameName ?? "").Trim();
        if (name.Length == 0) { error = "a game name is required"; return null; }
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        if (!string.Equals(core?.Trim(), server.CoreFile, StringComparison.OrdinalIgnoreCase))
        {
            error = "the selected core is not the one this server runs";
            return null;
        }
        if (catalog.Cores.Count > 0 && !catalog.Cores.Any(c => string.Equals(c, core?.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            error = "unsupported core";
            return null;
        }
        string chosenMap = (map ?? "").Trim();
        if (chosenMap.Length == 0) { error = "a map is required"; return null; }
        if (catalog.Maps.Count > 0 && !catalog.Maps.Any(m => string.Equals(m, chosenMap, StringComparison.OrdinalIgnoreCase)))
        {
            error = "unsupported map";
            return null;
        }
        var parametersOut = parameters?.Clone() ?? new MissionParamsMsg();
        // Native replay of NewGameScreen::OnButtonCreate (newgamescreen.cpp:1034-1041):
        // Invalid(true) runs BEFORE the request is sent.
        if (parametersOut.Invalid(bFromClient: true) is { } why) { error = why; return null; }
        parametersOut.StrGameName = name;
        parametersOut.SzIgcStaticFile = core!.Trim();

        lock (_gate)
        {
            Prune();
            if (_requests.Count >= MaxRequests)
            {
                error = "too many game requests";
                return null;
            }
            // P1024 (R1 F10) — one account may hold only a few in-flight requests.
            if (_requests.Count(r => r.OwnerAccountId == accountId && InFlight(r)) >= MaxLiveRequestsPerAccount)
            {
                error = "too many live game requests for this account";
                return null;
            }
            var now = _time.GetUtcNow();
            var request = new GameCreationRequest($"game{++_nextId}", accountId, callsign,
                server.Id, server.Name, server.Address, server.Port, core.Trim(), chosenMap, name,
                parametersOut, State.Queued, now, now);
            _requests.Add(request);
            return request;
        }
    }

    private static bool InFlight(GameCreationRequest r) => r.RequestState is State.Queued or State.Claimed;

    private static TimeSpan MaxAgeOf(State state) => state switch
    {
        State.Queued => QueuedMaxAge,
        State.Claimed => ClaimedMaxAge,
        _ => FinishedMaxAge,
    };

    /// <summary>
    /// P1024 (R1 F10) — drop every request that can no longer be acted on:
    /// cancelled rows, requests older than their state's age, and requests whose
    /// server listing has disappeared from the directory. Runs under the lock on
    /// every Create and on the relay-feedback reads, so a stale row can never count
    /// against <see cref="MaxRequests"/> for long (the list is capped at that size
    /// anyway, so memory stays bounded even when nobody reads it).
    /// </summary>
    private void Prune()
    {
        var now = _time.GetUtcNow();
        _requests.RemoveAll(r => r.RequestState == State.Cancelled
            || now - r.UpdatedUtc > MaxAgeOf(r.RequestState)
            || (_registry is not null && _registry.Get(r.ServerId) is null));
    }

    /// <summary>Owner-only parameter editing (native keeps the mission owner
    /// editing the advertised params through MISSIONPARAMS).</summary>
    public GameCreationRequest? UpdateParams(string accountId, string id, MissionParamsMsg parameters,
        out string? error)
    {
        error = null;
        lock (_gate)
        {
            Prune();
            int index = _requests.FindIndex(r => r.Id == id);
            if (index < 0) { error = "unknown request"; return null; }
            var current = _requests[index];
            if (current.OwnerAccountId != accountId) { error = "only the game's creator may edit it"; return null; }
            var edited = parameters.Clone();
            if (edited.Invalid(bFromClient: true) is { } why) { error = why; return null; }
            edited.StrGameName = current.GameName;
            edited.SzIgcStaticFile = current.Core;
            var updated = current with { Params = edited, UpdatedUtc = _time.GetUtcNow() };
            _requests[index] = updated;
            return updated;
        }
    }

    /// <summary>Owner-only cancel (native's create-game UI can abandon a request).
    /// P1024 (R1 F10) — the row is removed outright: a cancelled request is not
    /// feedback and must not hold a slot in the directory cap.</summary>
    public bool Cancel(string accountId, string id)
    {
        lock (_gate)
        {
            Prune();
            int index = _requests.FindIndex(r => r.Id == id);
            if (index < 0 || _requests[index].OwnerAccountId != accountId) return false;
            _requests.RemoveAt(index);
            return true;
        }
    }

    /// <summary>The relayed requests a server may claim: queued requests for that
    /// listing (its own registration token is the server's proof).</summary>
    public IReadOnlyList<GameCreationRequest> QueuedFor(string serverId)
    {
        lock (_gate)
        {
            Prune();
            return _requests.Where(r => r.RequestState == State.Queued && r.ServerId == serverId).ToArray();
        }
    }

    /// <summary>Mark a request claimed by its game server.</summary>
    public GameCreationRequest? Claim(string serverId, string id)
    {
        lock (_gate)
        {
            Prune();
            int index = _requests.FindIndex(r => r.Id == id && r.ServerId == serverId);
            if (index < 0 || _requests[index].RequestState != State.Queued) return null;
            var claimed = _requests[index] with { RequestState = State.Claimed, UpdatedUtc = _time.GetUtcNow() };
            _requests[index] = claimed;
            return claimed;
        }
    }

    public GameCreationRequest? Complete(string serverId, string id, uint? cookie)
    {
        lock (_gate)
        {
            Prune();
            int index = _requests.FindIndex(r => r.Id == id && r.ServerId == serverId);
            if (index < 0) return null;
            var current = _requests[index];
            if (current.RequestState == State.Ready || current.RequestState == State.Failed) return current;
            if (current.RequestState != State.Claimed || cookie == uint.MaxValue) return null;
            return _requests[index] = current with { Cookie = cookie,
                RequestState = cookie is null ? State.Failed : State.Ready, UpdatedUtc = _time.GetUtcNow() };
        }
    }

    /// <summary>Relay feedback: every non-cancelled request, newest last.</summary>
    public IReadOnlyList<GameCreationRequest> All()
    {
        lock (_gate)
        {
            Prune();
            return _requests.Where(r => r.RequestState != State.Cancelled).ToArray();
        }
    }
}
