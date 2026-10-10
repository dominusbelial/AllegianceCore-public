using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// M9 — proof that <c>TcpTransport._threads</c> stays bounded across
/// connection churn. Each peer connection adds a read-loop thread via
/// <see cref="Allegiance.Core.Transport.TcpTransport.Spawn"/>, which now
/// prunes itself when its loop ends, so the tracked-thread count returns to
/// the baseline (just the host's accept loop) after every disconnect instead
/// of growing monotonically.
/// </summary>
[Collection("LiveNetwork")]
public class TcpTransportThreadLeakTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Thread Leak Test", Map = "XXV_simple.igc", Teams = 1, CivIds = [1], SideNames = ["Alpha"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 908u, DwCookie = 0x908u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 96000000000000000ul, DrmHash = "threadleak",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Server_transport_thread_count_stays_bounded_across_connection_churn()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby

        var transport = server.Host.Transport!;
        int baseline = transport.TrackedThreadCount();
        Assert.True(baseline >= 1, "host transport should track at least its accept loop thread");

        for (int cycle = 0; cycle < 3; cycle++)
        {
            using var bot = new FltBot();
            bot.Connect(server.BoundPort, $"m9_bot{cycle}");
            bot.SendLogonRequest(Request($"M9BOT{cycle}"));
            int got = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
            Assert.True(got < 4000, $"cycle {cycle}: no ship assignment");
            Assert.True(transport.TrackedThreadCount() > baseline,
                $"cycle {cycle}: a connected peer should add a tracked read-loop thread");

            bot.Dispose();   // abrupt drop → read loop ends → Spawn self-prunes
            int settled = PumpUntil(server, () => transport.TrackedThreadCount() == baseline, 2500);
            Assert.True(settled < 2500,
                $"cycle {cycle}: tracked thread count did not return to baseline " +
                $"({transport.TrackedThreadCount()} != {baseline})");
        }

        Console.WriteLine($"M9-LIVE: baseline {baseline} tracked threads; after 3 connect/disconnect cycles " +
                          $"still {transport.TrackedThreadCount()} (no unbounded growth)");
    }
}
