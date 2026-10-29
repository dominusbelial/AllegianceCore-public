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
    public const int MaxRequests = 128;
    public const int MaxNameLength = 32;
    private readonly object _gate = new();
    private readonly List<GameCreationRequest> _requests = new();
    private readonly TimeProvider _time;
    private long _nextId;

    public GameCreationStore(TimeProvider time) => _time = time;

    public enum State { Queued, Claimed, Cancelled }

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
        DateTimeOffset UpdatedUtc);

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
            if (_requests.Count(r => r.RequestState != State.Cancelled) >= MaxRequests)
            {
                error = "too many game requests";
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

    /// <summary>Owner-only parameter editing (native keeps the mission owner
    /// editing the advertised params through MISSIONPARAMS).</summary>
    public GameCreationRequest? UpdateParams(string accountId, string id, MissionParamsMsg parameters,
        out string? error)
    {
        error = null;
        lock (_gate)
        {
            int index = _requests.FindIndex(r => r.Id == id);
            if (index < 0 || _requests[index].RequestState == State.Cancelled) { error = "unknown request"; return null; }
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

    /// <summary>Owner-only cancel (native's create-game UI can abandon a request).</summary>
    public bool Cancel(string accountId, string id)
    {
        lock (_gate)
        {
            int index = _requests.FindIndex(r => r.Id == id);
            if (index < 0 || _requests[index].OwnerAccountId != accountId) return false;
            _requests[index] = _requests[index] with { RequestState = State.Cancelled, UpdatedUtc = _time.GetUtcNow() };
            return true;
        }
    }

    /// <summary>The relayed requests a server may claim: queued requests for that
    /// listing (its own registration token is the server's proof).</summary>
    public IReadOnlyList<GameCreationRequest> QueuedFor(string serverId)
    {
        lock (_gate)
            return _requests.Where(r => r.RequestState == State.Queued && r.ServerId == serverId).ToArray();
    }

    /// <summary>Mark a request claimed by its game server.</summary>
    public GameCreationRequest? Claim(string serverId, string id)
    {
        lock (_gate)
        {
            int index = _requests.FindIndex(r => r.Id == id && r.ServerId == serverId);
            if (index < 0 || _requests[index].RequestState != State.Queued) return null;
            var claimed = _requests[index] with { RequestState = State.Claimed, UpdatedUtc = _time.GetUtcNow() };
            _requests[index] = claimed;
            return claimed;
        }
    }

    /// <summary>Relay feedback: every non-cancelled request, newest last.</summary>
    public IReadOnlyList<GameCreationRequest> All()
    {
        lock (_gate) return _requests.Where(r => r.RequestState != State.Cancelled).ToArray();
    }
}
