using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P219 — live loopback proof of the deviation/staleness-based heavy ship-update
/// budget (<see cref="Srv.BroadcastShipsUpdate"/>), the follow-up P139 named but
/// never built. Native FedSrv sorts the per-cluster ship-update candidates by
/// each ship's accumulated staleness (<c>CFSShip::m_deviation</c>,
/// <c>FedSrv.CPP:3923</c>) and serves a fixed quota of
/// <c>cHeavyPerInterval + cLightPerInterval = 4</c> per peer per tick
/// (<c>FedSrv.CPP:3844-3845</c>); ships skipped accrue deviation and rise to the
/// front next tick — a fair round-robin. This port folds the (unwired) LIGHT
/// tier into HEAVY, so each peer's HEAVY entry count is bounded to 4 per tick
/// regardless of how many ships share the cluster — the fix for the measured
/// O(peers²) per-tick broadcast cost.
///
/// The three verification targets, each using ONE seated bot peer plus N ownerless
/// NPC ships in the same cluster (so only the single peer's per-player loop mutates
/// the shared <see cref="Ship.Deviation"/> — a clean rotation):
/// <list type="number">
/// <item><b>bounded:</b> with 6 candidates (peer + 5 NPCs) each HEAVY update carries
/// exactly 4 entries, never the full 5 others — proportional-to-ship-count growth is
/// gone;</item>
/// <item><b>fairness:</b> a ship dropped one tick is served the next (it rose to the
/// front via its +1 staleness bump), and over a window every ship is served — no
/// single ship is starved forever;</item>
/// <item><b>locked target:</b> a peer's <see cref="Ship.CommandTarget"/> is always
/// included in slot 0 regardless of its own deviation.</item>
/// </list>
/// Harness mirrors the P139 <c>ShipsUpdateLiveTests</c> boot (in-process server,
/// no VM), with the single-peer + NPC shape of <c>NpcCrossClusterLiveTests</c>.
/// </summary>
[Collection("LiveNetwork")]
public sealed class ShipUpdateBudgetLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

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
            new Gm { Name = "Ship Update Budget Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 993u, DwCookie = 0x993u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99100000000000001ul, DrmHash = "shipbudget",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A booted server with ONE seated bot peer on side 0 plus
    /// <paramref name="npcCount"/> ownerless NPC ships in the same
    /// <c>Sectors[0]</c> cluster — so the single peer is the only per-player
    /// assembly loop mutating the shared deviations (clean, deterministic
    /// rotation). The peer parks at the origin; the NPCs line up +100 apart.</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public FltBot Bot = null!;
        public Ship Peer = null!;
        public List<Ship> Npcs = new();
        public Sector Sector = null!;

        public void Dispose()
        {
            Bot?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatBudget(int npcCount)
    {
        var seat = new Seated();
        seat.Server = new Srv();
        Mission? mission = null;
        short peerSide = 0;

        seat.Server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            seat.Sector = m.Sectors[0];
            peerSide = m.Sides[0].Id;
            var hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var peer = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peer, seat.Sector);
            return (peer, seat.Sector);
        };
        seat.Server.Boot(Config());
        var port = seat.Server.BoundPort;

        try
        {
            seat.Bot = new FltBot();
            seat.Bot.Connect(port, "p219_budget");
            seat.Bot.SendLogonRequest(Request("p219_budget"));
            PumpUntil(seat.Server, () => mission is not null && seat.Bot.ShipAssignment is not null, 4000);
            Assert.NotNull(mission);
            Assert.NotNull(seat.Bot.ShipAssignment);

            seat.Peer = seat.Server.Ships.First(s => s.Ship.Id == seat.Bot.ShipAssignment!.Ship.ShipId).Ship;
            Assert.NotNull(seat.Peer);

            // Ownerless NPCs in the SAME cluster (candidates regardless of side).
            short npcSide = peerSide == 0 ? (short)1 : (short)0;
            for (int i = 0; i < npcCount; i++)
                seat.Npcs.Add(seat.Server.SpawnNpcShip(mission!, seat.Sector, npcSide, new Vector3(200f + 100f * i, 0f, 0f)));

            Assert.Equal(npcCount, seat.Npcs.Count);
            return seat;
        }
        catch
        {
            seat.Dispose();
            throw;
        }
    }

    private static int ServedCount(HeavyShipsUpdate u) => u.HeavyShipEntries.Length;

    /// <summary>⭐ Bounded: with 6 candidates in the cluster (peer + 5 NPCs),
    /// each peer's HEAVY update carries exactly 4 entries every tick — never the
    /// full 5 "other" ships. Proves the budget bites (cost no longer scales with
    /// ship count) while the fast path (≤ 2 candidates) is untouched elsewhere.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Heavy_updates_are_bounded_to_the_budget_not_proportional_to_ship_count()
    {
        Require.Artwork();
        using var seat = SeatBudget(5);
        seat.Bot.ResetHeavyShipsUpdates();

        PumpUntil(seat.Server, () => seat.Bot.HeavyShipsUpdates.Count >= 8, 8000);
        Assert.True(seat.Bot.HeavyShipsUpdates.Count >= 8,
            "the peer must accrue HEAVY updates over several ticks");

        var updates = seat.Bot.HeavyShipsUpdates.TakeLast(8).ToList();
        foreach (var u in updates)
            Assert.Equal(4, ServedCount(u));   // 2 heavy + 2 light-folded, not the 5 other candidates

        Assert.True(seat.Npcs.Count > 4, "sanity: 5 others would be sent if the budget were absent");

        Console.WriteLine($"P219-BOUND: peer sees exactly {ServedCount(updates[0])} heavy entries " +
                          $"per tick with {seat.Npcs.Count + 1} ships in the cluster (was unbounded/O(peers²) pre-P219)");
    }

    /// <summary>⭐ Fairness: with 5 others and a budget of 4, exactly one ship is
    /// dropped per tick; the dropped ship gets its staleness bumped (+1) so it
    /// must be served the NEXT tick — a fair round-robin, no permanent starvation.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ship_dropped_one_tick_is_served_the_next_no_permanent_starvation()
    {
        Require.Artwork();
        using var seat = SeatBudget(5);
        seat.Bot.ResetHeavyShipsUpdates();

        PumpUntil(seat.Server, () => seat.Bot.HeavyShipsUpdates.Count >= 6, 10000);
        var updates = seat.Bot.HeavyShipsUpdates.TakeLast(6).ToList();
        Assert.Equal(6, updates.Count);

        short? prevDropped = null;
        for (int t = 0; t < updates.Count; t++)
        {
            var served = updates[t].HeavyShipEntries.Select(e => e.ShipId).ToHashSet();
            var dropped = seat.Npcs.Single(n => !served.Contains(n.Id)).Id;

            if (prevDropped is { } pd)
                Assert.True(served.Contains(pd),
                    $"the ship dropped at tick {t - 1} ({pd}) must be served at tick {t} (deviation bump rose it to the front)");
            prevDropped = dropped;
        }

        // No permanent starvation: within the window every NPC is served at least once.
        var union = new HashSet<short>();
        foreach (var u in updates) foreach (var e in u.HeavyShipEntries) union.Add(e.ShipId);
        Assert.All(seat.Npcs, n => Assert.Contains(n.Id, union));

        Console.WriteLine($"P219-FAIR: dropped ship rotates each tick; over {updates.Count} ticks all " +
                          $"{seat.Npcs.Count} ships served at least once (no single ship starved)");
    }

    /// <summary>⭐ Locked target: a peer's command target (<see cref="Ship.CommandTarget"/>)
    /// is always promoted to the first heavy slot regardless of its own deviation —
    /// P139's target promotion composes with the budget by consuming one heavy slot
    /// before the other candidates compete.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Locked_target_is_always_included_regardless_of_its_deviation()
    {
        Require.Artwork();
        using var seat = SeatBudget(5);
        var target = seat.Npcs[0];
        seat.Peer.CommandTarget = target;   // lock the target that would otherwise rotate/starve
        seat.Bot.ResetHeavyShipsUpdates();

        PumpUntil(seat.Server, () => seat.Bot.HeavyShipsUpdates.Count >= 10, 12000);
        var updates = seat.Bot.HeavyShipsUpdates.TakeLast(10).ToList();
        Assert.Equal(10, updates.Count);

        foreach (var u in updates)
        {
            Assert.Equal(target.Id, u.HeavyShipEntries[0].ShipId);   // target always in slot 0
            Assert.Equal(1, u.HeavyShipEntries.Count(e => e.ShipId == target.Id));   // exactly once
            Assert.Equal(4, ServedCount(u));   // still budget-bounded (target + 3 others)
        }

        Console.WriteLine($"P219-TARGET: locked target {target.Id} present in slot 0 of every update " +
                          $"(budget still caps total entries at {ServedCount(updates[0])}); " +
                          $"target hull {updates[0].BpTargetHull}/240 shield {updates[0].BpTargetShield}/240");
    }
}
