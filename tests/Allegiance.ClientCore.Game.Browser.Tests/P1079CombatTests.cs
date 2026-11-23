using Allegiance.ClientCore.Browser.Assets;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1079CombatTests
{
    private static readonly DataHullTypeIGC Hull = default(DataHullTypeIGC) with
    { HullId = 1, Length = 24, Mass = 1, MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1], Hardpoints = [], PmEquipment = new short[8] };
    private static ServerShipUpdate Pose(short id, uint time = 100) => ServerShipUpdate.FromModel(id,
        new Ship(id, Hull, SimVec.Zero, new(0, 0, -100), Orientation.Identity), time);
    private static ClientWorld World()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = Pose(1) });
        world.Apply(new ExportShip { ShipId = 1, HullId = 1, SideId = 0 });
        return world;
    }

    [Fact]
    public void Shield_and_hull_hits_are_exclusive_sector_gated_and_expire_without_artwork()
    {
        var world = World(); var effects = new BrowserCombatEffects(); effects.BeginFrame(world, 1, .016f);
        var shield = new HullHitEntity { TargetId = 1, ClusterId = 1, Radius = 12, Position = new(0, 0, 12), Normal = new(0, 0, 1), AbsorbedByShield = true };
        effects.IngestHit(shield, world, _ => Hull, (_, radius) => (new(5, 8, 12), radius));
        effects.FinishFrame(.016f, true);
        Assert.Single(effects.FlareViews); Assert.Empty(effects.HitViews); Assert.Empty(effects.Flashes);
        Assert.Equal(new SimVec(5, 8, 12), effects.FlareViews[0].Ellipse);
        Assert.Equal(SimVec.Zero, effects.FlareViews[0].Center);
        shield.AbsorbedByShield = false;
        effects.IngestHit(shield, world, _ => Hull, (_, r) => (new(r, r, r), r));
        effects.FinishFrame(0, true);
        Assert.Equal(2, effects.HitViews.Count); Assert.Single(effects.Flashes); Assert.Single(effects.FlareViews);
        shield.ClusterId = 2;
        effects.IngestHit(shield, world, _ => throw new Exception("wrong sector"), (_, _) => throw new Exception("wrong sector"));
        effects.BeginFrame(world, 1, 3); effects.FinishFrame(3, true);
        Assert.Empty(effects.HitViews); Assert.Empty(effects.FlareViews); Assert.Empty(effects.Flashes);
    }

    [Fact]
    public void Missile_trails_survive_reduced_load_and_view_changes_clear_all_old_particles()
    {
        var world = World(); var effects = new BrowserCombatEffects(); effects.BeginFrame(world, 1, .016f);
        effects.GatherMissile(new() { MissileId = 2, ClusterId = 2, Forward = new(0, 0, -1) });
        effects.GatherMissile(new() { MissileId = 1, ClusterId = 1, Forward = new(0, 0, -1) });
        Assert.Single(effects.Missiles);
        effects.FinishFrame(.016f, false);
        Assert.NotEmpty(effects.TrailViews); Assert.Contains(effects.TrailViews, p => p.Kind == TrailParticleKind.Fire);
        world.Apply(new SetCluster { SectorId = 2, Ship = Pose(1, 101) });
        effects.BeginFrame(world, 2, .016f); effects.FinishFrame(.016f, false);
        Assert.Empty(effects.TrailViews); Assert.Empty(effects.DebrisViews); Assert.Empty(effects.Missiles);
    }

    [Fact]
    public void Continuous_damage_uses_live_hull_and_reduced_load_sheds_smoke_but_keeps_hit_feedback()
    {
        var world = World(); var ship = world.Ships[1]; var effects = new BrowserCombatEffects();
        var damaged = new Ship(1, Hull, SimVec.Zero, new(0, 0, -100), Orientation.Identity);
        damaged.SetFraction(.4f);
        typeof(ShipEntity).GetProperty("Latest")!.SetValue(ship, ServerShipUpdate.FromModel(1, damaged, 100));
        effects.BeginFrame(world, 1, .016f);
        effects.GatherDamage(ship, SimVec.Zero, new(0, 0, -100), Orientation.Identity, 12, .016f, 100, true);
        effects.FinishFrame(.016f, true); Assert.NotEmpty(effects.DebrisViews);
        effects.IngestHit(new() { ClusterId = 1, Radius = 12, Normal = new(0, 0, 1) }, world, _ => Hull, (_, r) => (new(r, r, r), r));
        effects.FinishFrame(.016f, false); Assert.Empty(effects.DebrisViews); Assert.NotEmpty(effects.HitViews);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)]
    public void Density_budget_draw_list_keeps_newest_and_never_exceeds_capacity(int capacity)
    {
        var result = new List<float>();
        BrowserCombatEffects.CopyNewest(new[] { .1f, .7f, .2f, .5f, .9f, .3f }, result, new ParticleDensityBudget(capacity), v => v);
        Assert.Equal(capacity, result.Count);
        Assert.Equal(new[] { .9f, .7f, .5f }.Take(capacity), result.OrderDescending());
    }

    [Fact]
    public void Every_explosion_category_uses_native_presets_and_only_current_sector_live_entries()
    {
        var world = World();
        int[] types = [ExplosionType.SmallShip, ExplosionType.BigShip, ExplosionType.Missile, ExplosionType.Mine, ExplosionType.SmallStation, ExplosionType.LargeStation, ExplosionType.Asteroid];
        foreach (int type in types)
            world.Apply(new ExplosionEvent { Type = type, ClusterId = 1, Position = new(type, 0, 0), Scale = 9999 });
        world.Apply(new ExplosionEvent { Type = ExplosionType.Missile, ClusterId = 2 });
        var draw = new List<ExplosionEntity>();
        BrowserCombatEffects.GatherExplosions(world, 1, Environment.TickCount64, draw);
        Assert.Equal(types.Length, draw.Count);
        foreach (var blast in draw)
        {
            var preset = ExplosionPresets.For(blast.Type);
            Assert.NotEmpty(ExplosionSampler.Sample(preset, .1f).Fireballs);
            Assert.Empty(ExplosionSampler.Sample(preset, preset.TotalDurationSeconds + 1).Fireballs);
        }
        BrowserCombatEffects.GatherExplosions(world, 1, Environment.TickCount64 + 120000, draw);
        Assert.Empty(draw);
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Own_and_remote_eject_remain_drawable_and_request_the_pod_pack_immediately(HudStyle style)
    {
        var world = World(); world.LifepodHullResolver = _ => 33;
        world.Apply(new ExportShip { ShipId = 2, HullId = 1, SideId = 1 });
        world.Apply(HeavyShipsUpdate.FromShips(101, SimVec.Zero,
            [new Ship(2, Hull, new(100, 0, 0), SimVec.Zero, Orientation.Identity)]));
        foreach (short id in new short[] { 1, 2 })
        {
            world.Apply(new KillShip { ShipId = id, BDeathCredit = false });
            world.Apply(Eject.ForSurvivor(id, new(10, 0, 0), SimVec.Zero, new(0, 0, -1)));
        }
        var scene = new BattleShipScene(new Dictionary<short, DataHullTypeIGC> { [33] = Hull with { HullId = 33, Length = 4, HabmCapabilities = DataHullTypeIGC.HabmLifepod } });
        var requests = new List<string>(); scene.Gather(world, 1, requests.Add);
        Assert.Equal(2, scene.Ships.Count); Assert.All(scene.Ships, s => Assert.Equal(33, s.HullId));
        Assert.Equal(new[] { "hull-33", "hull-33" }, requests);
        var contacts = scene.Ships.Select(s => new HudContact(HudContactKind.Ship, new(1, 1), true, 1, null, false, false)).ToArray();
        var settings = new HudSettings { Style = style };
        Assert.Equal(2, HudModelAdapter.Create(null, CameraMode.Chase, contacts: contacts).Context.Contacts.Count);
        Assert.Equal(style, settings.Style);
    }

    [Theory]
    [InlineData(4)] [InlineData(5)]
    public void Small_pod_is_inside_browser_chase_frustum_instead_of_clipped_by_old_ten_unit_plane(float length)
    {
        var camera = new ChaseCamera(350, 90, nearPlane: BrowserCombatEffects.FlightNearPlane);
        var (view, projection) = camera.Matrices(Vector3.Zero, Matrix.Identity, 1.6f, 0, out var eye, length * .5f);
        var hull = new BoundingSphere(-eye, length * .5f);
        Assert.NotEqual(ContainmentType.Disjoint, new BoundingFrustum(view * projection).Contains(hull));
        var oldCamera = new ChaseCamera(350, 90, nearPlane: 10);
        var (oldView, oldProjection) = oldCamera.Matrices(Vector3.Zero, Matrix.Identity, 1.6f, 0, out _, length * .5f);
        var oldFrustum = new BoundingFrustum(oldView * oldProjection);
        Assert.Equal(ContainmentType.Disjoint, oldFrustum.Contains(-eye));
        if (length == 4) Assert.Equal(ContainmentType.Disjoint, oldFrustum.Contains(hull));
    }
    [Theory]
    [InlineData("lens30.x", true)]
    [InlineData("other.x", false)]
    [InlineData("shader.fx", false)]
    [InlineData("script.js", false)]
    public void Only_the_exact_native_shield_cap_extends_the_visual_allowlist(string name, bool accepted)
    {
        static StagedAsset Asset(string source) => new(source, "packs/" + new string('a', 64), 1,
            new string('a', 64), "application/octet-stream", "fixture original", []);
        var resident = new StagedManifest(2, [Asset("core.igc")]);
        var manifest = new BattleManifest(1, [Asset(name)], [new("scene-combat", [name])]);
        if (accepted) manifest.Validate(resident);
        else Assert.Throws<InvalidDataException>(() => manifest.Validate(resident));
        // Hash and canonical-name validation still run for the new visual.
        Assert.Throws<InvalidDataException>(() => (manifest with
        { Assets = [Asset(name) with { Sha256 = new string('b', 64) }] }).Validate(resident));
        Assert.Throws<InvalidDataException>(() => StagedManifest.Canonical("../lens30.x"));
    }

    [Theory]
    [InlineData(3.534471f, 1.4184906f, .9733109f, 3.0875616f)] // original quizscoutv2 / hull 13
    [InlineData(64f, 51.2f, 32f, 57.6f)]
    [InlineData(194f, 155.2f, 97f, 174.6f)]
    public void Authored_shield_cap_inputs_include_native_parent_scale_before_world_space_drawing(float authoredRadius, float x, float y, float z)
    {
        const float radius = 8.5f;
        var authored = new SimVec(x, y, z);
        var shape = BrowserCombatEffects.ShieldShape(authored, authoredRadius, radius);
        Assert.Equal(radius, shape.MeshRadius);
        // Independent native RenderTargetGeo + SetRadius outer transform:
        // the unit shell's rendered per-axis extents are authored extents * scale.
        var expected = authored * (radius / authoredRadius);
        Assert.Equal(expected, shape.Ellipse);
        var apex = ShieldFlareCapMath.TransformCapPosition(new(0, 0, 1), Orientation.Identity,
            SimVec.Zero, new(0, 0, radius), shape.Ellipse, radius, shape.MeshRadius);
        Assert.Equal(-expected.Z, apex.Z, 4);
        Assert.InRange(apex.Length(), 0, radius);
        // Raw model-space inputs either compress the cap inside the hull
        // (the actual starter scout) or make it oversized. Both still submit
        // geometry, so counters alone cannot establish visible feedback.
        var old = ShieldFlareCapMath.TransformCapPosition(new(0, 0, 1), Orientation.Identity,
            SimVec.Zero, new(0, 0, radius), authored, radius, authoredRadius);
        Assert.True(Math.Abs(old.Length() - expected.Z) > expected.Z * .5f);
        Assert.Equal((new SimVec(radius, radius, radius), radius), BrowserCombatEffects.ShieldShape(default, 0, radius));
    }

    [Fact]
    public void Eject_blast_uses_previous_visible_hull_pose_and_velocity_without_a_server_explosion()
    {
        var world = World(); var effects = new BrowserCombatEffects();
        effects.BeginFrame(world, 1, .016f, 1000);
        var fighter = Hull with { HabmCapabilities = DataHullTypeIGC.HabmFighter };
        effects.ObserveShip(1, SimVec.Zero, new(0, 0, -100), fighter);
        world.LifepodHullResolver = _ => 33;
        world.Apply(new KillShip { ShipId = 1, BDeathCredit = false });
        world.Apply(Eject.ForSurvivor(1, new(20, 30, 40), new(0, 100, 0), new(0, 0, -1)));
        effects.BeginFrame(world, 1, .016f, 1016);
        var pair = Assert.Single(effects.EjectExplosions);
        Assert.Equal(1, pair.Value); Assert.Equal(ExplosionType.SmallShip, pair.Key.Type);
        Assert.Equal(SimVec.Zero, pair.Key.Position); Assert.Equal(new SimVec(0, 0, -100), pair.Key.Velocity);
        Assert.Equal(1016, pair.Key.ReceivedLocalMs); Assert.Equal(33, world.Ships[1].HullId);
        Assert.Empty(world.Explosions); // presentation-only; world/server are unchanged
        var draw = new List<ExplosionEntity>(); effects.AppendEjectExplosions(world, draw);
        Assert.Single(draw);
        effects.BeginFrame(world, 1, .016f, 1032); Assert.Single(effects.EjectExplosions);
        effects.BeginFrame(world, 1, 30, 31032); Assert.Empty(effects.EjectExplosions);
    }

    [Theory]
    [InlineData("credited")]
    [InlineData("hidden")]
    [InlineData("stale")]
    [InlineData("unknown-hull")]
    public void Eject_blast_excludes_true_deaths_hidden_models_and_stale_or_unknown_hulls(string kind)
    {
        var world = World(); var effects = new BrowserCombatEffects(); effects.BeginFrame(world, 1, .016f, 1000);
        effects.ObserveShip(1, SimVec.Zero, SimVec.Zero, kind == "unknown-hull" ? null : Hull);
        if (kind == "hidden") world.Apply(new ShipDelete { ShipId = 1, Sdr = ShipDelete.SdrLeftSector });
        if (kind == "stale") effects.BeginFrame(world, 1, .016f, 1016); // no drawn observation this frame
        world.Apply(new KillShip { ShipId = 1, BDeathCredit = kind == "credited" });
        effects.BeginFrame(world, 1, .016f, 1032);
        Assert.Empty(effects.EjectExplosions);
    }

    [Fact]
    public void An_authoritative_blast_replaces_an_overlapping_eject_cue_and_view_reset_clears_it()
    {
        var world = World(); var effects = new BrowserCombatEffects(); long now = Environment.TickCount64;
        effects.BeginFrame(world, 1, .016f, now);
        effects.ObserveShip(1, SimVec.Zero, SimVec.Zero, Hull with { HabmCapabilities = DataHullTypeIGC.HabmFighter });
        world.Apply(new KillShip { ShipId = 1, BDeathCredit = false });
        effects.BeginFrame(world, 1, .016f, now + 1); Assert.Single(effects.EjectExplosions);
        world.Apply(new ExplosionEvent { ClusterId = 1, Type = ExplosionType.SmallShip, Position = SimVec.Zero });
        var draw = new List<ExplosionEntity>(); BrowserCombatEffects.GatherExplosions(world, 1, now + 2, draw);
        effects.AppendEjectExplosions(world, draw); Assert.Single(draw);
        Assert.Same(world.Explosions[0], draw[0]);
        world.Apply(new SetCluster { SectorId = 2, Ship = Pose(1, 101) });
        effects.BeginFrame(world, 2, .016f, now + 3); Assert.Empty(effects.EjectExplosions);
    }

    [Fact]
    public void Shield_hit_uses_the_mesh_render_pose_and_preserves_the_authoritative_impact_offset()
    {
        var world = World(); var effects = new BrowserCombatEffects(); effects.BeginFrame(world, 1, .016f);
        var rendered = new SimVec(100, 20, 30); var orientation = Orientation.Identity;
        orientation.Yaw(.3f); var expectedForward = orientation.GetForward();
        effects.ObserveShip(1, rendered, SimVec.Zero, Hull, orientation);
        var hit = new HullHitEntity { TargetId = 1, ClusterId = 1, Radius = 12,
            Position = new(0, 0, 12), Normal = new(0, 0, 1), AbsorbedByShield = true };
        effects.IngestHit(hit, world, _ => Hull, (_, r) => (new(r, r, r), r));
        effects.FinishFrame(0, true);
        var flare = Assert.Single(effects.FlareViews);
        Assert.Equal(rendered, flare.Center);
        Assert.Equal(rendered + new SimVec(0, 0, 12), flare.HitWorldPos);
        Assert.Equal(new SimVec(0, 0, 12), hit.Position); // no mutation of wire/world state
        Assert.NotSame(orientation, flare.ShipOrientation);
        orientation.Yaw(1);
        Assert.Equal(expectedForward, flare.ShipOrientation.GetForward());
    }

}
