using System.Net.Http.Json;
using System.Reflection;
using Allegiance.Lobby;
using Microsoft.AspNetCore.Mvc.Testing;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Lhb = global::Allegiance.Server.LobbyHeartbeat;
using Lc = global::Allegiance.Server.LobbyConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// In-process host for the real <c>Allegiance.Lobby</c> app, reused by
/// <see cref="LobbyHeartbeatTests"/>. Same pattern as <c>tests/Allegiance.Lobby.Tests/LobbyTestApp.cs</c>
/// but in this assembly; no overrides — these tests exercise the real clock, real
/// registry and real endpoints (determinism comes from driving the heartbeat's
/// <c>PumpAsync</c> seam directly rather than waiting on a real timer).
/// </summary>
internal sealed class LobbyHarness : WebApplicationFactory<Allegiance.Lobby.ServerRegistry>
{
}

/// <summary>
/// P176 — the <c>Allegiance.Server</c> opt-in lobby heartbeat client. Verifies the
/// full loop against a real in-memory <c>Allegiance.Lobby</c>: register on the first
/// tick, idempotent in-place update on subsequent ticks (no duplicates), deregister
/// on Dispose, and that a config with <c>Lobby = null</c> constructs no client at all
/// (the fully-opt-out default). Deterministic: ticks are driven through the internal
/// <c>PumpAsync</c> seam, not a wall-clock timer.
/// </summary>
[Collection("LiveNetwork")]
public sealed class LobbyHeartbeatTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Lc Cfg(Uri baseAddress, string displayName = "P176 Test Server")
        => new() { Url = baseAddress.AbsoluteUri, DisplayName = displayName, PublicAddress = "1.2.3.4" };

    /// <summary>Build a heartbeat wired to the in-memory test server via its handler.</summary>
    private static (Lhb Heartbeat, HttpClient Http) MakeHeartbeat(LobbyHarness app, Lc cfg, Func<int> count)
    {
        var http = new HttpClient(app.Server.CreateHandler())
        {
            BaseAddress = app.ClientOptions.BaseAddress,
            Timeout = TimeSpan.FromSeconds(5),
        };
        // ownsHttp=false: the heartbeat does not own the injected client; the test disposes it.
        return (new Lhb(http, cfg, count, "Wing War", "PCore001.igc", "0.1.0", 5333), http);
    }

    [Fact]
    public async Task First_heartbeat_registers_server_in_directory()
    {
        using var app = new LobbyHarness();
        using var client = app.CreateClient();
        var (hb, http) = MakeHeartbeat(app, Cfg(app.ClientOptions.BaseAddress), () => 7);
        try
        {
            await hb.PumpAsync();

            var servers = await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers");
            var mine = Assert.Single(servers!);
            Assert.Equal("P176 Test Server", mine.Name);
            Assert.Equal(7, mine.PlayerCount);
            Assert.Equal("Wing War", mine.MissionName);
            Assert.Equal("PCore001.igc", mine.CoreFile);
        }
        finally { hb.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Second_heartbeat_updates_same_listing_in_place()
    {
        using var app = new LobbyHarness();
        using var client = app.CreateClient();
        int count = 5;
        var (hb, http) = MakeHeartbeat(app, Cfg(app.ClientOptions.BaseAddress), () => count);
        try
        {
            await hb.PumpAsync();
            var first = Assert.Single((await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!);

            count = 23;
            await hb.PumpAsync();

            var after = await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers");
            var mine = Assert.Single(after!);        // still exactly one entry — no duplicate
            Assert.Equal(first.Id, mine.Id);          // same listing id, refreshed in place
            Assert.Equal(23, mine.PlayerCount);       // reflects the new value
        }
        finally { hb.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Dispose_deregisters_listing()
    {
        using var app = new LobbyHarness();
        using var client = app.CreateClient();
        var (hb, http) = MakeHeartbeat(app, Cfg(app.ClientOptions.BaseAddress), () => 3);
        try
        {
            await hb.PumpAsync();
            Assert.Single((await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!);

            hb.Dispose();

            var after = await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers");
            Assert.Empty(after!);                     // best-effort DELETE fired on teardown
        }
        finally { http.Dispose(); }
    }

    /// <summary>
    /// The fully-opt-out default: a config with <c>Lobby = null</c> must not construct
    /// a heartbeat client (so zero HTTP calls can ever be made). Verified structurally by
    /// reflecting the private <c>_heartbeat</c> field after a real <c>Server.Boot</c>.
    /// </summary>
    [Fact]
    public void Lobby_null_constructs_no_heartbeat_client()
    {
        var server = new Srv();
        server.Boot(CfgWithoutLobby());
        try
        {
            var field = typeof(Srv).GetField("_heartbeat", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.Null(field.GetValue(server));
        }
        finally { server.Shutdown(); }
    }

    private static Cfg CfgWithoutLobby() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games = [new Gm { Name = "P176 No-Lobby Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };
}

/// <summary>
/// P176 — the cross-thread-safe <see cref="Srv.LivePlayerCount"/> snapshot. Confirms it
/// tracks the number of joined peers (the size of the sim-thread-owned <c>_peerShips</c>)
/// without ever enumerating <c>Ships</c> from an off-sim thread. Two real bots connect
/// through the wire transport and the count follows 0 → 1 → 2.
/// </summary>
[Collection("LiveNetwork")]
public sealed class LivePlayerCountTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games = [new Gm { Name = "P176 LivePlayerCount Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static Allegiance.Core.Wire.LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = Allegiance.Core.Wire.LogonRequest.MsgVer, Time = 557u, DwCookie = 0x557u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 55400000000000006ul, DrmHash = "p176count",
    };

    private static void PumpUntil(Srv server, Func<bool> done, int max = 9000)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
    }

    [Fact]
    public void LivePlayerCount_tracks_joined_peers()
    {
        var server = new Srv();
        server.Boot(Config());
        try
        {
            Assert.Equal(0, server.LivePlayerCount);

            using var botA = new FltBot();
            botA.Connect(server.BoundPort, "p176_count_a");
            botA.SendLogonRequest(Request("p176_count_a"));
            PumpUntil(server, () => botA.ShipAssignment is not null);
            Assert.Equal(1, server.LivePlayerCount);

            using var botB = new FltBot();
            botB.Connect(server.BoundPort, "p176_count_b");
            botB.SendLogonRequest(Request("p176_count_b"));
            PumpUntil(server, () => botB.ShipAssignment is not null);
            Assert.Equal(2, server.LivePlayerCount);
        }
        finally { server.Shutdown(); }
    }
}
