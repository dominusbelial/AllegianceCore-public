using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P871 / autoupdate::crcFileList — live proof that the server enforces the
/// client's reported file-set CRC (<c>LOGONREQ.crcFileList</c>,
/// <c>Inc/Messages.h:44</c>) against the supported release: a stale or forged
/// client is refused with the update-flow reason, the matching client is seated,
/// and a server with the gate off keeps the pre-P871 behaviour.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P871FileListLogonTests
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
    private const int ExpectedCrc = 0x51A7C0DE;

    private static Cfg Config(string name, bool require) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,
        RequireFileList = require,
        ExpectedFileListCrc = ExpectedCrc,
        Games = [new Gm { Name = name, Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static LogonRequest Request(int crc) => new()
    {
        CharacterName = "FILECHECK",
        ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL",
        MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer,
        Time = 933u,
        DwCookie = uint.MaxValue,
        CrcFileList = crc,
        SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0,
        SteamID = 87100000000000001ul,
        DrmHash = "p871crc",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static ConnectionSession Attempt(Cfg config, int crc)
    {
        using var server = new Srv();
        server.Boot(config);
        var session = new ConnectionSession();
        session.Connect(server.BoundPort, "FILECHECK");
        session.SendLogonRequest(Request(crc));
        int ticks = PumpUntil(server, () => session.Ack is not null, 4000);
        Assert.True(ticks < 4000, "timed out waiting for LOGONACK");
        return session;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_client_that_omits_or_mismatches_the_file_list_is_refused_with_the_update_reason()
    {
        Require.Artwork();
        // Native "checks disabled" (0 / -1) is refused when a file set is required,
        // so a forged client cannot just omit the value.
        using var omitted = Attempt(Config("File List", require: true), FileListCrc.Unknown);
        Assert.NotNull(omitted.Ack);
        Assert.False(omitted.Ack!.FValidated);
        Assert.Contains("file set", omitted.Ack.CharNameOrFailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((short)0, omitted.Ack.ShipID);

        using var stale = Attempt(Config("File List", require: true), ExpectedCrc ^ 0x1);
        Assert.NotNull(stale.Ack);
        Assert.False(stale.Ack!.FValidated);
        Assert.Contains("release", stale.Ack.CharNameOrFailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void The_matching_file_list_is_seated_and_the_gate_is_off_by_default()
    {
        Require.Artwork();
        using var match = Attempt(Config("File List", require: true), ExpectedCrc);
        Assert.True(match.Ack!.FValidated, $"matched file set refused: {match.Ack.CharNameOrFailureReason}");

        // Pre-P871 servers accept any client (auto-update unconfigured).
        using var legacy = Attempt(Config("File List", require: false), -1);
        Assert.True(legacy.Ack!.FValidated, $"legacy logon refused: {legacy.Ack.CharNameOrFailureReason}");
    }
}
