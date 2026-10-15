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
    /// <summary>Routes an <see cref="WebApplication"/>'s lobby endpoints.</summary>
    public static void MapLobbyEndpoints(this WebApplication app)
    {
        // Fresh registration (no token) or idempotent heartbeat/update (valid token).
        // Rate-limited: this is the single write path a public registry must protect.
        app.MapPost("/api/servers/register", (RegisterRequest req, ServerRegistry registry, HttpContext context) =>
        {
            // The socket source (or a configured trusted proxy's source) owns
            // the listing. A caller cannot redirect clients to a victim IP/DNS.
            if (context.Connection.RemoteIpAddress is not { } source) return Results.BadRequest("source address required");
            req.Address = source.IsIPv4MappedToIPv6 ? source.MapToIPv4().ToString() : source.ToString();
            if (req.Port is < 1 or > 65535) return Results.BadRequest("port must be in 1..65535");
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

        // Public read — no auth.
        app.MapGet("/api/servers", (ServerRegistry registry) => Results.Ok(registry.List()));

        app.MapGet("/api/servers/{id}", (string id, ServerRegistry registry) =>
        {
            var dto = registry.Get(id);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        // Trivial liveness probe for a container orchestrator's health check.
        app.MapGet("/healthz", () => Results.Ok());
    }
}
