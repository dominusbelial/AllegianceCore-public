using System.Net;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P1022 (R1 finding F7) — the pure counter behind the credential-guessing
/// limits: per connection, per source address, a doubling back-off, a bounded
/// table, and a caller-supplied clock (no sleeps). No artwork needed.
/// </summary>
public sealed class P1022CredentialGuessGuardTests
{
    private static CredentialGuessGuard Guard(int perAddressCap = 4096) =>
        new(new CredentialGuessLimitsConfig { MaxTrackedAddresses = perAddressCap });

    private static readonly string Address = "198.51.100.7";
    private static readonly string Other = "198.51.100.8";

    /// <summary>Five failures inside the window start a back-off; the address is
    /// refused while it runs and is evaluated again once it expires. Time is
    /// driven by the parameter — nothing sleeps.</summary>
    [Fact]
    public void Five_failures_in_the_window_lock_the_address_and_the_lockout_expires()
    {
        var guard = Guard();
        CredentialFailure outcome = default;
        for (int i = 0; i < 5; i++) outcome = guard.RecordFailure(new ConnectionId((uint)(100 + i)), Address, now: i);

        Assert.True(outcome.LockedOut);
        Assert.Equal(15d, outcome.LockoutSeconds, 3);
        Assert.Equal(5, outcome.AddressFailures);
        Assert.True(guard.IsLockedOut(new ConnectionId(999), Address, now: 4));      // the 5th failure was at t=4
        Assert.True(guard.IsLockedOut(new ConnectionId(999), Address, now: 18.9));
        Assert.False(guard.IsLockedOut(new ConnectionId(999), Address, now: 19.1));  // 4 + 15 s back-off
    }

    /// <summary>A correct credential is never counted: it clears the CONNECTION's
    /// failure count (so a typo-then-success peer is not retired) and records no
    /// failure of its own; the address's window keeps its history, so the fifth
    /// failure in the window still locks it.</summary>
    [Fact]
    public void A_correct_credential_is_never_counted()
    {
        var guard = Guard();
        var peer = new ConnectionId(210);
        guard.RecordFailure(peer, Address, now: 0);
        guard.RecordFailure(peer, Address, now: 1);
        guard.RecordFailure(new ConnectionId(211), Address, now: 2);
        guard.RecordSuccess(peer);
        Assert.False(guard.ConnectionExhausted(peer));                       // the connection's history is gone
        Assert.Equal(0d, guard.LockoutRemaining(Address, now: 3), 3);        // the success itself never counts
        guard.RecordFailure(new ConnectionId(212), Address, now: 3);         // 4th failure in the window
        Assert.True(guard.RecordFailure(new ConnectionId(213), Address, now: 4).LockedOut);   // the 5th locks it
    }

    /// <summary>One address's back-off never touches another address or another
    /// address's own failure count.</summary>
    [Fact]
    public void A_second_address_is_unaffected_by_another_addresss_lockout()
    {
        var guard = Guard();
        for (int i = 0; i < 5; i++) guard.RecordFailure(new ConnectionId((uint)(400 + i)), Address, now: i);
        Assert.True(guard.IsLockedOut(new ConnectionId(500), Address, now: 1));
        Assert.False(guard.IsLockedOut(new ConnectionId(501), Other, now: 1));
        for (int i = 0; i < 4; i++) Assert.False(guard.RecordFailure(new ConnectionId((uint)(510 + i)), Other, now: i).LockedOut);
    }

    /// <summary>Each re-armed back-off for the same address doubles, capped at
    /// <c>maxLockoutSeconds</c>; a quiet window resets the escalation.</summary>
    [Fact]
    public void The_lockout_doubles_and_is_capped()
    {
        var guard = Guard();
        double now = 0;
        foreach (double seconds in new[] { 15d, 30d, 60d, 120d, 240d, 300d, 300d })
        {
            CredentialFailure outcome = default;
            for (int i = 0; i < 5; i++) outcome = guard.RecordFailure(new ConnectionId((uint)(600 + i)), Address, now);
            Assert.True(outcome.LockedOut);
            Assert.Equal(seconds, outcome.LockoutSeconds, 3);
            now += seconds;                                              // the next burst arrives right at expiry
        }
    }

    /// <summary>The per-address table is bounded: a spoofed/rotating source
    /// address cannot grow it past the configured cap.</summary>
    [Fact]
    public void The_address_table_is_bounded()
    {
        var guard = Guard(perAddressCap: 8);
        for (int i = 0; i < 50; i++)
            guard.RecordFailure(new ConnectionId((uint)(700 + i)), $"203.0.113.{i}", now: i * 0.1);
        Assert.True(guard.TrackedAddressCount <= 8, $"{guard.TrackedAddressCount} address entries held with a cap of 8");
        Assert.True(guard.EvictedAddressCount > 0);
    }

