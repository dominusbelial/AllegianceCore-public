using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P862b (1) — the aleph destination glimpse must resolve its destination SECTOR
/// the way native does: <c>DataWarpIGC.DestinationId</c> is the id of the PARTNER
/// warp (<c>warpIGC.h:129-141</c>, <c>GetWarp(m_warpDef.destinationID)</c>), and the
/// destination sector is that partner's <c>ClusterId</c> — the P540c rule
/// <see cref="WarpDestinationResolver"/> owns and the server transit / autopilot
/// already use. Before P862b the glimpse fed the partner WARP id straight into the
/// cluster table, so on a map whose warp ids and cluster ids do not overlap (this
/// one: clusters 1050-1062, warps 2150-2193) it resolved nothing and drew nothing.
///
/// These tests fail against that code: the decoy cluster below has the id of the
/// partner warp, so a warp-id-as-sector lookup picks the decoy while the correct
/// resolution picks the real partner sector.
/// </summary>
public sealed class P862bGlimpseDestinationTests
{
    private const short Omega = 1053;      // the sector the pilot is in
    private const short Alderan = 1052;    // the sector its aleph leads to
    private const short WarpOmegaToAlderan = 2153;
    private const short WarpAlderanToOmega = 2152;   // the partner warp
    private const short Decoy = 2152;      // a cluster whose id equals a WARP id

    private static DataWarpIGC Warp(short id, short cluster, short destination, float x = 0f, float y = 0f, float z = 0f)
        => new(id, destination, 60, "warp", "icon", $"warp{id}",
            [x, y, z], [0f, 0f, 1f], [0f, 0f, 0f], 1f, cluster);

    private static DataClusterIGC Cluster(short id, string name)
        => new(371u, [1f, 0f, 0f], 1u, 0f, 0f, id, 0, 300, name,
            "globe3", "nebplnt06bmp", 10, 20, 50, false, false);

    private static Dictionary<short, DataWarpIGC> AlephPair() => new()
    {
        [WarpOmegaToAlderan] = Warp(WarpOmegaToAlderan, Omega, WarpAlderanToOmega),
        [WarpAlderanToOmega] = Warp(WarpAlderanToOmega, Alderan, WarpOmegaToAlderan),
    };

    private static Dictionary<short, DataClusterIGC> ClustersWithDecoy() => new()
    {
        [Omega] = Cluster(Omega, "Omega"),
        [Alderan] = Cluster(Alderan, "Alderan"),
        // The trap: this cluster is named after the PARTNER WARP's id, so the
        // pre-P862b `Clusters[DestinationId]` lookup resolves IT, not Alderan.
        [Decoy] = Cluster(Decoy, "Phantom (warp id, not a sector)"),
    };

    [Fact]
    public void Glimpse_resolves_the_partner_sectors_id_not_the_raw_destination_id()
    {
        var targets = AlephGlimpseRenderer.CollectTargets(
            AlephPair(), ClustersWithDecoy(), Omega, Vector3.Zero);

        Assert.Single(targets);
        // The glimpse aims at Alderan — the sector the partner warp sits in...
        Assert.Equal(Alderan, targets[0].DestSector);
        Assert.Equal("Alderan", targets[0].Dest.Name);
        // ...NOT at the cluster whose id happens to equal the partner warp id.
        Assert.NotEqual(Decoy, targets[0].DestSector);
        Assert.Equal(WarpOmegaToAlderan, targets[0].Warp.WarpId);
    }

    [Fact]
    public void Glimpse_resolution_agrees_with_the_shared_WarpDestinationResolver()
    {
        var warps = AlephPair();
        var targets = AlephGlimpseRenderer.CollectTargets(warps, ClustersWithDecoy(), Omega, Vector3.Zero);

        // Same rule the server transit / autopilot use (P540c): one definition.
        Assert.Equal(WarpDestinationResolver.DestinationSector(warps, WarpOmegaToAlderan),
            targets[0].DestSector);
    }

    [Fact]
    public void A_warp_whose_partner_is_not_synced_yields_no_glimpse()
    {
        var warps = new Dictionary<short, DataWarpIGC>
        {
            [WarpOmegaToAlderan] = Warp(WarpOmegaToAlderan, Omega, destination: 9999),  // dangling
        };

        Assert.Empty(AlephGlimpseRenderer.CollectTargets(warps, ClustersWithDecoy(), Omega, Vector3.Zero));
    }

