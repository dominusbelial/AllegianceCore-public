using System.Diagnostics;
using System.Globalization;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P990 — the lane breakdown the slice-D decision needs on top of P983's five
/// categories: the ordered one-shot lane per wire <c>fmid</c> and the coalescing
/// lane per key family, plus the reason a stale (peer, ship) pair is stale.
/// The reconciliation tests are the contract: the per-family/per-id rows must add
/// up to the P983 category totals exactly, or the breakdown is not trustworthy.
/// </summary>
public sealed class P990BroadcastLaneTelemetryTests
{
    private static byte[] Frame(IFedMessage message) => FedPacket.Pack(message.Encode());

    private static byte[] StatusFrame(short shipId) => Frame(new ShipStatusMessage
    {
        ShipId = shipId, HullId = 1, SectorId = 1, StationId = -1, ParentId = -1,
        State = ShipStatusMessage.StateFlying, StateTime = 7,
    });

    [Fact]
    public void Ordered_lane_frames_are_attributed_per_fmid_and_reconcile_exactly()
    {
        var telemetry = new BroadcastTelemetry();
        telemetry.SetSecond(100);
        var peer = new ConnectionId(1);
        // Three ordered frames: two SHIP_STATUS(90), one EXPORT(26) that carries
        // TWO messages (a packet is a flat concatenation, Messagecore.h:451-471).
        telemetry.RecordSendFrame(peer, 0, BroadcastCategory.Other, StatusFrame(11));
        telemetry.RecordSendFrame(peer, 0, BroadcastCategory.Other, StatusFrame(12));
        var export = new ExportShip { ShipId = 13, SideId = 0, HullId = 1, CharacterName = "p990" };
        byte[] twoMessages = FedPacket.Pack([export.Encode(), export.Encode()]);
        telemetry.RecordSendFrame(peer, 0, BroadcastCategory.Other, twoMessages);

        long other = telemetry.WindowBytes(BroadcastCategory.Other, 100);
        long status = telemetry.OrderedLaneWindowBytes(ShipStatusMessage.Fmid, 100);
        long exports = telemetry.OrderedLaneWindowBytes(ExportShip.Fmid, 100);
        Assert.Equal(2 * StatusFrame(11).Length, status);
        Assert.Equal(twoMessages.Length, exports);
        // Per-id bytes and messages reconcile with the P983 category total.
        Assert.Equal(other, status + exports);
        Assert.Equal(3, telemetry.WindowMessages(BroadcastCategory.Other, 100));
        Assert.Equal(2, telemetry.OrderedLaneWindowMessages(ShipStatusMessage.Fmid, 100));
        Assert.Equal(2, telemetry.OrderedLaneWindowMessages(ExportShip.Fmid, 100));
        // Nothing landed in the "unparsed frame" slot.
        Assert.Equal(0, telemetry.OrderedLaneWindowBytes(0, 100));
    }

    [Fact]
    public void Keyed_frames_are_attributed_per_family_and_reconcile_exactly()
    {
        var telemetry = new BroadcastTelemetry();
        telemetry.SetSecond(200);
        var peer = new ConnectionId(2);
        telemetry.RecordSendFrame(peer, key: 12, BroadcastCategory.Other, StatusFrame(1));      // own pose
        telemetry.RecordSendFrame(peer, key: 3, BroadcastCategory.Other, StatusFrame(2));       // stations
        telemetry.RecordSendFrame(peer, key: 3, BroadcastCategory.Other, StatusFrame(3));       // stations
        telemetry.RecordSendFrame(peer, key: 99, BroadcastCategory.Other, StatusFrame(4));      // overflow family
        telemetry.RecordSendFrame(peer, key: 0, BroadcastCategory.Other, StatusFrame(5));       // ordered lane

        long keyed = telemetry.WindowBytes(BroadcastCategory.ReliableKeyed, 200);
        long own = telemetry.KeyedLaneWindowBytes(12, 200);
        long stations = telemetry.KeyedLaneWindowBytes(3, 200);
        long overflow = telemetry.KeyedLaneWindowBytes(BroadcastTelemetry.KeyedFamilies - 1, 200);
        Assert.Equal(StatusFrame(1).Length, own);
        Assert.Equal(2 * StatusFrame(2).Length, stations);
        Assert.Equal(StatusFrame(4).Length, overflow);
        Assert.Equal(keyed, own + stations + overflow);
        Assert.Equal(2, telemetry.KeyedLaneWindowMessages(3, 200));
        // The key-0 frame stayed on the ordered lane and was attributed per id.
        Assert.Equal(StatusFrame(5).Length, telemetry.OrderedLaneWindowBytes(ShipStatusMessage.Fmid, 200));
        Assert.Equal(StatusFrame(5).Length, telemetry.WindowBytes(BroadcastCategory.Other, 200));
    }

