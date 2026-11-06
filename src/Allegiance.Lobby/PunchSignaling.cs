using System.Net;
using Allegiance.Core.Transport;

namespace Allegiance.Lobby;

public sealed class PunchSignaling(TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<PunchIntroduction>> _pending = new();
    private readonly Dictionary<string, NatObservationStore.Window> _limits = new();
    private NatObservationStore.Window _global;

    public PunchIntroduction? Introduce(ServerListingDto host, IPEndPoint guest)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            long milliseconds = now.ToUnixTimeMilliseconds();
            foreach (var key in _limits.Where(x => milliseconds - x.Value.Start > 10000).Select(x => x.Key).ToArray()) _limits.Remove(key);
            foreach (var key in _pending.Where(x => x.Value.Count == 0 || x.Value.All(i => i.ExpiresUtc <= now)).Select(x => x.Key).ToArray()) _pending.Remove(key);
            if (_pending.Count >= 384 && !_pending.ContainsKey(host.Id) || _limits.Count >= 4096) return null;
            if (!NatObservationStore.Take(ref _global, milliseconds, 128, 1000)
                || !Take("host:" + host.Id, milliseconds, 6) || !Take("guest:" + guest.Address, milliseconds, 3)) return null;
            var secret = Guid.NewGuid().ToString("N");
            var expires = now.AddSeconds(10);
            if (!_pending.TryGetValue(host.Id, out var queue)) _pending[host.Id] = queue = new();
            if (queue.Count >= 32) return null;
            queue.Enqueue(new(NatEndpoint.From(guest), secret, expires));
            return new(new(host.Address, host.UdpPort!.Value), secret, expires);
        }
    }
    private bool Take(string key, long now, int limit)
    {
        var window = _limits.GetValueOrDefault(key, new(now, 0));
        bool allowed = NatObservationStore.Take(ref window, now, limit, 10000);
        _limits[key] = window;
        return allowed;
    }
    public PunchBatch Poll(string id)
    {
        lock (_gate)
        {
            if (!_pending.Remove(id, out var queue)) return new([]);
            return new(queue.Where(i => i.ExpiresUtc > time.GetUtcNow()).ToArray());
        }
    }
}

public static class NatEndpoints
{
    public static void MapNatEndpoints(this WebApplication app)
    {
        app.MapGet("/api/nat/config", (LobbySettings settings, NatBindingResponder responder) =>
            Results.Ok(new NatConfiguration(settings.PlayerHostedGames, responder.PrimaryPort, responder.SecondaryPort)));
        app.MapPost("/api/servers/{id}/punch", (string id, PunchRequest request, HttpContext context,
            LobbySettings settings, ServerRegistry registry, NatObservationStore observations, PunchSignaling signaling) =>
        {
            if (!settings.PlayerHostedGames) return Results.StatusCode(403);
            var host = registry.Get(id);
            if (host is not { PlayerHosted: true, UdpPort: > 0 }) return Results.NotFound();
            if (context.Connection.RemoteIpAddress is not { } source || observations.Resolve(request.Receipt, source) is not { } guest)
                return Results.BadRequest("A fresh lobby UDP observation receipt is required");
            var introduction = signaling.Introduce(host, guest);
            return introduction is null ? Results.StatusCode(429) : Results.Ok(introduction);
        });
        app.MapPost("/api/servers/{id}/punch/poll", (string id, PunchPollRequest request, HttpContext context,
            LobbySettings settings, ServerRegistry registry, PunchSignaling signaling) =>
        {
            if (!settings.PlayerHostedGames) return Results.StatusCode(403);
            var host = registry.Get(id);
            if (host is not { PlayerHosted: true }) return Results.NotFound();
            if (!registry.VerifyToken(id, request.Token)
                || context.Connection.RemoteIpAddress is not { } source || NatObservationStore.Normalize(new(source, 1)).Address.ToString() != host.Address)
                return Results.Unauthorized();
            return Results.Ok(signaling.Poll(id));
        }).RequireRateLimiting(RateLimiting.PunchPollPolicy);
    }
}
