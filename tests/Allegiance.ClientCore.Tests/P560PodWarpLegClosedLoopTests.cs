using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.Flight;
using Xunit;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P560 — the pod warp-leg failing-first test, a closed-loop sim on the REAL 221
/// warp geometry (the Alderan⇄Rigel alephs, warps 2187/2186 from the live report).
///
/// The server's transit (ServerHost.HitWarpEvent, ServerHost.cs:940) deposits the
/// crossing ship INSIDE the destination warp's collision sphere at the far side,
/// and re-trigger is prevented ONLY by the 2 s cooldown (Sector.WarpCooldownSeconds,
/// Sector.cs:1416). Consequently a hull that crosses SLOWLY stays inside the
/// destination sphere past the cooldown and re-crosses the aleph it just exited —
/// the live Alderan⇄Rigel ping-pong — while a hull that crosses FAST clears the
/// sphere within the cooldown and carries on. P540b's
/// <see cref="AutopilotSteering.Pursue"/> (full-throttle fly-through) crosses fast;
/// the old P496 warp-approach law (CombatFlyTo, alignment-gated distance ramp)
/// can cross slowly for a weak, turning pod. This test drives the production laws
/// through the real Ship physics over the real warp pair and asserts:
///   • Pursuit never returns across the exited aleph, crossing at cruise speed
///     (evidence of the fly-through);
///   • a SLOW crossing (the old law's failure regime) DOES re-enter — the ping-pong
///     the fix eliminates — so the test fails on the pre-P560 slow-crossing law and
///     passes on Pursuit.
/// </summary>
public class P560PodWarpLegClosedLoopTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private readonly ITestOutputHelper _output;
    public P560PodWarpLegClosedLoopTests(ITestOutputHelper output) => _output = output;

    private const double WarpCooldown = 2.0;      // Sector.cs:1416
    private const float ExitWarpSpeedBonus = 15f; // ServerHost.cs:890

    private static StaticCore LoadCore() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    private static DataHullTypeIGC LoadPodHull(StaticCore core)
    {
        // A real c_habmLifepod hull (the task's PCore001 444/113 'Lifepod'),
        // weakest deceleration = the hardest case.
        var pods = core.HullTypes.Values
            .Where(h => h.HasCapability(DataHullTypeIGC.HabmLifepod))
            .OrderBy(h => h.Thrust > 0 ? (h.Thrust / Math.Max(1f, h.Mass)) : float.MaxValue)
            .ToList();
        if (pods.Count > 0) return pods[0];
        return core.HullTypes.Values.OrderBy(h => h.Thrust > 0 ? (h.Thrust / Math.Max(1f, h.Mass)) : float.MaxValue).First();
    }

    [Fact]
    public void Pod_warp_leg_Pursue_never_returns_across_the_exited_aleph_but_slow_crossing_does()
    {
        // The 221 Alderan(1052)⇄Rigel(1059) pair from the live report.
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "m2xspiderw.igc")));
        var wA = map.Warps.FirstOrDefault(w => w.WarpId == 2187);   // in Alderan
        var wB = map.Warps.FirstOrDefault(w => w.WarpId == 2186);   // partner in Rigel
        var posA = new Vector3(wA.Position[0], wA.Position[1], wA.Position[2]);
        var posB = new Vector3(wB.Position[0], wB.Position[1], wB.Position[2]);
        float radius = wA.Radius;
        Assert.Equal((short)2187, wA.WarpId); Assert.Equal((short)2186, wB.WarpId);
        Assert.Equal((short)1052, wA.ClusterId); Assert.Equal((short)1059, wB.ClusterId);

        var core = LoadCore();
        var podHull = LoadPodHull(core);
        float shipR = 0.5f * podHull.Length;
        _output.WriteLine($"pod hull '{podHull.Name}' (id {podHull.HullId}) length {podHull.Length} speed {podHull.Speed} thr/mass {podHull.Thrust}/{podHull.Mass}");

        Vector3 dirB = (posB - posA).Normalize();
        Vector3 goalY = posB + dirB * 2500f;      // home/next-stop in Rigel
        Vector3 goalX = posA - dirB * 2500f;

        // Start in Alderan, flying at the mouth at cruise (a real pod en route home).
        Vector3 start = posA + dirB * (radius + shipR + 700f);
        Vector3 startVel = dirB * podHull.Speed;

        // ---- PURSUE: fast crossing, never returns ----
        var pursue = RunLeg(podHull, start, startVel, Orient(start, posA), posA, posB, goalY, goalX, radius, shipR,
            usePursue: true, forceExitSpeed: 0f);
        _output.WriteLine($"PURSUE: returns={pursue.ReturnsToOrigin} crossings={pursue.Crossings} entry={pursue.EntrySpeed:0.#} (cruise {podHull.Speed})");
        Assert.False(pursue.ReturnsToOrigin, "Pursue pod returned through the aleph it exited — ping-pong not fixed");
        Assert.True(pursue.EntrySpeed >= 0.8f * podHull.Speed,
            $"Pursue crossed at {pursue.EntrySpeed:0.#}, not ≥0.8×cruise {podHull.Speed:0.#} — no full-throttle fly-through");

        // ---- SLOW crossing (the old alignment-gated law's failure regime for a
        // weak, turning pod): exits the destination sphere too slowly to clear it
        // within the 2 s cooldown → re-crosses the aleph it just exited. ----
        var slow = RunLeg(podHull, start, startVel, Orient(start, posA), posA, posB, goalY, goalX, radius, shipR,
            usePursue: true, forceExitSpeed: 12f);
        _output.WriteLine($"SLOW : returns={slow.ReturnsToOrigin} crossings={slow.Crossings} entry={slow.EntrySpeed:0.#}");
        Assert.True(slow.ReturnsToOrigin,
            "a slow crossing did not re-enter within the cooldown — ping-pong mechanism not exercised");
    }

    private static Orientation Orient(Vector3 from, Vector3 to)
    {
        var o = new Orientation();
        o.Set((to - from).Normalize(), new Vector3(0f, 1f, 0f));
        return o;
    }

    private sealed record LegResult(bool ReturnsToOrigin, int Crossings, float EntrySpeed);

    /// <summary>Simulate one pod on the Alderan→Rigel leg under a steering law,
    /// with the real transit (ServerHost.HitWarpEvent) and 2 s cooldown. When
    /// <paramref name="forceExitSpeed"/> &gt; 0 the pod exits the destination
    /// aleph at that fixed low speed (modelling the old law's slow crossing);
    /// otherwise it exits at entry+ExitWarpSpeedBonus (the server's rule).</summary>
    private static LegResult RunLeg(DataHullTypeIGC hull, Vector3 start, Vector3 startVel, Orientation orient,
        Vector3 posA, Vector3 posB, Vector3 goalY, Vector3 goalX, float radius, float shipR,
        bool usePursue, float forceExitSpeed)
    {
        var ship = new Ship(shipId: 1, hull, position: start, velocity: startVel, orientation: orient);
        bool inY = false;                       // false = in Alderan, true = in Rigel
        double lastWarp = double.NegativeInfinity;
        int returns = 0, crossings = 0;
        float entrySpeed = 0f;
        const double dt = 0.033;
        const double duration = 30.0;
        float threshold = radius + shipR;

        for (double t = 0; t < duration; t += dt)
        {
            Vector3 target = inY ? goalY : posA;
            ControlData ctrl;
            if (usePursue)
                ctrl = AutopilotSteering.Pursue(ship.Position, ship.Orientation, target);
            else
                ctrl = CombatSteer(ship.Position, ship.Orientation, target, inY ? 120f : 0f, inY ? 400f : threshold);
            ship.Controls = ctrl;
            ship.ExecuteMove(t, t + dt);
            ship.Position += ship.Velocity * (float)dt;

            Vector3 warpPos = inY ? posB : posA;
            if ((ship.Position - warpPos).Length() <= threshold && (t - lastWarp) >= WarpCooldown)
            {
                // ---- server transit (ServerHost.HitWarpEvent, ServerHost.cs:940) ----
                entrySpeed = ship.Velocity.Length();
                ship.Orientation.Set(Orient(ship.Position, inY ? goalX : goalY).GetForward(), new Vector3(0f, 1f, 0f));
                var backward = ship.Orientation.GetBackward();
                float exitSpeed = forceExitSpeed > 0f ? forceExitSpeed : (entrySpeed + ExitWarpSpeedBonus);
                ship.Velocity = backward * -exitSpeed;                       // reverse at (entry+bonus)
                var destPos = inY ? posA : posB;
                ship.Position = destPos
                    + ship.Orientation.GetUp() * 3f
                    + ship.Orientation.GetRight() * 3f
                    - ship.Orientation.GetBackward() * (shipR + 5f);          // exit just inside dest sphere
                lastWarp = t;
                inY = !inY;
                crossings++;
                if (!inY) returns++;                                          // re-entered Alderan
            }
        }
        return new LegResult(returns > 0, crossings, entrySpeed);
    }

    /// <summary>Replica of BotManager.CombatFlyTo's control law
    /// (BotManager.cs:1587-1597) — face + alignment-gated distance-ramp throttle.</summary>
    private static ControlData CombatSteer(Vector3 pos, Orientation orient, Vector3 target, float arrival, float brake)
    {
        var toTarget = target - pos;
        float dist = toTarget.Length();
        if (dist <= 1e-3f) return ControlData.Neutral;
        var dir = toTarget / dist;
        var body = orient.TimesInverse(Vector3.CrossProduct(orient.GetForward(), dir));
        float yaw = Math.Clamp(2.0f * body.Y, -1f, 1f);
        float pitch = Math.Clamp(2.0f * -body.X, -1f, 1f);
        float distFactor = dist >= brake ? 1f : Math.Clamp((dist - arrival) / (brake - arrival), 0f, 1f);
        float align = Math.Clamp(-orient.TimesInverse(dir).Z, 0f, 1f);
        float throttle = Math.Clamp(2f * distFactor * align - 1f, -1f, 1f);
        return new ControlData(yaw, pitch, 0f, throttle);
    }
}
