using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P575 — the shared <see cref="DockApproach"/> station/carrier dock-approach
/// planner. These tests pin the port's behaviour to the <b>native mathematics</b>
/// of <c>Waypoint::DoApproach</c> + <c>GetGotoPosition</c>'s <c>OT_station</c> case
/// (<c>src/Igc/common.cpp:1157-1545</c>): bay selection, the staging point on the
/// approach line, the enter/roll/pivot/no-dodge flags, the no-bay "power glide"
/// fallback, and the fighter-vs-capital slot selector. Every expected value is
/// hand-derived below from the native closed forms (see each test's remarks), so
/// a regression to "fly straight at the centre" (the pre-P575 behaviour) fails
/// immediately. Written before the <c>DockApproach</c> module existed (the
/// reference on old code fails to compile = red), then green after the port.
/// </summary>
public class DockApproachTests
{
    /// <summary>Station at the origin, identity orientation. <c>myRadius</c>
    /// (= ship radius + station radius + 10, native common.cpp:1170) = 30.</summary>
    private const float R = 30f;

    /// <summary>A bay whose entry faces +Z: centre (0,0,20), direction (0,0,1).</summary>
    private static readonly DockApproach.DockBay BayZPlus = new(new Vector3(0f, 0f, 20f), new Vector3(0f, 0f, 1f));

    /// <summary>A bay offset in +Y, same entry direction.</summary>
    private static readonly DockApproach.DockBay BayYOffset = new(new Vector3(0f, 100f, 20f), new Vector3(0f, 0f, 1f));

    /// <summary>No bay (hull class has none) → native power glide.</summary>
    [Fact]
    public void No_bays_power_glides_at_the_centre()
    {
        var step = DockApproach.DockStation(
            new Vector3(0f, 0f, 200f), Vector3.Zero, Orientation.Identity,
            Vector3.Zero, new[] { BayZPlus }, nLand: 0,
            distanceRest: 0f, Vector3.Zero, R, rateYaw: 0f, ratePitch: 0f, experimental: false);

        // c_gpmFast at the centre, skip the target (common.cpp:1517-1524).
        Assert.True(step.IsFast());
        Assert.Equal(Vector3.Zero, step.Goal);
        Assert.Equal(-1, step.BayIndex);
    }

    /// <summary>
    /// <b>Native enter behaviour.</b> When the ship is on the approach line,
    /// aligned and not turning, native charges AT THE BAY CENTRE — not a staged
    /// point — <c>*pvectorGoto = *pcenterBest + itsPosition</c>
    /// (<c>common.cpp:1287-1288</c>), with <c>c_gpmEnter | c_gpmNoDodge</c>
    /// (<c>:1308</c>), and the station case ORs <b>Roll</b>
    /// (<c>common.cpp:1540-1542</c>). With bay centre (0,0,20) that is the goal;
    /// facing = goal − myPosition = (0,0,-180).
    /// </summary>
    [Fact]
    public void Aligned_not_turning_enters_with_roll_at_the_bay_centre()
    {
        var ship = new Vector3(0f, 0f, 200f);
        var o = Orientation.Identity;              // forward (0,0,-1) — nose pointing at the bay

        var step = DockApproach.DockStation(
            ship, Vector3.Zero, o, Vector3.Zero,
            new[] { BayZPlus }, nLand: 1,
            distanceRest: 0f, positionRest: ship, R,
            rateYaw: 0f, ratePitch: 0f, experimental: false);

        Assert.Equal(0, step.BayIndex);
        Assert.True(step.IsEnter(), $"expected Enter, mask=0x{step.Mask:x}");
        Assert.True(step.WantsRoll(), "station case must OR c_gpmRoll on enter");
        Assert.True(step.Has(DockApproach.Mask.NoDodge), "enter implies c_gpmNoDodge");
        // Native flies at the BAY CENTRE on enter (common.cpp:1287-1288).
        Assert.Equal(20f, step.Goal.Z, 3);
        Assert.Equal(0f, step.Goal.X, 3);
        // Facing points from the ship to the bay centre: (0,0,20)-(0,0,200).
        Assert.Equal(0f, step.Facing.X, 3);
        Assert.Equal(0f, step.Facing.Y, 3);
        Assert.Equal(-180f, step.Facing.Z, 3);
    }

    /// <summary>
    /// <b>Native staging point (drift branch).</b> When the rest position is far
    /// from the approach line (error² &gt; 36), native stages on the approach line:
    /// <c>*pvectorGoto = goalBest + itsPosition</c> (<c>common.cpp:1252</c>) where
    /// <c>goalBest</c> is the quadratic solve (<c>:1178-1196</c>): for myRadius=30
    /// and bay (0,0,20)/dir(0,0,1), <c>b=20, c=−500, disc=900, t=√900−20=10</c> →
    /// <c>goalBest=(0,0,30)</c>. Also pins that a ship moving in the right
    /// direction keeps the <c>c_gpmDodgeShips</c> hint and does NOT set pivot.
    /// </summary>
    [Fact]
    public void Drift_stage_flies_at_the_native_staging_point()
    {
        var ship = new Vector3(0f, 0f, 200f);
        var o = Orientation.Identity;
        // Rest position forced off the approach line (Y=100) → error² > 36 → drift.
        var rest = new Vector3(0f, 100f, 200f);

        var step = DockApproach.DockStation(
            ship, Vector3.Zero, o, Vector3.Zero,
            new[] { BayZPlus }, nLand: 1,
            distanceRest: 0f, positionRest: rest, R,
            rateYaw: 0f, ratePitch: 0f, experimental: false);

        // Native staging point (0,0,30) on the approach line, NOT the centre.
        Assert.Equal(30f, step.Goal.Z, 3);
        Assert.Equal(0f, step.Goal.X, 3);
        Assert.Equal(0f, step.Goal.Y, 3);
        Assert.True(step.Has(DockApproach.Mask.DodgeShips),
            "moving in the right direction keeps c_gpmDodgeShips (common.cpp:1255-1261)");
        Assert.False(step.IsPivot());
        Assert.False(step.IsEnter());
    }