    [Fact]
    public void Lane_attribution_rolls_with_the_five_second_window()
    {
        var telemetry = new BroadcastTelemetry();
        var peer = new ConnectionId(3);
        telemetry.SetSecond(300);
        telemetry.RecordSendFrame(peer, 0, BroadcastCategory.Other, StatusFrame(1));
        telemetry.SetSecond(305);
        telemetry.RecordSendFrame(peer, 0, BroadcastCategory.Other, StatusFrame(2));
        Assert.Equal(StatusFrame(2).Length, telemetry.OrderedLaneWindowBytes(ShipStatusMessage.Fmid, 305));
        Assert.Equal(StatusFrame(2).Length, telemetry.WindowBytes(BroadcastCategory.Other, 305));
    }

    [Theory]
    [InlineData(true, true, false, false, false, true, true, "Docked")]
    [InlineData(true, false, true, false, false, true, true, "Dead")]
    [InlineData(true, false, false, true, false, true, true, "Lifepod")]
    [InlineData(true, false, false, false, true, true, true, "Parented")]
    [InlineData(true, false, false, false, false, true, true, "Starved")]
    [InlineData(true, false, false, false, false, false, true, "OtherSector")]
    [InlineData(false, false, false, false, false, false, true, "SubjectUnknown")]
    public void Stale_reason_classification_covers_docked_lifepod_dead_parented_and_moving_subjects(
        bool known, bool docked, bool dead, bool lifepod, bool parented, bool sameSector, bool sweptRecently,
        string expected)
        => Assert.Equal(expected, Srv.ClassifyStale(
            new AgeSubject(known, docked, dead, lifepod, parented, sameSector, Stationary: false),
            new AgeViewer(sweptRecently, NewViewer: false)).ToString());

    [Fact]
    public void Stale_reason_classification_separates_a_parked_subject_from_real_starvation()
    {
        // A visible, flying, in-sector subject whose record the peer still holds
        // exactly (it has not moved) is not a starvation case.
        Assert.Equal(StaleReason.Stationary, Srv.ClassifyStale(
            new AgeSubject(true, false, false, false, false, true, Stationary: true),
            new AgeViewer(SweptRecently: true, NewViewer: false)));
        // The same ship, but it moved since its stamp: real starvation.
        Assert.Equal(StaleReason.Starved, Srv.ClassifyStale(
            new AgeSubject(true, false, false, false, false, true, Stationary: false),
            new AgeViewer(SweptRecently: true, NewViewer: false)));
        // Nothing about the subject is wrong when the viewer's own pass stopped.
        Assert.Equal(StaleReason.ViewerNotSwept, Srv.ClassifyStale(
            new AgeSubject(true, false, false, false, false, true, Stationary: false),
            new AgeViewer(SweptRecently: false, NewViewer: false)));
    }

    [Fact]
    public void Never_posed_pairs_separate_a_new_viewer_from_a_tracked_but_unserved_pair()
    {
        var moving = new AgeSubject(true, false, false, false, false, true, Stationary: false);
        Assert.Equal(StaleReason.NewPair, Srv.ClassifyNeverPosed(moving, new AgeViewer(true, NewViewer: true)));
        Assert.Equal(StaleReason.Unserved, Srv.ClassifyNeverPosed(moving, new AgeViewer(true, NewViewer: false)));
        Assert.Equal(StaleReason.Docked, Srv.ClassifyNeverPosed(
            new AgeSubject(true, true, false, false, false, true, false), new AgeViewer(true, NewViewer: true)));
    }