    [Fact]
    public void A_known_partner_in_a_sector_the_client_does_not_know_yields_no_glimpse()
    {
        var clusters = new Dictionary<short, DataClusterIGC> { [Omega] = Cluster(Omega, "Omega") };
        Assert.Empty(AlephGlimpseRenderer.CollectTargets(AlephPair(), clusters, Omega, Vector3.Zero));
    }

    [Fact]
    public void Only_the_current_sectors_warps_glimpse()
    {
        var targets = AlephGlimpseRenderer.CollectTargets(
            AlephPair(), ClustersWithDecoy(), Alderan, Vector3.Zero);

        Assert.Single(targets);
        Assert.Equal(Omega, targets[0].DestSector);          // looking the other way
        Assert.Equal(WarpAlderanToOmega, targets[0].Warp.WarpId);
    }

    [Fact]
    public void Distance_is_measured_from_the_camera_eye()
    {
        var warps = AlephPair();
        warps[WarpOmegaToAlderan] = Warp(WarpOmegaToAlderan, Omega, WarpAlderanToOmega, x: 300f, y: 400f, z: 0f);
        var targets = AlephGlimpseRenderer.CollectTargets(
            warps, ClustersWithDecoy(), Omega, new Vector3(0f, 0f, 0f));

        Assert.Equal(300f * 300f + 400f * 400f, targets[0].DistSq, 3);
    }

    [Fact]
    public void The_same_destination_from_two_warps_shares_one_glimpse_key()
    {
        // Two alephs in one sector leading to the same destination sector must
        // share the cached target (the cache is keyed by the RESOLVED sector).
        const short OtherWarp = 2160;
        var warps = AlephPair();
        warps[OtherWarp] = Warp(OtherWarp, Omega, WarpAlderanToOmega, x: 1000f);

        var targets = AlephGlimpseRenderer.CollectTargets(warps, ClustersWithDecoy(), Omega, Vector3.Zero);

        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.Equal(Alderan, t.DestSector));
        Assert.Single(targets.Select(t => t.DestSector).Distinct());
    }
}

/// <summary>
/// P862b (2) — the sector warm-up must be able to prepare the GLIMPSE sky the
/// arrival frame will ask for: one item per destination sector, deduped across
/// alephs and sectors, priority-promoted with the sector being approached.
/// </summary>
public sealed class P862bGlimpseWarmupTests
{
    [Fact]
    public void Glimpse_sky_item_is_one_per_sector()
    {
        var queue = new SectorWarmupQueue();

        Assert.True(queue.Enqueue(SectorWarmupPlanner.GlimpseSky(1053)));
        Assert.False(queue.Enqueue(SectorWarmupPlanner.GlimpseSky(1053)));
        Assert.True(queue.Enqueue(SectorWarmupPlanner.GlimpseSky(1052)));

        Assert.Equal(2, queue.PendingCount);
        Assert.Equal(WarmAssetKind.GlimpseSky, queue.Pending[0].Kind);
        Assert.Equal((short)1053, queue.Pending[0].SectorId);
    }

    [Fact]
    public void Glimpse_sky_item_is_distinct_from_the_sector_sky_item()
    {
        // The planet/dome may be resident (the sector's own Sky item) while the
        // glimpse's small target is not — they are different resources.
        var queue = new SectorWarmupQueue();

        Assert.True(queue.Enqueue(SectorWarmupPlanner.Sky(1053, "nebplnt43bmp", "globe2")));
        Assert.True(queue.Enqueue(SectorWarmupPlanner.GlimpseSky(1053)));
        Assert.Equal(2, queue.PendingCount);
    }

    [Fact]
    public void Glimpse_sky_item_is_promoted_with_the_sector_being_approached()
    {
        var queue = new SectorWarmupQueue();
        queue.Enqueue(SectorWarmupPlanner.GlimpseSky(1053));   // the sector being left
        queue.Enqueue(new WarmAsset(WarmAssetKind.Rock, "bgrnd05", null, 1052));
        queue.Enqueue(new WarmAsset(WarmAssetKind.GlimpseSky, "glimpse/1060", null, 1060));

        Assert.Equal(1, queue.Promote(1052));
        Assert.Equal((short)1052, queue.Pending[0].SectorId);
        // The departing sector's glimpse keeps its place behind the approach set.
        Assert.Equal((short)1053, queue.Pending[1].SectorId);
    }

