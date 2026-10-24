using System.Diagnostics;
using System.IO;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;
using Allegiance.ClientCore.MonoGame.Flight;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P804 — the client-side lead/aim solve and the native HUD rules it feeds.
/// Native computes all of this on the client from IGC state
/// (<c>WinTrek/indicator.cpp:183-405</c> + <c>Igc/common.cpp:121-149</c>); the
/// port now runs the shared Core <see cref="LeadSolver"/> on the client instead
/// of reading a server-solved lead point, and the four parity rows
/// (ballistic lead marker, aim hit-probability predictor, chase aim without
/// target, radar viewport) are pinned here:
/// <list type="bullet">
/// <item>client lead == the Core solve for fixed scenes (moving, stationary,
/// out of range, no solution, muzzle offset, absolute projectiles);</item>
/// <item>native's single solve feeds BOTH the cockpit and the chase/missile
/// indicators (<see cref="ClientLead"/>): the forward-projected aim point, the
/// <c>bWillHit</c>/green ramp and the cockpit screen-space <c>g</c> ramp;</item>
/// <item>the visibility split in the shared HUD model, for BOTH
/// <see cref="HudStyle"/> consumers;</item>
/// <item>the eye-space radar clamp and the round-radar circle (WinTrek.cpp:7882-7944).</item>
/// </list>
/// Pure/headless (no graphics device) except the per-style source contracts,
/// which follow the Hud19CentreHudTests discipline.
/// </summary>
public sealed class P804ClientLeadTests
{
    private readonly ITestOutputHelper _out;
    public P804ClientLeadTests(ITestOutputHelper output) => _out = output;

    private static readonly Orientation Identity = Orientation.Identity;

    // ── 1. the client lead is the Core solve ─────────────────────────────────

    /// <summary>Build a native-faithful input: own ship at the origin facing
    /// -Z, identity attitude unless specified.</summary>
    private static TargetWeaponRangeInput Input(
        SimVec? targetPos = null, SimVec? targetVel = null, float targetRadius = 8f,
        float lifespan = 2f, float speed = 1000f, bool absolute = false, float power = 5f,
        bool friendly = false, bool chase = false, bool sameCluster = true, bool canSee = true,
        bool hasWeapon = true, SimVec? myPos = null, SimVec? myVel = null, SimVec? mount = null,
        bool ammo = false, bool energy = false, float speedAmmoGa = 1f, float lifespanEnergyGa = 1f,
        Orientation? orientation = null)
        => new(
            SameCluster: sameCluster, CanSee: canSee, HasWeapon: hasWeapon, ChaseCamera: chase,
            FriendlyOrAllied: friendly, ProjectilePower: power, AbsoluteF: absolute,
            AmmoPerShot: ammo, EnergyWeapon: energy, ProjectileSpeed: speed,
            ProjectileLifespan: lifespan, SpeedAmmoGa: speedAmmoGa, LifespanEnergyGa: lifespanEnergyGa,
            MyPosition: myPos ?? SimVec.Zero, MyOrientation: orientation ?? Identity,
            MyVelocity: myVel ?? SimVec.Zero, MountPosition: mount ?? SimVec.Zero,
            TargetRadius: targetRadius, TargetPosition: targetPos ?? new SimVec(0f, 0f, -1000f),
            TargetVelocity: targetVel ?? SimVec.Zero);

    private static LeadSolver.Solution CoreSolve(in TargetWeaponRangeInput input)
        => LeadSolver.Solve(new LeadSolver.Input(
            input.MyPosition, input.MyVelocity, input.TargetPosition, input.TargetVelocity,
            input.MountPosition * input.MyOrientation,
            input.ProjectileSpeed * (input.AmmoPerShot ? input.SpeedAmmoGa : 1f),
            input.AbsoluteF, input.TargetRadius));

