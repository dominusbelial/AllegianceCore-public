using System.Net.Http.Json;
using System.Text;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P970 — the mission-password gate at the JOIN, the operator log of a refusal, and
/// the advertised "password required" fact reaching the lobby directory.
/// <para>Native rules cited: the check is <c>FedSrv/FedSrv.CPP:648-661</c> (case
/// sensitive <c>Strcmp</c>, refusal <c>DELPOSITIONREQ</c> with
/// <c>DPR_BadPassword</c> and <c>iSide = NA</c>), and the client retried in place
/// (<c>WinTrek/gamescreen.cpp:1498-1505</c>). The native lobby never carried the
/// flag (<c>lobby/messagesall.h:25-63</c>), so the advertisement is the port's
/// design (recorded in docs/reports/JOIN_PASSWORD_UX_P970.md).</para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P970JoinPasswordTests
{
    private const string Password = "s3cret-pass";
    private const string Attempt = "hunter2z-not-it";

    private static ServerConfig Config() => new()
    {
        ArtPath = TestArtRoot.Value,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games = [Guarded("Guarded", Password), Guarded("Open", null)],
    };

    private static GameConfig Guarded(string name, string? password) => new()
    {
        Name = name, Map = "XXV_simple.igc", Teams = 2,
        CivIds = [-1, -1], SideNames = [name + "0", name + "1"], Password = password ?? "",
    };

    private static void Pump(Srv server, Func<bool> condition)
    {
        for (int i = 0; i < 4000; i++)
        {
            server.TickSim();
            if (condition()) return;
            Thread.Sleep(1);
        }
        Assert.True(condition(), "timed out waiting for wire state");
    }

    private static ConnectionSession Logon(Srv server, string name, uint cookie, string? password)
    {
        var client = new ConnectionSession();
        client.Connect(server.BoundPort, name);
        client.SendLogonRequest(new LogonRequest
        {
            CharacterName = name, FedsrvVer = LogonRequest.MsgVer, DwCookie = cookie,
            CrcFileList = -1, MissionPassword = password ?? "",
        });
        Pump(server, () => client.Ack is not null);
        return client;
    }

    // The two configured games boot in order (Games[0] = "Guarded", Games[1] = "Open").
    private static uint CookieOf(Srv server, int index) => server.Missions[index].Cookie;

    /// <summary>
    /// The refusal arrives at the JOIN — after the logon ack, before any side
    /// selection — with native's DPR_BadPassword and no roster, so the client is
    /// still on its join screen. Then the same client can retry with the right
    /// password and be seated (native retried without leaving the game list,
    /// gamescreen.cpp:1498-1505).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Wrong_password_is_refused_at_the_join_before_side_selection_and_a_retry_seats()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        uint cookie = CookieOf(server, 0);

        using var refused = Logon(server, "P970 refused pilot", cookie, Attempt);
        Assert.True(refused.Ack!.FValidated);   // native validates the logon, then refuses the mission join
        Pump(server, () => refused.LastPositionRefusal is not null);

        var reason = refused.LastPositionRefusal!;
        Assert.Equal(DelPositionReqReason.BadPassword, reason.Reason);
        Assert.Equal(-1, reason.SideId);        // native iSide = NA (FedSrv.CPP:655)
        Assert.Equal(refused.Ack.ShipID, reason.ShipId);
        // Nothing seated and no roster: the player never reached side selection.
        lock (refused.Gate)
        {
            Assert.Null(refused.World.SideRoster);
            Assert.Null(refused.World.MyShipId);
        }
        Assert.DoesNotContain(server.Ships, s => s.Ship.Name == "P970 refused pilot");

        // Retry on the same server with the right password: roster, side choice, seat.
        using var retry = Logon(server, "P970 refused pilot", cookie, Password);
        Pump(server, () => retry.World.SideRoster is not null);
        retry.SendPositionReq(0);
        Pump(server, () => retry.World.MyShipId is not null);
        Assert.Single(server.Ships, s => s.Ship.Name == "P970 refused pilot");
    }

    /// <summary>
    /// The CLIENT half of the early check: the exact production sequence
    /// <c>BeginConnect</c> runs — logon, THEN <c>WaitForJoinAnswer</c>. A wrong
    /// password answers with DPR_BadPassword (so the join screen can bounce back
    /// with the field focused), and a retry on the same session object with the
    /// right password answers with the roster and seats (native retried in place,
    /// gamescreen.cpp:1498-1505).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void The_client_join_answer_is_a_refusal_or_the_roster_and_a_retry_seats()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        uint cookie = CookieOf(server, 0);

        using var client = new ConnectionSession();
        var answer = JoinAnswer(client, server, "P970 answer pilot", cookie, Attempt);
        Assert.NotNull(answer);
        Assert.Equal(DelPositionReqReason.BadPassword, answer!.Reason);
        Assert.Equal(-1, answer.SideId);

        // Retry (a fresh Connect on the same session, exactly what the join screen
        // re-invokes): the join answer is the roster, not a refusal.
        Assert.Null(JoinAnswer(client, server, "P970 answer pilot", cookie, Password));
        Pump(server, () => client.World.SideRoster is not null);
        client.SendPositionReq(0);
        Pump(server, () => client.World.MyShipId is not null);
    }

    /// <summary>One production join sequence: connect, logon, ack, join answer.</summary>
    private static DelPositionReq? JoinAnswer(ConnectionSession client, Srv server, string name, uint cookie, string? password)
    {
        client.Connect(server.BoundPort, name);
        client.SendLogonRequest(new LogonRequest
        {
            CharacterName = name, FedsrvVer = LogonRequest.MsgVer, DwCookie = cookie,
            CrcFileList = -1, MissionPassword = password,
        });
        Pump(server, () => client.Ack is not null);
        Assert.True(client.Ack!.FValidated);
        // Drive the server while the client blocks on its own answer, like the
        // real client whose session thread keeps pumping.
        var waiter = Task.Run(() => client.WaitForJoinAnswer(5000));
        Pump(server, () => waiter.IsCompleted);
        return waiter.GetAwaiter().GetResult();
    }

    /// <summary>The compare stays case-sensitive, like native's lstrcmpA.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Password_compare_is_case_sensitive()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var wrongCase = Logon(server, "P970 case pilot", CookieOf(server, 0), Password.ToUpperInvariant());
        Pump(server, () => wrongCase.LastPositionRefusal is not null);
        Assert.Equal(DelPositionReqReason.BadPassword, wrongCase.LastPositionRefusal!.Reason);
    }

    /// <summary>An open game in the same server needs no password at all.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void An_open_game_still_admits_an_empty_password()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var client = Logon(server, "P970 open pilot", CookieOf(server, 1), null);
        Pump(server, () => client.World.SideRoster is not null);
        Assert.Null(client.LastPositionRefusal);
    }

    /// <summary>
    /// One operator log line per refusal, naming the peer and callsign — and NEVER
    /// the attempted or the real password.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_refusal_is_logged_once_with_the_pilot_and_no_password_text()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        var log = new StringBuilder();
        var saved = Console.Out;
        List<string> lines;
        try
        {
            Console.SetOut(new StringWriter(log));
            using var refused = Logon(server, "P970 logged pilot", CookieOf(server, 0), Attempt);
            Pump(server, () => refused.LastPositionRefusal is not null);
        }
        finally { Console.SetOut(saved); }
        lines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var refusal = Assert.Single(lines, l => l.Contains("[join]", StringComparison.Ordinal)
            && l.Contains("game password incorrect", StringComparison.Ordinal));
        Assert.Contains("P970 logged pilot", refusal, StringComparison.Ordinal);
        Assert.Contains("refused", refusal, StringComparison.Ordinal);
        // Neither the attempt nor the configured password may appear anywhere.
        Assert.DoesNotContain(lines, l => l.Contains(Attempt, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(Password, StringComparison.Ordinal));

        // Repeats from one peer are rate-limited rather than flooding the log.
        for (int i = 0; i < 12; i++)
        {
            var savedAgain = Console.Out;
            try
            {
                Console.SetOut(new StringWriter(log));
                using var again = Logon(server, "P970 logged pilot", CookieOf(server, 0), Attempt + i);
                Pump(server, () => again.LastPositionRefusal is not null);
            }
            finally { Console.SetOut(savedAgain); }
        }
        int refusalLines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(l => l.Contains("[join]", StringComparison.Ordinal)
                        && (l.Contains("game password incorrect", StringComparison.Ordinal)
                            || l.Contains("suppressed", StringComparison.Ordinal)));
        Assert.True(refusalLines is > 0 and < 13, $"{refusalLines} bad-password log lines for 13 refusals — the log is not rate-limited");
    }

    /// <summary>
    /// End to end for the advertised flag: a real server with a password-protected
    /// game publishes <c>passwordRequired</c> on its heartbeat, the real lobby
    /// stores and publishes it, and the client's own listing record (server-level
    /// AND per mission) reads it. The password VALUE never leaves the server.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public async Task Listing_advertises_password_required_end_to_end_without_the_password()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        server.TickSim();

        var missions = server.LiveMissions;
        Assert.True(missions.Single(m => m.Name == "Guarded").PasswordRequired);
        Assert.False(missions.Single(m => m.Name == "Open").PasswordRequired);

        using var app = new LobbyHarness();
        using var http = new HttpClient(app.Server.CreateHandler())
        {
            BaseAddress = app.ClientOptions.BaseAddress,
            Timeout = TimeSpan.FromSeconds(5),
        };
        var heartbeat = new LobbyHeartbeat(http, new LobbyConfig
        {
            Url = app.ClientOptions.BaseAddress.AbsoluteUri,
            DisplayName = "P970 encrypted server",
            PublicAddress = "1.2.3.4",
        }, () => 0, "Guarded", "PCore001.igc", "0.1.0", server.BoundPort);
        heartbeat.LiveMissions = () => missions;
        await heartbeat.PumpAsync();

        string json = await http.GetStringAsync("/api/servers");
        // The value never rides the wire: only the boolean fact.
        Assert.DoesNotContain(Password, json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"password\":", json, StringComparison.OrdinalIgnoreCase);

        var listings = System.Text.Json.JsonSerializer.Deserialize<List<Allegiance.Lobby.ServerListingDto>>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var listing = Assert.Single(listings);
        Assert.True(listing.PasswordRequired);
        var guarded = Assert.Single(listing.Missions!, m => m.Name == "Guarded");
        Assert.True(guarded.PasswordRequired);
        Assert.False(Assert.Single(listing.Missions!, m => m.Name == "Open").PasswordRequired);
        // (The client mirror's half of this round trip lives in
        // tests/Allegiance.ClientCore.MonoGame.Tests/P970JoinPasswordUiTests.cs.)
    }
}