    /// <summary>One connection spends its three failures and is retired — the
    /// counter must report the close exactly once.</summary>
    [Fact]
    public void A_connection_is_retired_once_it_spends_its_failures()
    {
        var guard = Guard();
        var peer = new ConnectionId(800);
        Assert.False(guard.RecordFailure(peer, Address, now: 0).CloseConnection);
        Assert.False(guard.RecordFailure(peer, Address, now: 1).CloseConnection);
        Assert.True(guard.RecordFailure(peer, Address, now: 2).CloseConnection);
        Assert.True(guard.ConnectionExhausted(peer));
        Assert.True(guard.IsLockedOut(peer, Other, now: 3));       // the connection, not the address
        Assert.False(guard.IsLockedOut(new ConnectionId(801), Other, now: 3));
        guard.ForgetPeer(peer);
        Assert.False(guard.ConnectionExhausted(peer));
    }

    /// <summary>The config-off arm: nothing is counted, nothing is closed.</summary>
    [Fact]
    public void Disabled_limits_count_nothing()
    {
        var guard = new CredentialGuessGuard(new CredentialGuessLimitsConfig { Enabled = false });
        for (int i = 0; i < 100; i++)
        {
            var outcome = guard.RecordFailure(new ConnectionId(1), Address, now: i);
            Assert.False(outcome.CloseConnection);
            Assert.False(outcome.LockedOut);
        }
        Assert.False(guard.IsLockedOut(new ConnectionId(1), Address, now: 100));
        Assert.Equal(0, guard.TrackedAddressCount);
    }
}

