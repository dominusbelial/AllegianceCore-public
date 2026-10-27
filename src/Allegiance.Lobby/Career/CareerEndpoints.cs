using Allegiance.Lobby.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Allegiance.Lobby.Career;

/// <summary>
/// Career-statistics + leaderboard HTTP surface (P808b) — the lobby serving the
/// club-DB data the native character-info and leaderboard screens show. Reads
/// are public (like native's club read; the roster-equivalent privacy concern
/// does not apply to career stats); the two writes are gated: result ingestion
/// requires an enrolled (verified) game server, and a description edit requires
/// the owning account session.
/// </summary>
public static class CareerEndpoints
{
    public static void MapCareer(this WebApplication app)
    {
        var group = app.MapGroup("/api/career");

        // Game servers report end-of-match results (verified only). Mirrors the
        // register endpoint's server proof (LobbyEndpoints.cs:25-27): the
        // enrolled-server id+secret must match, or the report is rejected.
        // Rate-limited: a public write path.
        group.MapPost("/results", (CareerContracts.MatchReport report, CareerStore career, IdentityStore identities, Allegiance.Lobby.Squad.SquadStore squads) =>
        {
            var context = identities.VerifyServer(report.ServerId, report.ServerSecret);
            if (context is null) return Results.Unauthorized();
            return Results.Ok(career.ApplyMatchReport(report, context, identities.Now, squads));
        }).RequireRateLimiting(RateLimiting.RegisterPolicy);

        // Public read: a player's full character info (per-civ career lines).
        group.MapGet("/{accountId}", (string accountId, CareerStore career) =>
        {
            var info = career.GetCareer(accountId);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        // Owner-only write: edit the profile description.
        group.MapPut("/{accountId}/description", (string accountId, DescriptionRequest req, HttpContext ctx, CareerStore career, IdentityStore store) =>
        {
            var account = store.Authenticate(IdentityEndpoints.SessionSecret(ctx));
            if (account is null || !string.Equals(account.Id, accountId, StringComparison.Ordinal)) return Results.Unauthorized();
            if (req.Description.Length > 256 || req.Description.Any(char.IsControl)) return Results.BadRequest("description must be ≤256 printable characters");
            career.SetDescription(accountId, req.Description, store.Now);
            return Results.NoContent();
        });

        // Public read: leaderboard window by civ/sort/basis.
        group.MapGet("/leaderboard", ([FromQuery] short civ, [FromQuery] CareerContracts.LeaderBoardSort sort,
            [FromQuery] string? basis, [FromQuery] string? basisAccountId, CareerStore career) =>
        {
            var resp = career.Leaderboard(new CareerContracts.LeaderboardQuery(civ, sort, basis, basisAccountId));
            return resp.FailReason is not null
                ? Results.NotFound(new { error = resp.FailReason == "no players"
                    ? "No characters were found for the chosen faction."
                    : $"Could not find ranking info for '{resp.FailReason}'" })
                : Results.Ok(resp);
        });
    }

    public sealed record DescriptionRequest(string Description);
}
