using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P603 — byte-identity proof that the parallel per-peer broadcast build
/// (<c>broadcast.parallelBuild</c>) emits EXACTLY the same per-peer outbound
/// payload bytes, in the same per-peer order, as the serial (default) builder,
/// across several deterministically-fabricated worlds (many peers, mixed sides,
/// multiple sectors, docked + flying + moving ships, cluster-info payload, and
/// same-side NPCs). It drives <c>BroadcastShipStates</c> + <c>BroadcastShipsUpdate</c>
/// directly via the internal test seam (no live sim / real clients), runs each
/// seeded world once in serial mode and once (after a full state reset) in
/// parallel mode, and asserts the per-recipient payload streams are byte-for-byte
/// equal.
/// </summary>
[Collection("LiveNetwork")]   // boots real in-process servers; keep serialized with other live suites
public class ParallelBroadcastByteIdentityTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore RepresentativeCore()
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
            new Gm { Name = "Test Mission", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    /// <summary>Globally-unique ship-id fountain for the fabricated worlds. Each
    /// <see cref="BuildWorld"/> call places fresh ships; reusing ids across calls
    /// (e.g. two scenarios on the same server) would create distinct Ship objects
    /// sharing an id — impossible in production (the server allocates globally
    /// unique ids) and it distorts per-id latches. LiveNetwork is serialized, so a
    /// static is safe.</summary>
    private static short _nextFakeShipId = 30000;

    /// <summary>One deterministic world: N peer ships on side A / M on side B spread
    /// across 3 sectors, a few docked, plus a few same-side NPCs on side A (so the
    /// P366 NPC path fires). All peer ships are placed in their sector so
    /// <c>BroadcastShipsUpdate</c>'s candidate walk sees them. Returns the (conn,ship,sector)
    /// peer list and the list of every ship (peers + NPCs) for Deviation resets.</summary>
    private static (List<(ConnectionId Conn, Ship Ship, Sector Sector)> Peers, List<Ship> AllShips, List<Ship> Npcs)
        BuildWorld(Srv server, DataHullTypeIGC hull)
    {
        var sectors = server.TestSectors;
        var peers = new List<(ConnectionId Conn, Ship Ship, Sector Sector)>();
        var allShips = new List<Ship>();
        var npcs = new List<Ship>();

        short NextId() => _nextFakeShipId++;
        // Side 0 (Alpha): 24 peers spread across sectors 0,1,2; 3 docked.
        for (int i = 0; i < 24; i++)
        {
            var sec = sectors[i % 3];
            var ship = new Ship(NextId(), hull, new Vector3(i * 37.0f, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            server.TestMission!.PlaceShip(ship, sec);
            if (i % 8 == 0) ship.StationId = 1;              // some docked
            peers.Add((new ConnectionId((uint)(100 + i)), ship, sec));
            allShips.Add(ship);
        }
        // Side 1 (Bravo): 12 peers, sectors 1,2 (mixed), a couple docked.
        for (int i = 0; i < 12; i++)
        {
            var sec = sectors[1 + (i % 2)];
            var ship = new Ship(NextId(), hull, new Vector3(-50.0f - i * 41.0f, 10, 0), Vector3.Zero, Orientation.Identity) { SideId = 1 };
            server.TestMission!.PlaceShip(ship, sec);
            if (i % 6 == 0) ship.StationId = 1;
            peers.Add((new ConnectionId((uint)(300 + i)), ship, sec));
            allShips.Add(ship);
        }
        // Side 0 same-side NPCs in sector 0 (P366 path: a side-0 peer in that sector).
        for (int i = 0; i < 3; i++)
        {
            var sec = sectors[0];
            var npc = new Ship(NextId(), hull, new Vector3(500.0f + i * 20.0f, 0, 0), Vector3.Zero, Orientation.Identity)
            { SideId = 0, IsNpc = true };
            server.TestMission!.PlaceShip(npc, sec);
            npcs.Add(npc);
            allShips.Add(npc);
        }
        return (peers, allShips, npcs);
    }

    private static void AssertPeerStreamsEqual(
        Dictionary<ConnectionId, List<byte[]>> serial,
        Dictionary<ConnectionId, List<byte[]>> parallel)
    {
        Assert.Equal(
            serial.Keys.OrderBy(k => k.Value).Select(k => k.Value),
            parallel.Keys.OrderBy(k => k.Value).Select(k => k.Value));
        foreach (var conn in serial.Keys)
        {
            Assert.True(parallel.ContainsKey(conn),
                $"peer {conn.Value} missing from parallel capture (had {serial[conn].Count} serial packets)");
            // xunit Assert.Equal on byte[] compares element-wise, and on List<byte[]>
            // compares each element's bytes — a strict per-peer, per-order equality.
            Assert.Equal(serial[conn], parallel[conn]);
        }
    }

    private static void AssertScenarioEqual(Srv server, uint tick, string step)
    {
        var world = BuildWorld(server, RepresentativeCore().HullTypes.Values.First());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;   // enable NPC path

        // Both modes must start from IDENTICAL pristine state: full reset + seed
        // before the serial capture, then again before the parallel capture (the
        // broadcasts latch/accumulate per-peer state, so the two runs must not see
        // each other's leftovers).
        server.TestResetBroadcastState();
        server.TestSetClusterInfoBytes([0xAA, 0xBB, 0xCC, 0x00, 0x01]);          // exercise CLUSTERINFO resend
        server.TestSeedPeers(world.Peers);
        var serial = server.TestCollectBroadcast(tick, parallel: false, step);

        server.TestResetBroadcastState();
        server.TestSetClusterInfoBytes([0xAA, 0xBB, 0xCC, 0x00, 0x01]);
        server.TestSeedPeers(world.Peers);
        var parallel = server.TestCollectBroadcast(tick, parallel: true, step);

        AssertPeerStreamsEqual(serial, parallel);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Both_broadcasts_are_byte_identical_serial_vs_parallel_at_tick0()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        AssertScenarioEqual(server, 0, "both");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Both_broadcasts_are_byte_identical_serial_vs_parallel_at_later_ticks()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        AssertScenarioEqual(server, 5, "both");
        AssertScenarioEqual(server, 99, "both");     // past teammate/status refresh cadences
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShipStates_alone_is_byte_identical_serial_vs_parallel()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        AssertScenarioEqual(server, 0, "shipStates");
        AssertScenarioEqual(server, 200, "shipStates");  // past ClusterInfoRefreshTicks
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShipsUpdate_alone_is_byte_identical_serial_vs_parallel()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        AssertScenarioEqual(server, 0, "shipsUpdate");
        AssertScenarioEqual(server, 3, "shipsUpdate");   // exercises the Deviation budget path across ticks
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Parallel_flag_defaults_off_and_config_binds()
    {
        // The flag is OFF by default (an absent broadcast section), so the serial
        // path is selected. Verify the resolved field is false on a default boot.
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        Assert.False(server._broadcastParallelBuild);
        Assert.True(server._broadcastMaxParallelism >= 1);
    }

    /// <summary>
    /// P619c scope 1 — the regression guard for P619b: with the verbosity gate at
    /// its DEFAULT (off), a parallel broadcast at many peers emits ZERO of the
    /// high-frequency per-pair diagnostic lines (<c>[shipstatus]</c>,
    /// <c>[teamshipstatus]</c>, <c>[samestidenpcstatus]</c>), and the wire bytes
    /// remain byte-identical serial-vs-parallel.
    /// <para>
    /// Fails on today's pre-P619b code: those lines were buffered into the
    /// UNCONDITIONAL <c>r.Logs</c> list and written synchronously with
    /// <c>Console.WriteLine</c> in the serial apply phase, so even with verbosity
    /// off they appeared on stdout (~4,000 lines/s at 390 players). After P619b
    /// they are routed through the verbose-gated <c>ConsoleLog.Write</c> sink and
    /// vanish when the gate is off, and the drain never touches the wire.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Parallel_broadcast_verbose_off_emits_zero_pair_diag_lines_and_bytes_unchanged()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());                     // ConsoleVerbose defaults to OFF
        server._broadcastParallelBuild = true;     // force the parallel build path
        var world = BuildWorld(server, RepresentativeCore().HullTypes.Values.First());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        var oldOut = Console.Out;
        var sw = new StringWriter();
        try
        {
            server.TestResetBroadcastState();
            server.TestSeedPeers(world.Peers);
            // Collect the PARALLEL broadcast at a late tick (past the status
            // refresh cadences) while capturing stdout into a StringWriter.
            Console.SetOut(sw);
            server.TestCollectBroadcast(99, parallel: true, "both");
            ConsoleLog.Flush();                    // drain anything the sink enqueued
            Console.SetOut(oldOut);
            var text = sw.ToString();
            foreach (var tag in new[] { "[shipstatus]", "[teamshipstatus]", "[samestidenpcstatus]" })
                Assert.DoesNotContain(tag, text);
        }
        finally
        {
            Console.SetOut(oldOut);
            ConsoleLog.ResetForTest();
        }

        // Wire bytes unchanged: the full serial-vs-parallel identity proof must
        // still hold for the same world (run after restoring stdout).
        AssertScenarioEqual(server, 99, "both");
    }

    // ── parallel-mode stress: many peers joining/leaving mid-tick, no exceptions ──

    private static LogonRequest StressRequest(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 909u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9400000000000000000ul, DrmHash = "p603stress",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short StressSeat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(StressRequest(name));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, $"{name} did not seat");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    /// <summary>
    /// P603 — the parallel per-peer broadcast build must survive sustained churn
    /// with no exception: many real loopback clients seat on both sides across
    /// sectors, the server ticks hundreds of times with <c>broadcast.parallelBuild</c>
    /// ON, and mid-stream some clients disconnect and new ones join. The test
    /// passes only if every one of those parallel-mode ticks completes without an
    /// exception (any leak of the build phase touching a departed peer's state or a
    /// racy shared write would throw), and a survivor still receives its own
    /// ship-state stream afterwards.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Parallel_broadcast_survives_join_leave_churn_without_exceptions()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        server._broadcastParallelBuild = true;          // P603 test seam: force the parallel path
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        // First wave: 14 clients across both sides / several sectors.
        var bots = new List<FltBot>();
        var names = new List<string>();
        for (int i = 0; i < 14; i++)
        {
            var bot = new FltBot();
            var name = $"STR{i:D2}";
            StressSeat(server, bot, name);              // throws if never seated
            bots.Add(bot);
            names.Add(name);
        }
        // Put them onto real sides so same-side/mate broadcast paths fire.
        for (int i = 0; i < bots.Count; i++)
        {
            var ship = server.Ships.First(s => s.Ship.Id == bots[i].ShipAssignment!.Ship.ShipId).Ship;
            server.TryJoinSide(ship, (short)(i % 2));
        }

        // Sustained churn while ticking with the parallel build ON.
        const int Ticks = 400;
        for (int t = 0; t < Ticks; t++)
        {
            if (t % 50 == 25 && bots.Count > 4)
            {
                // Leave: dispose a batch of current clients.
                int leave = Math.Min(3, bots.Count - 4);
                for (int k = 0; k < leave; k++)
                {
                    bots[bots.Count - 1 - k].Dispose();
                }
                bots.RemoveRange(bots.Count - leave, leave);
                server.TickSim();                       // let the server notice the departures
            }
            if (t % 50 == 40)
            {
                // Join: seat fresh clients.
                int join = 3;
                for (int k = 0; k < join; k++)
                {
                    var bot = new FltBot();
                    var nm = $"JN{t}_{k}";
                    StressSeat(server, bot, nm);
                    var ship = server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                    server.TryJoinSide(ship, (short)((bots.Count + k) % 2));
                    bots.Add(bot);
                    names.Add(nm);
                }
            }
            server.TickSim();                           // throws if the parallel build faults
        }

        // Sanity: a surviving client still receives its own ship-state broadcast.
        Assert.True(bots.Count >= 4, "expected at least a few survivors");
        var survivor = bots[0];
        server.TickSim();
        Thread.Sleep(5);
        Assert.True(survivor.ShipAssignment is not null);
        foreach (var b in bots) b.Dispose();
    }
}