    [Fact]
    public void Lane_record_and_stamp_paths_allocate_nothing_after_warmup()
    {
        // P983 pinned the category record path; P990 adds the ordered-lane fmid
        // parse and the dense stamp table, so pin those too (a per-frame allocation
        // at the rig's ~10k frames/tick would be a GC problem, not a diagnostic).
        var telemetry = new BroadcastTelemetry();
        telemetry.SetSecond(400);
        var peer = new ConnectionId(5);
        byte[] frame = StatusFrame(1);
        for (int i = 0; i < 200; i++)
        {
            telemetry.RecordSendFrame(peer, 0, BroadcastCategory.Other, frame);
            telemetry.RecordSendFrame(peer, 12, BroadcastCategory.Other, frame);
            telemetry.MarkSweep(peer, i);
            telemetry.Peer(peer).MarkPose((short)i, i);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            telemetry.RecordSendFrame(peer, 0, BroadcastCategory.Other, frame);
            telemetry.RecordSendFrame(peer, 12, BroadcastCategory.Other, frame);
            telemetry.MarkSweep(peer, i);
            telemetry.Peer(peer).MarkPose((short)(i % 200), i);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Pose_stamp_table_matches_a_dictionary_under_arbitrary_insert_update_and_remove()
    {
        // The table is the hot-path and report data structure, so pin its contract
        // against a Dictionary on a randomised sequence (the same shapes production
        // produces: a per-tick sweep over the peer's visible ships, a world-rotation
        // retire, and a reuse of ids after removal).
        var table = new PoseStampTable();
        var reference = new Dictionary<short, (double Ms, Vector3 Pos, bool Has)>();
        var random = new Random(990);
        for (int step = 0; step < 4000; step++)
        {
            short id = (short)random.Next(0, 600);
            int op = random.Next(0, 10);
            if (op < 6)
            {
                double ms = step;
                var pos = new Vector3(random.Next(-50, 50), random.Next(-50, 50), random.Next(-50, 50));
                bool has = random.Next(2) == 0;
                table.Set(id, ms, pos, has);
                reference[id] = (ms, pos, has);
            }
            else if (op < 9)
            {
                Assert.Equal(reference.Remove(id), table.Remove(id));
            }
            else
            {
                Assert.Equal(reference.ContainsKey(id), table.Contains(id));
            }
            Assert.Equal(reference.Count, table.Count);
        }
        foreach (var (id, expected) in reference)
        {
            Assert.True(table.TryGet(id, out var stamp), $"id {id} missing");
            Assert.Equal(id, stamp.ShipId);
            Assert.Equal(expected.Ms, stamp.SimMs);
            Assert.Equal(expected.Pos, stamp.Position);
            Assert.Equal(expected.Has, stamp.HasPosition);
        }
        // A world rotation keeps only the ids it is told to keep, and the ids that
        // survive still answer exactly.
        var keep = reference.Keys.Where((_, i) => i % 3 == 0).ToHashSet();
        table.RemoveExcept(keep);
        Assert.Equal(keep.Count, table.Count);
        foreach (var (id, expected) in reference.Where(pair => keep.Contains(pair.Key)))
        {
            Assert.True(table.TryGet(id, out var stamp));
            Assert.Equal(expected.Ms, stamp.SimMs);
        }
        int enumerated = 0;
        foreach (var stamp in table)
        {
            Assert.Contains(stamp.ShipId, keep);
            enumerated++;
        }
        Assert.Equal(keep.Count, enumerated);
    }

    [Fact]
    public void Fmid_names_read_as_messages_not_numbers()
    {
        Assert.Equal("SHIPSTATUS", Srv.MessageName(ShipStatusMessage.Fmid));
        Assert.Equal("MISSIONDEF", Srv.MessageName(MissionDef.Fmid));
        Assert.Equal("fmid1001", Srv.MessageName(1001));
    }
}

/// <summary>
/// P990 — the fixture half: a real loopback world with the diagnostic on, the
/// lane rows reconciling with the P983 category totals, and the stale-age reason
/// counters naming what a docked / lifepod / stationary / moving subject is.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P990BroadcastLaneFixtureTests
{
    private static byte[] Frame(IFedMessage message) => FedPacket.Pack(message.Encode());

    private static ServerConfig Config(bool budget = true) => new()
    {
        ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        Profiler = new() { Enabled = false, BroadcastBudget = budget },
        Broadcast = new() { MinUpdateHz = 2, ShipUpdateModel = "native" },
        Games = [new() { Name = "P990", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    /// <summary>The reason counters must classify a docked ship, a lifepod, a
    /// parked ship and a moving visible ship in one real pass over a live world.</summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Age_reasons_name_a_docked_lifepod_stationary_and_moving_subject()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        // A non-pod hull for the ordinary ships (the demo core's first hull is the
        // Observer/pod hull, which carries c_habmLifepod), and the pod hull for the
        // lifepod subject.
        var shipHull = mission.Core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod));
        var podHull = mission.Core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
        var sector = server.TestSectors[0];
        var peers = new List<(ConnectionId Conn, Ship Ship, Sector Sector)>();
        for (int i = 0; i < 24; i++)
        {
            var ship = new Ship((short)(10000 + i), shipHull, new Vector3(i * 90, 0, 0),
                Vector3.Zero, Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(ship, sector);
            if (i < 4) peers.Add((new ConnectionId((uint)(700 + i)), ship, sector));
        }
        server.TestSeedPeers(peers);
        var pod = new Ship(10999, podHull, new Vector3(500, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(pod, sector);

        var telemetry = server.BroadcastBudgetTelemetry!;
        telemetry.SetSecond(6000);
        const double simNow = 2000;
        for (uint tick = 0; tick < 6; tick++)
        {
            telemetry.SimNowMs = simNow;
            server.TestProjectRecordedTick(tick, snapshot: true);
        }
        server.FlushBroadcastSnapshots();

        // Four subjects, four states, all stamped 5 s ago for peer 0.
        var poses = telemetry.Peer(peers[0].Conn).LastPoses;
        var docked = peers[1].Ship;
        docked.StationId = 1;
        poses.Set(docked.Id, simNow - 5000, docked.Position, true);

        var moving = peers[2].Ship;
        moving.Velocity = new Vector3(60, 0, 0);
        poses.Set(moving.Id, simNow - 5000, moving.Position - new Vector3(400, 0, 0), true);

        var parked = peers[3].Ship;
        parked.Velocity = Vector3.Zero;
        poses.Set(parked.Id, simNow - 5000, parked.Position, true);

        poses.Set(pod.Id, simNow - 5000, pod.Position, true);

        telemetry.SimNowMs = simNow;
        string report = server.TakeBroadcastBudgetReport(6000, 5);
        Console.WriteLine(string.Join("\n", report.Split('\n').Where(line => line.StartsWith("[bb-age"))));
        string[] why = report.Split('\n').Where(line => line.StartsWith("[bb-age-why] older3s=")).ToArray();
        Assert.Single(why);
        Assert.Contains("older3s=4", why[0]);
        Assert.Contains("docked=1", why[0]);
        Assert.Contains("lifepod=1", why[0]);
        Assert.Contains("stationary=1", why[0]);
        Assert.Contains("starved=1", why[0]);
        Assert.Contains("viewerNotSwept=0", why[0]);
        Assert.Contains("dead=0", why[0]);
        Assert.Contains("parented=0", why[0]);
        Assert.Contains("otherSector=0", why[0]);
        // The never-posed line still reconciles with the [bb-age] counters.
        string age = report.Split('\n').First(line => line.StartsWith("[bb-age]"));
        int never = int.Parse(Field(age, "neverPosed="), CultureInfo.InvariantCulture);
        string[] neverWhy = report.Split('\n').Where(line => line.StartsWith("[bb-age-why] neverPosed=")).ToArray();
        Assert.Single(neverWhy);
        Assert.Contains($"neverPosed={never}", neverWhy[0]);
        // Every stale pair is accounted for by exactly one reason.
        Assert.Equal(4, ReasonSum(why[0]));
        Assert.Equal(never, ReasonSum(neverWhy[0]));
    }

    /// <summary>Sum of every reason counter on a <c>[bb-age-why]</c> line: the split
    /// must account for every stale/never-posed pair, never a subset. Returns the
    /// reason sum, or -1 when it does not add up to the line's own total.</summary>
    private static int ReasonSum(string line)
    {
        var tokens = line[(line.IndexOf(']') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int total = int.Parse(tokens[0].Split('=')[1], CultureInfo.InvariantCulture);
        int sum = tokens.Skip(1).Sum(token => int.Parse(token.Split('=')[1], CultureInfo.InvariantCulture));
        return total == sum ? sum : -1;
    }

    /// <summary>The largest local loopback fixture (144 seated peers, one sector,
    /// every send family live): the lane rows reconcile with the P983 category
    /// totals, and the block is the table the report doc quotes.</summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Loopback_showcase_breaks_the_ordered_and_keyed_lanes_down()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        var peers = UpdateCadenceTests.Seed(server, ships: 144, viewers: 144);
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        var telemetry = server.BroadcastBudgetTelemetry!;
        var core = mission.Core;
        var sector = peers[0].Sector;
        var probe = new Probe(25000, sector, core.ProbeTypes.Values.First(), new(10, 20, 30), 0, 0, peers[0].Ship);
        var mine = new Mine(25001, sector, core.MineTypes.Values.First(), new(20, 30, 40), 0, peers[0].Ship);
        sector.Add(probe); sector.Add(mine);
        foreach (var station in sector.StationRuntimes) station.SideVisibility.SetSideVisibility(0, true);
        for (int i = 0; i < peers.Count; i++)
        {
            peers[i].Ship.SideId = 0;
            peers[i].Ship.CommandTarget = peers[(i + 1) % peers.Count].Ship;
        }
        double startWall = telemetry.Clock();
        uint tick = 0;
        for (; tick < 400; tick++)
        {
            for (int i = 0; i < peers.Count; i++)
            {
                var ship = peers[i].Ship;
                ship.Position = new(i * 13 + tick * (i % 3 + 1), (i % 7) * 100, tick * 2);
                ship.Velocity = new(i % 3, 0, 2);
                ship.Orientation.Reset();
                ship.Orientation.Yaw(0.11f * (i + tick));
            }
            telemetry.SimNowMs = tick * 50.0;
            telemetry.RefreshSecond();
            server.TestProjectRecordedTick(tick, snapshot: true);
        }
        server.FlushBroadcastSnapshots();
        double now = telemetry.Clock();
        long second = (long)now;
        double reportMs = 0;
        string report = "";
        for (int pass = 0; pass < 2; pass++)
        {
            long reportStart = Stopwatch.GetTimestamp();
            report = server.TakeBroadcastBudgetReport(now, Math.Max(0.001, now - startWall));
            reportMs = Stopwatch.GetElapsedTime(reportStart).TotalMilliseconds;
        }
        Console.WriteLine(report);
        Console.WriteLine($"[p990-report] peers={telemetry.PeerCount} stamps={telemetry.PoseStampCount()} " +
                          $"reportMsWarm={reportMs:0.00}");

        Assert.Contains("[bb-lane]", report);
        Assert.Contains("[bb-other] fmid=", report);
        Assert.Contains("[bb-keyed] key=", report);
        Assert.Contains("[bb-age-why] older3s=", report);
        foreach (string line in report.Split('\n').Where(l => l.StartsWith("[bb-age-why]")))
            Assert.Equal(
                int.Parse(line[(line.IndexOf(']') + 2)..].Split(' ')[0].Split('=')[1], CultureInfo.InvariantCulture),
                ReasonSum(line));

        // Exact reconciliation: the per-id rows are the ordered lane, the
        // per-family rows are the coalescing lane, nothing is double booked.
        long laneBytes = 0;
        for (int fmid = 0; fmid < BroadcastTelemetry.FmidSlots; fmid++)
            laneBytes += telemetry.OrderedLaneWindowBytes(fmid, second);
        Assert.Equal(telemetry.WindowBytes(BroadcastCategory.Other, second), laneBytes);
        long familyBytes = 0;
        for (int family = 0; family < BroadcastTelemetry.KeyedFamilies; family++)
            familyBytes += telemetry.KeyedLaneWindowBytes(family, second);
        Assert.Equal(telemetry.WindowBytes(BroadcastCategory.ReliableKeyed, second), familyBytes);
        Assert.True(telemetry.OrderedLaneWindowBytes(ShipStatusMessage.Fmid, second) > 0,
            "no SHIP_STATUS(90) bytes were attributed on the ordered lane");
        Assert.True(laneBytes > 0, "the ordered lane carried no bytes");
    }

    private static string Field(string line, string key)
    {
        int start = line.IndexOf(key, StringComparison.Ordinal) + key.Length;
        int end = start;
        while (end < line.Length && char.IsDigit(line[end])) end++;
        return line[start..end];
    }

    /// <summary>P990 — the same §4 method as P983, on a workload that mirrors the
    /// real per-tick mix: ordered frames whose headers must be parsed for the
    /// per-fmid split, keyed frames across the port's families, and one pose stamp
    /// per heavy record. The gate is the diagnostic's cost against the measured
    /// p95 projection tick on the same 144-peer fixture.</summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Perf")]
    public void Mixed_lane_overhead_stays_within_30ns_per_call_and_three_percent_of_the_p95_tick()
    {
        Require.Artwork();
        var (offBlocks, _) = MeasureTickBlocks();
        double offP95 = offBlocks.Order().ToArray()[offBlocks.Length / 2];

        var telemetry = new BroadcastTelemetry();
        var ids = Enumerable.Range(1, 144).Select(i => new ConnectionId((uint)i)).ToArray();
        var statusFrames = Enumerable.Range(0, 24).Select(i => Frame(new ShipStatusMessage
        {
            ShipId = (short)(10000 + i), HullId = 1, SectorId = 1, StationId = -1, ParentId = -1,
            State = ShipStatusMessage.StateFlying, StateTime = (uint)i,
        })).ToArray();
        var keyedFrames = Enumerable.Range(0, 8).Select(_ => Frame(new ExportShip { ShipId = 1, HullId = 1 })).ToArray();
        var heavy = Enumerable.Range(0, 16).Select(i => new PoseStamp((short)(10000 + i), 0, Vector3.Zero, true)).ToArray();
        telemetry.SetSecond(1);
        foreach (var id in ids) { var peer = telemetry.Peer(id); for (short s = 0; s < 140; s++) peer.MarkPose(s, 0); }
        void Workload()
        {
            foreach (var id in ids)
            {
                var peer = telemetry.Peer(id);
                // 1 HEAVY(176) + LIGHT(175) frame, ~24 ordered frames, 8 keyed.
                telemetry.RecordSendFrame(id, 2, BroadcastCategory.HeavyShipUpdates, keyedFrames[0], splitBytes: 24);
                for (int i = 0; i < statusFrames.Length; i++) telemetry.RecordSendFrame(id, 0, BroadcastCategory.Other, statusFrames[i]);
                for (int i = 0; i < keyedFrames.Length; i++) telemetry.RecordSendFrame(id, i + 1, BroadcastCategory.Other, keyedFrames[i]);
                for (int i = 0; i < heavy.Length; i++) peer.MarkPose(heavy[i].ShipId, 1);
            }
        }
        // Per-arm attribution: which of the four accounting kinds costs what
        // (each arm runs the exact per-tick call count of its kind at 144 peers).
        void HeavyArm()
        {
            foreach (var id in ids)
                telemetry.RecordSendFrame(id, 2, BroadcastCategory.HeavyShipUpdates, keyedFrames[0], splitBytes: 24);
        }
        void OrderedArm()
        {
            foreach (var id in ids)
                for (int i = 0; i < statusFrames.Length; i++)
                    telemetry.RecordSendFrame(id, 0, BroadcastCategory.Other, statusFrames[i]);
        }
        void KeyedArm()
        {
            foreach (var id in ids)
                for (int i = 0; i < keyedFrames.Length; i++)
                    telemetry.RecordSendFrame(id, i + 1, BroadcastCategory.Other, keyedFrames[i]);
        }
        void StampArm()
        {
            foreach (var id in ids)
            {
                var peer = telemetry.Peer(id);
                for (int i = 0; i < heavy.Length; i++) peer.MarkPose(heavy[i].ShipId, 1);
            }
        }
        double Arm(Action action)
        {
            for (int i = 0; i < 20; i++) action();
            const int armReps = 50;
            long armStart = Stopwatch.GetTimestamp();
            for (int i = 0; i < armReps; i++) action();
            return (Stopwatch.GetTimestamp() - armStart) * 1000.0 / Stopwatch.Frequency / armReps;
        }
        double heavyMs = Arm(HeavyArm), orderedMs = Arm(OrderedArm), keyedMs = Arm(KeyedArm), stampMs = Arm(StampArm);

        for (int i = 0; i < 20; i++) Workload();
        const int reps = 50;
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < reps; i++) Workload();
        double costMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency / reps;

        const int peers = 144, perPeerCalls = 1 + 24 + 8 + 16;
        double perCallNs = costMs * 1_000_000 / (peers * perPeerCalls);
        Console.WriteLine($"[p990-overhead] offP95={offP95:0.000}ms mixedPerTick={costMs:0.0000}ms " +
                          $"share={costMs / offP95 * 100:0.000}% frames={telemetry.Probes} " +
                          $"perCallNs={perCallNs:0.0} " +
                          $"arms[heavy={heavyMs:0.0000} ordered24={orderedMs:0.0000} keyed8={keyedMs:0.0000} stamps16={stampMs:0.0000}]ms");
        Assert.True(telemetry.Probes > 0);
        Assert.True(telemetry.OrderedLaneWindowBytes(ShipStatusMessage.Fmid, 1) > 0);

        // The bound is the PER-ACCOUNTED-CALL cost, not a share of one fixture's
        // p95: the diagnostic's work is exactly "one pass per frame and per pose
        // stamp the server already hands to the transport", so a share bound only
        // moves with how many frames the fixture happens to send per peer. P983's
        // 1 %-of-p95 bound was measured on a 14-calls/peer workload; the rig's real
        // mix (P990 audit of the b104 window: ~25 frames + ~19 poses per peer per
        // tick) is ~50 calls/peer, i.e. 7 200 calls per 144-peer tick, so at the
        // ~20 ns/call this path measures, the honest expectation at 144 peers is
        // ~0.14 ms = ~2.3 % of a ~6 ms tick — under 1 % is not reachable at 144
        // peers for the real mix with any per-frame accounting. 30 ns/call leaves
        // the gate ~35 % above the measured cost (this host runs at load 13-24 from
        // other lanes) while still catching a regression back towards the
        // dictionary-based path (25-28 ns/call before the flat table).
        Assert.True(perCallNs <= 30,
            $"the diagnostic costs {perCallNs:0.0} ns per accounted frame/pose stamp " +
            $"({costMs:0.0000} ms per 144-peer mixed tick, {costMs / offP95 * 100:0.0}% of the {offP95:0.000}ms p95 tick)");
        Assert.True(costMs <= offP95 * 0.03,
            $"the diagnostic costs {costMs:0.0000}ms per 144-peer mixed tick, " +
            $"{costMs / offP95 * 100:0.0}% of the {offP95:0.000}ms p95 tick (bound 3% = ~1.3x the " +
            "derived 50-calls/peer expectation above)");
    }

    /// <summary>P990 — the rig-scale question: at 411 seated peers (the b104 run's
    /// shape: three sectors, ~136 visible ships per peer) what does the diagnostic
    /// actually add to a projection tick, and where does it go? OFF/ON blocks
    /// alternate inside ONE fixture so boot, JIT and machine drift cancel; the gate
    /// is the per-block MEAN delta (a constant per-tick cost, so the mean is the
    /// right estimator on a shared host where a 20-tick p95 swings ±20 %), with the
    /// p95 reported alongside. The stamp micro-comparison measures the P983
    /// <c>Dictionary&lt;short,double&gt;</c> per-pair bookkeeping against the P990
    /// dense table at exactly the rig's stamp count — the number the b104
    /// <c>step:broadcastShipsUpdate</c> delta came from — and the report cost is
    /// measured warm, because a whole-window hitch is the other half of the b104
    /// tick-wall delta.</summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Perf")]
    public void Rig_scale_projection_shows_what_the_diagnostic_adds_per_tick()
    {
        Require.Artwork();
        const int peersCount = 411, ships = 411, blocks = 3, warm = 12, measured = 40;
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(budget: false));
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        // The b104 shape: 411 peers spread over three sectors, ~136 ships visible
        // per peer (the rig's bb-top ageShips), all same-side so every candidate is
        // pose-visible (the showcase convention).
        var sectors = server.TestSectors;
        Assert.True(sectors.Count >= 3, "the fixture map must have at least 3 sectors");
        var hull = mission.Core.HullTypes.Values.First();
        var peers = new List<(ConnectionId Conn, Ship Ship, Sector Sector)>(peersCount);
        for (int i = 0; i < ships; i++)
        {
            var sector = sectors[i % 3];
            var ship = new Ship((short)(10000 + i), hull, new Vector3(i * 90, 0, 0),
                Vector3.Zero, Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(ship, sector);
            if (i < peersCount) peers.Add((new ConnectionId((uint)(700 + i)), ship, sector));
        }
        server.TestSeedPeers(peers);
        uint tick = 0;
        for (int i = 0; i < 30; i++) Advance();   // JIT + latch warm-up, both arms cold-free

        var offMean = new double[blocks];
        var onMean = new double[blocks];
        var offP95 = new double[blocks];
        var onP95 = new double[blocks];
        long probes = 0, stamps = 0;
        double reportMs = 0;
        for (int block = 0; block < blocks; block++)
        {
            (offMean[block], offP95[block]) = Block(on: false);
            (onMean[block], onP95[block]) = Block(on: true);
            if (server.BroadcastBudgetTelemetry is { } telemetry)
            {
                probes = server.TestBroadcastTelemetryProbes;
                stamps = server.BroadcastTelemetryCounts().PoseStamps;
                long reportStart = Stopwatch.GetTimestamp();
                server.TakeBroadcastBudgetReport(100, 5);
                server.TakeBroadcastBudgetReport(100, 5);
                reportMs = Stopwatch.GetElapsedTime(reportStart).TotalMilliseconds / 2;
            }
        }
        server.TestSetBroadcastTelemetry(false);
        double off = Percentile(offMean, 0.5), on = Percentile(onMean, 0.5);
        double delta = on - off;
        var ratios = offMean.Zip(onMean, (o, n) => n / o).Order().ToArray();
        double ratio = ratios[ratios.Length / 2];

        // The stamp structure itself, at the rig's shape (411 peers x ~140 visible
        // ships per peer), P983's dictionary vs P990's dense table.
        var legacy = new Dictionary<short, double>[peersCount];
        for (int i = 0; i < peersCount; i++)
        {
            legacy[i] = new Dictionary<short, double>(160);
            for (short s = 0; s < 140; s++) legacy[i][s] = 0;
        }
        var table = new PoseStampTable[peersCount];
        for (int i = 0; i < peersCount; i++)
        {
            table[i] = new PoseStampTable();
            for (short s = 0; s < 140; s++) table[i].Set(s, 0, Vector3.Zero, true);
        }
        short[] stampsPerTick = Enumerable.Range(0, 16).Select(i => (short)((i * 7) % 140)).ToArray();
        double StampCost(Func<int, short, double> mark)
        {
            for (int rep = 0; rep < 5; rep++)
                for (int i = 0; i < peersCount; i++)
                    foreach (short s in stampsPerTick) mark(i, s);
            long start = Stopwatch.GetTimestamp();
            const int reps = 40;
            for (int rep = 0; rep < reps; rep++)
                for (int i = 0; i < peersCount; i++)
                    foreach (short s in stampsPerTick) mark(i, s);
            return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency / reps;
        }
        double dictMs = StampCost((i, s) => { legacy[i][s] = 1000; return 0; });
        double tableMs = StampCost((i, s) => { table[i].Set(s, 1000, Vector3.Zero, true); return 0; });

        Console.WriteLine($"[p990-rigscale] peers={peersCount} offMean={off:0.000}ms onMean={on:0.000}ms " +
                          $"delta={delta:0.000}ms share={delta / off * 100:0.0}% ratio={ratio:0.000} " +
                          $"offP95={Percentile(offP95, 0.5):0.00}ms onP95={Percentile(onP95, 0.5):0.00}ms " +
                          $"offBlocks=[{string.Join(",", offMean.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)))}] " +
                          $"onBlocks=[{string.Join(",", onMean.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)))}] " +
                          $"probes={probes} stamps={stamps} " +
                          $"stampBookkeeping[dict411x140={dictMs:0.0000} table411x140={tableMs:0.0000}]ms " +
                          $"reportMsWarm={reportMs:0.00}");
        Assert.True(probes > 0, "the ON arm recorded no send");
        Assert.True(stamps > 0, "the ON arm recorded no pose stamp");
        // Honest bound: the whole diagnostic (per-tick bookkeeping + the 5 s report)
        // must stay inside a small constant per tick at the rig's peer count. The
        // b104 A/B measured +3 % mean tick-wall / +0.9 ms in step:broadcastShipsUpdate
        // on the P983 dictionary path; this flat-table path is gated at <= 1.5 ms
        // mean per 411-peer tick (~6 % of the local 14 ms p95) so a regression back
        // towards per-pair dictionary bookkeeping cannot pass unnoticed.
        Assert.True(delta <= 1.5,
            $"the diagnostic added {delta:0.000}ms per 411-peer tick (ratio {ratio:0.000}, " +
            $"off={off:0.00}ms on={on:0.00}ms mean): bound is 1.5ms");

        (double Mean, double P95) Block(bool on)
        {
            server.TestSetBroadcastTelemetry(on);
            if (on && server.BroadcastBudgetTelemetry is { } telemetry)
            {
                telemetry.SetSecond(100);
                telemetry.SimNowMs = 1000;
            }
            for (int i = 0; i < warm; i++) Advance();
            var times = new double[measured];
            for (int i = 0; i < measured; i++)
            {
                long start = Stopwatch.GetTimestamp();
                server.TestProjectRecordedTick(tick, snapshot: true);
                times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Advance();
            }
            server.FlushBroadcastSnapshots();
            return (times.Average(), Percentile(times, 0.95));
        }

        void Advance()
        {
            for (int i = 0; i < peers.Count; i++)
            {
                var ship = peers[i].Ship;
                ship.Position = new(i * 13 + tick * (i % 3 + 1), (i % 7) * 100, tick * 2);
                ship.Velocity = new(i % 3, 0, 2);
                ship.Orientation.Reset();
                ship.Orientation.Yaw(0.11f * (i + tick));
            }
            tick++;
            server.FlushBroadcastSnapshots();
        }
    }

    private static (double[] BlockP95, double[] Times) MeasureTickBlocks(int blocks = 4, int warm = 15, int measured = 120)
    {
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(budget: false));
        var peers = UpdateCadenceTests.Seed(server, ships: 144, viewers: 144);
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        for (int i = 0; i < peers.Count; i++) peers[i].Ship.SideId = (short)(i % 2);
        uint tick = 0;
        var blocksOut = new double[blocks];
        var all = new List<double>(blocks * measured);
        for (int block = 0; block < blocks; block++)
        {
            for (int i = 0; i < warm; i++) Advance();
            var times = new double[measured];
            for (int i = 0; i < measured; i++)
            {
                long start = Stopwatch.GetTimestamp();
                server.TestProjectRecordedTick(tick, snapshot: true);
                times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                all.Add(times[i]);
                Advance();
            }
            blocksOut[block] = Percentile(times, 0.95);
        }
        server.FlushBroadcastSnapshots();
        return (blocksOut, all.ToArray());

        void Advance()
        {
            for (int i = 0; i < peers.Count; i++)
            {
                var ship = peers[i].Ship;
                ship.Position = new(i * 13 + tick * (i % 3 + 1), (i % 7) * 100, tick * 2);
                ship.Velocity = new(i % 3, 0, 2);
                ship.Orientation.Reset();
                ship.Orientation.Yaw(0.11f * (i + tick));
            }
            tick++;
            server.FlushBroadcastSnapshots();
        }
    }

    private static double Percentile(double[] values, double fraction)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];
    }
}
