using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P862 — the sector asset warm-up scheduler. These are the properties the warp
/// stall fix rests on: one resource per (kind, stem) no matter how many sectors
/// share it, an approached sector jumps the queue, the per-frame budget can never
/// wedge, and a warmed asset is never handed out again — so the SECOND entry into
/// a sector does no new loads (the caches themselves live for the match).
///
/// The loaders themselves (native mesh/texture/planet decode + upload) need a
/// GraphicsDevice and are exercised by the P862 harness
/// (<c>scripts/p862-warp-stall.sh</c>), whose [sector-entry] line reports what the
/// entry frames actually created.
/// </summary>
public sealed class P862SectorWarmupTests
{
    private static WarmAsset Rock(string stem, short sector = 1052, string? texture = null)
        => new(WarmAssetKind.Rock, stem, texture, sector);

    [Fact]
    public void Enqueue_accepts_the_same_rock_only_once_across_sectors()
    {
        var queue = new SectorWarmupQueue();

        Assert.True(queue.Enqueue(Rock("bgrnd05", sector: 1052)));
        Assert.False(queue.Enqueue(Rock("bgrnd05", sector: 1059)));   // same model, other sector
        Assert.False(queue.Enqueue(Rock("bgrnd05", sector: 1052)));   // same model, same sector

        Assert.Equal(1, queue.PendingCount);
        Assert.Equal(1, queue.KnownCount);
        Assert.Equal((short)1052, queue.Pending[0].SectorId);        // first sighting keeps it
    }

