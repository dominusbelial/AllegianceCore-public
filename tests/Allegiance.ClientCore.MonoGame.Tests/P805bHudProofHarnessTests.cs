using System;
using System.Linq;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P805b — the real-window proof path for P805's target-pane preview and the
/// per-style selection box. Two things are pinned here, both headless:
/// <list type="bullet">
/// <item><b>The preview draw path is total.</b> P805 added
/// <see cref="TargetPreviewKind.Probe"/> to the <c>SetTargetGeo</c> gate
/// (WinTrek.cpp:8783-8818) and its art resolve, but the draw switch read
/// <c>art.Treasure!.Value</c> for probes while the resolve never set it — the
/// first real probe target threw. <see cref="TargetModelPreview.DrawPathFor"/>
/// now picks the path from the kind AND the art that resolved, so a resolve gap
/// is native's empty viewport.</item>
/// <item><b>The proof-harness grammar.</b> <c>--auto-target-seq</c> selects a
/// sequence of kinds without engaging the autopilot (the target pane needs
/// same-cluster, not proximity, and an approach toward an aleph transits it),
/// <c>--auto-drop-probe-at</c> fires the real dispenser (the N key, TK_DropMine,
/// trekinput.cpp:1424) and <c>--auto-summon-enemy-at</c> relocates an
/// MCP-harness enemy into view; <c>--auto-stop-after</c> is the Backspace
/// throttle-zero preset (WinTrek.cpp:10939-10960).</item>
/// </list>
/// </summary>
public class P805bHudProofHarnessTests
{
    // ── preview draw path (the P805 probe bug) ──────────────────────────────

    [Fact]
    public void Probe_preview_draws_its_mesh_through_the_treasure_model_path()
    {
        // probetypeigc.cpp:28-33 — a probe's ThingGeo is a REAL mesh.
        Assert.Equal(TargetModelPreview.PreviewDrawPath.TreasureModel,
            TargetModelPreview.DrawPathFor(TargetPreviewKind.Probe,
                hasShipGeo: true, hasTreasureVisual: true, treasureIsDecal: false, hasWarpTexture: false));
    }

    [Fact]
    public void Probe_preview_without_its_visual_draws_nothing_instead_of_throwing()
    {
        // The P805 shape: a probe resolved a mesh but no TreasureVisual, and the
        // draw switch still dereferenced it. The path must be None (native's
        // empty viewport) — never a path whose art is absent.
        Assert.Equal(TargetModelPreview.PreviewDrawPath.None,
            TargetModelPreview.DrawPathFor(TargetPreviewKind.Probe,
                hasShipGeo: true, hasTreasureVisual: false, treasureIsDecal: false, hasWarpTexture: false));
    }

    [Fact]
    public void Warp_preview_is_the_load_aleph_vortex_not_a_flat_quad()
    {
        // warpIGC.cpp:33-34; modelIGC.cpp:127-132; effect/alephgeo.cpp:80-102 —
        // LoadAleph builds the additive textured FUNNEL, so the pane must draw the
        // vortex mesh. P805 drew the aleph texture as a flat camera-facing quad
        // (wrong art); this pins the corrected path.
        Assert.Equal(TargetModelPreview.PreviewDrawPath.WarpFunnel,
            TargetModelPreview.DrawPathFor(TargetPreviewKind.Warp,
                hasShipGeo: false, hasTreasureVisual: false, treasureIsDecal: false, hasWarpTexture: true));
        Assert.Equal(TargetModelPreview.PreviewDrawPath.None,
            TargetModelPreview.DrawPathFor(TargetPreviewKind.Warp,
                hasShipGeo: false, hasTreasureVisual: false, treasureIsDecal: false, hasWarpTexture: false));
    }

    [Fact]
    public void Treasure_preview_is_a_decal_for_a_flag_and_a_model_for_the_rest()
    {
        // treasureIGC.cpp:118-171 — flag → biosflag decal, part/cash/powerup/
        // artifact → the buyable's model.
        Assert.Equal(TargetModelPreview.PreviewDrawPath.Decal,
            TargetModelPreview.DrawPathFor(TargetPreviewKind.Treasure,
                hasShipGeo: false, hasTreasureVisual: true, treasureIsDecal: true, hasWarpTexture: false));
        Assert.Equal(TargetModelPreview.PreviewDrawPath.TreasureModel,
            TargetModelPreview.DrawPathFor(TargetPreviewKind.Treasure,
                hasShipGeo: false, hasTreasureVisual: true, treasureIsDecal: false, hasWarpTexture: false));
        Assert.Equal(TargetModelPreview.PreviewDrawPath.None,
            TargetModelPreview.DrawPathFor(TargetPreviewKind.Treasure,
                hasShipGeo: false, hasTreasureVisual: false, treasureIsDecal: false, hasWarpTexture: false));
    }

