using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Capture;
using SimVec = Allegiance.Core.Collision.Vector3;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P417 — unit tests for the small leftover key-bindings batch:
/// <list type="bullet">
/// <item><b>TargetCenter</b> — the one with a real ranking requirement: select the
/// candidate closest to the RETICLE/crosshair, not closest in raw distance. This
/// port reuses the existing <see cref="Game1.SelectCandidates"/> nearest-to-
/// crosshair ranking (the same screen-space DistSq ordering CollectTargetCandidates
/// applies) — the native c_ttFront nearest-target analogue (WinTrek.cpp:10127-10151).
/// These tests prove that ranking picks the on-reticle candidate even when a
/// different candidate is physically closer, i.e. the "closest to view center"
/// semantic the brief demands.</item>
/// <item><b>ScreenShot</b> — the standalone QoL capture: a timestamped PNG written
/// to a sensible <c>screenshots/</c> path via the shared RGBA→PNG encoder. Tests
/// pin the path format (sorted, collision-resistant, under the working dir) and
/// that <see cref="CapturePngSaver.Encode"/> yields a structurally-valid PNG of the
/// right dimensions.</item>
/// </list>
/// </summary>
public class MiscKeyBindingTests
{
    // ── TargetCenter: nearest to the reticle, not nearest in raw distance ──

    private static Game1.CandidateObject CamObj(short id, SimVec pos) =>
        new(pos, Game1.TargetKind.Ship, id, $"ship{id}");

    /// <summary>⭐ TargetCenter's core: a candidate ON the reticle is selected even
    /// though a different candidate is physically CLOSER in raw distance but sits
    /// off to one side of the crosshair. This is the exact semantic the brief pins
    /// ("closest to the reticle/crosshair, not closest in raw distance") and the
    /// discriminator that makes TargetCenter ≠ the raw-distance family.</summary>
    [Fact]
    public void Center_picks_on_reticle_candidate_over_a_physically_closer_off_reticle_one()
    {
        // Two ships, both projected inside the frustum:
        //   ship 1 is physically NEARER (pos distance 100) but off-reticle (sx=0.8)
        //   ship 2 is physically FARTHER (pos distance 900) but dead-on the reticle (sx=0)
        var objs = new List<Game1.CandidateObject>
        {
            CamObj(1, new SimVec(100, 0, 0)),
            CamObj(2, new SimVec(900, 0, 0)),
        };

        // Projector maps world pos → screen offset; we derive the offset from the
        // raw X distance exactly as the "nearer but off-reticle" story describes:
        // anything within 500 units is off-reticle (0.8), beyond is on-reticle (0).
        var ordered = Game1.SelectCandidates(objs, pos =>
        {
            float sx = pos.X < 500 ? 0.8f : 0f;
            return (sx, 0f);
        }, out _, out _, out _, out _, out _);

        // Ordering is nearest-to-crosshair (screen DistSq): ship 2 (sx=0) then ship 1 (0.64).
        Assert.Equal(new short[] { 2, 1 }, ordered.Select(x => x.Id).ToArray());
    }

    /// <summary>⭐ An object physically nearest but OUTSIDE the frustum is never
    /// offered, while the on-reticle in-view object is — the reticle metric is
    /// screen-relative, so raw world distance is irrelevant to TargetCenter.</summary>
    [Fact]
    public void Center_ignores_out_of_frustum_candidates_regardless_of_raw_distance()
    {
        var objs = new List<Game1.CandidateObject>
        {
            CamObj(1, new SimVec(1, 0, 0)),      // very close, but projector says out of view
            CamObj(2, new SimVec(900, 0, 0)),    // on the reticle
        };

        var ordered = Game1.SelectCandidates(objs, pos => pos.X < 500 ? null : (0f, 0f),
            out _, out _, out _, out _, out _);

        Assert.Equal(new short[] { 2 }, ordered.Select(x => x.Id).ToArray());
    }