    [Fact]
    public void A_warmed_glimpse_is_never_handed_out_again()
    {
        var queue = new SectorWarmupQueue();
        var item = SectorWarmupPlanner.GlimpseSky(1053);
        Assert.True(queue.Enqueue(item));

        Assert.True(queue.TryTake(out var taken));
        queue.Complete(taken, created: true);

        Assert.True(queue.IsWarmed(item));
        Assert.Equal(0, queue.PendingCount);
        Assert.False(queue.Enqueue(item));                    // a second approach adds nothing
    }
}

/// <summary>
/// P862b (3) — the transit-flare occlusion test without the in-frame readback.
/// The old code called <c>DepthRT.GetData</c> inside the frame it was drawing
/// (a synchronous GPU→CPU readback that waits for the depth pass), for every
/// flaring aleph, every frame of a pulse. The probe answers from LAST frame's
/// depth — the same exact pixel/depth pair, one frame late — and caps itself to
/// one readback per frame. These tests drive it with a fake depth source, so the
/// scheme is verified without a GPU.
/// </summary>
public sealed class P862bFlareOcclusionTests
{
    private const float AlephNorm = 0.20f;   // e.g. the aleph at ~1200 u

    private static Color[] Fill(Rectangle rect, float sceneDepth)
        => Enumerable.Repeat(LinearDepth.Encode(sceneDepth), rect.Width * rect.Height).ToArray();

    private sealed class FakeDepth
    {
        public float Scene = 0.10f;
        public int Calls;
        public List<Rectangle> Rects = [];

        public Color[]? Read(Rectangle rect)
        {
            Calls++;
            Rects.Add(rect);
            return Fill(rect, Scene);
        }
    }

    [Fact]
    public void Deferred_answers_from_the_previous_frames_depth_one_frame_later()
    {
        var depth = new FakeDepth { Scene = 0.10f };            // geometry in front of the aleph
        var probe = new FlareOcclusionProbe(FlareOcclusionMode.Deferred, depth.Read);

        probe.BeginFrame();                                     // frame 1
        Assert.False(probe.Query(7, 100, 100, AlephNorm, 1280, 720, 1280, 720),
            "with no verdict yet the flare stays visible (the P585 failure mode)");
        probe.BeginFrame();                                     // frame 2
        probe.SampleDeferred();                                 // reads frame 1's depth
        Assert.Equal(1, depth.Calls);
        Assert.True(probe.Query(7, 100, 100, AlephNorm, 1280, 720, 1280, 720),
            "the previous frame's depth says the funnel centre is behind a rock");

        // Once the rock is gone the verdict follows within the refresh window
        // (RefreshFrames at 60 fps = ~100 ms), not on the very next frame: the
        // decision is one frame late by construction and re-read periodically.
        depth.Scene = 0.90f;
        bool cleared = false;
        for (int i = 0; i < 3 * FlareOcclusionProbe.RefreshFrames && !cleared; i++)
        {
            probe.BeginFrame();
            bool occluded = probe.Query(7, 100, 100, AlephNorm, 1280, 720, 1280, 720);
            Assert.True(occluded, "nothing has been re-read yet, so the flare stays hidden");
            int before = depth.Calls;
            probe.SampleDeferred();                       // one rendered frame
            if (depth.Calls == before) continue;
            probe.BeginFrame();                           // the NEXT frame sees the new verdict
            cleared = !probe.Query(7, 100, 100, AlephNorm, 1280, 720, 1280, 720);
        }
        Assert.True(cleared, "a cleared sight line must be picked up inside the refresh window");
    }

    [Fact]
    public void Deferred_is_one_frame_late_by_construction_not_stale_forever()
    {
        var depth = new FakeDepth { Scene = 0.10f };
        var probe = new FlareOcclusionProbe(FlareOcclusionMode.Deferred, depth.Read);

        probe.BeginFrame();
        probe.Query(7, 100, 100, AlephNorm, 1280, 720, 1280, 720);
        probe.BeginFrame();
        probe.SampleDeferred();

        // The flare stops drawing for longer than MaxVerdictAgeFrames: the old
        // verdict must be forgotten rather than hiding it when it comes back.
        for (int i = 0; i < FlareOcclusionProbe.MaxVerdictAgeFrames + 2; i++) probe.BeginFrame();
        Assert.False(probe.Query(7, 100, 100, AlephNorm, 1280, 720, 1280, 720));
    }