    [Fact]
    public void Mesh_kinds_require_their_geometry()
    {
        Assert.Equal(TargetModelPreview.PreviewDrawPath.Ship,
            TargetModelPreview.DrawPathFor(TargetPreviewKind.Ship, true, false, false, false));
        Assert.Equal(TargetModelPreview.PreviewDrawPath.Station,
            TargetModelPreview.DrawPathFor(TargetPreviewKind.Station, true, false, false, false));
        Assert.Equal(TargetModelPreview.PreviewDrawPath.Asteroid,
            TargetModelPreview.DrawPathFor(TargetPreviewKind.Asteroid, true, false, false, false));
        Assert.Equal(TargetModelPreview.PreviewDrawPath.None,
            TargetModelPreview.DrawPathFor(TargetPreviewKind.Ship, false, false, false, false));
    }

    // ── candidate pool: the proof's probe/treasure selection ────────────────
    [Fact]
    public void GatherSectorCandidates_offers_probes_and_treasures_in_my_sector_only()
    {
        const short me = 8100;
        const short sector = 1054;
        var w = new ClientWorld();
        w.Apply(new ExportProbe
        {
            Probe = new DataProbeExport(new SimVec(10f, 0f, 0f), 0u, 501, true, 22, 0, sector, -1, 0, -1, true),
        });
        w.Apply(new ExportProbe
        {
            Probe = new DataProbeExport(new SimVec(20f, 0f, 0f), 0u, 502, true, 22, 0, (short)(sector + 1), -1, 0, -1, true),
        });
        w.Apply(new TreasureSpawn
        {
            Data = new DataTreasureIGC([30f, 0f, 0f], [0f, 0f, 0f], 300f, 0u, 601,
                (short)TreasureCode.Cash, sector, 500, TreasureCode.Cash, true),
        });

        var objs = Game1.GatherSectorCandidates(
            w.Ships, w.Warps, w.Stations, w.Asteroids, me, sector,
            probes: w.Probes, treasures: w.Treasures);

        var probeIds = objs.Where(o => o.Kind == Game1.TargetKind.Probe).Select(o => o.Id).ToList();
        Assert.Equal(new short[] { 501 }, probeIds);       // 502 is another sector
        var treasureIds = objs.Where(o => o.Kind == Game1.TargetKind.Treasure).Select(o => o.Id).ToList();
        Assert.Equal(new short[] { 601 }, treasureIds);

        // P805b — the resolved art must key on the treasure's PAYLOAD
        // (DataTreasureIGC.TreasureId), not on its object id: native resolves a
        // treasure's visual from the payload (treasureIGC.cpp:118-171), and P805
        // passed the object id, so every PART treasure previewed nothing.
        var a = new TargetPreviewKey(TargetPreviewKind.Treasure, 601, (int)TreasureCode.Part, "Part", null, payloadId: 27);
        Assert.True(a.Matches(new TargetPreviewKey(TargetPreviewKind.Treasure, 601, (int)TreasureCode.Part, "Part", null, payloadId: 27)));
        Assert.False(a.Matches(new TargetPreviewKey(TargetPreviewKind.Treasure, 601, (int)TreasureCode.Part, "Part", null, payloadId: 28)));

        // The default (no probe/treasure sources) pool stays byte-identical for
        // every pre-P805b caller.
        var legacy = Game1.GatherSectorCandidates(w.Ships, w.Warps, w.Stations, w.Asteroids, me, sector);
        Assert.DoesNotContain(legacy, o => o.Kind is Game1.TargetKind.Probe or Game1.TargetKind.Treasure);
    }

    // ── proof-harness grammar ───────────────────────────────────────────────

    [Fact]
    public void Parse_target_sequence_is_time_ordered_and_rejects_bad_entries()
    {
        var seq = AutoProofPlan.ParseTargetSequence("treasure@22, aleph@4,bogus@9,probe,enemy@x,probe@14");

        Assert.Equal(new[]
        {
            new AutoProofPlan.TimedTarget(AutoTargetKind.Aleph, 4),
            new AutoProofPlan.TimedTarget(AutoTargetKind.Probe, 14),
            new AutoProofPlan.TimedTarget(AutoTargetKind.Treasure, 22),
        }, seq);
    }

