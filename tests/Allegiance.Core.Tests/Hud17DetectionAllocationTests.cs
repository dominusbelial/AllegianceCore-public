using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// HUD-17 — the per-tick cost of the new own-side detection step at 400 peers
/// (2 sides, about half seen): the <c>shipDetection</c> projection stage must be
/// <b>0 bytes/tick</b> (the change set and baseline are permanent fields cleared
/// in place, <c>ShipDetection.IsDetectedByEnemy</c> is an indexed side loop with
/// no LINQ/lambdas), and the status broadcast's allocation must not grow against
/// a baseline replay with no visibility edges. The test also records the number
/// of SHIP_STATUS(90) packets per tick so the edge re-sends are measurable
/// (report: docs/reports/HUD17_DETECTION_EYE.md).
/// </summary>
[Collection("LiveNetwork")]
[Trait("Tier", "Perf")]
public sealed class Hud17DetectionAllocationTests
{
    private const int Peers = 400;
    private const int WarmupTicks = 120;
    private const int MeasuredTicks = 100;

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Ship_detection_step_is_allocation_free_and_status_resends_only_grow_on_edges()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        // P887 (triaged by NT-3) — the `broadcastShipStates` budget below is the
        // SERIAL builder's allocation (the HUD-17 claim: the change set and the
        // baseline are permanent fields cleared in place). P887 made
        // `broadcast.parallelBuild` default ON, and the parallel builder gives
        // each tick's per-peer work to worker threads, so this thread's counter
        // no longer measures the builder (it logged 82–150 KB/tick for the same
        // unchanged code that logs 488 B/tick serially). Pin the serial builder,
        // per P887's own "the test that needs the old path pins it" rule; the
        // parallel build's numbers are in docs/reports/NT3_NIGHTLY_TRIAGE.md.
        var config = InterestTierTests.Config();
        config.Broadcast!.ParallelBuild = false;
        server.Boot(config);
        server.TestMission!.Stage = MissionStage.Started;
        var peers = UpdateCadenceTests.Seed(server, Peers, Peers);
        for (int i = 0; i < peers.Count; i++)
        {
            peers[i].Ship.SideId = (short)(i % 2);
            peers[i].Ship.Position = new((i % 40) * 350, (i / 40) * 350, 0);
        }

        double now = 0;
        server.TestUpdateCadenceClock = () => now;

        void ApplyVisibility(uint tick, bool toggle)
        {
            for (int i = 0; i < peers.Count; i++)
            {
                var ship = peers[i].Ship;
                short own = ship.SideId!.Value;
                short enemy = (short)(1 - own);
                // Own side is always visible (FedSrv.CPP:10890-10992); the other
                // side sees about half the ships, and with `toggle` that pattern
                // flips every 10 passes (the native visibility-pass cadence), so
                // every ship crosses the detection edge twice per 20 ticks.
                ship.SideVisibility.SetSideVisibility(own, true);
                bool seen = (i % 2 == 0) ^ (toggle && ((tick / 10) % 2 == 1));
                ship.SideVisibility.SetSideVisibility(enemy, seen);
            }
        }

        var stages = new Dictionary<string, long>();
        int statusPackets = 0;
        server.TestProjectionAllocationCapture = (stage, bytes) =>
            stages[stage] = stages.GetValueOrDefault(stage) + bytes;
        server.TestPublicationCapture = (_, bytes, _, _) =>
        {
            foreach (var message in FedPacket.Unpack(bytes))
                if ((ushort)(message[2] | message[3] << 8) == ShipStatusMessage.Fmid)
                    statusPackets++;
        };

        long Measure(bool toggle)
        {
            server.TestResetSnapshotReplay();
            server.TestSeedPeers(peers);
            uint tick = 0;
            void Tick()
            {
                now = tick / 20d;
                ApplyVisibility(tick, toggle);
                server.TestProjectRecordedTick(tick, snapshot: true);
                server.FlushBroadcastSnapshots();
                tick++;
            }
            for (; tick < WarmupTicks; tick++) Tick();
            stages.Clear();
            statusPackets = 0;
            try
            {
                for (int n = 0; n < MeasuredTicks; n++) Tick();
            }
            finally
            {
                // leave the capture delegates in place for the caller's reads
            }
            return stages.TryGetValue("step:broadcastShipStates", out var bss) ? bss / MeasuredTicks : -1;
        }

        // Edge replay: visibility flips throughout, so ~40 ships change bit per
        // tick and each change re-sends that ship's status.
        long edgeBss = Measure(toggle: true);
        int edgeStatuses = statusPackets;
        long detectionBytes = stages.GetValueOrDefault("shipDetection");
        Assert.True(stages.ContainsKey("shipDetection"), "the shipDetection allocation stage must exist");
        Assert.Equal(0, detectionBytes);
        Assert.True(edgeBss >= 0, "the broadcastShipStates stage must exist");

        // Baseline replay: same 400 peers and the same ~half-seen steady state,
        // but no visibility edges — the difference is exactly the extra
        // change-driven status packets.
        long baseBss = Measure(toggle: false);
        int baseStatuses = statusPackets;
        Assert.Equal(0, stages.GetValueOrDefault("shipDetection"));

        Console.WriteLine($"HUD17-COST: {Peers} peers; shipDetection={detectionBytes / MeasuredTicks} B/tick; " +
            $"broadcastShipStates edge={edgeBss} B/tick base={baseBss} B/tick; " +
            $"SHIP_STATUS edge={edgeStatuses / (double)MeasuredTicks:0.0}/tick base={baseStatuses / (double)MeasuredTicks:0.0}/tick; " +
            $"extra={(edgeStatuses - baseStatuses) / (double)MeasuredTicks:0.0}/tick");
        // The change path is a handful of pooled packets and dictionary marks:
        // it must not add a materially larger per-tick allocation than the
        // baseline status traffic.
        Assert.True(edgeBss <= baseBss + 2048,
            $"edge replay bss allocation {edgeBss} B/tick vs base {baseBss} B/tick (allowed +2048)");
        Assert.True(edgeStatuses > baseStatuses,
            "the toggling replay must produce the extra change-driven SHIP_STATUS packets");
    }
}