    [Fact]
    public void Blocking_reads_the_current_frames_depth_immediately()
    {
        var depth = new FakeDepth { Scene = 0.10f };
        var probe = new FlareOcclusionProbe(FlareOcclusionMode.Blocking, depth.Read);

        probe.BeginFrame();
        Assert.True(probe.Query(7, 100, 100, AlephNorm, 1280, 720, 1280, 720));
        Assert.Equal(1, depth.Calls);

        depth.Scene = 0.90f;
        Assert.False(probe.Query(7, 100, 100, AlephNorm, 1280, 720, 1280, 720));
        Assert.Equal(2, depth.Calls);
    }

    [Fact]
    public void Off_never_reads_and_never_occludes()
    {
        var depth = new FakeDepth { Scene = 0.10f };
        var probe = new FlareOcclusionProbe(FlareOcclusionMode.Off, depth.Read);

        probe.BeginFrame();
        Assert.False(probe.Query(7, 100, 100, AlephNorm, 1280, 720, 1280, 720));
        probe.BeginFrame();
        probe.SampleDeferred();

        Assert.Equal(0, depth.Calls);
        Assert.Equal(0, probe.PendingCount);
    }

    [Fact]
    public void No_depth_source_treats_the_flare_as_visible()
    {
        var probe = new FlareOcclusionProbe(FlareOcclusionMode.Deferred, readDepth: null);
        probe.BeginFrame();
        Assert.False(probe.Query(7, 100, 100, AlephNorm, 1280, 720, 1280, 720));
        probe.BeginFrame();
        probe.SampleDeferred();
        Assert.False(probe.Query(7, 100, 100, AlephNorm, 1280, 720, 1280, 720));

        var unreadable = new FlareOcclusionProbe(FlareOcclusionMode.Deferred, _ => null);
        unreadable.BeginFrame();
        unreadable.Query(7, 100, 100, AlephNorm, 1280, 720, 1280, 720);
        unreadable.BeginFrame();
        unreadable.SampleDeferred();
        Assert.False(unreadable.Query(7, 100, 100, AlephNorm, 1280, 720, 1280, 720));
    }

    [Fact]
    public void A_pixel_outside_the_depth_target_is_visible_and_not_sampled()
    {
        var depth = new FakeDepth();
        var probe = new FlareOcclusionProbe(FlareOcclusionMode.Deferred, depth.Read);

        probe.BeginFrame();
        Assert.False(probe.Query(7, -20, 10, AlephNorm, 1280, 720, 1280, 720));
        Assert.False(probe.Query(7, 100, 900, AlephNorm, 1280, 720, 1280, 720));
        probe.BeginFrame();
        probe.SampleDeferred();

        Assert.Equal(0, depth.Calls);
    }

    [Fact]
    public void Screen_pixels_map_onto_the_bottom_left_origin_depth_target()
    {
        // Same-size target: the Y flip only.
        Assert.True(FlareOcclusionProbe.TryPixel(0, 0, 1280, 720, 1280, 720, out int x, out int y));
        Assert.Equal(0, x);
        Assert.Equal(719, y);

        Assert.True(FlareOcclusionProbe.TryPixel(1279, 719, 1280, 720, 1280, 720, out x, out y));
        Assert.Equal(1279, x);
        Assert.Equal(0, y);

        // Half-resolution target (an upscaled render): both axes scale.
        Assert.True(FlareOcclusionProbe.TryPixel(640, 360, 1280, 720, 640, 360, out x, out y));
        Assert.Equal(320, x);
        Assert.Equal(179, y);

        // Degenerate sizes never produce a pixel.
        Assert.False(FlareOcclusionProbe.TryPixel(0, 0, 0, 0, 0, 0, out _, out _));
    }

    [Fact]
    public void The_scene_must_be_in_front_of_the_aleph_by_the_margin()
    {
        // P585's rule, unchanged: nearer by more than OcclusionMargin.
        Assert.True(FlareOcclusionProbe.IsSceneNearer(0.10f, 0.20f));
        Assert.False(FlareOcclusionProbe.IsSceneNearer(0.20f, 0.20f));
        Assert.False(FlareOcclusionProbe.IsSceneNearer(0.30f, 0.20f));
        Assert.False(FlareOcclusionProbe.IsSceneNearer(
            0.20f - FlareOcclusionProbe.OcclusionMargin * 0.5f, 0.20f));
        Assert.True(FlareOcclusionProbe.IsSceneNearer(
            0.20f - FlareOcclusionProbe.OcclusionMargin * 2f, 0.20f));
    }

