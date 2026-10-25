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
        app.MapPost("/api/servers/register", (RegisterRequest req, ServerRegistry registry, Identity.IdentityStore identities, HttpContext context) =>
        {
            // The socket source (or a configured trusted proxy's source) owns
            // the listing. A caller cannot redirect clients to a victim IP/DNS.
            if (context.Connection.RemoteIpAddress is not { } source) return Results.BadRequest("source address required");
            req.Address = source.IsIPv4MappedToIPv6 ? source.MapToIPv4().ToString() : source.ToString();
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
