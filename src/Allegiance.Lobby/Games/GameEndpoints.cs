using Allegiance.Core.Wire;
using Allegiance.Lobby.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Allegiance.Lobby.Games;

/// <summary>
/// P871 / lobby::Create game — the new-game wizard's HTTP surface: the native
/// <c>CREATE_MISSION_REQ</c>/<c>ACK</c>/<c>NACK</c> protocol
/// (<c>lobby/messageslc.h:54-66</c>, <c>lobby/client.cpp:390-465</c>) plus the
/// authoritative server/core/map catalog the client's selection UI reads.
/// </summary>
public static class GameEndpoints
{
    // P871b — the mission parameters cross this HTTP boundary as the NATIVE WIRE
    // STRUCT, base64'd, never as JSON: `MissionParams` is a 252-byte packed
    // struct of public FIELDS (Igc/igc.h:1122-1334) whose layout the fedmsg codec
    // already owns, and P871's answer — a global `IncludeFields = true` on the
    // lobby's serializer — changed how every OTHER endpoint (identity, career,
    // squad, events) emitted its JSON. Native's CREATE_MISSION_REQ carried the
    // same struct on the wire (lobby/messageslc.h:54-59), so this is also the
    // faithful shape: base64 of `MissionParamsMsg.Encode()`.
    public sealed record CreateGameRequest(string? ServerId, string? Core, string? Map,
        string? GameName, string? ParamsWire);
    public sealed record EditParamsRequest(string? ParamsWire);
    public sealed record ClaimRequest(string? ServerId, string? Token);
    public sealed record CatalogDto(IReadOnlyList<ServerChoice> Servers, IReadOnlyList<string> Cores,
        IReadOnlyList<string> Maps);
    public sealed record ServerChoice(string Id, string Name, string Address, int Port,
        string Core, string MissionName);
    public sealed record GameDto(string Id, string Owner, string ServerId, string ServerName,
        string Address, int Port, string Core, string Map, string GameName,
        string ParamsWire, string State, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

    public static void MapGames(this WebApplication app)
    {
        var group = app.MapGroup("/api/games");

        // The authoritative catalogs the wizard renders: the joinable servers,
        // plus the release's supported cores and the lobby's map catalog.
        group.MapGet("/catalog", (ServerRegistry registry, LobbySettings settings) =>
        {
            var servers = registry.List()
                .Select(s => new ServerChoice(s.Id, s.Name, s.Address, s.Port, s.CoreFile, s.MissionName))
                .ToArray();
            var cores = settings.GameCores.Count > 0
                ? settings.GameCores.ToArray()
                : servers.Select(s => s.Core).Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var maps = settings.GameMaps.ToArray();
            return Results.Ok(new CatalogDto(servers, cores, maps));
        });

        // Relay feedback: every live request (the creator's own included).
        group.MapGet("", (GameCreationStore games, IdentityStore identities, HttpContext ctx) =>
        {
            var account = identities.Authenticate(IdentityEndpoints.SessionSecret(ctx));
            return Results.Ok(games.All().Select(g => ToDto(g, account?.Id)).ToArray());
        });

        // CREATE_MISSION_REQ. Authenticated: native's creator was a lobby-session
        // client and the request is bound to that identity.
        group.MapPost("", (CreateGameRequest req, GameCreationStore games, ServerRegistry registry,
            LobbySettings settings, IdentityStore identities, HttpContext ctx) =>
        {
            if (identities.Authenticate(IdentityEndpoints.SessionSecret(ctx)) is not { } account)
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.ServerId) || registry.Get(req.ServerId!) is not { } server)
                return Results.NotFound(new { error = "that server is not listed" });
            var catalog = new GameCreationStore.Catalog(settings.GameCores.ToArray(), settings.GameMaps.ToArray());
            if (!TryDecodeParams(req.ParamsWire, out var parameters, out var wireError))
                return Results.BadRequest(new { error = wireError });
            var created = games.Create(account.Id, account.Callsign, server, req.Core ?? "",
                req.Map ?? "", req.GameName ?? "", parameters, catalog, out var error);
            return created is null
                ? Results.BadRequest(new { error })
                : Results.Ok(ToDto(created, account.Id));
        }).RequireRateLimiting(RateLimiting.ChatPolicy);

        // Owner-only parameter editing (native kept editing the advertised params
        // through MISSIONPARAMS after the mission existed).
        group.MapPost("/{id}/params", (string id, EditParamsRequest req, GameCreationStore games,
            IdentityStore identities, HttpContext ctx) =>
        {
            if (identities.Authenticate(IdentityEndpoints.SessionSecret(ctx)) is not { } account)
                return Results.Unauthorized();
            if (!TryDecodeParams(req.ParamsWire, out var parameters, out var wireError))
                return Results.BadRequest(new { error = wireError });
            var updated = games.UpdateParams(account.Id, id, parameters, out var error);
            return updated is null ? Results.BadRequest(new { error }) : Results.Ok(ToDto(updated, account.Id));
        }).RequireRateLimiting(RateLimiting.ChatPolicy);

        // Owner-only cancel.
        group.MapPost("/{id}/cancel", (string id, GameCreationStore games, IdentityStore identities,
            HttpContext ctx) =>
        {
            if (identities.Authenticate(IdentityEndpoints.SessionSecret(ctx)) is not { } account)
                return Results.Unauthorized();
            return games.Cancel(account.Id, id) ? Results.NoContent() : Results.NotFound();
        }).RequireRateLimiting(RateLimiting.ChatPolicy);

        // Server-side claim: the selected game server proves ownership with its
        // registration token and receives the request (CREATE_MISSION_REQ forwarded,
        // lobby/client.cpp:445-455).
        group.MapPost("/{id}/claim", (string id, ClaimRequest req, GameCreationStore games,
            ServerRegistry registry) =>
        {
            if (string.IsNullOrWhiteSpace(req.ServerId) || string.IsNullOrWhiteSpace(req.Token)
                || !registry.VerifyToken(req.ServerId!, req.Token!))
                return Results.Unauthorized();
            var claimed = games.Claim(req.ServerId!, id);
            return claimed is null ? Results.NotFound() : Results.Ok(ToDto(claimed, null));
        });
    }

    private static GameDto ToDto(GameCreationStore.GameCreationRequest r, string? readerAccountId) =>
        new(r.Id, r.OwnerCallsign, r.ServerId, r.ServerName, r.Address, r.Port, r.Core, r.Map,
            r.GameName, Convert.ToBase64String(r.Params.Encode()), r.RequestState.ToString(),
            r.CreatedUtc, r.UpdatedUtc);

    /// <summary>Decode the native <c>MissionParams</c> blob a create/edit request
    /// carries. A missing blob means "native defaults" for create (native's
    /// NewGameScreen always sent one, but the store fills defaults) and is
    /// refused for an edit, where there is nothing to change.</summary>
    private static bool TryDecodeParams(string? wire, out MissionParamsMsg parameters, out string? error)
    {
        parameters = new MissionParamsMsg();
        error = null;
        if (string.IsNullOrWhiteSpace(wire)) return true;
        try
        {
            parameters = MissionParamsMsg.Decode(Convert.FromBase64String(wire));
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            error = "the mission parameters are not a native MissionParams struct";
            return false;
        }
    }
}
