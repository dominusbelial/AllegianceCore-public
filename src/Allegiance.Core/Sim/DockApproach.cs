using System;
using System.Collections.Generic;
using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>
/// The shared, pure, unit-testable station/carrier <b>dock-approach planner</b> —
/// a faithful port of the native autopilot's bay line-up and entry logic that
/// both the player autopilot and every NPC/bot "go home to dock" path use
/// instead of flying straight at the station centre.
/// </summary>
/// <remarks>
/// <para><b>Source.</b> This is a 1:1 port of <c>Waypoint::GetGotoPosition</c>'s
/// <c>OT_station</c> / <c>OT_ship</c>(carrier) cases plus <c>Waypoint::DoApproach</c>,
/// <c>src/Igc/common.cpp:1157-1545</c>, together with the part of
/// <c>GotoPlan::SetControls</c> (<c>common.cpp:2030-2170</c>) that turns the
/// resulting mask into yaw/pitch/roll/throttle. The problem it solves is the one
/// Domingo reported live: "some of the ships are getting stuck on the station
/// outer ring". Today the port's autopilot flies the straight line at the
/// <b>centre</b> and relies on luck that the line happens to enter a bay; the
/// native autopilot instead picks the nearest landing bay, stages OUTSIDE it on
/// the approach line, faces the entry direction, then rolls and enters. The
/// server's dock test (<c>Station.InGarage</c>, P550) is already native and is
/// untouched here — the whole point is to bring the ship INTO that bay so the
/// existing test succeeds.</para>
/// <para><b>Mask grammar.</b> Every method returns a <see cref="Step"/>
/// carrying the native <see cref="GotoPositionMask"/> bits it chose
/// (<c>common.cpp:1140-1149</c>) plus the world-space <see cref="Step.Goal"/>
/// (where to fly) and <see cref="Step.Facing"/> (what direction to point the
/// nose); <see cref="Steer"/> turns that into a <see cref="ControlData"/> the
/// way native <c>SetControls</c> does. Keeping the mask exposed (rather than
/// only a collapsed enum) preserves the exact native state machine and lets
/// every consumer behave identically — the shared routine the task asks for.</para>
/// <para><b>Pure &amp; MonoGame-free.</b> Operates only on the repo's sim types
/// (<see cref="Vector3"/>, <see cref="Orientation"/>, <see cref="ControlData"/>),
/// so it is unit-testable without a graphics device — the same discipline as
/// <c>AutopilotSteering</c> and <c>FlightInput</c>.</para>
/// </remarks>
public static class DockApproach
{
    /// <summary>The native <c>GotoPositionMask</c> bits (<c>common.cpp:1140-1149</c>).</summary>
    public static class Mask
    {
        public const uint KillThrottle = 0x01;
        public const uint Finished     = 0x02;
        public const uint Pivot        = 0x04;
        public const uint Enter        = 0x08;
        public const uint NoDodge      = 0x10;
        public const uint Fast         = 0x40;
        public const uint DodgeShips   = 0x80;
        public const uint Roll         = 0x100;
    }

    /// <summary>One candidate landing bay in <b>world</b> space — the plane-0
    /// centre and entry direction of a station/carrier land slot, already
    /// rotated by the target's orientation (native
    /// <c>centers[i] = pst->GetLandPosition(i,0) * itsOrientation</c>,
    /// <c>common.cpp:1528-1531</c>).</summary>
    public readonly record struct DockBay(Vector3 Center, Vector3 Direction);

    /// <summary>The planner's per-frame decision for one approach: which bay,
    /// where to fly, which way to point, and the native mask governing the
    /// steering (<see cref="Steer"/>).</summary>
    public readonly record struct Step(
        uint Mask,
        int BayIndex,
        Vector3 Goal,
        Vector3 Facing);

    // Helper accessors so callers read flags naturally.
    public static bool Has(this in Step s, uint bit) => (s.Mask & bit) != 0;
    public static bool IsEnter(this in Step s) => (s.Mask & Mask.Enter) != 0;
    public static bool IsPivot(this in Step s) => (s.Mask & Mask.Pivot) != 0;
    public static bool IsKillThrottle(this in Step s) => (s.Mask & Mask.KillThrottle) != 0;
    public static bool IsFast(this in Step s) => (s.Mask & Mask.Fast) != 0;
    public static bool WantsRoll(this in Step s) => (s.Mask & Mask.Roll) != 0;

