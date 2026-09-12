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

            options.AddPolicy(RegisterPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    // ⚠ REVERSE-PROXY NOTE (deployment-critical, do not ignore):
                    // This partitions by the request's immediate source IP. Real public
                    // infrastructure is very likely behind a reverse proxy (nginx/Caddy/
                    // Cloudflare), in which case EVERY request arrives from the proxy's IP
                    // and this partitioning collapses to a single shared bucket — making the
                    // rate limit meaningless (and worse, letting one abusive self-hoster
                    // throttle everyone, or vice versa). The deployment (Stage 3 /
                    // LOBBY_APP.md) must configure `ForwardedHeaders` middleware trusting
                    // ONLY the actual proxy so RemoteIpAddress carries the real client IP.
                    // That config lives outside this class by design — we only flag it.
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
