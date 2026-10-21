using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Allegiance.Lobby;

/// <summary>
/// ASP.NET Core's in-box <c>Microsoft.AspNetCore.RateLimiting</c> middleware (available
/// since .NET 7 — no external package). A public internet-facing server directory needs
/// basic abuse protection on the write path, so register/heartbeat traffic is partitioned
/// per-client-IP and throttled with a fixed-window limiter.
/// </summary>
public static class RateLimiting
{
    /// <summary>Named policy applied to <c>POST /api/servers/register</c> (fresh + heartbeat).</summary>
    public const string RegisterPolicy = "register";

    /// <summary>P761b — named policy for the per-name find-player query
    /// (<c>GET /api/servers/findplayer</c>). The endpoint answers "is pilot X on
    /// any listed server", so unthrottled it would be a directory-wide roster
    /// oracle; the same per-IP fixed window as registration bounds it.</summary>
    public const string FindPlayerPolicy = "findplayer";

    /// <summary>Permitted register/heartbeat calls per IP per window.</summary>
    private const int RegisterPermitLimit = 30;

    /// <summary>Rate window for the register policy.</summary>
    private static readonly TimeSpan RegisterWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Registers the rate limiter middleware with a named, IP-partitioned policy.
    /// </summary>
    public static void AddLobbyRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = RegisterWindow, QueueLimit = 0, AutoReplenishment = true }));

            options.AddPolicy(FindPlayerPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = RegisterPermitLimit,
                        Window = RegisterWindow,
                        AutoReplenishment = true,
                        QueueLimit = 0,
                    }));

            options.AddPolicy(RegisterPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    // UseForwardedHeaders runs first and trusts configured IPs only.
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = RegisterPermitLimit,
                        Window = RegisterWindow,
                        AutoReplenishment = true,
                        QueueLimit = 0,
                    }));
        });
    }
}