    /// <summary>⭐ Priority tiers still apply under TargetCenter: with a ship and a
    /// dense asteroid field both in view, the ship tier wins (the P256 tier rule),
    /// so TargetCenter still surfaces the meaningful object — not a rock that
    /// happens to sit dead-centre.</summary>
    [Fact]
    public void Center_respects_priority_tiers_ship_beats_asteroid()
    {
        var ship = new Game1.CandidateObject(
            new SimVec(100, 0, 0), Game1.TargetKind.Ship, 1, "ship1");
        var rock = new Game1.CandidateObject(
            new SimVec(0, 0, 0), Game1.TargetKind.Asteroid, 7, "rock7"); // dead centre

        var ordered = Game1.SelectCandidates(new[] { ship, rock }, _ => (0f, 0f),
            out _, out _, out _, out _, out _);

        // Ships rank above asteroids, so even a dead-centre rock is suppressed.
        Assert.Equal(new short[] { 1 }, ordered.Select(x => x.Id).ToArray());
    }

    // ── ScreenShot ─────────────────────────────────────────────────────────

    /// <summary>⭐ The screenshot path lives under a <c>screenshots/</c> subdir of
    /// the working dir and is timestamped with enough resolution that rapid shots
    /// don't collide (native names shots by local time too, WinTrek.cpp:4079-4092 —
    /// this port's millisecond stamp is the collision-resistant version).</summary>
    [Fact]
    public void ScreenShot_path_is_timestamped_under_the_screenshots_dir()
    {
        var t = new DateTime(2026, 9, 27, 14, 5, 3, 042);
        string path = ScreenShot.BuildPath(t, workingDir: "/tmp/p417base");

        Assert.Equal("/tmp/p417base/screenshots/shot_2026-09-27_14-05-03_042.png", path);
    }

    /// <summary>⭐ The encoder actually produces a valid PNG of the requested
    /// dimensions (the same encoder the F11 screenshot uses).</summary>
    [Fact]
    public void ScreenShot_encoder_produces_a_valid_png_of_requested_size()
    {
        var px = new Microsoft.Xna.Framework.Color[2 * 2];
        px[0] = new Microsoft.Xna.Framework.Color(255, 0, 0, 255);
        px[1] = new Microsoft.Xna.Framework.Color(0, 255, 0, 255);
        px[2] = new Microsoft.Xna.Framework.Color(0, 0, 255, 255);
        px[3] = new Microsoft.Xna.Framework.Color(255, 255, 255, 255);

        byte[] png = CapturePngSaver.Encode(px, 2, 2);

        // PNG signature.
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                     png.Take(8).ToArray());
        // IHDR: width (big-endian) at offset 16, height at offset 20.
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        Assert.Equal(2, w);
        Assert.Equal(2, h);
        // Ends with IEND chunk.
        Assert.Equal((byte)'I', png[png.Length - 8]);
        Assert.Equal((byte)'E', png[png.Length - 7]);
        Assert.Equal((byte)'N', png[png.Length - 6]);
        Assert.Equal((byte)'D', png[png.Length - 5]);
    }

    /// <summary>⭐ Two different frames encode to different byte streams — the
    /// screenshot is a real capture of the frame, not a fixed placeholder.</summary>
    [Fact]
    public void ScreenShot_encoder_distinguishes_two_frames()
    {
        var a = Enumerable.Range(0, 4).Select(i => new Microsoft.Xna.Framework.Color(10, 10, 10, 255)).ToArray();
        var b = Enumerable.Range(0, 4).Select(i => new Microsoft.Xna.Framework.Color(200, 200, 200, 255)).ToArray();
        byte[] pa = CapturePngSaver.Encode(a, 2, 2);
        byte[] pb = CapturePngSaver.Encode(b, 2, 2);
        Assert.False(pa.SequenceEqual(pb));
    }
}