/// <summary>
/// P1022 (R1 finding F7) — the limits wired into the production dispatcher: a
/// wrong game password counts per connection and per source address, a wrong
/// bearer secret counts the same way, a correct credential is never slowed, the
/// back-off expires, and the config-off arm is byte-for-byte the old path.
/// <para><b>Native:</b> none of this is native — a wrong password is answered at
/// once (<c>FedSrv/FedSrv.CPP:648-661</c>), so every assertion here is the
/// port's deliberate hardening (Domingo's R1 decision: limit guessing on every
/// server, not only password-protected ones).</para>
/// </summary>
[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public sealed class P1022CredentialGuessTests
{
    /// <summary>A really booted server (the P966/P1022 harness shape) driven
    /// through the production dispatcher; peers log on and pick sides over the
    /// wire.</summary>
    private sealed class Booted : IDisposable
    {
        public readonly Srv Server = new() { SpawnInitialMinersAtBoot = false };
        public readonly List<(int Conn, byte[] Message)> Output = [];
        public Booted(Action<ServerConfig>? configure = null)
        {
            Require.Artwork();
            var cfg = new ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
                Games = [new GameConfig { Name = "R1", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], AutoStart = false }],
            };
            configure?.Invoke(cfg);
            Server.Boot(cfg);
            Server._testSendCapture = (c, p) => Output.AddRange(FedPacket.Unpack(p).Select(m => ((int)c.Value, m)));
        }
        public Mission Mission => Server.Missions[0];
        public static LogonRequest Request(string name, string password = "", string cdKey = "") => new()
        {
            CharacterName = name, ZoneTicket = null, CDKey = cdKey, MissionPassword = password,
            FedsrvVer = LogonRequest.MsgVer, Time = 1u, DwCookie = uint.MaxValue,
            CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0, SteamID = 1ul, DrmHash = "r1",
        };
        public void Send(int conn, IFedMessage message) =>
            Server.TestProcessDatagram(new ConnectionId((uint)conn), FedPacket.Pack([message.Encode()]));
        public List<byte[]> SentTo(int conn, ushort fmid) =>
            Output.Where(o => o.Conn == conn && FedMessageHeader.Read(o.Message).Fmid == fmid).Select(o => o.Message).ToList();
        public LogonAck Logon(int conn, LogonRequest req)
        {
            Send(conn, req);
            return LogonAck.Decode(SentTo(conn, LogonAck.Fmid)[^1]);
        }
        /// <summary>Log on, pick <paramref name="side"/> and tick until seated.</summary>
        public Ship Seat(int conn, string name, short side, string cdKey = "")
        {
            var ack = Logon(conn, Request(name, cdKey: cdKey));
            Assert.True(ack.FValidated, ack.CharNameOrFailureReason);
            Send(conn, new PositionReq { SideId = side });
            for (int i = 0; i < 400 && Server.Ships.All(s => s.Ship.Id != ack.ShipID); i++) Server.TickSim();
            return Server.Ships.First(s => s.Ship.Id == ack.ShipID).Ship;
        }
        /// <summary>Move the server's simulation clock (the limits' clock) forward
        /// without sleeping or ticking a world.</summary>
        public void AdvanceSimClock(double seconds) =>
            typeof(Srv).GetField("_simNow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(Server, Server.SimNow + seconds);
        public string Tally() => string.Join(", ", Output.GroupBy(o => FedMessageHeader.Read(o.Message).Fmid).Select(g => $"fmid {g.Key} x{g.Count()}"));
        public void Dispose() => Server.Dispose();
    }

    private static readonly IPAddress SourceA = IPAddress.Parse("198.51.100.10");
    private static readonly IPAddress SourceB = IPAddress.Parse("198.51.100.11");

    /// <summary>Give each synthetic connection a source address (the production
    /// path reads the transport's accept-time address; a test-injected peer has
    /// none).</summary>
    private static void MapAddresses(Srv server, params (int Conn, IPAddress Address)[] peers)
    {
        var map = peers.ToDictionary(p => (uint)p.Conn, p => p.Address);
        server.TestPeerAddressOverride = id => map.TryGetValue(id.Value, out var ip) ? ip : null;
    }

    /// <summary>Exactly the configured number of wrong passwords on ONE
    /// connection are answered; the connection then spends its failures and is
    /// retired (2000 guesses, not 2000 answers).</summary>
    [SkippableFact]
    public void Wrong_game_passwords_close_the_connection_after_the_limit()
    {
        using var w = new Booted(c => c.Games[0].Password = "s3cret");
        for (int i = 0; i < 2000; i++) w.Send(1, Booted.Request("GUESSER", "guess" + i));
        int answered = w.SentTo(1, DelPositionReq.Fmid).Count;
        Assert.Equal(3, answered);
        Assert.True(w.Server.CredentialGuesses.ConnectionExhausted(new ConnectionId(1)));
    }

    /// <summary>The config-off control arm: with <c>enabled=false</c> every
    /// attempt is evaluated and answered, exactly the native path.</summary>
    [SkippableFact]
    public void Credential_limits_off_restores_the_unthrottled_native_path()
    {
        using var w = new Booted(c =>
        {
            c.Games[0].Password = "s3cret";
            c.CredentialGuessLimits = new CredentialGuessLimitsConfig { Enabled = false };
        });
        for (int i = 0; i < 50; i++) w.Send(1, Booted.Request("GUESSER", "guess" + i));
        Assert.Equal(50, w.SentTo(1, DelPositionReq.Fmid).Count);
        Assert.False(w.Server.CredentialGuesses.ConnectionExhausted(new ConnectionId(1)));
    }

    /// <summary>Five bad password attempts from address A lock A only: the same
    /// correct password from address B is admitted while A is refused.</summary>
    [SkippableFact]
    public void A_second_address_is_unaffected_by_another_addresss_lockout()
    {
        using var w = new Booted(c => c.Games[0].Password = "s3cret");
        MapAddresses(w.Server, (1, SourceA), (2, SourceA), (3, SourceA), (4, SourceB));

        for (int i = 0; i < 3; i++) w.Send(1, Booted.Request("GUESS1", "bad" + i));   // connection 1 spent
        for (int i = 0; i < 2; i++) w.Send(2, Booted.Request("GUESS2", "bad" + i));   // address A reaches 5 -> back-off
        Assert.True(w.Server.CredentialGuesses.LockoutRemaining("198.51.100.10", w.Server.SimNow) > 0);

        w.Send(3, Booted.Request("RIGHT-A", "s3cret"));                              // A: refused without evaluation
        Assert.Empty(w.SentTo(3, LogonAck.Fmid));

        var b = w.Logon(4, Booted.Request("RIGHT-B", "s3cret"));                     // B: unaffected
        Assert.True(b.FValidated, b.CharNameOrFailureReason);
    }

    /// <summary>The back-off expires on the server's clock: the same correct
    /// password is refused during the lockout and admitted afterwards.</summary>
    [SkippableFact]
    public void A_correct_password_works_once_the_back_off_ends()
    {
        using var w = new Booted(c => c.Games[0].Password = "s3cret");
        MapAddresses(w.Server, (1, SourceA), (2, SourceA), (3, SourceA), (4, SourceA));
        for (int i = 0; i < 3; i++) w.Send(1, Booted.Request("GUESS1", "bad" + i));
        for (int i = 0; i < 2; i++) w.Send(2, Booted.Request("GUESS2", "bad" + i));
        w.Send(3, Booted.Request("RIGHT-EARLY", "s3cret"));
        Assert.Empty(w.SentTo(3, LogonAck.Fmid));

        w.AdvanceSimClock(16);                                                       // one second past the 15 s back-off
        var late = w.Logon(4, Booted.Request("RIGHT-LATE", "s3cret"));
        Assert.True(late.FValidated, late.CharNameOrFailureReason);
    }

    /// <summary>A wrong bearer secret presented before a known admin cheat
    /// command counts per connection and retires it after three attempts.</summary>
    [SkippableFact]
    public void A_wrong_admin_chat_secret_is_counted_and_closes_the_connection()
    {
        using var w = new Booted(c => c.LiveSettings = new LiveSettingsConfig
        {
            AdminCheats = new AdminCheatsConfig { Enabled = true, Secret = "admin-secret-1", AllowNames = ["ADMIN"] },
        });
        var ship = w.Seat(1, "ADMIN", 0);
        w.Mission.Stage = MissionStage.Started;
        for (int i = 0; i < 3; i++)
            w.Send(1, new ChatMessage { Message = $"guess{i} relocate {ship.Id} 0 0 0 0", ChatTarget = (int)ChatTarget.Everyone });
        Assert.True(w.Server.CredentialGuesses.ConnectionExhausted(new ConnectionId(1)));
    }

    /// <summary>A CORRECT admin chat secret is never counted — the privileged
    /// operator (and the MCP harness) are unaffected.</summary>
    [SkippableFact]
    public void A_correct_admin_chat_secret_is_never_counted()
    {
        using var w = new Booted(c => c.LiveSettings = new LiveSettingsConfig
        {
            AdminCheats = new AdminCheatsConfig { Enabled = true, Secret = "admin-secret-1", AllowNames = ["ADMIN"] },
        });
        var ship = w.Seat(1, "ADMIN", 0);
        w.Mission.Stage = MissionStage.Started;
        for (int i = 0; i < 5; i++)
            w.Send(1, new ChatMessage { Message = $"admin-secret-1 relocate {ship.Id} 0 0 0 0", ChatTarget = (int)ChatTarget.Everyone });
        Assert.False(w.Server.CredentialGuesses.ConnectionExhausted(new ConnectionId(1)));
        Assert.Equal(0d, w.Server.CredentialGuesses.LockoutRemaining("198.51.100.10", w.Server.SimNow), 3);
    }

    /// <summary>A wrong harness bearer at logon (a peer claiming the harness
    /// prefix) counts per source address: after five guesses that address is in
    /// back-off and a sixth logon is refused without evaluation.</summary>
    [SkippableFact]
    public void A_wrong_harness_bearer_at_logon_is_counted_per_address()
    {
        using var w = new Booted(c => c.LiveSettings = new LiveSettingsConfig
        {
            TestHarness = new TestHarnessConfig { Enabled = true, Secret = "harness-secret-1", CharacterNamePrefix = "mcp-" },
        });
        MapAddresses(w.Server, (1, SourceA), (2, SourceA), (3, SourceA), (4, SourceA), (5, SourceA), (6, SourceA), (7, SourceB));
        for (int conn = 1; conn <= 5; conn++)
        {
            var ack = w.Logon(conn, Booted.Request($"mcp-bot{conn}", cdKey: $"guess{conn}"));
            Assert.True(ack.FValidated, $"conn {conn}: {ack.CharNameOrFailureReason} ({w.Tally()})");
        }
        Assert.True(w.Server.CredentialGuesses.LockoutRemaining("198.51.100.10", w.Server.SimNow) > 0);

        w.Send(6, Booted.Request("mcp-bot6", cdKey: "guess6"));
        Assert.Empty(w.SentTo(6, LogonAck.Fmid));

        var other = w.Logon(7, Booted.Request("mcp-bot7", cdKey: "harness-secret-1"));   // another address, right secret
        Assert.True(other.FValidated, other.CharNameOrFailureReason);
    }

    /// <summary>An ordinary client's placeholder CD key is NOT a secret attempt:
    /// a whole lobby of them from one address is admitted (the finding's
    /// "correct credentials / normal play are unaffected").</summary>
    [SkippableFact]
    public void An_ordinary_placeholder_cd_key_is_not_counted_as_a_guess()
    {
        using var w = new Booted(c => c.LiveSettings = new LiveSettingsConfig
        {
            AdminCheats = new AdminCheatsConfig { Enabled = true, Secret = "admin-secret-1", AllowNames = ["ADMIN"] },
            TestHarness = new TestHarnessConfig { Enabled = true, Secret = "harness-secret-1", CharacterNamePrefix = "mcp-" },
        });
        MapAddresses(w.Server, Enumerable.Range(1, 10).Select(i => (i, SourceA)).ToArray());
        for (int conn = 1; conn <= 10; conn++)
        {
            var ack = w.Logon(conn, Booted.Request($"PILOT{conn}", cdKey: "ABCDEFGHIJKL"));
            Assert.True(ack.FValidated, ack.CharNameOrFailureReason);
        }
        Assert.Equal(0d, w.Server.CredentialGuesses.LockoutRemaining("198.51.100.10", w.Server.SimNow), 3);
    }
}
