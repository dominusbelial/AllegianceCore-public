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

    /// <summary>P866 / SIM-0572 — the DIRTY FLAG, native
    /// <c>CFSMission::SetLobbyIsDirty</c> + <c>UpdateLobby</c>
    /// (<c>fsmission.cpp:5606-5628</c>): a marked mission publishes on the next
    /// tick AFTER the ten-second coalesce window, even when the player count and
    /// the lease are unchanged; an unmarked mission keeps waiting for the lease.
    /// </summary>
    [Fact]
    public async Task Dirty_version_triggers_a_publication_after_the_ten_second_coalesce()
    {
        var clock = new Clock(); var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        long version = 1;
        using var heartbeat = new LobbyHeartbeat(http, new() { HeartbeatIntervalSeconds = 25 },
            () => 1, "P866", "core", "v1", 1234, clock,
            livePlayerNames: null, liveGameParams: null, lobbyDirtyVersion: () => version);

        await heartbeat.PumpAsync(periodic: true);
        Assert.Single(handler.Requests);                 // first registration

        // Dirty inside the coalesce window: nothing publishes, the flag stays set.
        version = 2;                                     // e.g. a host mission-param edit
        clock.Advance(5); await heartbeat.PumpAsync(periodic: true);
        Assert.Single(handler.Requests);

        // Ten seconds after the last publish the dirty mission republishes, with an
        // unchanged player count and no lease expiry.
        clock.Advance(5); await heartbeat.PumpAsync(periodic: true);
        Assert.Equal(2, handler.Requests.Count);

        // No new dirty event and no lease expiry (25 s) → quiet.
        clock.Advance(10); await heartbeat.PumpAsync(periodic: true);
        Assert.Equal(2, handler.Requests.Count);

        // Another dirty event publishes at the next eligible tick, not at the lease.
        version = 3;
        clock.Advance(10); await heartbeat.PumpAsync(periodic: true);
        Assert.Equal(3, handler.Requests.Count);

        // Finally the clean lease refresh still fires at the configured interval.
        clock.Advance(25); await heartbeat.PumpAsync(periodic: true);
        Assert.Equal(4, handler.Requests.Count);
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