    [Fact]
    public void Parse_new_proof_flags()
    {
        var plan = AutoProofPlan.Parse(new[]
        {
            "--auto-target-seq", "aleph@4,probe@14,treasure@22,enemy@32",
            "--auto-drop-probe-at", "10",
            "--auto-summon-enemy-at", "28",
            "--auto-summon-enemy-dist", "650",
            "--auto-stop-after", "2",
            "--auto-target", "treasure",
        }, out var rest);

        Assert.Equal(4, plan.TargetSequence.Length);
        Assert.Equal(10.0, plan.AutoDropProbeAt);
        Assert.Equal(28.0, plan.AutoSummonEnemyAt);
        Assert.Equal(650f, plan.AutoSummonEnemyDistance);
        Assert.Equal(2.0, plan.StopAfter);
        Assert.Equal(AutoTargetKind.Treasure, plan.Target);
        Assert.True(plan.AnyActive);
        Assert.Empty(rest);

        // Equals forms, and the defaults for an inert plan.
        var eq = AutoProofPlan.Parse(new[]
        {
            "--auto-target-seq=probe@3", "--auto-drop-probe-at=1.5",
            "--auto-summon-enemy-at=2.5", "--auto-summon-enemy-dist=700",
            "--auto-stop-after=0.5",
        }, out _);
        Assert.Equal(new[] { new AutoProofPlan.TimedTarget(AutoTargetKind.Probe, 3) }, eq.TargetSequence);
        Assert.Equal(1.5, eq.AutoDropProbeAt);
        Assert.Equal(2.5, eq.AutoSummonEnemyAt);
        Assert.Equal(700f, eq.AutoSummonEnemyDistance);
        Assert.Equal(0.5, eq.StopAfter);

        var inert = new AutoProofPlan();
        Assert.Empty(inert.TargetSequence);
        Assert.Null(inert.AutoDropProbeAt);
        Assert.Null(inert.AutoSummonEnemyAt);
        Assert.Null(inert.StopAfter);
        Assert.Equal(500f, inert.AutoSummonEnemyDistance);
        Assert.False(inert.AnyActive);
    }

    [Fact]
    public void Machine_emits_the_scripted_sequence_drop_summon_and_stop_once()
    {
        var plan = AutoProofPlan.Parse(new[]
        {
            "--auto-side", "0", "--auto-ready", "--auto-launch", "Scout",
            "--auto-target-seq", "aleph@4,probe@14",
            "--auto-drop-probe-at", "10",
            "--auto-summon-enemy-at", "20",
            "--auto-summon-enemy-dist", "650",
            "--auto-stop-after", "2",
            "--exit-after", "30",
        }, out _);
        var machine = new AutoProofMachine(plan);
        var kinds = new System.Collections.Generic.List<(AutoActionKind Kind, int IntArg)>();

        machine.Tick(0);
        machine.NoteSeated();
        machine.NoteDocked();
        foreach (var a in machine.ConsumeDue()) kinds.Add((a.Kind, a.IntArg));

        // Launch at seated+docked, then walk the after-launch clock.
        machine.Tick(1);
        foreach (var a in machine.ConsumeDue()) kinds.Add((a.Kind, a.IntArg));
        machine.NoteUndocked();
        machine.NoteLaunch();
        for (int t = 2; t <= 31; t++)
        {
            machine.Tick(t);
            foreach (var a in machine.ConsumeDue()) kinds.Add((a.Kind, a.IntArg));
        }

        Assert.Equal(1, kinds.Count(k => k.Kind == AutoActionKind.Stop));
        Assert.Equal(1, kinds.Count(k => k.Kind == AutoActionKind.DropProbe));
        Assert.Equal(1, kinds.Count(k => k.Kind == AutoActionKind.SummonEnemy));
        Assert.Equal(650, kinds.Single(k => k.Kind == AutoActionKind.SummonEnemy).IntArg);
        Assert.Equal(2, kinds.Count(k => k.Kind == AutoActionKind.SelectTargetSeq));
        Assert.Equal(new[]
        {
            (int)AutoTargetKind.Aleph,
            (int)AutoTargetKind.Probe,
        }, kinds.Where(k => k.Kind == AutoActionKind.SelectTargetSeq).Select(k => k.IntArg).ToArray());
        // The single --auto-target path stays untouched (no Target action here).
        Assert.DoesNotContain(kinds, k => k.Kind == AutoActionKind.SelectTarget);
        Assert.Equal(1, kinds.Count(k => k.Kind == AutoActionKind.Exit));
    }
}