    /// <summary>Fallback state when the hull has no bay of its class — native
    /// "power glide in" (<c>c_gpmFast</c> at the centre, <c>common.cpp:1517-1524</c>).</summary>
    public static Step PowerGlide(in Vector3 itsPosition) =>
        new(Mask.Fast, -1, itsPosition, Vector3.Zero);

    /// <summary>
    /// Compute the native dock-approach step for a <b>station</b> target —
    /// <c>Waypoint::GetGotoPosition</c>'s <c>OT_station</c> case
    /// (<c>common.cpp:1505-1545</c>) forwarding to <see cref="DoApproach"/>.
    /// </summary>
    /// <param name="isFighter">Whether the approaching hull carries
    /// <c>c_habmFighter</c> — native picks <c>GetLandSlots()</c> for fighters vs
    /// <c>GetCapLandSlots()</c> for capitals (<c>common.cpp:1513-1515</c>).
    /// The <paramref name="bays"/> list passed in is expected to be the set the
    /// caller resolved for the ship's class (see the remark in <see cref="StationBaysForClass"/>).</param>
    /// <param name="totalRadius"><c>shipRadius + stationRadius + 10</c> — native's
    /// <c>myRadius</c> in <c>DoApproach</c> (<c>common.cpp:1170</c>).</param>
    public static Step DockStation(
        in Vector3 myPosition,
        in Vector3 myVelocity,
        in Orientation myOrientation,
        in Vector3 stationCenter,
        IReadOnlyList<DockBay> bays,
        int nLand,
        float distanceRest,
        in Vector3 positionRest,
        float totalRadius,
        float rateYaw,
        float ratePitch,
        bool experimental)
    {
        if (nLand <= 0)
            return PowerGlide(stationCenter);   // no bays for this hull class — native power glide (common.cpp:1517-1524)

        Step step = DoApproach(
            myPosition, myVelocity, stationCenter, bays, nLand,
            distanceRest, positionRest, totalRadius, myOrientation,
            rateYaw, ratePitch, experimental);

        // c_gpmEnter → add c_gpmRoll (station: roll to fit the bay's orientation,
        // common.cpp:1540-1542 — the same line the carrier case adds at :1494).
        if (step.IsEnter())
            return step with { Mask = step.Mask | Mask.Roll };
        return step;
    }

    /// <summary>
    /// Resolve how many of <paramref name="bays"/> a hull of the given class may
    /// use. Faithful to native's selector (<c>common.cpp:1513-1515</c>):
    /// a fighter uses <c>GetLandSlots()</c>, a non-fighter (capital) uses
    /// <c>GetCapLandSlots()</c>. The list contains the capital prefix followed
    /// by ordinary fighter bays (Igc/stationtypeigc.cpp:71-144).
    /// </summary>
    public static int StationBaysForClass(bool isFighter, IReadOnlyList<DockBay> bays, int capLandSlots)
        => isFighter ? bays.Count : Math.Min(bays.Count, capLandSlots);

