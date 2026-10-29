using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Allegiance.Lobby;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Lhb = global::Allegiance.Server.LobbyHeartbeat;
using Lc = global::Allegiance.Server.LobbyConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

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
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        => builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, SourceAddress>());

    private sealed class SourceAddress : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next)
            => app =>
            {
                Microsoft.AspNetCore.Builder.UseExtensions.Use(app, async (context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
                    await continuation();
                });
                next(app);
            };
    }
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
    private static (Lhb Heartbeat, HttpClient Http) MakeHeartbeat(LobbyHarness app, Lc cfg, Func<int> count,
        Func<System.Collections.Generic.IReadOnlyList<string>>? names = null, bool publishNames = false)
    {
        if (publishNames) cfg.PublishPlayerNames = true;
        var http = new HttpClient(app.Server.CreateHandler())
        {
            BaseAddress = app.ClientOptions.BaseAddress,
            Timeout = TimeSpan.FromSeconds(5),
        };
        // ownsHttp=false: the heartbeat does not own the injected client; the test disposes it.
        return (new Lhb(http, cfg, count, "Wing War", "PCore001.igc", "0.1.0", 5333, clock: null, names), http);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public async Task First_heartbeat_registers_server_in_directory()
    {
        Require.Artwork();
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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lobby_null_constructs_no_heartbeat_client()
    {
        Require.Artwork();
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

    /// <summary>P185 — the promoted named <c>LobbyRegisterRequest</c> serializes
    /// through the source-generated context byte-identically to the anonymous
    /// payload the heartbeat used to POST under System.Net.Http.Json's Web
    /// defaults (camelCase keys, null Token still emitted). The wire bytes must
    /// not change when AOT swaps in source-gen. P806 appended the optional
    /// <c>Game</c> settings object (also emitted as null when absent), so the
    /// reference payload carries it too; P810 likewise appended the optional
    /// <c>minClientVersion</c> (null when the server declares no requirement).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void LobbyRegisterRequest_serializes_to_byte_identical_wire_shape()
    {
        Require.Artwork();
        var req = new global::Allegiance.Server.LobbyRegisterRequest(
            "Srv", "1.2.3.4", 5333, 7, 0, "Wing War", "PCore001.igc", "0.1.0", null,
            PublishPlayers: false, Token: null, Game: null, MinClientVersion: null);
        string viaContext = JsonSerializer.Serialize(req,
            global::Allegiance.Server.ServerJsonContext.Default.LobbyRegisterRequest);

        // Exactly what the old anonymous type emitted under Web defaults.
        var anon = new { Name = "Srv", Address = "1.2.3.4", Port = 5333, PlayerCount = (int?)7,
            MaxPlayers = 0, MissionName = "Wing War", CoreFile = "PCore001.igc", Version = "0.1.0",
            Players = (System.Collections.Generic.IReadOnlyList<string>?)null,
            PublishPlayers = false, Token = (string?)null,
            Game = (global::Allegiance.Server.LobbyGameParams?)null,
            MinClientVersion = (string?)null,
            // P883b — the native cur/max game counts (FedSrv.CPP:8107,8850; lobby/client.cpp:361-362).
            // Missions is omitted when null (WhenWritingNull), so it does not appear here.
            CurrentGames = 0, MaxGames = 99 };
        string viaWebDefaults = JsonSerializer.Serialize(anon, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(viaWebDefaults, viaContext);           // byte-identical wire shape
        Assert.Contains("\"token\":null", viaContext);      // null Token still emitted (no IgnoreCondition)
        Assert.Contains("\"game\":null", viaContext);       // P806 — absent settings still emitted (no IgnoreCondition)
        Assert.Contains("\"minClientVersion\":null", viaContext); // P810 — absent requirement still emitted
        Assert.Contains("\"playerCount\":7", viaContext);   // camelCase, as before
        Assert.Contains("\"maxGames\":99", viaContext);     // P883b — native default MaxGames advertised
    }

    /// <summary>P185 — <c>LobbyRegisterResponse</c> round-trips through the
    /// source-generated context to the same camelCase wire shape the reflection
    /// path read (case-insensitively) before.</summary>
    [Fact]
    public void LobbyRegisterResponse_roundtrips_exact_wire_shape()
    {
        var ctx = global::Allegiance.Server.ServerJsonContext.Default;
        var resp = new global::Allegiance.Server.LobbyRegisterResponse("id-7", "tok-9");
        string json = JsonSerializer.Serialize(resp, ctx.LobbyRegisterResponse);
        Assert.Equal("{\"id\":\"id-7\",\"token\":\"tok-9\"}", json);
        Assert.Equal(resp, JsonSerializer.Deserialize(json, ctx.LobbyRegisterResponse));
    }

    /// <summary>P761/P761b — a server that opts in to publishing its pilot names
    /// has them answer the per-name find-player query; the PUBLIC listing still
    /// carries none (privacy fix).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public async Task Heartbeat_publishes_the_player_list_to_the_directory()
    {
        Require.Artwork();
        using var app = new LobbyHarness();
        using var client = app.CreateClient();
        var (hb, http) = MakeHeartbeat(app, Cfg(app.ClientOptions.BaseAddress), () => 2,
            names: () => ["PilotOne", "PilotTwo"], publishNames: true);
        using var _ = hb;

        await hb.PumpAsync();

        // /api/servers: the count only — the roster is NOT published.
        var raw = await client.GetStringAsync("/api/servers");
        Assert.DoesNotContain("PilotOne", raw);
        Assert.DoesNotContain("PlayerNames", raw);
        var servers = await client.GetFromJsonAsync<List<global::Allegiance.Lobby.ServerListingDto>>("/api/servers");
        Assert.Equal(2, Assert.Single(servers!).PlayerCount);

        // The per-name query locates the game the named pilot sits in.
        var hit = await client.GetFromJsonAsync<List<global::Allegiance.Lobby.FindPlayerResultDto>>(
            "/api/servers/findplayer?name=pilotone");
        Assert.Equal("Wing War", Assert.Single(hit!).MissionName);
    }

    /// <summary>P761b — a server that does NOT opt in leaks nothing: no names on
    /// the listing and no find-player hit, exactly the default.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public async Task Heartbeat_without_the_opt_in_publishes_no_pilot_names()
    {
        Require.Artwork();
        using var app = new LobbyHarness();
        using var client = app.CreateClient();
        var (hb, http) = MakeHeartbeat(app, Cfg(app.ClientOptions.BaseAddress), () => 2,
            names: () => ["PilotOne", "PilotTwo"]);
        using var _ = hb;

        await hb.PumpAsync();
        var raw = await client.GetStringAsync("/api/servers");
        Assert.DoesNotContain("PilotOne", raw);
        var hit = await client.GetFromJsonAsync<List<global::Allegiance.Lobby.FindPlayerResultDto>>(
            "/api/servers/findplayer?name=PilotOne");
        Assert.Empty(hit!);
    }
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
        FedsrvVer = Allegiance.Core.Wire.LogonRequest.MsgVer, Time = 557u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 55400000000000006ul, DrmHash = "p176count",
    };

    private static void PumpUntil(Srv server, Func<bool> done, int max = 9000)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void LivePlayerCount_tracks_joined_peers()
    {
        Require.Artwork();
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

    /// <summary>P761 — the pilot-name snapshot paired with the count: joined
    /// peers appear in <see cref="Srv.LivePlayerNames"/> (the data the
    /// heartbeat publishes for the directory's player lists — the client's
    /// find-player search, native gamescreen.cpp:1348-1352).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void LivePlayerNames_tracks_joined_peers()
    {
        Require.Artwork();
        var server = new Srv();
        server.Boot(Config());
        try
        {
            Assert.Empty(server.LivePlayerNames);

            using var bot = new FltBot();
            bot.Connect(server.BoundPort, "p761_names_a");
            bot.SendLogonRequest(Request("p761_names_a"));
            PumpUntil(server, () => server.LivePlayerNames.Contains("p761_names_a"));
            Assert.Contains("p761_names_a", server.LivePlayerNames);
        }
        finally { server.Shutdown(); }
    }
}
