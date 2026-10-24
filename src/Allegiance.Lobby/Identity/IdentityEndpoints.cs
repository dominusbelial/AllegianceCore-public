using System.Data.Common;
using Allegiance.Core.Identity;
using Fido2NetLib;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Allegiance.Lobby.Identity;

public static class IdentityEndpoints
{
    public const string Cookie = "allegiance.session";
    public static string? SessionSecret(HttpContext ctx) => ctx.Request.Headers.Authorization.ToString() is { } h && h.StartsWith("Bearer ", StringComparison.Ordinal)
        ? h[7..] : ctx.Request.Cookies[Cookie];
    public static void SetCookie(HttpContext ctx, IdentitySettings settings, string session) => ctx.Response.Cookies.Append(Cookie, session, new CookieOptions
    {
        HttpOnly = true, Secure = settings.PublicUrl.StartsWith("https:", StringComparison.Ordinal), SameSite = SameSiteMode.Strict,
        MaxAge = TimeSpan.FromHours(12), Path = "/",
    });
    public static void MapIdentity(this WebApplication app, IdentitySettings settings)
    {
        var group = app.MapGroup("/api/identity").RequireRateLimiting(RateLimiting.IdentityPolicy);
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var request = ctx.HttpContext.Request;
            ctx.HttpContext.Response.Headers.CacheControl = "no-store";
            if (request.Method == "POST")
            {
                string origin = request.Headers.Origin.ToString();
                if ((origin.Length > 0 && origin != settings.PublicUrl) || (request.Cookies.Count > 0 && origin != settings.PublicUrl))
                    return Results.StatusCode(403);
                if (!request.HasJsonContentType()) return Results.StatusCode(415);
            }
            try { return await next(ctx); }
            catch (Exception e) when (e is ArgumentException or Fido2VerificationException or FormatException)
            { return Results.BadRequest(new { error = "Invalid or expired identity proof, code or callsign" }); }
            catch (DbException e) when (e is SqliteException { SqliteErrorCode: 19 } or PostgresException { SqlState: "23505" })
            { return Results.Conflict(new { error = "Callsign or credential already registered" }); }
        });
        group.MapGet("/public-key", (IdentitySigningKey key) => Results.Ok(new { issuer = settings.PublicUrl, publicKey = key.PublicKey }));
        group.MapGet("/providers", () => Results.Ok(new[] { "GitHub", "Google", "Steam" }.Where(p => ExternalProviders.Enabled(settings, p))));
        group.MapPost("/passkeys/register/options", (RegisterOptionsRequest req, PasskeyService passkeys) => Results.Ok(passkeys.RegisterOptions(req.Callsign)));
        group.MapPost("/passkeys/register", async (RegisterPasskeyRequest req, PasskeyService passkeys, IdentityStore store, HttpContext ctx, CancellationToken ct) =>
        {
            var account = await passkeys.Register(req.Challenge, req.Credential, ct);
            SetCookie(ctx, settings, store.Session(account.Id)); return Results.Ok(account);
        });
        group.MapPost("/passkeys/login/options", (PasskeyService passkeys) => Results.Ok(passkeys.LoginOptions()));
        group.MapPost("/passkeys/login", async (LoginPasskeyRequest req, PasskeyService passkeys, IdentityStore store, HttpContext ctx, CancellationToken ct) =>
        {
            var account = await passkeys.Login(req.Challenge, req.Credential, ct);
            SetCookie(ctx, settings, store.Session(account.Id)); return Results.Ok(account);
        });
        group.MapGet("/me", (HttpContext ctx, IdentityStore store) => store.Authenticate(SessionSecret(ctx)) is { } account ? Results.Ok(account) : Results.Unauthorized());
        group.MapPost("/signout", (HttpContext ctx, IdentityStore store) =>
        {
            if (SessionSecret(ctx) is { } secret) store.Revoke(secret);
            ctx.Response.Cookies.Delete(Cookie); return Results.NoContent();
        });
        group.MapPost("/device", (DeviceStartRequest req, IdentityStore store) => Results.Ok(store.StartDevice(req)));
        group.MapPost("/device/poll", (DevicePollRequest req, IdentityStore store) => req.DeviceCode.Length > 128 ? Results.BadRequest() : Results.Ok(store.Poll(req.DeviceCode)));
        group.MapGet("/device/{code}", (string code, HttpContext ctx, IdentityStore store) =>
        {
            if (store.Authenticate(SessionSecret(ctx)) is null) return Results.Unauthorized();
            var d = store.Device(code); return d is null ? Results.NotFound() : Results.Ok(new { purpose = d[0], name = d[1], approved = d[2].Length != 0 });
        });
        group.MapPost("/device/approve", (ApproveDeviceRequest req, HttpContext ctx, IdentityStore store) =>
        {
            var account = store.Authenticate(SessionSecret(ctx));
            return account is null ? Results.Unauthorized() : store.Approve(req.UserCode, account.Id) ? Results.NoContent() : Results.StatusCode(403);
        });
        group.MapPost("/join-token", (JoinTokenRequest req, HttpContext ctx, IdentityStore store, IdentitySigningKey key, ServerRegistry registry, TimeProvider time) =>
        {
            var account = store.Authenticate(SessionSecret(ctx));
            if (account is null) return Results.Unauthorized();
            if (!registry.List().Any(s => s.Verified && s.IdentityServerId == req.ServerId)) return Results.NotFound();
            return Results.Ok(new JoinTokenResponse(key.Issue(account, req.ServerId, time.GetUtcNow())));
        });
        group.MapExternalProviders(settings);
    }
}
public sealed record ApproveDeviceRequest(string UserCode);