    /// <summary>
    /// Port of <c>Waypoint::DoApproach</c> (<c>common.cpp:1157-1320</c>) — chooses
    /// the best bay, stages the goal outside it on the approach line, and decides
    /// coast / pivot / enter. <paramref name="bays"/> are world-space
    /// (<see cref="DockBay"/>); <paramref name="nLand"/> is the count to consider.
    /// </summary>
    public static Step DoApproach(
        in Vector3 myPosition,
        in Vector3 myVelocity,
        in Vector3 itsPosition,
        IReadOnlyList<DockBay> bays,
        int nLand,
        float distanceRest,
        in Vector3 positionRest,
        float myRadius,
        in Orientation myOrientation,
        float rateYaw,
        float ratePitch,
        bool experimental)
    {
        Vector3 dpNow = myPosition - itsPosition;
        Vector3 dpRest = positionRest - itsPosition;

        int bayBest = -1;
        Vector3 centerBest = Vector3.Zero;
        Vector3 directionBest = Vector3.Zero;
        Vector3 goalBest = Vector3.Zero;
        float error2Best = float.MaxValue;
        float offset2Best = float.MaxValue;

        for (int i = 0; i < nLand; i++)
        {
            Vector3 pcenter = bays[i].Center;
            Vector3 pdirection = bays[i].Direction;

            // Goal: point on the approach line where |pcenter + dir*t| == R.
            // Solve (pc + pd*t)^2 = R^2 → pc² + 2·(pc·pd)t + pd²t² − R² = 0.
            // Common.cpp:1180-1196 — t = sqrt(b²−c) − b, with a guarded 0.1
            // when b²−c < 0.
            float b = pcenter * pdirection;
            float c = pcenter * pcenter - myRadius * myRadius;
            float disc = b * b - c;
            float t = disc < 0.0f ? 0.1f : MathF.Sqrt(disc) - b;
            Vector3 goal = pcenter + pdirection * t;

            Vector3 deltaNow = dpNow - pcenter;
            float dotNow = deltaNow * pdirection;
            Vector3 deltaRest = dpRest - pcenter;
            float dotRest = deltaRest * pdirection;

            float error2;
            float offset2;
            if (dotNow >= 0.0f || dotRest >= myRadius)
            {
                // In front of the bay — error is distance from rest position to the approach line.
                error2 = deltaRest.LengthSquared() - dotRest * dotRest;
                offset2 = deltaNow.LengthSquared() - dotNow * dotNow;
            }
            else
            {
                // Behind the bay — error is distance from rest position to the approach point.
                error2 = (dpRest - goal).LengthSquared();
                offset2 = float.MaxValue;
            }

            if (error2 < error2Best)
            {
                centerBest = pcenter; directionBest = pdirection; goalBest = goal;
                error2Best = error2; offset2Best = offset2; bayBest = i;
            }
        }

        uint gpm;
        Vector3 gotoPos;
        Vector3 facing = Vector3.Zero;

        if (error2Best > 36.0f)
        {
            // Not able to drift into position — continue normally (common.cpp:1250-1267).
            gotoPos = goalBest + itsPosition;
            float d2 = (dpNow - goalBest).LengthSquared();
            if ((d2 > distanceRest * distanceRest * 2.0f) ||
                ((dpRest - goalBest).LengthSquared() <= d2 + 1.0f))
            {
                gpm = ((dpNow - centerBest) * directionBest >= 0.0f) ? Mask.DodgeShips : 0u;
            }
            else
            {
                facing = gotoPos - myPosition;
                gpm = Mask.Pivot;
            }
        }
        else if (offset2Best > 40.0f)
        {
            // Not close to the approach line — coast in (kill throttle) (common.cpp:1272-1280).
            gpm = Mask.KillThrottle | Mask.DodgeShips;
            gotoPos = goalBest + itsPosition;
        }
        else
        {
            // On the line: aim to enter (common.cpp:1282-1318).
            gotoPos = centerBest + itsPosition;
            facing = gotoPos - myPosition;

            float rate2 = rateYaw * rateYaw + ratePitch * ratePitch;
            float rateMax = experimental ? 0.10f : 0.01f;
            const float cosMin = 0.999f * 0.999f;
            if (rate2 < rateMax * rateMax && CosForward2(myOrientation, facing) > cosMin)
                gpm = Mask.Enter | Mask.NoDodge;      // on track, right way, not turning → charge
            else
                gpm = Mask.Pivot | Mask.NoDodge;       // on track and stopped → pivot to face the bay
        }

        return new Step(gpm, bayBest, gotoPos, facing);
    }

    /// <summary>
    /// <c>Orientation::CosForward2</c> (<c>orientation.cpp:441-444</c>): the signed
    /// squared cosine of the angle between the ship's nose and <paramref name="xyz"/> —
    /// positive when <c>forward·xyz ≥ 0</c>, else negative (magnitude squared).
    /// </summary>
    public static float CosForward2(in Orientation o, in Vector3 xyz)
    {
        Vector3 fwd = o.GetForward();
        float dot = fwd * xyz;
        float l2 = xyz.LengthSquared();
        if (l2 == 0f) return 0f;
        float sq = (dot * dot) / l2;
        return dot >= 0f ? sq : -sq;
    }

