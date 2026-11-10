using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P991 — <c>broadcast.shipStatusChangeGate</c>: every <c>FM_S_SHIP_STATUS(90)</c>
/// send becomes native's per-side change gate
/// (<c>FedSrv/FedSrv.CPP:3665-3719</c>: every other frame, per side, per ship,
/// <c>if (*pss != *pssOld)</c> → send to the side group, then copy into the old
/// status), with native's join-time per-ship dump
/// (<c>FedSrv/fsmission.cpp:4303-4323</c>) replacing the port's three periodic
/// refreshes. These tests drive the real broadcast suffix
/// (<c>TestProjectRecordedTick</c>: cross-side status sweep → ship detection →
/// ship states → ship updates → …) against deterministic seeded worlds and read
/// the SHIP_STATUS frames back off the publication sink.
/// <para>Each fixture sets the flag through <c>BroadcastConfig.ShipStatusChangeGate</c>.
/// <c>ALLEGIANCE_BROADCAST_SHIP_STATUS_CHANGE_GATE</c> in the ambient environment WINS over that
/// config (env-wins, like every other <c>broadcast.*</c> switch), so a lane that exported it globally
/// would flip the flag-ON fixtures back off; the Fast-tier runs and the gate records in the report
/// were taken without it set.</para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P991ShipStatusChangeGateTests
{
    private sealed record Status(uint Tick, uint Peer, ShipStatusMessage Message)
    {
        public short ShipId => Message.ShipId;
    }

    /// <summary>One deterministic world: <paramref name="peersPerSide"/> peers on
    /// each of two sides, all flying in sector 0, plus one same-side NPC on side 0
    /// and a second sector to warp into. Cross-side visibility is set explicitly
    /// (that is what the P132 sweep would latch) so the cross-side path fires.</summary>
    private sealed class Fixture : IDisposable
    {
        public required Srv Server;
        public required List<(ConnectionId Conn, Ship Ship, Sector Sector)> Peers;
        public required Sector Home;
        public required Sector Elsewhere;
        public required Ship Npc;
        public List<Status> Statuses { get; } = new();
        public uint Tick { get; private set; }

        public void Dispose()
        {
            Server.TestPublicationCapture = null;
            Server.Dispose();
        }

        /// <summary>Run the whole broadcast suffix for <paramref name="ticks"/>
        /// ticks, capturing every SHIP_STATUS frame each peer receives.</summary>
        public void Run(int ticks, Action<int>? beforeTick = null)
        {
            var captured = Statuses;
            Server.TestPublicationCapture = (peer, bytes, key, guaranteed) =>
            {
                foreach (var message in FedPacket.Unpack(bytes))
                    if ((ushort)(message[2] | message[3] << 8) == ShipStatusMessage.Fmid)
                        lock (captured) captured.Add(new(Tick, peer.Value, ShipStatusMessage.Decode(message)));
            };
            try
            {
                for (int i = 0; i < ticks; i++)
                {
                    beforeTick?.Invoke(i);
                    Server.TestProjectRecordedTick(Tick, snapshot: false);
                    Tick++;
                }
            }
            finally { Server.TestPublicationCapture = null; }
        }

        /// <summary>Re-seed the per-peer records (keeps every broadcast latch, so a
        /// status change can be driven by moving/docking a subject) with the same
        /// conns and the given ships/sectors.</summary>
        public void Reseed(Func<Ship, Sector>? sectorOf = null)
        {
            Server.TestSeedPeers(Peers.Select(p => (p.Conn, p.Ship, sectorOf?.Invoke(p.Ship) ?? p.Sector)).ToList());
        }

        /// <summary>Move a ship to another sector the way the sim does (it leaves
        /// the old sector's model list and joins the new one), so the cross-side
        /// sweep's <c>ship.Sector</c> reads the new cluster.</summary>
        public void MoveShip(Ship ship, Sector sector)
        {
            ship.Sector!.Remove(ship);
            Server.TestMission!.PlaceShip(ship, sector);
        }

        public void SetSeenForAll(short seeingSide, bool seen)
        {
            foreach (var (_, ship, _) in Peers)
                ship.SideVisibility.SetSideVisibility(seeingSide, seen);
            Npc.SideVisibility.SetSideVisibility(seeingSide, seen);
        }

        public int CountFor(short subject) => Statuses.Count(s => s.ShipId == subject);
    }

    private static Cfg Config(bool gate, bool parallel = false) => new()
    {
        ArtPath = TestArtRoot.Value,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Broadcast = new()
        {
            ShipStatusChangeGate = gate,
            ParallelBuild = parallel,
            MaxDegreeOfParallelism = 4,
            ShipUpdateModel = "native",
        },
        Games =
        [
            new() { Name = "P991", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static Fixture Boot(bool gate, bool parallel = false, int peersPerSide = 3)
    {
        var server = new Srv { SpawnInitialMinersAtBoot = false };
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        server.Boot(Config(gate, parallel));
        var home = server.TestSectors[0];
        var elsewhere = server.TestSectors[1];
        var hull = server.TestMission!.Core.HullTypes.Values.First();
        var peers = new List<(ConnectionId, Ship, Sector)>();
        for (int side = 0; side < 2; side++)
            for (int i = 0; i < peersPerSide; i++)
            {
                short id = (short)(20000 + side * 100 + i);
                var ship = new Ship(id, hull, new Vector3(id * 10, side * 500, 0), Vector3.Zero, Orientation.Identity)
                    { SideId = (short)side };
                server.TestMission.PlaceShip(ship, home);
                peers.Add((new ConnectionId((uint)(500 + peers.Count)), ship, home));
            }
        var npc = new Ship(20500, hull, new Vector3(4000, 0, 0), Vector3.Zero, Orientation.Identity)
            { SideId = 0, IsNpc = true };
        server.TestMission.PlaceShip(npc, home);
        server.TestMission.Stage = MissionStage.Started;   // enables the NPC + cross-side paths
        foreach (var (_, ship, _) in peers)
            for (short side = 0; side < 2; side++)
                ship.SideVisibility.SetSideVisibility(side, side == ship.SideId);
        npc.SideVisibility.SetSideVisibility(0, true);
        npc.SideVisibility.SetSideVisibility(1, true);
        server.TestResetSnapshotReplay();                  // pristine latches for every mode
        server.TestSeedPeers(peers);
        return new Fixture { Server = server, Peers = peers, Home = home, Elsewhere = elsewhere, Npc = npc };
    }

    private static short[] SideOf(Fixture f, short side) =>
        f.Peers.Where(p => p.Ship.SideId == side).Select(p => p.Ship.Id).ToArray();

    // ── 1. the steady state: no repeats ──────────────────────────────────────

    /// <summary>
    /// A stationary visible teammate, NPC and (already spotted) enemy produce
    /// exactly ONE status each and then nothing, however many ticks run — the
    /// whole point of the gate: no periodic refresh.
    /// </summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Flag_on_sends_a_stationary_teammate_npc_and_enemy_status_exactly_once()
    {
        Require.Artwork();
        using var f = Boot(gate: true);
        // Side 0 sees all of side 1 (and vice versa) so the cross-side path fires.
        f.SetSeenForAll(0, true);
        f.SetSeenForAll(1, true);
        f.Run(12);

        // Each (recipient, subject) pair exactly once, on the first tick.
        var byPair = f.Statuses.GroupBy(s => (s.Peer, s.ShipId)).ToArray();
        Assert.NotEmpty(byPair);
        foreach (var pair in byPair)
            Assert.Single(pair);
        Assert.All(f.Statuses, s => Assert.Equal(0u, s.Tick));

        // The populations: own + teammate + NPC for each side-0 recipient, plus
        // the one status per seen enemy; same for side 1.
        foreach (var (conn, ship, _) in f.Peers)
        {
            var mine = f.Statuses.Where(s => s.Peer == conn.Value).ToArray();
            // own-side copy of every ship on its side (its own included) + the NPC
            // when that NPC is on its side, plus the cross-side copy of every enemy
            // ship and of the NPC when it is on the other side.
            int ownSide = SideOf(f, ship.SideId!.Value).Length + (ship.SideId == 0 ? 1 : 0);
            int crossSide = SideOf(f, (short)(1 - ship.SideId!.Value)).Length + (ship.SideId != 0 ? 1 : 0);
            Assert.Equal(ownSide + crossSide, mine.Length);
        }
    }

    /// <summary>
    /// The same steady state with the flag OFF keeps refreshing (the behaviour
    /// the flag replaces): the fixture is otherwise identical, so this also proves
    /// the two arms really differ on the wire.
    /// </summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Flag_off_still_refreshes_the_same_stationary_world()
    {
        Require.Artwork();
        using var off = Boot(gate: false);
        off.SetSeenForAll(0, true);
        off.SetSeenForAll(1, true);
        off.Run(12);
        using var on = Boot(gate: true);
        on.SetSeenForAll(0, true);
        on.SetSeenForAll(1, true);
        on.Run(12);
        Assert.True(off.Statuses.Count > on.Statuses.Count,
            $"flag off sent {off.Statuses.Count} SHIP_STATUS frames, flag on {on.Statuses.Count} — expected the refresh to be the difference");
    }

    // ── 2. every edge fires through the comparison ───────────────────────────

    /// <summary>
    /// Each status-relevant edge of the dependency table sends exactly ONE status
    /// with the right content, and nothing else changes: sector change (warp),
    /// dock, launch, hull change, boarding, and the HUD-17 Detected flip.
    /// </summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Flag_on_sends_exactly_one_status_per_content_edge_with_the_right_content()
    {
        Require.Artwork();
        using var f = Boot(gate: true);
        var recipient = f.Peers[0];                       // side 0
        var teammate = f.Peers[1];                        // side 0
        short teammateId = teammate.Ship.Id;

        f.Run(3);                                          // steady state
        int Baseline() => f.Statuses.Count(s => s.Peer == recipient.Conn.Value && s.ShipId == teammateId);
        int baseline = Baseline();

        // (a) sector change — the port's LiveShip sector, the field the client's
        // strategy reads (appmsg.cpp:2723-2729 ShipWarped).
        f.Server.TestSeedPeers(f.Peers.Select(p => (p.Conn, p.Ship, p.Ship.Id == teammateId ? f.Elsewhere : p.Sector)).ToList());
        f.Run(2);
        var moved = f.Statuses.Where(s => s.Peer == recipient.Conn.Value && s.ShipId == teammateId).ToArray();
        Assert.Equal(baseline + 1, moved.Length);
        Assert.Equal(f.Elsewhere.Id, moved[^1].Message.SectorId);

        // (b) dock — station + state docked.
        teammate.Ship.StationId = 3;
        f.Run(2);
        var docked = f.Statuses.Where(s => s.Peer == recipient.Conn.Value && s.ShipId == teammateId).ToArray();
        Assert.Equal(baseline + 2, docked.Length);
        Assert.Equal(ShipStatusMessage.StateDocked, docked[^1].Message.State);
        Assert.Equal((short)3, docked[^1].Message.StationId);

        // (c) launch.
        teammate.Ship.StationId = null;
        f.Run(2);
        var launched = f.Statuses.Where(s => s.Peer == recipient.Conn.Value && s.ShipId == teammateId).ToArray();
        Assert.Equal(baseline + 3, launched.Length);
        Assert.Equal(ShipStatusMessage.StateFlying, launched[^1].Message.State);
        Assert.Equal((short)-1, launched[^1].Message.StationId);

        // (d) hull change (a respawn/upgrade): the hull id is in the status.
        var otherHull = f.Server.TestMission!.Core.HullTypes.Values.First(h => h.HullId != teammate.Ship.HullType.HullId);
        teammate.Ship.ChangeHull(otherHull);
        f.Run(2);
        var rehulled = f.Statuses.Where(s => s.Peer == recipient.Conn.Value && s.ShipId == teammateId).ToArray();
        Assert.Equal(baseline + 4, rehulled.Length);
        Assert.Equal(otherHull.HullId, rehulled[^1].Message.HullId);

        // (e) boarding: observer/turret state, parent id, hull NA (FedSrv.CPP:2453-2454).
        teammate.Ship.ParentShip = f.Peers[2].Ship;
        f.Run(2);
        var boarded = f.Statuses.Where(s => s.Peer == recipient.Conn.Value && s.ShipId == teammateId).ToArray();
        Assert.Equal(baseline + 5, boarded.Length);
        Assert.Equal(f.Peers[2].Ship.Id, boarded[^1].Message.ParentId);
        Assert.Equal((short)-1, boarded[^1].Message.HullId);
        Assert.Equal(ShipStatusMessage.StateObserver, boarded[^1].Message.State);
        teammate.Ship.ParentShip = null;
        f.Run(2);

        // (f) the HUD-17 Detected flip: an enemy side stops seeing the ship, so the
        // own-side copy's Detected bit clears (fsship.cpp:318/321-334).
        int before = Baseline();
        f.Peers.First(p => p.Ship.SideId == 1).Ship.SideVisibility.SetSideVisibility(1, true);
        teammate.Ship.SideVisibility.SetSideVisibility(1, true);
        f.Run(2);
        int detectedOn = Baseline();
        Assert.True(detectedOn > before, "the Detected edge did not re-send the teammate status");
        Assert.True(f.Statuses.Where(s => s.Peer == recipient.Conn.Value && s.ShipId == teammateId).Last().Message.Detected);
        teammate.Ship.SideVisibility.SetSideVisibility(1, false);
        f.Run(2);
        var detectedOff = f.Statuses.Where(s => s.Peer == recipient.Conn.Value && s.ShipId == teammateId).Last();
        Assert.False(detectedOff.Message.Detected);
    }

    // ── 3. cross-side: one copy per side, never a leak while hidden ──────────

    /// <summary>
    /// The cross-side path is gated on the SEEING SIDE's copy (native's per-side
    /// <c>ShipStatus</c>): a changed copy goes to the whole side once, and a hidden
    /// ship sends nothing — never a fresh sector for a ship the side cannot see
    /// (the flag-off catch-up is silent there too).
    /// </summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Flag_on_cross_side_copy_changes_send_once_and_never_leak_a_hidden_ship()
    {
        Require.Artwork();
        using var f = Boot(gate: true);
        short enemyId = f.Peers.First(p => p.Ship.SideId == 1).Ship.Id;
        var side0 = f.Peers.Where(p => p.Ship.SideId == 0).ToArray();
        var enemy = f.Peers.First(p => p.Ship.SideId == 1);

        // Spotted edge: every side-0 peer is told once (the side's sensor net).
        enemy.Ship.SideVisibility.SetSideVisibility(0, true);
        f.Run(2);
        foreach (var peer in side0)
        {
            var got = f.Statuses.Where(s => s.Peer == peer.Conn.Value && s.ShipId == enemyId).ToArray();
            Assert.Single(got);
            Assert.False(got[0].Message.Unknown);
            Assert.Equal(ShipStatusMessage.StateFlying, got[0].Message.State);
        }

        // Still visible, no change: silence (the enemy's own-side status about
        // itself to its OWN side is a different copy and is not counted here).
        var side0Conns = side0.Select(p => p.Conn.Value).ToHashSet();
        f.Run(3);
        Assert.True(side0.Length == f.Statuses.Count(s => s.ShipId == enemyId && side0Conns.Contains(s.Peer)),
            "the cross-side copy was re-sent without a change");

        // The enemy warps while visible: ONE status to each side-0 peer, carrying
        // the new sector.
        f.MoveShip(enemy.Ship, f.Elsewhere);
        f.Server.TestSeedPeers(f.Peers.Select(p => (p.Conn, p.Ship, p.Ship.Id == enemyId ? f.Elsewhere : p.Sector)).ToList());
        f.Run(2);
        foreach (var peer in side0)
        {
            var got = f.Statuses.Where(s => s.Peer == peer.Conn.Value && s.ShipId == enemyId).ToArray();
            Assert.Equal(2, got.Length);
            Assert.Equal(f.Elsewhere.Id, got[^1].Message.SectorId);
        }

        // Hidden edge: one unknown status each.
        enemy.Ship.SideVisibility.SetSideVisibility(0, false);
        f.Run(2);
        foreach (var peer in side0)
        {
            var got = f.Statuses.Where(s => s.Peer == peer.Conn.Value && s.ShipId == enemyId).ToArray();
            Assert.True(3 == got.Length,
                "peer " + peer.Conn + ": " + string.Join(", ", got.Select(g => $"t{g.Tick} unk={g.Message.Unknown} sec={g.Message.SectorId}")));
            Assert.True(got[^1].Message.Unknown);
        }

        // While hidden, warp again: NOTHING may be sent to side 0 (the side has no
        // idea where the ship is; a fresh sector would be an information leak).
        f.MoveShip(enemy.Ship, f.Home);
        f.Server.TestSeedPeers(f.Peers.Select(p => (p.Conn, p.Ship, p.Ship.Id == enemyId ? f.Home : p.Sector)).ToList());
        f.Run(3);
        foreach (var peer in side0)
            Assert.Equal(3, f.Statuses.Count(s => s.Peer == peer.Conn.Value && s.ShipId == enemyId));

        // Re-spotted: one status each with the (current) sector.
        enemy.Ship.SideVisibility.SetSideVisibility(0, true);
        f.Run(2);
        foreach (var peer in side0)
        {
            var got = f.Statuses.Where(s => s.Peer == peer.Conn.Value && s.ShipId == enemyId).ToArray();
            Assert.Equal(4, got.Length);
            Assert.False(got[^1].Message.Unknown);
        }
    }

    // ── 4. late joiners and reconnects ───────────────────────────────────────

    /// <summary>
    /// A peer that arrives after the world is running (the seeded equivalent of a
    /// join: every per-(recipient, subject) latch is empty, exactly as it is after
    /// the join burst) still learns every ship it should know about — its own
    /// status and its teammates' inside one tick, a seen enemy inside one
    /// cross-side catch-up interval (the same bound the flag-off refresh gives).
    /// </summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Flag_on_late_joiner_learns_its_own_side_first_tick_and_seen_enemies_within_one_interval()
    {
        Require.Artwork();
        using var f = Boot(gate: true);
        f.Run(5);                                          // the world is running
        var joiner = new Ship(20900, f.Peers[0].Ship.HullType, new Vector3(9000, 0, 0), Vector3.Zero, Orientation.Identity)
            { SideId = 0 };
        f.Server.TestMission!.PlaceShip(joiner, f.Home);
        joiner.SideVisibility.SetSideVisibility(0, true);
        joiner.SideVisibility.SetSideVisibility(1, true);
        // The enemies are visible to side 0 (so the cross-side copy exists for it).
        foreach (var (_, ship, _) in f.Peers)
            if (ship.SideId == 1) ship.SideVisibility.SetSideVisibility(0, true);
        var conn = new ConnectionId(999);
        f.Server.TestSeedPeers(f.Peers.Append((conn, joiner, f.Home)).ToList());

        f.Run(1);
        var firstTick = f.Statuses.Where(s => s.Peer == conn.Value).Select(s => s.ShipId).ToHashSet();
        Assert.Contains(joiner.Id, firstTick);                       // own status
        foreach (var id in SideOf(f, 0)) Assert.Contains(id, firstTick);   // every side-0 teammate
        Assert.Contains(f.Npc.Id, firstTick);                        // the same-side NPC

        f.Run(100);                                                  // one catch-up interval
        var learned = f.Statuses.Where(s => s.Peer == conn.Value).Select(s => s.ShipId).ToHashSet();
        foreach (var id in SideOf(f, 1)) Assert.Contains(id, learned);      // the seen enemies
        // And nothing was repeated on the way there.
        Assert.All(f.Statuses.Where(s => s.Peer == conn.Value).GroupBy(s => s.ShipId), g => Assert.Single(g));
    }

    /// <summary>
    /// P991 review — the join dump tells the JOINER and nobody else. It must not
    /// rewrite any side's committed copy: writing the cross-side copy of a seen
    /// enemy under the ENEMY's side key clobbered that side's own-side copy
    /// (Detected=true vs the cross copy's false), so the next sweep re-sent the
    /// enemy's status to its whole side on every join; and re-stamping a committed
    /// copy marks every other peer of the side as "behind". After the dump,
    /// existing peers of both sides receive nothing.
    /// </summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Flag_on_join_dump_reaches_only_the_joiner_and_leaves_every_side_copy_alone()
    {
        Require.Artwork();
        using var f = Boot(gate: true);
        foreach (var (_, ship, _) in f.Peers)
            if (ship.SideId == 1) ship.SideVisibility.SetSideVisibility(0, true);   // side 0 sees side 1 → side 1 is Detected
        f.Run(3);                                          // every copy committed and delivered

        var joiner = new Ship(20901, f.Peers[0].Ship.HullType, new Vector3(9100, 0, 0), Vector3.Zero, Orientation.Identity)
            { SideId = 0 };
        f.Server.TestMission!.PlaceShip(joiner, f.Home);
        joiner.SideVisibility.SetSideVisibility(0, true);
        var conn = new ConnectionId(998);
        f.Server.TestSeedPeers(f.Peers.Append((conn, joiner, f.Home)).ToList());

        int before = f.Statuses.Count;
        f.Run(3, beforeTick: i => { if (i == 0) f.Server.TestSendStatusDumpToJoiner(conn); });
        var after = f.Statuses.Skip(before).ToList();

        // The joiner learned every seen enemy (from the dump, before any catch-up).
        var toJoiner = after.Where(s => s.Peer == conn.Value).Select(s => s.ShipId).ToHashSet();
        foreach (var id in SideOf(f, 1)) Assert.Contains(id, toJoiner);
        // Existing peers: only the joiner's own ship is news to them (a new teammate
        // for side 0; side 1 does not see it). Nothing about the existing ships.
        var existing = f.Peers.Select(p => p.Conn.Value).ToHashSet();
        var leaked = after.Where(s => existing.Contains(s.Peer) && s.ShipId != joiner.Id).ToList();
        Assert.True(leaked.Count == 0,
            "existing peers re-sent: " + string.Join(", ", leaked.Select(s => $"peer {s.Peer} <- ship {s.ShipId} @{s.Tick}")));
    }

    // ── 5. the state is bounded ──────────────────────────────────────────────

    /// <summary>
    /// "A stationary world does not accumulate": the gate stores one content value
    /// per (side, subject) — native's <c>m_rgShipStatus</c> shape — so after the
    /// first tick the table is constant, and the comparison itself allocates
    /// nothing (it is a struct compare against a dictionary entry).
    /// </summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Flag_on_gate_state_stays_constant_and_the_comparison_allocates_nothing()
    {
        Require.Artwork();
        using var f = Boot(gate: true);
        f.SetSeenForAll(0, true);
        f.SetSeenForAll(1, true);
        f.Run(3);
        var (bySide, _) = f.Server.StatusGateCounts();
        Assert.True(bySide > 0, "the gate stored nothing — the fixture did not exercise it");

        // Steady state: repeated sweeps must neither grow the table nor allocate.
        for (int i = 0; i < 5; i++) f.Server.TestSweepShipDetection();      // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) f.Server.TestSweepShipDetection();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(bySide, f.Server.StatusGateCounts().BySide);

        // And the wire stays silent for the whole steady state.
        f.Run(20);
        Assert.DoesNotContain(f.Statuses, s => s.Tick > 0);
    }

    // ── 6. serial == parallel with the gate on ───────────────────────────────

    private static List<(uint Tick, uint Peer, byte[] Bytes)> Replay(bool parallel, int ticks)
    {
        using var f = Boot(gate: true, parallel: parallel);
        f.SetSeenForAll(0, true);
        f.SetSeenForAll(1, true);
        var frames = new List<(uint, uint, byte[])>();
        var tick = 0u;
        f.Server.TestPublicationCapture = (peer, bytes, key, guaranteed) => frames.Add((tick, peer.Value, bytes));
        try
        {
            for (; tick < ticks; tick++)
            {
                // A little world motion so both builders have work to do.
                foreach (var (_, ship, _) in f.Peers)
                {
                    ship.Position = new Vector3(ship.Position.X + tick * 3, ship.Position.Y, ship.Position.Z + tick);
                    if (tick == 6 && ship.SideId == 0) ship.StationId = 1;
                    if (tick == 9 && ship.SideId == 1) ship.StationId = null;
                }
                f.Server.TestProjectRecordedTick(tick, snapshot: false);
            }
        }
        finally { f.Server.TestPublicationCapture = null; }
        return frames;
    }

    /// <summary>
    /// P603's byte-identity pin, extended to the change gate: with the flag ON the
    /// serial and the parallel per-peer builders must emit the same frames (the
    /// per-subject gate decision is made once, serially, in the detection sweep, so
    /// no worker writes gate state).
    /// </summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Flag_on_serial_and_parallel_builds_are_byte_identical()
    {
        Require.Artwork();
        var serial = Replay(parallel: false, ticks: 12);
        var parallel = Replay(parallel: true, ticks: 12);
        Assert.NotEmpty(serial);
        Assert.Equal(serial.Count, parallel.Count);
        foreach (var peer in serial.Select(f => f.Peer).Distinct().OrderBy(p => p))
        {
            var left = serial.Where(f => f.Peer == peer).ToArray();
            var right = parallel.Where(f => f.Peer == peer).ToArray();
            Assert.Equal(left.Select(f => (f.Tick, f.Bytes)), right.Select(f => (f.Tick, f.Bytes)));
        }
    }

    // ── 7. a client view built ON equals the view built OFF ──────────────────

    /// <summary>
    /// The acceptance criterion for the whole task: after the same seeded
    /// scenario, a CLIENT view built from the flag-ON stream must know exactly the
    /// same ships, with the same sector / docked-launched / detected / hull state,
    /// as one built from the flag-OFF stream. Replays every captured message of a
    /// per-recipient stream into a real <see cref="ClientWorld"/> (the port's own
    /// client state machine), so a missing edge would show up as a stale entity.
    /// <para>The scenario runs past the flag-off refresh cadences
    /// (<c>TeammateStatusRefreshTicks</c>/<c>CrossSideStatusRefreshTicks</c> = 100)
    /// on purpose: that is the arm where the OFF view can only be repaired by the
    /// refresh, so equality at the end proves the ON edges delivered every change
    /// the OFF refreshes eventually did — and the ON arm is strictly no staler on
    /// the way (its sector change is immediate, the OFF one waits for its phase).</para>
    /// </summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Flag_on_client_view_matches_the_flag_off_view_after_the_same_scenario()
    {
        Require.Artwork();
        var off = ClientViews(gate: false);
        var on = ClientViews(gate: true);
        Assert.Equal(off.Keys.OrderBy(k => k), on.Keys.OrderBy(k => k));
        foreach (var (peer, offWorld) in off)
        {
            var onWorld = on[peer];
            var offShips = ShipView(offWorld);
            var onShips = ShipView(onWorld);
            Assert.NotEmpty(offShips);
            Assert.Equal(offShips, onShips);
        }
    }

    private static List<(short Id, short Hull, short Sector, short Station, byte State, bool Unknown, bool Detected)>
        ShipView(Allegiance.ClientCore.State.ClientWorld world) =>
        world.Ships.Values
            .Select(s => (s.ShipId, s.HullId, s.SectorId, s.StationId, s.State, s.Unknown, s.Detected))
            .OrderBy(s => s.ShipId)
            .ToList();

    private static Dictionary<uint, Allegiance.ClientCore.State.ClientWorld> ClientViews(bool gate)
    {
        using var f = Boot(gate);
        f.SetSeenForAll(0, true);
        f.SetSeenForAll(1, true);
        var worlds = new Dictionary<uint, Allegiance.ClientCore.State.ClientWorld>();
        var enemy = f.Peers.First(p => p.Ship.SideId == 1);
        var teammate = f.Peers[1];
        f.Server.TestPublicationCapture = (peer, bytes, key, guaranteed) =>
        {
            if (!worlds.TryGetValue(peer.Value, out var world))
                worlds[peer.Value] = world = new Allegiance.ClientCore.State.ClientWorld();
            foreach (var message in FedPacket.Unpack(bytes))
                ApplyToWorld(world, message);
        };
        try
        {
            for (uint tick = 0; tick < 130; tick++)
            {
                if (tick == 2) teammate.Ship.StationId = 2;                                  // dock
                if (tick == 4) teammate.Ship.StationId = null;                               // launch
                if (tick == 6) f.MoveShip(enemy.Ship, f.Elsewhere);                          // warp (cross-side)
                if (tick == 8) teammate.Ship.SideVisibility.SetSideVisibility(1, true);      // Detected flip
                if (tick == 10) teammate.Ship.SideVisibility.SetSideVisibility(1, false);
                f.Server.TestProjectRecordedTick(tick, snapshot: false);
            }
        }
        finally { f.Server.TestPublicationCapture = null; }
        return worlds;
    }

    /// <summary>Decode and apply the messages a status/sector/dock/detected view
    /// is built from; everything else (poses, statics, lobby) is irrelevant here.</summary>
    private static void ApplyToWorld(Allegiance.ClientCore.State.ClientWorld world, byte[] message)
    {
        switch ((ushort)(message[2] | message[3] << 8))
        {
            case ShipStatusMessage.Fmid:
                world.Apply(ShipStatusMessage.Decode(message));
                break;
            case ExportShip.Fmid when message.Length >= 12
                                      && BitConverter.ToInt32(message.AsSpan(8, 4)) == ExportShip.OtShip:
                world.Apply(ExportShip.Decode(message));
                break;
            case PlayerInfo.Fmid:
                world.Apply(PlayerInfo.Decode(message));
                break;
            case SetCluster.Fmid:
                world.Apply(SetCluster.Decode(message));
                break;
        }
    }

    // ── 8. the gate's own state is freed when peers leave ────────────────────

    /// <summary>Default ON since 2026-11-10 (Domingo, after the 400-bot rig A/B
    /// b105/b106: SHIP_STATUS bytes −87 %, ages/starvation/bot outcomes unchanged).
    /// A config without the key, and a config without a broadcast section, both
    /// resolve to the native change gate; only an explicit false restores the
    /// pre-P991 periodic refreshes.</summary>
    [Fact]
    public void Default_is_the_native_change_gate()
    {
        Assert.True(new global::Allegiance.Server.BroadcastConfig().ShipStatusChangeGate);
        Assert.True(Srv.ResolveShipStatusChangeGateFlag(new global::Allegiance.Server.BroadcastConfig().ShipStatusChangeGate, null));
        Assert.False(Srv.ResolveShipStatusChangeGateFlag(true, "0"));
    }

    /// <summary>
    /// "Last-sent state must be dropped when either side leaves": real loopback
    /// clients seat, the gate fills, clients disconnect, and the per-(side,
    /// subject) table returns to empty with the last peer — no growth over
    /// join/leave churn, which is what makes a two-match showcase bounded.
    /// </summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Flag_on_gate_state_is_freed_when_peers_leave()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(gate: true));
        var cfg = server.Config;
        Assert.True(cfg.Broadcast!.ShipStatusChangeGate);
        foreach (var mission in server.Missions) mission.Stage = MissionStage.Started;

        var bots = new List<Allegiance.Bot.Bot>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var bot = new Allegiance.Bot.Bot();
                bot.Connect(server.BoundPort, $"P991CL{i:D2}");
                bot.SendLogonRequest(new LogonRequest
                {
                    CharacterName = $"P991CL{i:D2}", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL",
                    MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer, Time = 9u,
                    DwCookie = uint.MaxValue, CrcFileList = -1, SteamAuthTicket = new byte[16],
                    SteamID = 991_000ul + (ulong)i, DrmHash = "p991",
                });
                PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
                Assert.True(bot.ShipAssignment is not null, $"bot {i} never seated");
                var ship = server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                server.TryJoinSide(ship, (short)(i % 2));
                bots.Add(bot);
            }
            for (int t = 0; t < 10; t++) { server.TickSim(); Thread.Sleep(2); }
            int afterJoin = server.StatusGateCounts().BySide;
            Assert.True(afterJoin > 0, "the gate stored nothing after eight seated peers");

            // Half leave: their subjects (and their own copies) go with them.
            bots[^1].Dispose(); bots[bots.Count - 2].Dispose(); bots[bots.Count - 3].Dispose();
            bots.RemoveRange(bots.Count - 3, 3);
            for (int t = 0; t < 10; t++) { server.TickSim(); Thread.Sleep(2); }
            int afterHalf = server.StatusGateCounts().BySide;
            Assert.True(afterHalf < afterJoin,
                $"the gate did not shrink when three of eight peers left ({afterJoin} -> {afterHalf})");

            // Everyone leaves: nothing of the departed ships may survive.
            foreach (var bot in bots) bot.Dispose();
            bots.Clear();
            for (int t = 0; t < 10; t++) { server.TickSim(); Thread.Sleep(2); }
            int afterAll = server.StatusGateCounts().BySide;
            foreach (var mission in server.Missions)
                Assert.Equal(0, afterAll - server.Ships.Count(s => s.Ship.IsNpc && s.Ship.SideId is not null));
        }
        finally
        {
            foreach (var bot in bots) bot.Dispose();
        }
    }

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // ── 9. the flag resolves like the other broadcast.* switches ─────────────

    [Theory]
    [InlineData("1", false, true)]
    [InlineData("true", false, true)]
    [InlineData("0", true, false)]
    [InlineData("false", true, false)]
    [InlineData(null, true, true)]     // absent → the config value
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    public void Flag_grammar_is_the_same_as_the_other_broadcast_switches(string? env, bool configured, bool expected)
        => Assert.Equal(expected, Srv.ResolveShipStatusChangeGateFlag(configured, env));

    [Fact]
    public void Invalid_environment_is_rejected() => Assert.Throws<InvalidDataException>(
        () => Srv.ResolveShipStatusChangeGateFlag(false, "tru"));
}