    [Fact]
    public void Enqueue_rejects_blank_stems()
    {
        var queue = new SectorWarmupQueue();
        Assert.False(queue.Enqueue(Rock("")));
        Assert.False(queue.Enqueue(Rock("   ")));
        Assert.False(queue.Enqueue(new WarmAsset(WarmAssetKind.Sky, "", null, 1052)));
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, queue.KnownCount);
    }

    [Fact]
    public void EnqueueAll_reports_how_many_were_new()
    {
        var queue = new SectorWarmupQueue();
        var rocks = SectorWarmupPlanner.Rocks(1052,
            [("bgrnd05", ""), ("bgrnd03", ""), ("bgrnd05", ""), (null, ""), ("bgrnd56", "bgrnd56bmp")]);

        Assert.Equal(3, queue.EnqueueAll(rocks));
        Assert.Equal(3, queue.PendingCount);
        Assert.Equal(0, queue.EnqueueAll(SectorWarmupPlanner.Rocks(1052, [("bgrnd05", "")])));
    }

    [Fact]
    public void Promote_moves_only_the_approached_sector_to_the_front_and_keeps_order()
    {
        var queue = new SectorWarmupQueue();
        queue.Enqueue(Rock("a", sector: 1053));
        queue.Enqueue(Rock("b", sector: 1052));
        queue.Enqueue(Rock("c", sector: 1053));
        queue.Enqueue(Rock("d", sector: 1052));

        Assert.Equal(2, queue.Promote(1052));

        Assert.Equal(new[] { "b", "d", "a", "c" }, queue.Pending.Select(p => p.Stem).ToArray());
    }

    [Fact]
    public void Promote_of_a_sector_with_nothing_pending_changes_nothing()
    {
        var queue = new SectorWarmupQueue();
        queue.Enqueue(Rock("a", sector: 1053));
        queue.Enqueue(Rock("b", sector: 1053));

        Assert.Equal(0, queue.Promote(1052));
        Assert.Equal(new[] { "a", "b" }, queue.Pending.Select(p => p.Stem).ToArray());
    }

    [Fact]
    public void Take_returns_the_front_and_shrinks_the_queue()
    {
        var queue = new SectorWarmupQueue();
        queue.Enqueue(Rock("a"));
        queue.Enqueue(Rock("b"));

        Assert.True(queue.TryTake(out var first));
        Assert.Equal("a", first.Stem);
        Assert.Equal(1, queue.PendingCount);
        Assert.True(queue.TryTake(out var second));
        Assert.Equal("b", second.Stem);
        Assert.False(queue.TryTake(out _));
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void A_completed_asset_is_never_warmed_again_so_a_second_entry_loads_nothing()
    {
        var queue = new SectorWarmupQueue();
        var rock = Rock("bgrnd05");

        queue.Enqueue(rock);
        queue.TryTake(out var taken);
        queue.Complete(taken, created: true);

        Assert.True(queue.IsWarmed(rock));
        Assert.Equal(1, queue.WarmedCount);
        Assert.Equal(1, queue.CreatedCount);

        // Second entry into the same sector: the same asset must not come back.
        Assert.False(queue.Enqueue(rock));
        Assert.False(queue.Enqueue(Rock("bgrnd05", sector: 1059)));
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(1, queue.WarmedCount);   // unchanged: no new load
    }

    [Fact]
    public void Resident_and_unavailable_items_are_counted_apart_from_created_ones()
    {
        var queue = new SectorWarmupQueue();
        queue.Enqueue(Rock("shared"));      // two sectors sharing one model
        queue.Enqueue(Rock("shared", 1059));
        queue.Enqueue(Rock("missing"));

        queue.TryTake(out var shared);
        queue.Complete(shared, created: true);
        queue.TryTake(out var missing);
        queue.Complete(missing, created: false, failed: true);

        Assert.Equal(2, queue.WarmedCount);
        Assert.Equal(1, queue.CreatedCount);
        Assert.Equal(1, queue.AlreadyResidentCount);
        Assert.Equal(1, queue.FailedCount);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void PendingFor_counts_only_that_sector()
    {
        var queue = new SectorWarmupQueue();
        queue.Enqueue(Rock("a", sector: 1052));
        queue.Enqueue(Rock("b", sector: 1052));
        queue.Enqueue(Rock("c", sector: 1053));

        Assert.Equal(2, queue.PendingFor(1052));
        Assert.Equal(1, queue.PendingFor(1053));
        Assert.Equal(0, queue.PendingFor(1060));
    }

    [Fact]
    public void The_frame_budget_spends_one_heavy_item_per_frame_and_never_wedges()
    {
        var budget = new WarmupBudget(frameBudgetMs: 2.0);

        budget.BeginFrame();
        Assert.True(budget.CanStart());      // the first item always starts
        budget.Spend(240);                   // a 2048² texture decode + upload
        Assert.False(budget.CanStart());     // the next frame must wait
        Assert.Equal(240, budget.SpentMs);
        Assert.Equal(1, budget.Frames);

        budget.BeginFrame();
        Assert.Equal(0, budget.SpentMs);
        Assert.True(budget.CanStart());      // the overshoot never wedges the queue
        budget.Spend(0.4);
        Assert.True(budget.CanStart());      // cheap items still fit together
        budget.Spend(2.0);
        Assert.False(budget.CanStart());
    }

    [Fact]
    public void The_budget_ignores_negative_spend()
    {
        var budget = new WarmupBudget(2.0);
        budget.BeginFrame();
        budget.Spend(-5);
        Assert.Equal(0, budget.SpentMs);
    }

    [Fact]
    public void Planner_rocks_trim_names_and_skip_blank_models()
    {
        var items = SectorWarmupPlanner
            .Rocks(1052, [(" bgrnd05 ", ""), (null, "x"), ("   ", "y"), ("bgrnd56", " bgrnd56bmp ")])
            .ToArray();

        Assert.Equal(2, items.Length);
        Assert.Equal("bgrnd05", items[0].Stem);
        Assert.Null(items[0].Hint);                    // an empty texture name is "use the model's own"
        Assert.Equal("bgrnd56", items[1].Stem);
        Assert.Equal("bgrnd56bmp", items[1].Hint);
        Assert.All(items, i => Assert.Equal(WarmAssetKind.Rock, i.Kind));
        Assert.All(items, i => Assert.Equal((short)1052, i.SectorId));
    }

    [Fact]
    public void Planner_sky_carries_the_planet_in_the_stem_and_the_poster_in_the_hint()
    {
        var sky = SectorWarmupPlanner.Sky(1052, " nebplnt06bmp ", " globe3 ");

        Assert.Equal(WarmAssetKind.Sky, sky.Kind);
        Assert.Equal("nebplnt06bmp", sky.Stem);
        Assert.Equal("globe3", sky.Hint);
        Assert.True(SectorWarmupPlanner.HasSky(sky));

        var posterOnly = SectorWarmupPlanner.Sky(1053, "", "globe2");
        Assert.Equal("", posterOnly.Stem);
        Assert.True(SectorWarmupPlanner.HasSky(posterOnly));

        Assert.False(SectorWarmupPlanner.HasSky(SectorWarmupPlanner.Sky(1053, null, null)));
    }

    [Fact]
    public void Planner_ships_and_stations_share_the_queue_dedupe()
    {
        var queue = new SectorWarmupQueue();
        // A hull and a station can carry the same stem (different kinds are
        // different caches, so both must survive).
        Assert.Equal(1, queue.EnqueueAll(SectorWarmupPlanner.Ships(1052, [("ss24a", null)])));
        Assert.Equal(1, queue.EnqueueAll(SectorWarmupPlanner.Stations(1052, [("ss24a", null)])));
        Assert.Equal(2, queue.PendingCount);

        // …but two sectors flying the same hull load it once.
        Assert.Equal(0, queue.EnqueueAll(SectorWarmupPlanner.Ships(1053, [("ss24a", null)])));
    }

    [Fact]
    public void A_whole_sector_seeds_once_and_a_re_seed_only_adds_new_hulls()
    {
        // Mirrors the client: seed at the approach, then re-seed every scan while
        // closing on the aleph (late arrivals must not be missed).
        var queue = new SectorWarmupQueue();
        var rocks = new List<(string?, string?)> { ("bgrnd05", ""), ("bgrnd03", "") };
        var hulls = new List<(string?, string?)> { ("fig05", ""), ("fig21", "") };

        int first = queue.EnqueueAll(SectorWarmupPlanner.Rocks(1052, rocks))
                  + queue.EnqueueAll(SectorWarmupPlanner.Ships(1052, hulls))
                  + (queue.Enqueue(SectorWarmupPlanner.Sky(1052, "nebplnt06bmp", "globe3")) ? 1 : 0);
        Assert.Equal(5, first);

        // Same world again: nothing new.
        int again = queue.EnqueueAll(SectorWarmupPlanner.Rocks(1052, rocks))
                  + queue.EnqueueAll(SectorWarmupPlanner.Ships(1052, hulls));
        Assert.Equal(0, again);

        // A hull that arrived after the first scan is the only new item.
        Assert.Equal(1, queue.EnqueueAll(SectorWarmupPlanner.Ships(1052, [("fig23", "")])));
        Assert.Equal(6, queue.PendingCount);
    }
}

/// <summary>
/// P862 — the autopilot hold rule: a frame that could not steer (no own pose yet,
/// no warp route yet) must not drop the order; only a target that is really gone
/// disengages. Pinned here because the failure it fixes was silent — a launch-time
/// goto logged "autopilot ON … autopilot OFF (TargetLost)" and the ship never
/// warped, so no harness could have measured a transit at all.
/// </summary>
public sealed class P862AutopilotHoldTests
{
    private static bool Disengage(bool engaged, bool steered, bool targetKnown,
        double nowMs = 1000, double holdSinceMs = 900)
        => Allegiance.ClientCore.MonoGame.Flight.AutopilotHold
            .ShouldDisengage(engaged, steered, targetKnown, nowMs, holdSinceMs);

    [Fact]
    public void A_transient_frame_with_a_live_target_keeps_the_order()
        => Assert.False(Disengage(engaged: true, steered: false, targetKnown: true));

    [Fact]
    public void A_vanished_target_disengages()
        => Assert.True(Disengage(engaged: true, steered: false, targetKnown: false));

    [Fact]
    public void A_steered_frame_never_disengages()
        => Assert.False(Disengage(engaged: true, steered: true, targetKnown: false));

    [Fact]
    public void An_idle_autopilot_is_left_alone()
        => Assert.False(Disengage(engaged: false, steered: false, targetKnown: false));

    [Fact]
    public void An_order_that_stays_unsteerable_past_the_limit_disengages()
    {
        const double limit = Allegiance.ClientCore.MonoGame.Flight.AutopilotHold.HoldLimitMs;
        const double holdStart = 5000;
        // Just inside the limit: still holding.
        Assert.False(Disengage(true, steered: false, targetKnown: true, nowMs: holdStart + limit - 1, holdSinceMs: holdStart));
        // At the limit: an order that has not steered for two seconds is lost.
        Assert.True(Disengage(true, steered: false, targetKnown: true, nowMs: holdStart + limit, holdSinceMs: holdStart));
    }
}
