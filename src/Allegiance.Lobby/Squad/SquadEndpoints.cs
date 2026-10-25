using Allegiance.Lobby.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Allegiance.Lobby.Squad;

/// <summary>
/// Squad HTTP surface (P820a) — the modern replacement for the native Club
/// squad socket messages (<c>Inc/ClubMessages.h:129-301</c>): the client Squads
/// screen and the lobby's read-only squad pages both read these, and every
/// membership/ownership mutation is an authenticated endpoint enforcing native's
/// permission rules (see <see cref="SquadStore"/>). Reads are public (like
/// native's club reads); every write requires a signed-in session and is
/// rate-limited (a public write path).
/// </summary>
public static class SquadEndpoints
{
    public static void MapSquad(this WebApplication app)
    {
        var group = app.MapGroup("/api/squads");

        // Public reads (native club reads).
        // All-tab paged list — SQUAD_NEXT_PAGE_ALL (Inc/ClubMessages.h:158-161).
        group.MapGet("/", ([FromQuery] SquadContracts.SquadSortColumn sort, [FromQuery] string? basisId,
            [FromQuery] int count, SquadStore squads) =>
        {
            if (count <= 0 || count > 84) count = 21;   // c_cSquadsShown (SquadsScreen.cpp:25)
            return Results.Ok(squads.All(sort, basisId, count));
        });

        // My squads (requires the signed-in player) — SQUAD_NEXT_PAGE_MY.
        group.MapGet("/mine", (HttpContext ctx, SquadStore squads, IdentityStore store) =>
            store.Authenticate(Session(ctx)) is { } me
                ? Results.Ok(squads.ByPlayer(me.Id))
                : Results.Unauthorized());

        // A player's squads (DudeX tab / character-info squads) — SQUAD_DUDEX_LIST,
        // SQUAD_NEXT_PAGE_DUDEX, CHARACTER_INFO_SQUAD (Inc/ClubMessages.h:166-178,129-138).
        group.MapGet("/by-player/{accountId}", (string accountId, SquadStore squads) => Results.Ok(squads.ByPlayer(accountId)));

        // Partial-name find — SQUAD_PAGE_FIND.
        group.MapGet("/find", ([FromQuery] string? q, SquadStore squads) => Results.Ok(squads.Find(q ?? "")));

        // Full details — SQUAD_DETAILS_REQ → SQUAD_DETAILS (+ _PLAYER).
        group.MapGet("/{id}", (string id, SquadStore squads) =>
            squads.Details(id) is { } d ? Results.Ok(d) : Results.NotFound());

        // Ownership/audit log — SQUAD_LOG_REQ → SQUAD_LOG_INFO.
        group.MapGet("/{id}/log", (string id, SquadStore squads) =>
            squads.Details(id) is null ? Results.NotFound() : Results.Ok(squads.Log(id)));

        // ---- Writes (signed-in only) ----
        var write = group.MapGroup("")
            .RequireRateLimiting(RateLimiting.RegisterPolicy)
            .AddEndpointFilter(async (ctx, next) =>
            {
                var store = ctx.HttpContext.RequestServices.GetRequiredService<IdentityStore>();
                return store.Authenticate(Session(ctx.HttpContext)) is null ? Results.Unauthorized() : await next(ctx);
            });

        // Create — SQUAD_CREATE → SQUAD_CREATE_ACK.
        write.MapPost("/", (CreateBody body, HttpContext ctx, SquadStore squads, IdentityStore store, TimeProvider time) =>
            Handle(squads.CreateSquad(Acting(ctx, store), body.ToRequest(), time.GetUtcNow().ToUnixTimeSeconds())));

        // Edit — SQUAD_EDIT (owner only).
        write.MapPut("/{id}", (string id, EditBody body, HttpContext ctx, SquadStore squads, IdentityStore store, TimeProvider time) =>
            Handle(squads.EditSquad(Acting(ctx, store), id, body.ToRequest(), time.GetUtcNow().ToUnixTimeSeconds())));

        // Membership / ownership actions — SQUAD_MAKE/CANCEL/ACCEPT/REJECT/BOT/
        // QUIT/PROMOTE/DEMOTE/TRANSFER (Inc/ClubMessages.h:212-253).
        write.MapPost("/{id}/join", (string id, HttpContext ctx, SquadStore squads, IdentityStore store, TimeProvider time) =>
            Handle(squads.Join(Acting(ctx, store), id, time.GetUtcNow().ToUnixTimeSeconds())));
        write.MapPost("/{id}/cancel-join", (string id, HttpContext ctx, SquadStore squads, IdentityStore store, TimeProvider time) =>
            Handle(squads.CancelJoin(Acting(ctx, store), id, time.GetUtcNow().ToUnixTimeSeconds())));
        write.MapPost("/{id}/accept/{memberId}", (string id, string memberId, HttpContext ctx, SquadStore squads, IdentityStore store, TimeProvider time) =>
            Handle(squads.Accept(Acting(ctx, store), id, memberId, time.GetUtcNow().ToUnixTimeSeconds())));
        write.MapPost("/{id}/reject/{memberId}", (string id, string memberId, HttpContext ctx, SquadStore squads, IdentityStore store, TimeProvider time) =>
            Handle(squads.RejectOrBoot(Acting(ctx, store), id, memberId, time.GetUtcNow().ToUnixTimeSeconds())));
        write.MapPost("/{id}/promote/{memberId}", (string id, string memberId, HttpContext ctx, SquadStore squads, IdentityStore store, TimeProvider time) =>
            Handle(squads.Promote(Acting(ctx, store), id, memberId, time.GetUtcNow().ToUnixTimeSeconds())));
        write.MapPost("/{id}/demote/{memberId}", (string id, string memberId, HttpContext ctx, SquadStore squads, IdentityStore store, TimeProvider time) =>
            Handle(squads.Demote(Acting(ctx, store), id, memberId, time.GetUtcNow().ToUnixTimeSeconds())));
        write.MapPost("/{id}/transfer/{memberId}", (string id, string memberId, HttpContext ctx, SquadStore squads, IdentityStore store, TimeProvider time) =>
            Handle(squads.Transfer(Acting(ctx, store), id, memberId, time.GetUtcNow().ToUnixTimeSeconds())));
        write.MapPost("/{id}/quit", (string id, HttpContext ctx, SquadStore squads, IdentityStore store, TimeProvider time) =>
            Handle(squads.Quit(Acting(ctx, store), id, time.GetUtcNow().ToUnixTimeSeconds())));
    }

    private static string Acting(HttpContext ctx, IdentityStore store) =>
        store.Authenticate(Session(ctx))!.Id;

    private static string? Session(HttpContext ctx) => IdentityEndpoints.SessionSecret(ctx);

    private static IResult Handle(SquadContracts.SquadActionResult r) => r.Ok
        ? Results.Ok(r)
        : (r.Error == "Squad not found."
            ? Results.NotFound(new { error = r.Error })
            : Results.Conflict(new { error = r.Error }));

    public sealed record CreateBody(string Name, string Tag, string Description, string Url, short Civ)
    {
        public SquadContracts.CreateSquadRequest ToRequest() => new(Name, Tag, Description, Url, Civ);
    }
    public sealed record EditBody(string Description, string Url, short Civ)
    {
        public SquadContracts.EditSquadRequest ToRequest() => new(Description, Url, Civ);
    }
}
