using Allegiance.Lobby.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Allegiance.Lobby.Events;

/// <summary>
/// Zone-events HTTP surface (P820b) — the modern replacement for the native
/// club zone-events download the client consumed
/// (<c>WinTrek/zoneeventsscreen.cpp</c>). The event list is public and
/// read-only for players (like the native feed); managing events (create /
/// edit / delete) is an <b>operator-only</b> surface — a signed-in session
/// whose account is listed in <see cref="IdentitySettings.OperatorAccountIds"/>
/// (the lobby's declared operators). The configurable Zone web link is exposed
/// as <c>GET /api/config/zone-web</c> for the client's online hub.
/// </summary>
public static class EventEndpoints
{
    public static void MapEvents(this WebApplication app)
    {
        var group = app.MapGroup("/api/events");

        // Public read-only list (native zone-events feed; players read only).
        group.MapGet("/", (EventStore events) => Results.Ok(events.List()));
        group.MapGet("/{id}", (string id, EventStore events) =>
            events.Get(id) is { } e ? Results.Ok(e) : Results.NotFound());

        // Operator-only management surface.
        var ops = group.MapGroup("")
            .RequireRateLimiting(RateLimiting.RegisterPolicy)
            .AddEndpointFilter(async (ctx, next) =>
            {
                var store = ctx.HttpContext.RequestServices.GetRequiredService<IdentityStore>();
                var settings = ctx.HttpContext.RequestServices.GetRequiredService<IdentitySettings>();
                var me = store.Authenticate(Session(ctx.HttpContext));
                if (me is null) return Results.Unauthorized();
                if (!settings.OperatorAccountIds.Contains(me.Id, StringComparer.Ordinal)) return Results.Forbid();
                return await next(ctx);
            });

        ops.MapPost("/", (CreateBody body, EventStore events, TimeProvider time) =>
            Handle(events.Create(body.ToRequest(), time.GetUtcNow().ToUnixTimeSeconds())));
        ops.MapPut("/{id}", (string id, UpdateBody body, EventStore events, TimeProvider time) =>
            Handle(events.Update(id, body.ToRequest(), time.GetUtcNow().ToUnixTimeSeconds())));
        ops.MapDelete("/{id}", (string id, EventStore events) =>
            events.Delete(id) ? Results.Ok(new { ok = true }) : Results.NotFound());
    }

    /// <summary>The configurable Zone web link surfaced for the client's online
    /// hub (a lobby-config setting, <c>LobbySettings.ZoneWebUrl</c>).</summary>
    public static void MapZoneWeb(this WebApplication app)
    {
        app.MapGet("/api/config/zone-web", (LobbySettings settings) => Results.Ok(new { url = settings.ZoneWebUrl }));
    }

    private static string? Session(HttpContext ctx) => IdentityEndpoints.SessionSecret(ctx);

    private static IResult Handle(EventContracts.EventActionResult r) => r.Ok
        ? Results.Ok(r)
        : (r.Error == "Event not found."
            ? Results.NotFound(new { error = r.Error })
            : Results.BadRequest(new { error = r.Error }));

    public sealed record CreateBody(string Date, string Time, string Name, string Description, string SignUpUrl, IReadOnlyList<string> Files)
    {
        public EventContracts.CreateEventRequest ToRequest() => new(Date, Time, Name, Description, SignUpUrl, Files);
    }
    public sealed record UpdateBody(string Date, string Time, string Name, string Description, string SignUpUrl, IReadOnlyList<string> Files)
    {
        public EventContracts.UpdateEventRequest ToRequest() => new(Date, Time, Name, Description, SignUpUrl, Files);
    }
}
