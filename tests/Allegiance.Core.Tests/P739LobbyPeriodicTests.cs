using Allegiance.Server;
using System.Net;
using System.Text;

namespace Allegiance.Core.Tests;

public sealed class P739LobbyPeriodicTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        public bool Reject;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.OK);
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(Reject ? HttpStatusCode.Conflict : HttpStatusCode.OK)
            { Content = new StringContent("{\"id\":\"listing\",\"token\":\"owner\"}", Encoding.UTF8, "application/json") };
        }
    }
    // FedSrv/fsmission.cpp:21,5606-5627: dirty updates coalesce at ten seconds.
    // HTTP leases need additional clean refreshes, independent of dirty changes.
    [Fact]
    public async Task Dirty_lobby_details_coalesce_at_ten_seconds_and_clean_lease_refreshes()
    {
        var clock = new Clock(); var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        int count = 1;
        using var heartbeat = new LobbyHeartbeat(http, new() { HeartbeatIntervalSeconds = 25 },
            () => count, "P739", "core", "v1", 1234, clock);
        await heartbeat.PumpAsync(periodic: true); Assert.Single(handler.Requests);
        count = 2; clock.Advance(9.999); await heartbeat.PumpAsync(periodic: true); Assert.Single(handler.Requests);
        count = 3; clock.Advance(.001); await heartbeat.PumpAsync(periodic: true);
        Assert.Equal(2, handler.Requests.Count); Assert.Contains("\"playerCount\":3", handler.Requests[1]);
        Assert.Contains("\"token\":\"owner\"", handler.Requests[1]);
        clock.Advance(24.999); await heartbeat.PumpAsync(periodic: true); Assert.Equal(2, handler.Requests.Count);
        clock.Advance(.001); await heartbeat.PumpAsync(periodic: true); Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Rejected_lobby_token_retries_without_token_after_rate_limit()
    {
        var clock = new Clock(); var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var heartbeat = new LobbyHeartbeat(http, new(), () => 1, "P739", "core", "v1", 1234, clock);
        await heartbeat.PumpAsync(periodic: true);
        handler.Reject = true; clock.Advance(25); await heartbeat.PumpAsync(periodic: true);
        handler.Reject = false; clock.Advance(9.99); await heartbeat.PumpAsync(periodic: true);
        Assert.Equal(2, handler.Requests.Count);
        clock.Advance(.01); await heartbeat.PumpAsync(periodic: true);
        Assert.Equal(3, handler.Requests.Count); Assert.DoesNotContain("\"token\":\"owner\"", handler.Requests[2]);
    }
}