    [Theory]
    // (a) stationary target straight ahead — no lead at all.
    [InlineData(0f, 0f, -1000f)]
    // (b) target drifting laterally (+X) — the lead swings toward its motion.
    [InlineData(0f, 0f, -600f, 120f, 0f, 0f)]
    // (c) target receding along the aim axis at half the round speed.
    [InlineData(0f, 0f, -800f, 0f, 0f, -300f)]
    // (d) target behind and to the left (a large yaw lead).
    [InlineData(200f, 100f, 500f, 0f, -50f, 0f)]
    public void Client_lead_equals_the_core_solve_on_fixed_scenes(
        float tx, float ty, float tz, float tvx = 0f, float tvy = 0f, float tvz = 0f)
    {
        var input = Input(targetPos: new SimVec(tx, ty, tz),
            targetVel: new SimVec(tvx, tvy, tvz), speed: 700f, lifespan: 5f);
        var client = ClientLead.Solve(input);
        var core = CoreSolve(input);

        Assert.True(client.GatePassed);
        Assert.Equal(core.Time, client.Time, 3);
        Assert.Equal(core.Direction.X, client.Direction.X, 3);
        Assert.Equal(core.Direction.Y, client.Direction.Y, 3);
        Assert.Equal(core.Direction.Z, client.Direction.Z, 3);
        // The solve direction is a unit vector (native's TransformDirectionToImage input).
        Assert.Equal(1f, client.Direction.Length(), 3);
    }

    [Fact]
    public void Lead_point_actually_intercepts_the_moving_target()
    {
        // Target 900 m ahead drifting +X at 150 m/s; round 700 m/s, radius 8.
        var input = Input(targetPos: new SimVec(0f, 0f, -900f),
            targetVel: new SimVec(150f, 0f, 0f), speed: 700f, lifespan: 5f, targetRadius: 8f);
        var r = ClientLead.Solve(input);
        Assert.True(r.Solved);

        // native solveForImpact semantics: |deltaP + t·deltaV| = t·speed + radius.
        var close = input.TargetPosition + r.Time * input.TargetVelocity;
        float reach = r.Time * input.ProjectileSpeed + input.TargetRadius;
        Assert.Equal(reach, close.Length(), 1);
        // and the lead is genuinely ahead of the target's CURRENT position (+X).
        Assert.True(r.Direction.X > 0.05f, $"lead should swing toward +X, got {r.Direction}");
    }

    [Fact]
    public void Stationary_target_needs_no_lead_and_hits_true()
    {
        var r = ClientLead.Solve(Input(targetPos: new SimVec(0f, 0f, -1000f), speed: 1000f, targetRadius: 10f));
        Assert.True(r.Solved);
        Assert.Equal(0f, r.Direction.X, 4);
        Assert.Equal(0f, r.Direction.Y, 4);
        Assert.Equal(-1f, r.Direction.Z, 4);
        Assert.Equal(0.99f, r.Time, 3);                 // (1000 − radius 10) / 1000 m/s
        Assert.True(r.InRange);
        // Chase forward-projected aim point: shipPos + forward·|target−ship|.
        Assert.Equal(-1000f, r.TrackingPoint.Z, 3);
        // Dead ahead: fA = t·speed = distance, cos = 1 → miss distance 0 → pure green.
        Assert.True(r.WillHit);
        Assert.Equal(1f, r.Green, 4);
    }

    [Fact]
    public void Out_of_range_when_the_intercept_exceeds_the_lifespan()
    {
        // radius 0 so t is exactly distance/speed (1000/1000 = 1 s).
        var at = ClientLead.Solve(Input(targetPos: new SimVec(0f, 0f, -1000f), targetRadius: 0f,
            speed: 1000f, lifespan: 1f));
        Assert.Equal(1f, at.Time, 3);
        Assert.True(at.InRange);                         // native: t <= lifespan

        var past = ClientLead.Solve(Input(targetPos: new SimVec(0f, 0f, -1000f), targetRadius: 0f,
            speed: 1000f, lifespan: 0.99f));
        Assert.False(past.InRange);
    }

