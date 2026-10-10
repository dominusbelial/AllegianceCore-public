using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// M1 — proof that <c>ReleasePeer</c> now frees every connection-scoped cache
/// keyed by a departing peer, not just <c>_peerShips</c>/<c>_peerNames</c>/
/// <c>_playerReady</c>/<c>_pendingSideSelect</c>/<c>_pendingJoinRequests</c>.
/// Two same-side peers populate the status/cluster/ping and teammate
/// (recipient, ship) caches via the per-tick broadcast passes, then are
/// disconnected; the test asserts every such cache returns to zero rather than
/// growing monotonically with churn. Repeated across two connect/disconnect
/// cycles so any leak would compound (2x entries) and be unmistakable.
/// </summary>
[Collection("LiveNetwork")]
public class ConnectionCacheCleanupTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // Teams=1 so BOTH peers land on the same side → the teammate caches
    // (_teammateExportSent / _teammatePlayerInfoSent / _teammateLoadoutVersionSent
    // / _teammateLoadoutCache / _lastTeammateStatusTick) actually populate.
    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Cache Cleanup Test", Map = "XXV_simple.igc", Teams = 1, CivIds = [1], SideNames = ["Alpha"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 902u, DwCookie = 0x902u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 97000000000000000ul, DrmHash = "cachecleanup",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>True once every connection-scoped cache has drained to zero —
    /// the M1 invariant after all peers leave.</summary>
    private static bool AllDrained(Srv server)
    {
        var c = server.ConnectionScopedCacheCounts();
        return c.PendingSideSelect == 0 && c.PeerNames == 0 && c.PlayerReady == 0
            && c.LastStatusSector == 0 && c.LastStatusTick == 0
            && c.LastClusterInfoTick == 0 && c.LastPingQuery == 0
            && c.TeammateExportSent == 0 && c.TeammatePlayerInfoSent == 0
            && c.TeammateLoadoutVersionSent == 0 && c.TeammateLoadoutCache == 0
            && c.CrossSideExportSent == 0
            && c.LastTeammateStatusTick == 0;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Connection_scoped_caches_drain_to_zero_after_repeated_disconnects()
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;   // P382: occupant-free home stations — the M1 drain-to-zero invariant counts connection churn, not persistent NPC subjects
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby

        for (int cycle = 0; cycle < 2; cycle++)
        {
            var bots = new[] { new FltBot(), new FltBot() };
            try
            {
                for (int i = 0; i < bots.Length; i++)
                {
                    bots[i].Connect(server.BoundPort, $"m1_c{cycle}_b{i}");
                    bots[i].SendLogonRequest(Request($"M1C{cycle}P{i}"));
                }
                int got = PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
                Assert.True(got < 4000, $"cycle {cycle}: no ship assignments");

                // Let the per-tick broadcast passes populate the caches.
                PumpUntil(server, () => false, 400);

                var before = server.ConnectionScopedCacheCounts();
                // Non-vacuous: the connection-keyed status caches and the
                // same-side teammate caches must actually have grown, so this
                // test is exercising the leak rather than asserting on empty
                // collections.
                Assert.True(before.LastStatusSector >= 1 && before.LastStatusTick >= 1,
                    $"cycle {cycle}: status caches never populated ({before})");
                Assert.True(before.TeammateExportSent >= 1 && before.TeammatePlayerInfoSent >= 1,
                    $"cycle {cycle}: teammate caches never populated ({before})");
                Assert.True(before.TeammateLoadoutVersionSent >= 1 || before.TeammateLoadoutCache >= 1,
                    $"cycle {cycle}: teammate loadout caches never populated ({before})");

                foreach (var b in bots) b.Dispose();   // abrupt drop → ReleasePeer

                int settled = PumpUntil(server, () => AllDrained(server), 1600);
                Assert.True(settled < 1600,
                    $"cycle {cycle}: caches did not drain to zero after disconnect: {server.ConnectionScopedCacheCounts()}");

                Console.WriteLine($"M1-LIVE: cycle {cycle} populated " +
                                  $"{before.TeammateExportSent} export / {before.TeammatePlayerInfoSent} playerinfo / " +
                                  $"{before.TeammateLoadoutVersionSent} loadout-version / {before.TeammateLoadoutCache} loadout-cache / " +
                                  $"{before.LastStatusSector} status entries, all drained to zero after disconnect");
            }
            finally
            {
                foreach (var b in bots) b.Dispose();
            }
        }
    }
}
