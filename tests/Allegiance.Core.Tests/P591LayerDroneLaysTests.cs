using System;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P591 — failing-first proof that a <see cref="Ship.PilotTypeLayer"/> drone
/// with a build/lay order actually LAYS its expendable at the target point and
/// is then consumed, closing the P161 deployment gap ("Actual deployment is
/// P161's known gap" — a layer drone was ORDERED to build but only parked at a
/// rock). This is the port of native <c>shipIGC.cpp:2810-2828</c>
/// (<c>c_cmdPlan == c_cidBuild</c> for a <c>c_ptLayer</c> → <c>LayExpendable</c>
/// at the plan buoy) and <c>FedSrv.CPP:13769-13863</c> (<c>LayExpendable</c>):
/// the layer flies to its <see cref="Ship.LayerBuildOrder"/> point, deploys the
/// laid expendable (<see cref="Ship.DeployMine"/> for a mine type) and is
/// consumed. On today's (pre-P591) code there is no lay flow at all — a layer
/// given a build order simply never lays (the P560 live finding: "the layer
/// drone is ORDERED to build (orderSent=true) but does NOT actually deploy"),
/// so asserting a mine appears at the lay point fails-first.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P591LayerDroneLaysTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private const float Dt = 0.05f;   // 20 Hz tick (matches server config)

    // A clean, world-free spot (no station/warp/asteroid interference) in the
    // first sector — the same trick MineCombatTests' Remote uses.
    private static readonly Vector3 Origin = new(3000f, 5400f, 7900f);
    private static readonly Vector3 LayPoint = new(3500f, 5400f, 7900f);   // 500 u away, same sector

    private sealed record Domain(
        StaticCore Core, Mission Mission, Sector Sector, LayerSite Site,
        Ship Layer, DataMineTypeIGC MineType);

    private static Domain AssembleLayerDomain()
    {
        var core = RepresentativeCore();

        // A real mine type with a usable blast radius as the laid expendable.
        // (The showcase's actual minefield layer is drone type 200 / EtidLaid 1
        // — a mine — while '.TowerAC' layers lay probes/towers.)
        var mineType = core.MineTypes.Values
            .Where(m => m.MineRadius > 0f && m.Power > 0f && m.Lifespan >= 60f)
            .OrderBy(m => m.MineRadius)
            .First();

        var site = new LayerSite();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var mission = Mission.Assemble(core, SomeMap(),
            [new SideSpec(0, "Alpha", civ), new SideSpec(1, "Bravo", civ)], site);
        mission.Stage = MissionStage.Started;
        var sector = mission.Sectors[0];

        var hull = core.HullTypes.Values.First(h => h.Length > 0f);
        var layer = new Ship(700, hull, Origin, Vector3.Zero, Orientation.Identity)
        {
            IsNpc = true,
            PilotType = Ship.PilotTypeLayer,
        };
        layer.SideId = 0;
        layer.LayerLaidExpendableTypeId = mineType.ExpendableTypeId;
        mission.PlaceShip(layer, sector);

        return new Domain(core, mission, sector, site, layer, mineType);
    }

    private static void Step(Domain d) => d.Mission.Update(d.Mission.LastUpdate + Dt);

    /// <summary>
    /// ⭐ The headline proof: a layer ordered to build at a point (native
    /// <c>c_cidBuild</c> targeting the waypoint buoy, <c>shipIGC.cpp:4050-4110</c>)
    /// flies there, lays its minefield (<c>LayExpendable</c>,
    /// <c>FedSrv.CPP:13769-13863</c>), and is consumed. Pre-P591 this never
    /// happened — the layer just parked (fails-first).
    /// </summary>
    [Fact]
    public void Ordered_layer_drone_flies_to_its_build_point_and_lays_its_minefield_then_is_consumed()
    {
        var d = AssembleLayerDomain();
        d.Layer.SetLayerBuild(new Ship.LayerBuildGoal(d.Sector.Id, LayPoint, 120f));

        // Drive the autopilot: it must fly from Origin to LayPoint (500 u) and
        // lay on arrival. Well within the mine's 600 s lifespan.
        for (int i = 0; i < 4000 && d.Site.MineCreates == 0; i++)
            Step(d);

        // 1) The minefield was actually laid, near the lay point (within the
        //    arrival radius + slack), and a Mine object now lives in the sector.
        Assert.Equal(1, d.Site.MineCreates);
        var mine = Assert.Single(d.Sector.Models.OfType<Mine>());
        float distToLayPoint = (mine.Position - LayPoint).Length();
        Assert.True(distToLayPoint < 150f, $"mine at {mine.Position} should be near the lay point {LayPoint} (dist {distToLayPoint:0})");

        // 2) The drone was consumed (native LayExpendable's "quietly kill the
        //    ship", FedSrv.CPP:13842-13860 → KillShipEvent → finalized dead).
        Assert.DoesNotContain(d.Layer, d.Sector.Models);
        Assert.True(d.Layer.Dead, "the laying drone must be consumed (dead)");
        Assert.Null(d.Layer.LayerBuildOrder);

        // 3) The mine carries the laying side so it denies the ENEMY (friendly gate).
        Assert.Equal((short)0, mine.SideId);
        Console.WriteLine($"P591-LAY: layer {d.Layer.Id} flew Origin→lay point, laid mine {mine.Id} at {mine.Position} " +
                          $"({distToLayPoint:0} from target), consumed; mine side {mine.SideId} exp {mine.MineType.ExpendableTypeId}");
    }

    /// <summary>
    /// The same flow with a PROBE (tower) laid expendable also deploys (native
    /// <c>LayExpendable</c> handles <c>OT_probeType</c> too, FedSrv.CPP:13801-13818):
    /// a tower-layer (e.g. '.TowerAC', EtidLaid 87) lays a probe, not a mine.
    /// This distinguishes the two layer kinds the showcase must choose between.
    /// </summary>
    [Fact]
    public void Ordered_layer_drone_with_a_probe_expendable_lays_a_probe_not_a_mine()
    {
        var d = AssembleLayerDomain();
        var probeType = d.Core.ProbeTypes.Values.FirstOrDefault(p => p.Lifespan >= 60f);
        Assert.True(probeType.ExpendableTypeId != 0, "representative core must have a probe type");
        d.Layer.LayerLaidExpendableTypeId = probeType.ExpendableTypeId;
        d.Layer.SetLayerBuild(new Ship.LayerBuildGoal(d.Sector.Id, LayPoint, 120f));

        for (int i = 0; i < 4000 && d.Site.ProbeCreates == 0; i++)
            Step(d);

        Assert.Equal(1, d.Site.ProbeCreates);
        var probe = Assert.Single(d.Sector.Models.OfType<Probe>());
        float distToLayPoint = (probe.Position - LayPoint).Length();
        Assert.True(distToLayPoint < 150f, $"probe at {probe.Position} should be near the lay point (dist {distToLayPoint:0})");
        Assert.DoesNotContain(d.Layer, d.Sector.Models);
        Assert.True(d.Layer.Dead);
        Console.WriteLine($"P591-LAY-PROBE: tower-layer laid probe {probe.Id} at {probe.Position} ({distToLayPoint:0} from target), consumed");
    }

    /// <summary>No build order → the layer never lays (falls back to its idle
    /// default order and parks), confirming laying is driven solely by the order.</summary>
    [Fact]
    public void Layer_without_a_build_order_never_lays()
    {
        var d = AssembleLayerDomain();
        for (int i = 0; i < 1200; i++)
            Step(d);
        Assert.Equal(0, d.Site.MineCreates);
        Assert.Equal(0, d.Site.ProbeCreates);
        Assert.Contains(d.Layer, d.Sector.Models);   // still flying/parked, not consumed
        Console.WriteLine("P591-NOORDER: layer without a build order laid nothing and was not consumed");
    }

    /// <summary>
    /// Guards the showcase's minefield-layer selection: on both 221 civs the
    /// layer-drone catalog has a MINEFIELD bucket — a layer whose laid
    /// expendable (<c>EtidLaid</c>) is a MINE type (native's
    /// <c>GetLaidExpendable()</c>, FedSrv.CPP:11523-11525) — whereas the
    /// '.TowerAC'/'.TowerSC' layers lay probes/towers. The MCP's
    /// <c>FindLayerBucket</c> must prefer the mine-laid bucket so
    /// <c>spawn_scenario … minefields</c> lays real mines. This fails-first
    /// on the pre-P591 choice (which bought '.TowerAC' — a tower layer).
    /// </summary>
    [Fact]
    public void Both_221_civs_have_a_minefield_layer_bucket_distinct_from_the_tower_layers()
    {
        var core = RepresentativeCore();
        foreach (short civId in new short[] { 25, 3 })   // Iron Coalition, Ga'Taraan
        {
            var civ = core.Civilizations[civId];
            var techs = TechTreeBitMask.FromBytes(civ.TtbmBaseTechs);
            var ga = GlobalAttributeSet.FromArray(civ.GasBaseAttributes);
            var views = BucketCatalog.Describe(core, techs, ga);

            var layerBuckets = views
                .Where(v => v.ItemType == BucketItemType.BitDroneType
                            && v.DroneType is { } dt && dt.PilotType == Ship.PilotTypeLayer)
                .ToList();

            // There is at least one MINE-laid layer bucket (the minefield).
            var mineLayers = layerBuckets.Where(v => core.MineTypes.ContainsKey(v.DroneType!.Value.EtidLaid)).ToList();
            Assert.NotEmpty(mineLayers);
            foreach (var ml in mineLayers)
                Assert.True(core.MineTypes.ContainsKey(ml.DroneType!.Value.EtidLaid),
                    $"minefield layer '{ml.Name}' EtidLaid {ml.DroneType.Value.EtidLaid} must be a mine type");

            // The '.TowerAC' layer is a PROBE (tower) layer — NOT a minefield.
            var towerAc = layerBuckets.FirstOrDefault(v => v.Name.Contains("TowerAC", StringComparison.OrdinalIgnoreCase));
            if (towerAc is not null)
                Assert.False(core.MineTypes.ContainsKey(towerAc.DroneType!.Value.EtidLaid),
                    $"'.TowerAC' (EtidLaid {towerAc.DroneType.Value.EtidLaid}) must be a tower/probe layer, not a minefield");

            // Sanity: the drill-down is complete (no other layer kind).
            Assert.All(layerBuckets, v =>
            {
                Assert.True(core.MineTypes.ContainsKey(v.DroneType!.Value.EtidLaid)
                            || core.ProbeTypes.ContainsKey(v.DroneType.Value.EtidLaid),
                    $"layer '{v.Name}' EtidLaid {v.DroneType.Value.EtidLaid} must be a mine or probe type");
            });
        }
        Console.WriteLine("P591-BUCKET: both 221 civs carry a minefield layer (drone type 200, EtidLaid 1, icon 'minefield') distinct from the '.TowerAC' tower layers");
    }

    private static MissionMap SomeMap()
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count > 0) return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    }

    /// <summary>A site that counts mine/probe CREATION and finalizes a consumed
    /// layer drone the way the server's death path does
    /// (<c>FinalizeDeadShip</c>, Server.Combat.cs:1962-1967: SetFraction(0) +
    /// sector.Remove + Sector=null). Everything else falls through to the
    /// interface default (no-op), so the domain test stays hermetic.</summary>
    private sealed class LayerSite : IIgcSite
    {
        public int MineCreates { get; private set; }
        public int ProbeCreates { get; private set; }
        public void MineCreatedEvent(Mine m) => MineCreates++;
        public void ProbeCreatedEvent(Probe p) => ProbeCreates++;
        public void KillShipEvent(double now, IShipIgc ship, IModelIgc? launcher, float amount, Vector3 p1, Vector3 p2)
        {
            if (ship is not Ship s) return;
            s.SetFraction(0f);
            if (s.Sector is { } sec) sec.Remove(s);
            s.Sector = null;
        }
    }
}