    [Fact]
    public void No_solution_is_never_in_range_and_never_hits()
    {
        // Projectile speed 0 makes solveForImpact's quadratic degenerate
        // (a == 0 → the native no-solution sentinel, common.cpp:86-92).
        var r = ClientLead.Solve(Input(speed: 0f, lifespan: 2f));
        Assert.True(r.GatePassed);
        Assert.False(r.Solved);
        Assert.Equal(LeadSolver.NoSolutionTime, r.Time);
        Assert.False(r.InRange);
        Assert.False(r.WillHit);
        Assert.Equal(0f, r.Green);
    }

    [Fact]
    public void Absolute_projectiles_ignore_the_shooters_own_velocity()
    {
        // Own ship moving -Z at 400; target 1000 ahead. A relative projectile
        // closes faster; an absolute one does not see the shooter's motion.
        var relative = ClientLead.Solve(Input(myVel: new SimVec(0f, 0f, -400f), speed: 800f, lifespan: 5f));
        var absolute = ClientLead.Solve(Input(myVel: new SimVec(0f, 0f, -400f), speed: 800f, lifespan: 5f, absolute: true));
        Assert.True(absolute.Time > relative.Time);
    }

    [Fact]
    public void Muzzle_offset_is_rotated_into_world_space_like_native()
    {
        // Native: hisPosition - (myPosition + weapon->GetPosition()*myOrientation).
        // A yawed ship (forward +X) with the gun 3 m to its right (-Z here).
        var orientation = new Orientation();
        orientation.Set(new SimVec(1f, 0f, 0f), new SimVec(0f, 1f, 0f));
        var mount = new SimVec(3f, 0f, 0f);
        var input = Input(targetPos: new SimVec(0f, 0f, -60f), targetRadius: 2f,
            speed: 900f, lifespan: 2f, mount: mount, orientation: orientation);
        var r = ClientLead.Solve(input);

        SimVec expectedMuzzleWorld = mount * orientation;
        var expected = LeadSolver.Solve(new LeadSolver.Input(
            input.MyPosition, input.MyVelocity, input.TargetPosition, input.TargetVelocity,
            expectedMuzzleWorld, input.ProjectileSpeed, input.AbsoluteF, input.TargetRadius));
        Assert.Equal(expected.Time, r.Time, 4);
        Assert.Equal(expected.Direction.X, r.Direction.X, 4);

        // The offset really moves the solve: a zero muzzle gives a different time.
        var noMuzzle = LeadSolver.Solve(new LeadSolver.Input(
            input.MyPosition, input.MyVelocity, input.TargetPosition, input.TargetVelocity,
            SimVec.Zero, input.ProjectileSpeed, input.AbsoluteF, input.TargetRadius));
        Assert.NotEqual(noMuzzle.Time, r.Time);
    }

    // ── 2. native gates ─────────────────────────────────────────────────────

    [Fact]
    public void No_target_cluster_or_visibility_fails_the_native_gate()
    {
        Assert.False(ClientLead.Solve(Input(hasWeapon: false)).GatePassed);   // indicator.cpp:207
        Assert.False(ClientLead.Solve(Input(sameCluster: false)).GatePassed); // :188
        Assert.False(ClientLead.Solve(Input(canSee: false)).GatePassed);      // :190-192
    }

    [Theory]
    [InlineData(-5f, false, false, false)]  // healing weapon on an enemy → no indicator
    [InlineData(-5f, false, true, true)]    // healing weapon on a friend → indicator
    [InlineData(5f, false, false, true)]    // damaging weapon on an enemy → indicator
    [InlineData(5f, false, true, false)]    // damaging weapon on a friend → no indicator
    [InlineData(-5f, true, false, true)]    // chase/missile cameras skip the test (:214)
    [InlineData(5f, true, true, true)]
    public void Healing_versus_damaging_weapon_gate_is_native(float power, bool chase, bool friendly, bool expected)
        => Assert.Equal(expected, ClientLead.Solve(Input(power: power, chase: chase, friendly: friendly)).GatePassed);

