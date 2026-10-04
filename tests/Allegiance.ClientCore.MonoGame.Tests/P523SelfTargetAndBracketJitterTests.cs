using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P523 — two live-playtest bugs (2026-10-03):
/// <list type="bullet">
/// <item><b>Bug 1</b> — the <c>Y</c> key was bound BOTH to "toggle ready"
/// (P282, ready-check screen) AND to "target self" (P417, TK_TargetSelf in
/// flight). Pressing Y to ready up left the press live as the mission started,
/// so flight selected the player's own ship ("[monogame] target: self
/// (domingo1 #8107)"). Native's <c>Artwork/defaultinputmap.mdl</c> actually
/// leaves <c>TK_TargetSelf (100)</c> UNBOUND by default — so this port's Y
/// binding was a deviation that caused the leak. The fix removes the Y→target-
/// self binding (matching native) and edge-gates the pre-flight→flight
/// transition so no key held across it can fire an in-flight edge action.</item>
/// <item><b>Bug 2</b> — the target bracket/label and the lock bracket were
/// anchored to each ship's RAW 20 Hz <c>ServerShipUpdate</c> pose while the
/// hull is drawn from the per-frame render pose (own-ship local sim, P508;
/// P448b dead-reckoned pose for remotes), so they jittered around the smooth
/// hull. The fix anchors every ship HUD element to the same render pose via
/// <see cref="Game1.ResolveShipAnchor"/>.</item>
/// </list>
/// All pure-logic tests: no graphics device (same discipline as
/// <see cref="HelpOverlayTests"/>). Failing-first: the KeyBindingTable test
/// failed against the pre-fix table (which still had a Y→TargetSelf entry) and
/// the anchor tests encode the render-pose precedence the old raw-pose path
/// violated.
/// </summary>
public class P523SelfTargetAndBracketJitterTests
{
    private const short Sector = 1050;

    // ── Bug 1 · no self-target on Y; ; stays TargetNothing ────────────────

    /// <summary>⭐ Failing-first (Bug 1): native leaves TK_TargetSelf unbound by
    /// default (Artwork/defaultinputmap.mdl has no TK 100 triple), so the port
    /// must NOT bind Y to target-self — Y is the P282 ready key, and the shared
    /// key leaked the ready press into flight selecting your own ship. The
    /// help/keybinding reference must reflect that: no "Y" entry in the
    /// TARGETING group, while ";" → TargetNothing (and ' → TargetCenter)
    /// survive.</summary>
    [Fact]
    public void KeyBindingTable_DoesNotBindY_ToTargetSelf_KeepsSemicolonTargetNothing()
    {
        var targeting = KeyBindingTable.All.First(g => g.Title.StartsWith("TARGETING"));

        // Y must not be a targeting key at all (previously "Target self").
        Assert.DoesNotContain(targeting.Entries, e => e.Key == "Y");

        // ; stays TargetNothing.
        var semi = targeting.Entries.First(e => e.Key == ";");
        Assert.Contains("Target nothing", semi.Description);

        // ' stays TargetCenter.
        Assert.Contains(targeting.Entries,
            e => e.Key == "'" && e.Description.Contains("Target center"));
    }

    /// <summary>⭐ Edge-gate (Bug 1): the pre-flight screens share Enter and R
    /// with in-flight EDGE actions (Enter opens chat; R opens quick-chat). A key
    /// held when the mission starts must be primed (marked already-pressed) so
    /// its in-flight edge can't fire on the first flight frame.</summary>
    [Fact]
    public void SharedPreFlightKeys_AreEdgePrimed_WhenHeldAcrossTransition()
    {
        var held = new KeyboardState(Keys.Enter, Keys.R);
        var (enter, r) = Game1.ComputeSharedEdgePrimes(held);
        Assert.True(enter, "Enter held at transition must be primed (no chat-open edge on first flight frame)");
        Assert.True(r, "R held at transition must be primed (no quick-chat edge on first flight frame)");

        var empty = new KeyboardState();
        var (e2, r2) = Game1.ComputeSharedEdgePrimes(empty);
        Assert.False(e2);
        Assert.False(r2);
    }

    // ── Bug 2 · ship HUD anchors use the render pose ──────────────────────

