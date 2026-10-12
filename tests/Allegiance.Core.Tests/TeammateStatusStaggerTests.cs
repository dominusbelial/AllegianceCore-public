using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P604b — the ~5 s periodic <c>broadcastShipStates</c> hitch. The periodic
/// same-side TEAMMATE SHIP_STATUS(90) refresh (P58,
/// <see cref="Srv.TeammateStatusRefreshTicks"/>=100) previously fired for EVERY
/// (recipient, subject) pair on the same aligned tick — every pair's `last` is
/// seeded to ~the same join-burst tick, so every 100th tick sent an O(P²)
/// SHIP_STATUS burst (measured as a ~36 ms <c>bss.mates</c> max here vs ~1.2 s
/// on the weaker/shared 221 box). P604b staggers the refresh PER RECIPIENT so
/// the ~P/interval recipients due per tick are spread (never the whole fleet).
///
/// These tests drive <see cref="Srv.BroadcastShipStates"/> via the internal
/// seams over many ticks and assert the fixed algorithm's contract:
/// <list type="number">
/// <item>after the join-burst first-status, no single tick has more than a
/// small bound of recipients refreshing their mates (the OLD code fired ALL of
/// them together on the aligned 100th tick);</item>
/// <item>every (recipient, mate) pair still receives a status on its first tick
/// and at least one periodic refresh within a couple of intervals — cadence is
/// preserved, only the simultaneity is removed.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public sealed class TeammateStatusStaggerTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private static short _nextFakeShipId = 30000;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P604b Teammate Status Stagger Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    /// <summary>Seed <paramref name="peerCount"/> same-side (side 0) peers in
    /// Sectors[0] with sequential ConnectionIds (700+i), so their refresh
    /// phases (ConnectionId % interval) land on distinct ticks.</summary>
    private static List<(ConnectionId Conn, Ship Ship, Sector Sector)> BuildWorld(Srv server, DataHullTypeIGC hull, int peerCount)
    {
        var sect0 = server.TestSectors[0];
        var peers = new List<(ConnectionId, Ship, Sector)>();
        for (int i = 0; i < peerCount; i++)
        {
            var ship = new Ship(_nextFakeShipId++, hull, new Vector3(i * 25.0f, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            server.TestMission!.PlaceShip(ship, sect0);
            peers.Add((new ConnectionId((uint)(700 + i)), ship, sect0));
        }
        return peers;
    }

    /// <summary>For one broadcast tick, the set of DISTINCT recipients that
    /// emitted a TEAMMATE SHIP_STATUS(90) (fmid 90, ShipId != the recipient's
    /// own ship) — the periodic refresh signal the hitch lives in.</summary>
    private static HashSet<ConnectionId> RecipientsEmittingTeammateStatus(Srv server, uint tick, IReadOnlyDictionary<ConnectionId, short> ownShip)
    {
        var outg = server.TestCollectBroadcast(tick, parallel: false, step: "shipStates");
        var emitters = new HashSet<ConnectionId>();
        foreach (var (conn, pkts) in outg)
        {
            foreach (var pkt in pkts)
            {
                foreach (var msg in FedPacket.Unpack(pkt))
                {
                    ushort fmid = (ushort)(msg[2] | (msg[3] << 8));
                    if (fmid == ShipStatusMessage.Fmid)
                    {
                        var st = ShipStatusMessage.Decode(msg);
                        if (st.ShipId != ownShip[conn]) { emitters.Add(conn); goto nextPeer; }
                    }
                }
            }
            nextPeer:;
        }
        return emitters;
    }

    /// <summary>⭐ Contract 1 — the periodic teammate-status refresh is
    /// staggered per recipient, never synchronized into an O(P) one-tick burst.
    /// With 6 same-side peers on distinct phases, at most ONE recipient should
    /// refresh on any steady-state tick; the OLD aligned scheme fired all 6
    /// together every TeammateStatusRefreshTicks.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Teammate_status_refresh_is_staggered_not_synchronized()
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;   // isolate the same-side PEER teammate refresh from the P366 NPC-status refresh
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        const int peerCount = 6;
        var peers = BuildWorld(server, Core().HullTypes.Values.First(), peerCount);
        var ownShip = peers.ToDictionary(p => p.Conn, p => p.Ship.Id);

        server.TestResetBroadcastState();
        server.TestSeedPeers(peers);
        server.TestSetShipUpdateModel(true);

        int maxRecipientsPerTick = 0;
        for (uint t = 0; t < 150; t++)
        {
            var emitters = RecipientsEmittingTeammateStatus(server, t, ownShip);
            if (t >= 1)   // skip tick 0: the join-burst first-status fires for everyone
                maxRecipientsPerTick = Math.Max(maxRecipientsPerTick, emitters.Count);
        }
        Console.WriteLine($"P604b-STAGGER: max recipients emitting teammate-status on one tick (steady state) = {maxRecipientsPerTick} of {peerCount}");
        Assert.True(maxRecipientsPerTick <= 2,
            $"teammate-status refresh synchronized: {maxRecipientsPerTick}/{peerCount} recipients emitted on one tick (expected ≤ 2, the O(P²) burst is gone)");
    }

    /// <summary>⭐ Contract 2 — cadence preserved: every (recipient, mate) pair
    /// gets its very first teammate status (join burst, tick 0) AND at least one
    /// periodic refresh within the measurement window — the stagger removes the
    /// simultaneity, not the refreshes.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Teammate_status_refresh_cadence_is_preserved()
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;   // isolate from the P366 NPC-status refresh (would inflate the pair count)
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        const int peerCount = 4;
        var peers = BuildWorld(server, Core().HullTypes.Values.First(), peerCount);
        var ownShip = peers.ToDictionary(p => p.Conn, p => p.Ship.Id);

        server.TestResetBroadcastState();
        server.TestSeedPeers(peers);
        server.TestSetShipUpdateModel(true);

        // Count, per (recipient, mate) pair, how many teammate statuses arrived.
        var seen = new Dictionary<(ConnectionId, short), int>();
        for (uint t = 0; t < 250; t++)
        {
            var outg = server.TestCollectBroadcast(t, parallel: false, step: "shipStates");
            foreach (var (conn, pkts) in outg)
            {
                foreach (var pkt in pkts)
                {
                    foreach (var msg in FedPacket.Unpack(pkt))
                    {
                        ushort fmid = (ushort)(msg[2] | (msg[3] << 8));
                        if (fmid == ShipStatusMessage.Fmid)
                        {
                            var st = ShipStatusMessage.Decode(msg);
                            if (st.ShipId == ownShip[conn]) continue;
                            var key = (conn, st.ShipId);
                            seen.TryGetValue(key, out var c);
                            seen[key] = c + 1;
                        }
                    }
                }
            }
        }
        // Every (recipient, mate) pair must be seen at least twice in 250 ticks
        // (tick-0 first-status + ≥1 periodic refresh at phase+100).
        int pairs = peerCount * (peerCount - 1);
        int underDelivered = seen.Count(kv => kv.Value < 2);
        Assert.Equal(pairs, seen.Count);
        Assert.True(underDelivered == 0, $"{(pairs - underDelivered)}/{pairs} pairs refreshed < 2× in 250 ticks — cadence broken by the stagger");
        Console.WriteLine($"P604b-CADENCE: {seen.Count} (recipient,mate) pairs, min deliveries = {seen.Values.Min()} over 250 ticks (first + periodic refresh preserved)");
    }
}
