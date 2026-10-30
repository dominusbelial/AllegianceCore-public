using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Allegiance.Lobby;

/// <summary>
/// Maps the server-directory HTTP surface onto the <see cref="ServerRegistry"/>.
/// </summary>
public static class LobbyEndpoints
{
    /// <summary>Routes an <see cref="WebApplication"/>'s lobby endpoints. The
    /// settings normally come from DI (Program registers the bound instance);
    /// the parameter stays for the proof tool's explicit call.</summary>
    public static void MapLobbyEndpoints(this WebApplication app, LobbySettings? settings = null)
    {
        settings ??= app.Services.GetRequiredService<LobbySettings>();
        // Fresh registration (no token) or idempotent heartbeat/update (valid token).
        // Rate-limited: this is the single write path a public registry must protect.
        app.MapPost("/api/servers/register", (RegisterRequest req, ServerRegistry registry, Identity.IdentityStore identities, LobbySettings cfg, HttpContext context) =>
        {
            // The socket source (or a configured trusted proxy's source) owns
            // the listing. A caller cannot redirect clients to a victim IP/DNS.
            if (context.Connection.RemoteIpAddress is not { } source) return Results.BadRequest("source address required");
            req.Address = source.IsIPv4MappedToIPv6 ? source.MapToIPv4().ToString() : source.ToString();

            // P894 / LOB-0005 — native's approved-server filter
            // (IsServerAllowed, lobby/server.cpp:24-84; loopback always allowed
            // at :131, refusal reason at :138). Applied BEFORE anything is stored,
            // exactly like native's logon path.
            if (!ServerAllowList.IsAllowed(req.Address, cfg.AllowedServers, cfg.ServerFilterMode))
            {
                Console.WriteLine($"[lobby] registration refused: {req.Address} is not on the approved-server list ({cfg.ServerFilterMode})");
                return Results.Text(ServerAllowList.RejectionReason, statusCode: StatusCodes.Status403Forbidden);
            }

            // P894 / LOB-0004 — native rejects a server whose lobby protocol
            // version is not this lobby's own (pfmLogon->verLobby != LOBBYVER_LS,
            // lobby/server.cpp:93,141-151) with a tailored NACK reason. A host
            // that omits the field is a pre-P894 build: accepted.
            if (!Allegiance.Core.LobbyProtocol.IsAccepted(req.LobbyProtocolVersion, cfg.LobbyProtocolVersion))
            {
                string reason = Allegiance.Core.LobbyProtocol.RejectionReason(req.LobbyProtocolVersion!.Value, cfg.LobbyProtocolVersion);
                Console.WriteLine($"[lobby] registration refused: {req.Address} speaks lobby protocol {req.LobbyProtocolVersion}, this lobby speaks {cfg.LobbyProtocolVersion}");
                return Results.Text($"{reason} (server protocol {req.LobbyProtocolVersion}, lobby protocol {cfg.LobbyProtocolVersion})",
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (req.Port is < 1 or > 65535) return Results.BadRequest("port must be in 1..65535");
            req.Verified = identities.VerifyServer(req.IdentityServerId, req.IdentityServerSecret) is not null;
            if (!req.Verified && (req.IdentityServerId is not null || req.IdentityServerSecret is not null)) return Results.Unauthorized();
            if (!req.Verified) req.IdentityServerId = null;
            var response = registry.Register(req);
            // Rejected either because the directory is full (new registration) or because a
            // presented token matched no listing. Both are caller errors — report 409.
            return response is null
                ? Results.Conflict("registration rejected: directory full, or token did not match any listing")
                : Results.Ok(response);
        }).RequireRateLimiting(RateLimiting.RegisterPolicy);

        // Delete requires the ownership token, passed as a query parameter: DELETE /api/servers/{id}?token=…
        app.MapDelete("/api/servers/{id}", ([FromRoute] string id, [FromQuery] string token, ServerRegistry registry) =>
        {
            if (string.IsNullOrWhiteSpace(token))
                return Results.BadRequest("token is required to deregister a server");
            return registry.Deregister(id, token)
                ? Results.NoContent()
                : Results.NotFound();
        });

        // Public read — no auth. P761b: a player COUNT only; no pilot names
        // (the roster is not published — see ServerListingDto's remarks).
        app.MapGet("/api/servers", (ServerRegistry registry) => Results.Ok(registry.List()));

        // P894 / LOB-0009 — the master core list, the HTTP analogue of the
        // SERVERS_LIST message's core array (native aggregates every connected
        // server's StaticCoreInfo into one list the client offers before joining,
        // lobby/server.cpp:100-120 BuildStaticCoreInfo; lobby/client.cpp:377-385).
        // Curated lobby catalogs (P871 gameCores) are included when configured.
        app.MapGet("/api/servers/cores", (ServerRegistry registry, LobbySettings cfg) =>
            Results.Ok(registry.CoreList(cfg.GameCores)));

        // P894 / LOB-0027 — the operational counters native kept in shared memory
        // (lobby/counters.h:4-26, lobby/lobbyapp.cpp:396-423): servers, missions,
        // players in missions and the per-server percent load. The HTTP directory
        // can answer them on demand instead of exporting perfmon counters.
        app.MapGet("/api/servers/stats", (ServerRegistry registry) => Results.Ok(registry.Stats()));

        // P761b — FIND_PLAYER: the lobby is asked about exactly ONE pilot and
        // answers which server(s) that pilot sits on (native
        // WinTrek/gamescreen.cpp:1348-1352 → FM_C_FIND_PLAYER). Only servers that
        // opted in to publishing their player list are searched, and the answer
        // carries no other pilot's name. Rate-limited: an exact-name endpoint is
        // otherwise a cheap roster oracle.
        app.MapGet("/api/servers/findplayer", ([FromQuery] string? name, ServerRegistry registry) =>
            string.IsNullOrWhiteSpace(name)
                ? Results.BadRequest("name is required")
                : Results.Ok(registry.FindPlayer(name)))
            .RequireRateLimiting(RateLimiting.FindPlayerPolicy);

        app.MapGet("/api/servers/{id}", (string id, ServerRegistry registry) =>
        {
            var dto = registry.Get(id);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        // Trivial liveness probe for a container orchestrator's health check.
        app.MapGet("/healthz", () => Results.Ok());

        // P810 — the version advertisement, the HTTP analogue of native's
        // AUTO_UPDATE_INFO(253) (lobby/messageslc.h:45-52). Every field is
        // optional: a lobby that configures none of them (the default) answers
        // with nulls and every client skips its update check, byte-for-byte
        // like a native lobby with AutoUpdate unconfigured. Read-only, no
        // credentials, no rate limit — it is a static-ish config answer.
        app.MapGet("/api/update/info", (LobbySettings cfg) => Results.Ok(new Allegiance.Update.LobbyUpdateInfo
        {
            CurrentVersion = NullIfEmpty(cfg.CurrentClientVersion),
            MinimumVersion = NullIfEmpty(cfg.MinimumClientVersion),
            FeedUrl = NullIfEmpty(cfg.UpdateFeedUrl),
        }));
    }

    /// <summary>An unconfigured optional field is advertised as absent (null),
    /// never as an empty string.</summary>
    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