    [Fact]
    public void Samples_within_a_small_rectangle_share_one_readback()
    {
        var depth = new FakeDepth { Scene = 0.10f };
        var probe = new FlareOcclusionProbe(FlareOcclusionMode.Deferred, depth.Read);

        probe.BeginFrame();
        probe.Query(2152, 500, 300, AlephNorm, 1280, 720, 1280, 720);
        probe.Query(2185, 505, 306, AlephNorm, 1280, 720, 1280, 720);
        probe.BeginFrame();
        probe.SampleDeferred();

        Assert.Equal(1, depth.Calls);                       // one batched read
        Assert.Equal(6, depth.Rects[0].Width);               // 500..505
        Assert.Equal(7, depth.Rects[0].Height);              // (y flipped) 5 rows apart
        Assert.Equal(2, probe.VerdictCount);

        probe.BeginFrame();
        Assert.True(probe.Query(2152, 500, 300, AlephNorm, 1280, 720, 1280, 720));
        Assert.True(probe.Query(2185, 505, 306, AlephNorm, 1280, 720, 1280, 720));
    }

    [Fact]
    public void Flares_spread_across_the_screen_cost_at_most_one_readback_a_frame()
    {
        var depth = new FakeDepth { Scene = 0.10f };
        var probe = new FlareOcclusionProbe(FlareOcclusionMode.Deferred, depth.Read);

        probe.BeginFrame();
        probe.Query(2152, 10, 10, AlephNorm, 1280, 720, 1280, 720);
        probe.Query(2185, 1200, 700, AlephNorm, 1280, 720, 1280, 720);
        probe.Query(2191, 640, 60, AlephNorm, 1280, 720, 1280, 720);
        probe.BeginFrame();
        probe.SampleDeferred();

        Assert.Equal(FlareOcclusionProbe.MaxReadbacksPerFrame, depth.Calls);
        Assert.Equal(1, depth.Rects[0].Width);               // the first sample only
        Assert.Equal(1, probe.VerdictCount);
    }

    [Fact]
    public void A_remembered_verdict_is_refreshed_only_every_few_frames()
    {
        var depth = new FakeDepth { Scene = 0.10f };
        var probe = new FlareOcclusionProbe(FlareOcclusionMode.Deferred, depth.Read);

        // One readback costs ~2.5 ms on this host's GL driver, so a pulse must not
        // re-test every frame: RefreshFrames (~10 Hz at 60 fps) is the cadence.
        // 60 frames also outlive MaxVerdictAgeFrames, so this proves a refreshed
        // verdict is never mistaken for a stale one (and so never drops back to
        // "visible" while the flare is still up).
        const int frames = 60;
        int reads = 0;
        for (int frame = 1; frame <= frames; frame++)
        {
            probe.BeginFrame();
            bool occluded = probe.Query(2152, 100, 100, AlephNorm, 1280, 720, 1280, 720);
            int before = depth.Calls;
            probe.SampleDeferred();
            if (depth.Calls > before) reads++;
            if (frame > 1)
                Assert.True(occluded, "the cached verdict keeps hiding the flare between refreshes");
        }

        int expected = frames / FlareOcclusionProbe.RefreshFrames;
        Assert.InRange(reads, expected - 1, expected + 1);
    }

    [Fact]
    public void The_mode_switch_parses_the_documented_values()
    {
        Assert.Equal(FlareOcclusionMode.Deferred, FlareOcclusionProbe.ParseMode(null));
        Assert.Equal(FlareOcclusionMode.Deferred, FlareOcclusionProbe.ParseMode("banana"));
        Assert.Equal(FlareOcclusionMode.Blocking, FlareOcclusionProbe.ParseMode("blocking"));
        Assert.Equal(FlareOcclusionMode.Off, FlareOcclusionProbe.ParseMode("off"));
        Assert.Equal(FlareOcclusionMode.Off, FlareOcclusionProbe.ParseMode("0"));
    }

    [Fact]
    public void Readbacks_are_counted_so_the_report_can_quote_them()
    {
        var depth = new FakeDepth { Scene = 0.10f };
        var probe = new FlareOcclusionProbe(FlareOcclusionMode.Deferred, depth.Read);

        probe.BeginFrame();
        probe.Query(2152, 100, 100, AlephNorm, 1280, 720, 1280, 720);
        probe.BeginFrame();
        probe.SampleDeferred();

        Assert.Equal(1, probe.Samples);
        Assert.Equal(1, probe.Batches);
        Assert.True(probe.TotalMs >= 0.0);
    }
}
