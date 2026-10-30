using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P894 / LOB-0020, LOB-0026, LOB-0004 — the minimum-client-version gate at the
/// logon the port's client actually performs.
///
/// <para><b>Native.</b> The lobby refused a client whose LOGON <c>crcFileList</c>
/// did not match its official list, sending <c>AUTO_UPDATE_INFO</c> and dropping the
/// connection (<c>lobby/client.cpp:272-288</c>), and NACKed a mismatched
/// <c>verLobby</c> (<c>:290-309</c>). The port's client connects straight to a game
/// server, so the game server's LOGONREQ handler is the logon point: the optional
/// <c>P894</c> client-version extension is compared with the server's advertised
/// minimum and an older (or silent) client is refused with a reason that names the
/// required build and the updater. The existing P871 file-set CRC gate
/// (<see cref="Cfg.RequireFileList"/>) is unchanged and still applies first.</para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P894VersionGateTests
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config(string name, string? minimum, string? legacyLobbyMinimum = null) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,
        MinimumClientVersion = minimum,
        Lobby = legacyLobbyMinimum is null ? null : new global::Allegiance.Server.LobbyConfig
        {
            Url = "http://127.0.0.1:1",
            DisplayName = "unused",
            PublicAddress = "127.0.0.1",
            MinimumClientVersion = legacyLobbyMinimum,
        },
        Games = [new Gm { Name = name, Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static LogonRequest Request(string? clientVersion) => new()
    {
        CharacterName = "VERCHECK",
        ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL",
        MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer,
        Time = 894u,
        DwCookie = uint.MaxValue,
        CrcFileList = -1,
        ClientVersion = clientVersion,
        SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0,
        SteamID = 89400000000000001ul,
        DrmHash = "p894ver",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static LogonAck? Attempt(Cfg config, string? clientVersion)
    {
        using var server = new Srv();
        server.Boot(config);
        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "VERCHECK");
        session.SendLogonRequest(Request(clientVersion));
        int ticks = PumpUntil(server, () => session.Ack is not null, 4000);
        Assert.True(ticks < 4000, "timed out waiting for LOGONACK");
        return session.Ack;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void An_older_client_is_refused_with_the_update_pointer()
    {
        Require.Artwork();
        var ack = Attempt(Config("Version Gate", minimum: "2.0.0"), clientVersion: "1.9.9");

        Assert.NotNull(ack);
        Assert.False(ack!.FValidated);
        Assert.Equal((short)0, ack.ShipID);
        Assert.Contains("older than this server's minimum", ack.CharNameOrFailureReason);
        Assert.Contains("2.0.0", ack.CharNameOrFailureReason);
        Assert.Contains("update", ack.CharNameOrFailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_client_that_reports_no_version_is_refused_when_a_minimum_is_required()
    {
        Require.Artwork();
        var ack = Attempt(Config("Version Gate", minimum: "2.0.0"), clientVersion: null);

        Assert.NotNull(ack);
        Assert.False(ack!.FValidated);
        // Fail-closed, like native's unknown CRC with AutoUpdate configured
        // (lobby/client.cpp:272).
        Assert.Contains("did not report a version", ack.CharNameOrFailureReason);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_current_or_newer_client_is_seated()
    {
        Require.Artwork();
        var same = Attempt(Config("Version Gate", minimum: "2.0.0"), clientVersion: "2.0.0");
        Assert.True(same!.FValidated, $"current client refused: {same.CharNameOrFailureReason}");

        var newer = Attempt(Config("Version Gate", minimum: "2.0.0"), clientVersion: "2.1.4");
        Assert.True(newer!.FValidated, $"newer client refused: {newer.CharNameOrFailureReason}");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_server_without_a_minimum_admits_every_client()
    {
        Require.Artwork();
        // No minimum (the default): pre-P894 clients that send no version keep
        // playing, exactly like a native lobby with AutoUpdate unconfigured.
        var silent = Attempt(Config("No Gate", minimum: null), clientVersion: null);
        Assert.True(silent!.FValidated, $"ungated server refused: {silent.CharNameOrFailureReason}");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void The_legacy_lobby_block_minimum_is_enforced_and_advertised()
    {
        Require.Artwork();
        // P810's advertised-only lobby field still works as the minimum the logon
        // gate enforces (one source of truth: ResolveMinimumClientVersion).
        var ack = Attempt(Config("Legacy Gate", minimum: null, legacyLobbyMinimum: "3.0.0"), clientVersion: "2.9.0");
        Assert.False(ack!.FValidated);
        Assert.Contains("3.0.0", ack.CharNameOrFailureReason);
    }

    [Fact]
    public void The_resolved_minimum_prefers_the_top_level_setting()
    {
        var config = new Cfg
        {
            MinimumClientVersion = "2.0.0",
            Lobby = new global::Allegiance.Server.LobbyConfig { MinimumClientVersion = "1.0.0" },
        };
        Assert.Equal("2.0.0", config.ResolveMinimumClientVersion());

        config.MinimumClientVersion = "  ";
        Assert.Equal("1.0.0", config.ResolveMinimumClientVersion());

        config.Lobby = null;
        Assert.Null(config.ResolveMinimumClientVersion());
    }

    [Fact]
    public void The_environment_override_sets_the_minimum()
    {
        var config = new Cfg();
        config.ApplyMinimumClientVersionEnvironment(" 4.2.0 ");
        Assert.Equal("4.2.0", config.ResolveMinimumClientVersion());
        config.ApplyMinimumClientVersionEnvironment(null);
        Assert.Equal("4.2.0", config.MinimumClientVersion);
    }
}