    [Fact]
    public void Ammo_speed_and_energy_lifespan_global_attributes_feed_the_solve()
    {
        // 700 m, 600 m/s round, 1.0 s lifespan, SpeedAmmo 1.5 → speed 900 → t 0.78.
        var ammoOff = Input(targetPos: new SimVec(0f, 0f, -700f), speed: 600f, lifespan: 1f, targetRadius: 0f);
        var ammoOn = Input(targetPos: new SimVec(0f, 0f, -700f), speed: 600f, lifespan: 1f, targetRadius: 0f,
            ammo: true, speedAmmoGa: 1.5f);
        Assert.False(ClientLead.Solve(ammoOff).InRange);              // 1.167 s > 1 s
        Assert.True(ClientLead.Solve(ammoOn).InRange);                // 0.778 s <= 1 s

        // Energy weapon: LifespanEnergy 1.2 stretches 0.5 s to 0.6 s.
        var energyOff = Input(targetPos: new SimVec(0f, 0f, -700f), speed: 1200f, lifespan: 0.5f, targetRadius: 0f);
        var energyOn = Input(targetPos: new SimVec(0f, 0f, -700f), speed: 1200f, lifespan: 0.5f, targetRadius: 0f,
            energy: true, lifespanEnergyGa: 1.2f);
        Assert.False(ClientLead.Solve(energyOff).InRange);            // 0.583 > 0.5
        Assert.True(ClientLead.Solve(energyOn).InRange);              // 0.583 <= 0.6
    }

    // ── 3. chase/missile forward-projected aim + hit ramp (indicator.cpp:246-276)

    [Fact]
    public void Chase_aim_is_the_forward_projection_at_the_target_range()
    {
        var input = Input(targetPos: new SimVec(300f, 0f, -400f), chase: true, speed: 1000f);
        var r = ClientLead.Solve(input);
        // distance = 500; tracking = shipPos + forward·distance (nose is -Z).
        Assert.Equal(500f, r.DistanceToTarget, 3);
        Assert.Equal(0f, r.TrackingPoint.X, 3);
        Assert.Equal(-500f, r.TrackingPoint.Z, 3);
        Assert.Equal(-1f, r.Forward.Z, 3);
    }

    [Fact]
    public void Chase_hit_ramp_is_the_native_fHASquared_rule()
    {
        // Near miss: a target 600 m dead ahead drifting sideways at 200 m/s is
        // well off the nose at intercept, so the round misses the 10 m radius.
        var miss = ClientLead.Solve(Input(
            targetPos: new SimVec(0f, 0f, -600f), targetVel: new SimVec(200f, 0f, 0f),
            targetRadius: 10f, speed: 1000f, lifespan: 2f, chase: true));
        Assert.True(miss.GatePassed);
        Assert.False(miss.WillHit);
        Assert.Equal(0f, miss.Green);

        // The same geometry against a 200 m radius: fHA² <= r² and the native
        // ramp 1 - fHA²/r² lands strictly between 0 and 1.
        var hit = ClientLead.Solve(Input(
            targetPos: new SimVec(0f, 0f, -600f), targetVel: new SimVec(200f, 0f, 0f),
            targetRadius: 200f, speed: 1000f, lifespan: 2f, chase: true));
        Assert.True(hit.WillHit);
        Assert.InRange(hit.Green, 0.01f, 0.99f);

        // Dead ahead stationary: pure green (fHA = 0).
        var perfect = ClientLead.Solve(Input(
            targetPos: new SimVec(0f, 0f, -600f), targetRadius: 10f, speed: 1000f, chase: true));
        Assert.True(perfect.WillHit);
        Assert.Equal(1f, perfect.Green, 4);
    }

    // ── 4. cockpit screen-space green ramp (indicator.cpp:334-357) ───────────

    [Theory]
    [InlineData(0f, 1f)]        // dead on the lead → full green
    [InlineData(0.25f, 0.8125f)] // f < 1 → 1 - 0.75f
    [InlineData(1f, 0.25f)]     // f == 1 → 0.5 - f/4
    [InlineData(1.5f, 0.125f)]
    [InlineData(2f, 0f)]        // f >= 2 → red
    [InlineData(4f, 0f)]
    public void Cockpit_green_is_the_native_screen_space_ramp(float f, float expected)
    {
        // l = r·sqrt(f) with r = 10 px; lead offset along +X from the centre.
        const float r = 10f;
        float l = r * MathF.Sqrt(f);
        Assert.Equal(expected, ClientLead.CockpitGreen(500f + l, 400f, 500f, 400f, r), 4);
    }