    private static ServerShipUpdate PoseAt(short id, float x, float y, float z) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 1f), time: 1u,
            new SimVec(x, y, z), Orientation.Identity, SimVec.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f);

    private static ClientWorld WorldWithOwnAndRemote(short me, short rem)
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster { SectorId = Sector, Ship = PoseAt(me, 720f, 730f, 920f) });
        w.Apply(new SingleShipUpdate { OidTarget = rem, Ship = PoseAt(rem, 100f, 0f, 0f) });
        w.Apply(new ShipStatusMessage { ShipId = rem, SectorId = Sector });
        return w;
    }

    /// <summary>⭐ Bracket anchor (Bug 2): the own-ship bracket must anchor to the
    /// LOCAL-SIM render pose (P508), and a remote ship's bracket to its P448b
    /// dead-reckoned render pose — NOT the raw 20 Hz ServerShipUpdate pose, which
    /// is what made the label jitter around the smooth hull.</summary>
    [Fact]
    public void ResolveShipAnchor_UsesRenderPose_ForSelfAndRemote()
    {
        const short me = 1, rem = 2;
        var w = WorldWithOwnAndRemote(me, rem);

        // Render poses differ from raw: own = local-sim (10,0,0) vs raw (720,730,920);
        // remote = dead-reckoned (150,0,0) vs raw (100,0,0).
        var render = new Dictionary<short, Flight.PredictedPose>
        {
            [me] = new(new SimVec(10f, 0f, 0f), Orientation.Identity),
            [rem] = new(new SimVec(150f, 0f, 0f), Orientation.Identity),
        };

        Assert.True(Game1.ResolveShipAnchor(render, w.Ships, me, out var selfPos));
        Assert.Equal(10f, selfPos.X);   // local-sim pose, not the raw 720

        Assert.True(Game1.ResolveShipAnchor(render, w.Ships, rem, out var remPos));
        Assert.Equal(150f, remPos.X);   // dead-reckoned pose, not the raw 100
    }

    /// <summary>Bracket anchor fallback (Bug 2): a ship that wasn't rendered this
    /// frame (not in the render-pose table) falls back to its raw last-known pose —
    /// there is no smoother pose to anchor to, and no drawn hull to glue to.</summary>
    [Fact]
    public void ResolveShipAnchor_FallsBackToRawPose_WhenNotRendered()
    {
        const short me = 1, rem = 2;
        var w = WorldWithOwnAndRemote(me, rem);
        var empty = new Dictionary<short, Flight.PredictedPose>();

        Assert.True(Game1.ResolveShipAnchor(empty, w.Ships, rem, out var rawPos));
        Assert.Equal(100f, rawPos.X);   // fell back to the raw ServerShipUpdate pose
    }

    /// <summary>⭐ Jitter metric (Bug 2): in a scripted run the frame-to-frame
    /// anchor delta is far smaller when the anchor follows the smoothly-advanced
    /// render pose than when it follows the coarsely-stepping raw 20 Hz pose.
    /// Metric = sum of squared frame-to-frame anchor deltas (penalises the big
    /// 50-unit steps the raw pose makes every 5th frame).</summary>
    [Fact]
    public void RenderPoseAnchor_JitterDropsVersusRawPoseAnchor_InScriptedRun()
    {
        const short me = 1, id = 2;
        var w = WorldWithOwnAndRemote(me, id);

        const int frames = 100;
        const float step = 10f;            // render pose advances 10 u/frame
        var renderDeltaSq = new List<float>();
        var rawDeltaSq = new List<float>();
        SimVec? prevRender = null, prevRaw = null;

        for (int f = 0; f < frames; f++)
        {
            // 20 Hz raw sampling: the server pose updates every 5th render frame,
            // jumping by 50 u (5 × 10) each time; the render pose is smooth.
            if (f % 5 == 0)
            {
                float rawX = (f / 5) * 50f;
                w.Apply(new SingleShipUpdate { OidTarget = id, Ship = PoseAt(id, rawX, 0f, 0f) });
            }
            var render = new Dictionary<short, Flight.PredictedPose>
            {
                [id] = new(new SimVec(f * step, 0f, 0f), Orientation.Identity),
            };

            Game1.ResolveShipAnchor(render, w.Ships, id, out var anchor);        // render pose
            Game1.ResolveShipAnchor(new Dictionary<short, Flight.PredictedPose>(), w.Ships, id, out var rawAnchor); // raw pose

            if (prevRender is { } pr) renderDeltaSq.Add((anchor - pr).LengthSquared());
            if (prevRaw is { } pra) rawDeltaSq.Add((rawAnchor - pra).LengthSquared());
            prevRender = anchor;
            prevRaw = rawAnchor;
        }

        float renderJitter = renderDeltaSq.Sum();
        float rawJitter = rawDeltaSq.Sum();

        // Render-pose anchor is essentially smooth (10² per frame ≈ 9900 total);
        // the raw anchor spikes 50² on every 5th frame (≈ 50000 total).
        Assert.True(renderJitter < rawJitter,
            $"expected render-pose jitter to drop below raw-pose jitter: render={renderJitter:F0} raw={rawJitter:F0}");
        Assert.True(rawJitter > renderJitter * 3,
            $"expected a clear margin: raw({rawJitter:F0}) should be >> render({renderJitter:F0})");
    }
}