    /// <summary>
    /// <b>Pivot when misaligned.</b> Identical geometry but the ship's nose points
    /// AWAY (+Z, forward (0,0,1)); <c>CosForward2(facing) &lt; 0</c> so the
    /// "on the line, on track, not turning" enter gate fails
    /// (<c>common.cpp:1301-1318</c>) and the step pivots to face the bay, killing
    /// throttle. Still <c>NoDodge</c> (the 04/08 mmf change).
    /// </summary>
    [Fact]
    public void Misaligned_on_the_line_pivots_to_face_the_bay()
    {
        var ship = new Vector3(0f, 0f, 200f);
        var o = new Orientation();
        o.Set(new Vector3(0f, 0f, 1f), new Vector3(0f, 1f, 0f));   // forward +Z (away)

        var step = DockApproach.DockStation(
            ship, Vector3.Zero, o, Vector3.Zero,
            new[] { BayZPlus }, nLand: 1,
            distanceRest: 0f, positionRest: ship, R,
            rateYaw: 0f, ratePitch: 0f, experimental: false);

        Assert.True(step.IsPivot());
        Assert.True(step.Has(DockApproach.Mask.NoDodge));
        Assert.False(step.IsEnter());
        // Facing still aims at the bay centre so the next ticks align the nose.
        Assert.Equal(new Vector3(0f, 0f, -180f).Z, step.Facing.Z, 3);
    }

    /// <summary>
    /// <b>Bay selection picks the nearest approach line.</b> Two bays, one dead in
    /// front (error² = 0 from the rest position to the line) and one offset +100 in
    /// Y (behind-its-line branch → error² = |dpRest−goal|² ≫ 0). DoApproach keeps
    /// the smallest error² (common.cpp:1200-1244) → bay 0.
    /// </summary>
    [Fact]
    public void Selects_the_nearest_bay()
    {
        var ship = new Vector3(0f, 0f, 200f);
        var o = Orientation.Identity;

        var step = DockApproach.DockStation(
            ship, Vector3.Zero, o, Vector3.Zero,
            new[] { BayZPlus, BayYOffset }, nLand: 2,
            distanceRest: 0f, positionRest: ship, R,
            rateYaw: 0f, ratePitch: 0f, experimental: false);

        Assert.Equal(0, step.BayIndex);
        Assert.Equal(20f, step.Goal.Z, 3);          // bay centre of the chosen bay 0
    }

    /// <summary>
    /// <b>Fighter vs capital slot selector.</b> Native picks <c>GetLandSlots()</c>
    /// for a <c>c_habmFighter</c> hull and <c>GetCapLandSlots()</c> otherwise
    /// (<c>common.cpp:1513-1515</c>). Given 3 resolved bays and a cap set of 2, a
    /// fighter uses all 3, a capital only the 2 cap bays.
    /// </summary>
    [Fact]
    public void Fighter_uses_all_bays_capital_only_the_cap_set()
    {
        var bays = new[] { BayZPlus, BayYOffset, BayYOffset with { Center = new Vector3(0f, 0f, -20f) } };
        Assert.Equal(3, DockApproach.StationBaysForClass(isFighter: true, bays, capLandSlots: 2));
        Assert.Equal(2, DockApproach.StationBaysForClass(isFighter: false, bays, capLandSlots: 2));
    }

    /// <summary>
    /// <b>Enter steering = face + full throttle (+ roll when misrolled).</b>
    /// Steer() of the enter step produces throttle +1 (native "charge",
    /// common.cpp:2169), and a rolled ship gets a non-zero roll command
    /// (common.cpp:2097-2148) to align its up with +Z.
    /// </summary>
    [Fact]
    public void Enter_step_charges_full_throttle_and_rolls_to_level_up()
    {
        var ship = new Vector3(0f, 0f, 200f);
        var o = Orientation.Identity;   // forward (0,0,-1); up = (0,1,0) → level, roll≈0

        var step = DockApproach.DockStation(
            ship, Vector3.Zero, o, Vector3.Zero,
            new[] { BayZPlus }, nLand: 1,
            distanceRest: 0f, positionRest: ship, R,
            rateYaw: 0f, ratePitch: 0f, experimental: false);

        var ctrl = DockApproach.Steer(step, ship, o, Vector3.Zero, distanceRest: 0f);
        Assert.True(ctrl.Throttle > 0.9f, $"enter must charge at full throttle, got {ctrl.Throttle}");

        // A rolled ship (up tilted to +X) rolls toward +Z; enter triggers it.
        var rolled = new Orientation();
        rolled.Set(new Vector3(0f, 0f, -1f), new Vector3(1f, 0f, 0f));  // up = +X
        var ctrlRolled = DockApproach.Steer(step, ship, rolled, Vector3.Zero, distanceRest: 0f,
            dt: 0.1f, maxTurnRateRoll: 2f);
        Assert.NotEqual(0f, ctrlRolled.Roll);
    }

    /// <summary>Power-glide steering = full forward at the centre (Fast).</summary>
    [Fact]
    public void Power_glide_steers_full_forward_at_the_centre()
    {
        var step = DockApproach.PowerGlide(new Vector3(10f, 20f, 30f));
        Assert.True(step.IsFast());
        var ctrl = DockApproach.Steer(step, Vector3.Zero, Orientation.Identity, Vector3.Zero, distanceRest: 0f);
        Assert.True(ctrl.Throttle > 0.9f);
    }
}