    [Fact]
    public void Cockpit_green_with_no_target_radius_is_the_red_end_not_NaN()
        => Assert.Equal(0f, ClientLead.CockpitGreen(480f, 400f, 500f, 400f, 0f));

    // ── 5. HUD model + both styles ──────────────────────────────────────────

    private static HudFrame Frame() => new(0.8f, 0.6f, 0.4f, 0.3f, 0.2f, 1f,
        120f, -1f, false, false, true, 0, false, 0f, false, false,
        0f, 0f, System.Array.Empty<WeaponStatusRow>(), false, 0);

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Both_hud_styles_consume_the_same_client_lead_descriptor(HudStyle style)
    {
        // The behaviour lives in the shared model, so the style only chooses
        // the renderer: the element visibility is identical for both.
        var settings = new HudSettings { Style = style };
        Assert.Equal(style, settings.Style);

        var lead = new HudLead(new(300, 400), new(350, 420), InRange: true, Green: .8f,
            NowSeconds: 1, Granted: true, Visible: true, TargetVisible: true);
        var cockpit = HudModelAdapter.Create(Frame(), CameraMode.Cockpit, lead: ToView(lead));
        Assert.True(cockpit.LeadMarkerVisible);
        Assert.True(cockpit.AimPredictorVisible);
        Assert.True(cockpit.Elements.Single(e => e.Id == "Ballistic lead marker").Visible);
        Assert.True(cockpit.Elements.Single(e => e.Id == "Aim hit-probability predictor").Visible);

        // No capability: the lead surface hides in BOTH styles, the crosshair stays.
        var noGrant = HudModelAdapter.Create(Frame(), CameraMode.Cockpit, lead: ToView(lead with { Granted = false }));
        Assert.False(noGrant.LeadMarkerVisible);
        Assert.True(noGrant.AimPredictorVisible);

        // Chase: the forward-projected predictor, and no cockpit lead surface.
        var chase = HudModelAdapter.Create(Frame(), CameraMode.Chase, lead: ToView(lead));
        Assert.False(chase.LeadMarkerVisible);
        Assert.True(chase.AimPredictorVisible);

        // The gate-failed chase case falls back to the grey no-target marker.
        var fallback = HudModelAdapter.Create(Frame(), CameraMode.Chase, chaseAim: new Vector2(640, 360));
        Assert.True(fallback.ChaseAimVisible);
        Assert.True(fallback.Elements.Single(e => e.Id == "Chase weapon aim without target").Visible);

        // Both skins route through the same HudRenderer.Draw gates and override
        // the three indicator draws for their own artwork.
        var classic = Method(Src(Path.Combine("Rendering", "HudRenderer.cs")), "public virtual void Draw(HudModel model, Viewport viewport)");
        Assert.Contains("model.LeadMarkerVisible", classic);
        Assert.Contains("model.AimPredictorVisible", classic);
        Assert.Contains("model.ChaseAimVisible", classic);
        var modern = Src(Path.Combine("Rendering", "ModernHudRenderer.cs"));
        Assert.Contains("public override void DrawLeadIndicator(LeadIndicatorView lead)", modern);
        Assert.Contains("public override void DrawAimPredictor(LeadIndicatorView v, CameraMode camera, Viewport viewport)", modern);
        Assert.Contains("protected override void DrawChaseAim(Vector2 position)", modern);
        // Modern's target tether honours the in-view gate too.
        Assert.Contains("if (lead.TargetVisible)", modern);
    }

    private static LeadIndicatorView ToView(HudLead lead)
        => new(new(lead.Center.X, lead.Center.Y), new(lead.TargetCenter.X, lead.TargetCenter.Y),
            lead.InRange, lead.Green, lead.NowSeconds, lead.Granted, lead.Visible, lead.TargetVisible);