    /// <summary>
    /// Turn the planner's <see cref="Step"/> into a <see cref="ControlData"/> —
    /// the port of <c>GotoPlan::SetControls</c>'s docking branches
    /// (<c>common.cpp:2030-2170</c>): pivot / kill-throttle / enter(+roll) / seek.
    /// The <em>face</em> law is the port's established "forward × dir" turn
    /// (the same cross-product signal <c>SteerToward</c> uses, <c>Ship.cs:5257-5260</c>);
    /// the <em>roll</em> is native's align-up-with-+Z computation
    /// (<c>common.cpp:2090-2150</c>). Not applicable (a docking-relevant mask
    /// absent) or the finished case returns <see cref="ControlData.Neutral"/>.
    /// </summary>
    /// <param name="turnGain">Turn-signal → yaw/pitch gain (default
    /// <c>AutopilotTurnGain = 2.0</c>).</param>
    /// <param name="dt">Tick seconds for the roll-rate clamp (native
    /// <c>maxRoll = dt * maxTurnRateRoll</c>, <c>common.cpp:2133</c>).</param>
    /// <param name="maxTurnRateRoll">Hull max roll turn rate (rad/s) sizing the
    /// roll clamp.</param>
    public static ControlData Steer(
        in Step step,
        in Vector3 myPosition,
        in Orientation myOrientation,
        in Vector3 velocity,
        float distanceRest,
        float turnGain = 2.0f,
        float dt = 0.033f,
        float maxTurnRateRoll = 2.0f)
    {
        // Finished / nothing to do → neutral kill.
        if ((step.Mask & (Mask.Pivot | Mask.KillThrottle | Mask.Enter)) == 0)
        {
            // Plain seek (mask == 0 with DodgeShips, or Fast power-glide):
            // face the goal and fly (Fast = full forward).
            Vector3 path = step.Goal - myPosition;
            var (yaw, pitch) = Face(myOrientation, path, turnGain);
            bool slowNear = (step.Mask & Mask.Fast) == 0 &&
                            (step.Goal - myPosition).LengthSquared() < distanceRest * distanceRest;
            float throttle = slowNear ? -1f : 1f;
            return new ControlData(yaw, pitch, 0f, throttle);
        }

        // Pivot or KillThrottle: kill throttle, face the facing/goal direction.
        if ((step.Mask & (Mask.Pivot | Mask.KillThrottle)) != 0)
        {
            Vector3 path = (step.Mask & Mask.Pivot) != 0 ? step.Facing : step.Goal - myPosition;
            var (yaw, pitch) = Face(myOrientation, path, turnGain);
            return new ControlData(yaw, pitch, 0f, -1f);   // native: throttle stays at its kill-init -1
        }

        // Enter: face the bay entry, optionally roll to align, full forward throttle.
        if ((step.Mask & Mask.Enter) != 0)
        {
            var (yaw, pitch) = Face(myOrientation, step.Facing, turnGain);
            float roll = (step.Mask & Mask.Roll) != 0
                ? ComputeRoll(myOrientation, yaw, pitch, dt, maxTurnRateRoll)
                : 0f;
            return new ControlData(yaw, pitch, roll, 1.0f);
        }

        return ControlData.Neutral;
    }

    /// <summary>Port's shared face law: forward × directionToTarget in ship-local
    /// space → (yaw, pitch), the same signal <c>SteerToward</c> emits.</summary>
    private static (float Yaw, float Pitch) Face(in Orientation o, in Vector3 direction, float turnGain)
    {
        float len = direction.Length();
        if (len <= 1e-3f) return (0f, 0f);
        Vector3 dir = direction / len;
        Vector3 body = o.TimesInverse(Vector3.CrossProduct(o.GetForward(), dir));
        float yaw = Math.Clamp(turnGain * body.Y, -1f, 1f);
        float pitch = Math.Clamp(turnGain * -body.X, -1f, 1f);
        return (yaw, pitch);
    }

    /// <summary>
    /// Native's roll-to-align-with-+Z (<c>common.cpp:2097-2148</c>): roll so the
    /// ship's up vector heads toward the world +Z, clipped to the frame's max roll
    /// rate and blended with the simultaneous yaw/pitch commands.
    /// </summary>
    private static float ComputeRoll(in Orientation o, float yaw, float pitch, float dt, float maxTurnRateRoll)
    {
        Vector3 up = o.GetUp();
        float cosRoll = up.Z;
        if (cosRoll >= 0.99f || cosRoll <= -0.99f) return 0f;   // already rolled flat / inverted — no roll cmd

        float roll = cosRoll >= 0.0f ? -MathF.Acos(Math.Clamp(cosRoll, -1f, 1f))
                                     : MathF.Acos(Math.Clamp(-cosRoll, -1f, 1f));
        if (o.GetRight().Z < 0.0f) roll = -roll;

        float maxRoll = dt * maxTurnRateRoll;
        if (maxRoll <= 1e-5f) return 0f;
        float r = roll / maxRoll;

        float d2 = pitch * pitch + yaw * yaw;
        if (d2 + r * r > 1.0f)
        {
            if (d2 > 1.0f) d2 = 1.0f;                 // common.cpp:2113-2119 guard
            float f = MathF.Sqrt(1.0f - d2);
            return r > 0.0f ? f : -f;
        }
        return r;
    }
}