    // ── 6. radar eye-space clamp + round radar (WinTrek.cpp:7882-7944) ──────

    [Fact]
    public void Square_clamp_is_the_native_eye_space_ray_parameter()
    {
        // 900×640 viewport, inset 10 → half extents 440 × 310.
        // eye (2,1): tX = 440/2 = 220, tY = 310/1 = 310 → tMin = 220
        // → x = 440 (right edge), y = 220 → screen y = 320 − 220 = 100.
        var site = RadarBracketLayout.Resolve(true, 3f, 1.5f, 2f, 1f, 900f, 640f, 10f);
        Assert.Equal(RadarBracketLayout.RadarPlacement.EdgeClamped, site.Placement);
        Assert.Equal(450f + 440f, site.Point.X, 3);
        Assert.Equal(320f - 220f, site.Point.Y, 3);

        // Native is scale-invariant in the eye vector (it divides by a ray
        // parameter), unlike the pre-P804 NDC normalisation.
        var scaled = RadarBracketLayout.Resolve(true, 3f, 1.5f, 200f, 100f, 900f, 640f, 10f);
        Assert.Equal(site.Point.X, scaled.Point.X, 3);
        Assert.Equal(site.Point.Y, scaled.Point.Y, 3);
    }

    [Fact]
    public void Clamp_lands_on_the_edge_the_eye_ray_crosses_not_the_corner()
    {
        // Equal lateral components in a 16:9 viewport: tY (310/0.5=620) <
        // tX (440/0.5=880), so the marker pins the TOP edge (y=10) at x=310,
        // not the corner — the native tMin behaviour.
        var site = RadarBracketLayout.Resolve(true, 2f, 2f, 0.5f, 0.5f, 900f, 640f, 10f);
        Assert.Equal(450f + 310f, site.Point.X, 3);
        Assert.Equal(10f, site.Point.Y, 3);
    }

    [Fact]
    public void Behind_the_eye_keeps_the_native_lateral_signs()
    {
        // Behind and left → left edge; behind and above → top edge.
        var left = RadarBracketLayout.Resolve(false, 0f, 0f, -3f, -0.5f, 900f, 640f, 10f);
        Assert.Equal(10f, left.Point.X, 3);
        var top = RadarBracketLayout.Resolve(false, 0f, 0f, 0.3f, 2f, 900f, 640f, 10f);
        Assert.Equal(10f, top.Point.Y, 3);
    }

    [Fact]
    public void Zero_lateral_direction_pins_the_native_edge_midpoint()
    {
        // Native x == 0 → y = YMin (y <= 0), i.e. bottom-centre, x unchanged 0.
        var site = RadarBracketLayout.Resolve(false, 0f, 0f, 0f, 0f, 900f, 640f, 10f);
        Assert.Equal(450f, site.Point.X, 3);
        Assert.Equal(630f, site.Point.Y, 3);
    }

    [Fact]
    public void Round_radar_radius_is_native()
    {
        // radarimage.cpp:167-173: (YMax−YMin)·0.48 on the border-expanded rect.
        Assert.Equal(0.48f * (1080f - 20f), RadarBracketLayout.RoundRadarRadius(1080f), 3);
        Assert.Equal(0.48f * (720f - 20f), RadarBracketLayout.RoundRadarRadius(720f), 3);
        Assert.Equal(10f, RadarBracketLayout.NativeBorderSide);
    }

    [Fact]
    public void Round_radar_uses_the_inscribed_circle_for_both_states()
    {
        // 1000×1000, inset 10 → radius 470.4; centre (500,500).
        float radius = RadarBracketLayout.RoundRadarRadius(1000f);
        Assert.Equal(470.4f, radius, 2);

        // In front, NDC (0.5,0): centred px (250,0) — inside the circle → exact.
        var on = RadarBracketLayout.Resolve(true, 0.5f, 0f, 0.5f, 0f, 1000f, 1000f, 10f, roundRadar: true);
        Assert.Equal(RadarBracketLayout.RadarPlacement.OnScreen, on.Placement);
        Assert.Equal(750f, on.Point.X, 3);
        Assert.Equal(500f, on.Point.Y, 3);

        // NDC (0.95,0): centred px 475 > 470.4 → off the circle → pinned to it.
        var off = RadarBracketLayout.Resolve(true, 0.95f, 0f, 0.95f, 0f, 1000f, 1000f, 10f, roundRadar: true);
        Assert.Equal(RadarBracketLayout.RadarPlacement.EdgeClamped, off.Placement);
        Assert.Equal(500f + radius, off.Point.X, 2);
        Assert.Equal(500f, off.Point.Y, 2);

        // A behind-eye contact pins to the circle in its lateral direction.
        var behind = RadarBracketLayout.Resolve(false, 0f, 0f, 0f, -1f, 1000f, 1000f, 10f, roundRadar: true);
        Assert.Equal(500f, behind.Point.X, 2);
        Assert.Equal(500f + radius, behind.Point.Y, 2);
    }

    [Fact]
    public void Round_border_is_drawn_for_both_styles()
    {
        // radarimage.cpp:1130-1177: 60-segment circle, (0.5,0.5,0.5,0.25),
        // radius 0.48·(h−20), drawn whenever GetRoundRadarMode() is on. Both
        // skins hand the radar to RadarBracketRenderer.Draw(HudModel...), which
        // draws it first, beneath the contacts.
        var radar = Src(Path.Combine("Rendering", "RadarBracketRenderer.cs"));
        Assert.Contains("public void Draw(HudModel model, Viewport viewport, bool colorBlindSafe = false)", radar);
        Assert.Contains("if (model.Context.RoundRadar)", radar);
        Assert.Contains("DrawRoundBorder(viewport)", radar);
        Assert.Contains("(viewport.Height - 20f) * 0.48f", radar);
        Assert.Contains("new Color(0.5f, 0.5f, 0.5f, 0.25f)", radar);
        Assert.Contains("const int segments = 60", radar);
        // Classic and Modern both route through it.
        var game = Src("Game1.Rendering.cs");
        Assert.Contains("_radarRenderer.Draw(model, GraphicsDevice.Viewport)", game);
        var modern = Src(Path.Combine("Rendering", "ModernHudRenderer.cs"));
        Assert.Contains("_radar.Draw(model, viewport, _settings.ColorBlindSafe)", modern);
    }

    // ── 7. per-frame cost / allocation discipline ───────────────────────────

    [Fact]
    public void Lead_solve_and_green_ramp_allocate_nothing_per_frame()
    {
        var input = Input(targetPos: new SimVec(0f, 0f, -900f),
            targetVel: new SimVec(120f, 0f, 0f), speed: 700f, lifespan: 5f, chase: true);
        // Warm up every path (JIT, first-use statics).
        for (int i = 0; i < 32; i++)
        {
            var warm = ClientLead.Solve(input);
            ClientLead.CockpitGreen(warm.Time, 0f, 100f, 100f, 10f);
        }

        var watch = new Stopwatch();
        float sink = 0f;
        const int frames = 1000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        watch.Start();
        for (int i = 0; i < frames; i++)
        {
            var r = ClientLead.Solve(input);
            sink += r.Green + r.Time + r.Direction.X;
            sink += ClientLead.CockpitGreen(i * 0.1f, 0f, 100f, 100f, 10f);
        }
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(sink > 0f);   // keep the loop alive
        _out.WriteLine(FormattableString.Invariant(
            $"P804 frame cost: {watch.Elapsed.TotalNanoseconds / frames:0} ns per lead solve + green ramp ({frames} frames)"));
        Assert.True(watch.Elapsed.TotalNanoseconds / frames < 20_000,
            "a lead solve must stay far below a frame budget");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string Src(string relative)
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        return File.ReadAllText(Path.Combine(root, "src", "Allegiance.ClientCore.Game", relative));
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing " + signature);
        int open = source.IndexOf('{', start), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }
        throw new InvalidOperationException("unterminated " + signature);
    }
}
