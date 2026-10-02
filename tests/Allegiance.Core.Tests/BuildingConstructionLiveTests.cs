using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P163 — the building-effect mechanic, Stage 1 of the builder/station-
/// construction arc, proven in the same lightweight live-loopback style as the
/// StationCombat/AsteroidCombat suites (real <c>PCore001.igc</c> static core, a
/// real mission on a real map, a <see cref="NullIgcSite"/> — no sockets, no
/// server, no VM). Two real scenarios exercise the whole lifecycle:
/// <list type="number">
/// <item><b>Successful build</b> — a Builder ship starts a build on a real
/// asteroid with a real station type; the mission is ticked through the full
/// 40-second completion deadline; the asteroid is gone from
/// <see cref="Sector.Mineable"/>, a new <see cref="Station"/> of the right type
/// exists at the asteroid's position in <see cref="Sector.StationRuntimes"/> and
/// <see cref="Sector.Stations"/>, and the Builder ship is gone from the sector's
/// live model list (the native self-sacrifice).</item>
/// <item><b>Interrupted build</b> — a build is started, the Builder is killed
/// partway (its <c>Fraction</c> dropped to 0 via a lethal
/// <see cref="Ship.ReceiveDamage"/>, the port's real ship-death path), and the
/// mission is ticked past what would have been completion: the asteroid is still
/// there unchanged and no new station was created (the native abort, without the
/// Stage-2 <c>BUILDINGEFFECT_DESTROYED</c> wire).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>The build is started explicitly via
/// <see cref="Sector.StartBuildingEffect"/> — the test-authored trigger point
/// that Stage 3's Builder AI decision layer will eventually call instead. The
/// timing is asserted against <see cref="BuildingEffect.BuildDuration"/> = 40
/// (the native <c>c_dtEnvelope+c_dtGrow+c_dtOpaque+c_dtShrink</c>,
/// <c>buildingeffectigc.h:19-22</c>). All assertions are data-derived from the
/// real core; nothing is hardcoded except the synthetic asteroid/rock geometry
/// the injected test builds.</para>
/// </remarks>
public sealed class BuildingConstructionLiveTests
{
    private static StaticCore Core()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        var file = IgcReader.ParseFile(path);
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
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

    private static Mission NewStartedMission(IIgcSite site)
    {
        var core = Core();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, SomeMap(), [new SideSpec(0, "Team 1", civ)], site);
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>A synthetic rock the building effect transforms. Carries the
    /// given buildability capability mask (passed the station type's AabmBuild,
    /// so the P280 construction-type gate accepts it); not He3-minable so the
    /// mining pass ignores it entirely. Identical record shape to the
    /// synthetic rocks MiningTests inject.</summary>
    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, short radius, short sectorId, short aabmCap) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: 0f, OreMax: 0f,
        AabmCapabilities: aabmCap,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    /// <summary>A real station type from the core to build with — the direct
    /// input this stage takes (Stage 3's build-ability discovery is out of
    /// scope). Any real type works for the transformation.</summary>
    private static DataStationTypeIGC SomeStationType(StaticCore core) =>
        core.StationTypes.Values.First();

    /// <summary>A real station type with a DIRECT successor, plus its successor,
    /// chosen so the P488 successor condition is falsifiable in both directions:
    /// under <c>sideDev = Empty</c> the successor is NOT buildable
    /// (succ.RequiredTechs ⊄ this.RequiredTechs), and under all-FF it is —
    /// so the "not researched" and "researched" outcomes are both deterministic.</summary>
    private static (DataStationTypeIGC Base, DataStationTypeIGC Succ) UpgradableStationPair(StaticCore core)
    {
        var all = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());
        foreach (var st in core.StationTypes.Values.OrderBy(s => s.StationTypeId))
        {
            if (st.SuccessorStationTypeId < 0) continue;
            if (!core.StationTypes.TryGetValue(st.SuccessorStationTypeId, out var succ)) continue;
            bool unResearched = !TechTreeBitMask.RequiredTechsBuyable(succ.TtbmRequired, st.TtbmRequired, TechTreeBitMask.Empty);
            bool researched = TechTreeBitMask.RequiredTechsBuyable(succ.TtbmRequired, st.TtbmRequired, all);
            if (unResearched && researched)
                return (st, succ);
        }
        throw new InvalidOperationException("no falsifiable station-successor pair in PCore001");
    }

    /// <summary>
    /// ⭐ P488 — the native "allow for an upgrade to the station being available"
    /// (FedSrv.CPP:11380: <c>pstationtype = pstationtype->GetSuccessorStationType(pside)</c>).
    /// A constructor ordered to build a station type the side has RESEARCHED the
    /// upgrade for builds the <b>successor</b> station type, not the base one.
    /// Red before P488 (unfixed code always builds the requested type).
    /// </summary>
    [Fact]
    public void Completed_build_upgrades_to_the_successor_station_type_when_the_side_has_outteched_it()
    {
        var core = Core();
        var (baseType, succType) = UpgradableStationPair(core);
        var mission = NewStartedMission(new NullIgcSite());
        var side = mission.Sides[0];
        side.DevelopmentTechs = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray()); // side researched the upgrade
        var sector = mission.Sectors[0];

        var rockPos = new Vector3(661f, 903f, 742f);
        var rock = new Asteroid(MkAsteroid(9701, rockPos, 60, sector.Id, baseType.AabmBuild));
        mission.PlaceAsteroid(rock, sector);

        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var builder = new Ship(9651, hull, new Vector3(-550f, -555f, -560f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(builder, sector);

        // Ordered to build the BASE type; the side has researched the successor.
        sector.StartBuildingEffect(builder, rock, baseType, mission.LastUpdate);
        mission.Update(mission.LastUpdate + BuildingEffect.BuildDuration + 1.0);

        var built = sector.StationRuntimes.Where(s => s.SideId == 0).Single(s => s.Data.Position is { Length: 3 } p && PositionsMatch(p, rockPos));
        Assert.Equal(succType.StationTypeId, built.Type.StationTypeId);       // successor type on the runtime
        Assert.Equal(succType.StationTypeId, built.Data.StationTypeId);       // and the wire record
        Assert.Equal(succType.Name, built.Data.Name);
        Console.WriteLine($"P488-STATION-SUCCESSOR: side 0 researched -> base '{baseType.Name}' (type {baseType.StationTypeId}) built as successor '{succType.Name}' (type {succType.StationTypeId})");
    }

    /// <summary>⭐ P488 — the negative half: a side that has NOT researched the
    /// upgrade (empty dev techs) builds the requested BASE station type, exactly
    /// as native (the successor leg of GetSuccessorStationType is skipped,
    /// stationtypeigc.h:185-195).</summary>
    [Fact]
    public void Completed_build_keeps_the_base_station_type_when_the_successor_is_not_researched()
    {
        var core = Core();
        var (baseType, _) = UpgradableStationPair(core);
        var mission = NewStartedMission(new NullIgcSite());
        mission.Sides[0].DevelopmentTechs = TechTreeBitMask.Empty;   // NOT researched
        var sector = mission.Sectors[0];

        var rockPos = new Vector3(773f, 824f, 911f);
        var rock = new Asteroid(MkAsteroid(9801, rockPos, 60, sector.Id, baseType.AabmBuild));
        mission.PlaceAsteroid(rock, sector);

        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var builder = new Ship(9751, hull, new Vector3(-660f, -664f, -666f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(builder, sector);

        sector.StartBuildingEffect(builder, rock, baseType, mission.LastUpdate);
        mission.Update(mission.LastUpdate + BuildingEffect.BuildDuration + 1.0);

        var built = sector.StationRuntimes.Where(s => s.SideId == 0).Single(s => s.Data.Position is { Length: 3 } p && PositionsMatch(p, rockPos));
        Assert.Equal(baseType.StationTypeId, built.Type.StationTypeId);   // unchanged: the requested base type
        Console.WriteLine($"P488-STATION-NO-SUCCESSOR: side 0 NOT researched -> built the requested '{baseType.Name}' (type {baseType.StationTypeId}), no upgrade");
    }

    private static bool PositionsMatch(float[] a, Vector3 b) =>
        a is { Length: 3 } &&
        Math.Abs(a[0] - b.X) < 0.01f && Math.Abs(a[1] - b.Y) < 0.01f && Math.Abs(a[2] - b.Z) < 0.01f;

    /// <summary>
    /// Kill a ship the way the port's combat path kills one: a lethal
    /// <see cref="Ship.ReceiveDamage"/> that drives its hull
    /// <see cref="Ship.Fraction"/> to 0 (marking <see cref="Ship.Dead"/>).
    /// Uses a real projectile damage type whose damage-matrix cell for the
    /// ship's hull defense type is strictly positive (so the damage actually
    /// lands), dealt at <c>float.MaxValue</c> so the hull fraction is guaranteed
    /// to bottom out regardless of the exact decoded constant.
    /// </summary>
    private static void KillShip(Ship ship, Mission mission, StaticCore core)
    {
        var dtm = core.Constants.DamageConstants;
        foreach (var pt in core.ProjectileTypes.Values)
        {
            if (pt.Power <= 0f) continue;
            int dmg = pt.DamageType, def = ship.HullType.DefenseType;
            if (dmg < 0 || dmg >= DataConstantsIGC.DmgidMax || def < 0 || def >= DataConstantsIGC.DefidMax) continue;
            if (dtm[dmg][def] > 0f)
            {
                ship.ReceiveDamage(pt.DamageType, float.MaxValue, mission.LastUpdate, ship.Position, ship.Position, launcher: null);
                return;
            }
        }
        throw new InvalidOperationException($"no projectile damage type with a positive cell for hull defense type {ship.HullType.DefenseType}");
    }

    /// <summary>⭐ A completed build transforms the asteroid into a real station
    /// of the Builder's carried type at the rock's position and consumes the
    /// Builder — the native self-sacrificing transaction
    /// (<c>buildingeffectigc.h:312-353</c>), end to end through the mission
    /// tick.</summary>
    [Fact]
    public void Completed_build_replaces_asteroid_with_station_and_consumes_builder()
    {
        var core = Core();
        var mission = NewStartedMission(new NullIgcSite());
        var sector = mission.Sectors[0];
        var stationType = SomeStationType(core);

        var rockPos = new Vector3(511f, 823f, 604f);          // distinctive, away from map geometry
        var rock = new Asteroid(MkAsteroid(7701, rockPos, 60, sector.Id, stationType.AabmBuild));
        mission.PlaceAsteroid(rock, sector);
        Assert.Contains(rock, sector.Mineable);

        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var builder = new Ship(5601, hull, new Vector3(-200f, -200f, -200f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(builder, sector);
        Assert.Contains(builder, sector.Models);

        var preExistingStationIds = new HashSet<short>(sector.StationRuntimes.Select(s => s.Data.StationId));

        // ── start the build at the current sim-time ─────────────────────────
        double start = mission.LastUpdate;
        var effect = sector.StartBuildingEffect(builder, rock, stationType, start);

        Assert.Single(sector.BuildingEffects);
        Assert.Equal(BuildingEffect.BuildDuration, effect.CompletionTime - start, 6);   // 40 s (buildingeffectigc.h:19-22)
        Assert.Equal(40.0, effect.CompletionTime - start, 6);

        // ── advance just shy of the 40 s deadline: still in progress ────────
        mission.Update(mission.LastUpdate + 39.0);
        Assert.Contains(rock, sector.Mineable);             // rock untouched
        Assert.Contains(builder, sector.Models);            // builder alive & in sector
        Assert.Contains(effect, sector.BuildingEffects);    // build still pending

        // ── cross the deadline: now = start + 41 ────────────────────────────
        mission.Update(mission.LastUpdate + 2.0);

        // Asteroid gone.
        Assert.DoesNotContain(rock, sector.Mineable);
        // Builder consumed (removed from the sector's live list).
        Assert.DoesNotContain(builder, sector.Models);
        // Effect cleared.
        Assert.Empty(sector.BuildingEffects);

        // Exactly one NEW station (the built one) of the right type/side at the
        // rock's position, in both the runtime and static lists.
        var built = sector.StationRuntimes.Where(s => !preExistingStationIds.Contains(s.Data.StationId)).ToList();
        var builtStation = Assert.Single(built);
        Assert.Equal(stationType.StationTypeId, builtStation.Type.StationTypeId);
        Assert.Equal(stationType.StationTypeId, builtStation.Data.StationTypeId);
        Assert.Equal((short)0, builtStation.SideId);                       // the Builder's side (SideId 0)
        Assert.Equal(sector.Id, builtStation.Data.ClusterId);
        Assert.Equal(stationType.Name, builtStation.Data.Name);
        Assert.True(PositionsMatch(builtStation.Data.Position, rockPos), "built station must sit at the asteroid's position");
        Assert.Contains(builtStation.Data, sector.Stations);               // static record registered too (AttachStation)

        Console.WriteLine($"P163-LIVE-SUCCESS: build completed at +40s → rock {rock.Data.AsteroidId} gone, station {builtStation.Data.StationId} '{builtStation.Data.Name}' (type {stationType.StationTypeId}, side 0) at {rockPos}, builder {builder.Id} consumed");
    }

    /// <summary>⭐ An interrupted build (Builder killed mid-build) aborts
    /// cleanly: the asteroid is untouched and no station is created, even after
    /// the tick passes what would have been completion — the interruption path
    /// of native <c>BuilderTerminated()</c> (<c>buildingeffectigc.h:294-297</c>),
    /// minus the Stage-2 wire.</summary>
    [Fact]
    public void Interrupted_build_aborts_without_touching_asteroid_or_creating_station()
    {
        var core = Core();
        var mission = NewStartedMission(new NullIgcSite());
        var sector = mission.Sectors[0];
        var stationType = SomeStationType(core);

        var rockPos = new Vector3(688f, 944f, 713f);
        var rock = new Asteroid(MkAsteroid(7801, rockPos, 60, sector.Id, stationType.AabmBuild));
        mission.PlaceAsteroid(rock, sector);
        Assert.Contains(rock, sector.Mineable);

        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var builder = new Ship(5701, hull, new Vector3(-300f, -300f, -300f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(builder, sector);
        int preExistingStationCount = sector.StationRuntimes.Count;

        sector.StartBuildingEffect(builder, rock, stationType, mission.LastUpdate);
        Assert.Single(sector.BuildingEffects);

        // Advance partway into the build, then kill the Builder.
        mission.Update(mission.LastUpdate + 10.0);
        Assert.False(builder.Dead);
        Assert.Contains(rock, sector.Mineable);

        KillShip(builder, mission, core);
        Assert.True(builder.Dead);                                         // the interruption trigger (Ship.cs:118)

        // Tick well past what would have been the 40 s completion.
        mission.Update(mission.LastUpdate + 40.0);                          // now = 50 > 40

        // Abort, cleanly: no station created, asteroid untouched, effect cleared.
        Assert.Empty(sector.BuildingEffects);
        Assert.Contains(rock, sector.Mineable);                            // rock still there, unchanged
        Assert.Equal(preExistingStationCount, sector.StationRuntimes.Count); // NO station added at all
        Assert.DoesNotContain(sector.StationRuntimes, s => PositionsMatch(s.Data.Position, rockPos));
        Assert.DoesNotContain(sector.Stations, sd => PositionsMatch(sd.Position, rockPos));

        Console.WriteLine("P163-LIVE-INTERRUPT: builder killed at +10s → build aborted, asteroid untouched, no station created past +40s");
    }

    /// <summary>⭐ P280 — the construction entry point itself rejects a build
    /// on an asteroid of the WRONG type for the station: <c>StartBuildingEffect</c>
    /// throws (the port of native's c_cidBuild legality gate, shipIGC.h:1695-1707),
    /// no effect registers and the rock is untouched.</summary>
    [Fact]
    public void StartBuildingEffect_rejects_asteroid_of_wrong_type()
    {
        var core = Core();
        var mission = NewStartedMission(new NullIgcSite());
        var sector = mission.Sectors[0];
        var stationType = SomeStationType(core);

        // A rock of the WRONG type — a capability mask sharing no bit with the
        // station's AabmBuild (Outpost 0x08 vs a carbon 0x40 special rock).
        short wrongCap = stationType.AabmBuild switch
        {
            DataAsteroidIGC.AabmBuildable => (short)(DataAsteroidIGC.AabmSpecial << 2),
            (short)(DataAsteroidIGC.AabmSpecial << 2) => DataAsteroidIGC.AabmBuildable,
            _ => DataAsteroidIGC.AabmBuildable,
        };
        Assert.Equal(0, stationType.AabmBuild & wrongCap);

        var rockPos = new Vector3(433f, 901f, 552f);
        var rock = new Asteroid(MkAsteroid(8801, rockPos, 60, sector.Id, wrongCap));
        mission.PlaceAsteroid(rock, sector);
        Assert.Contains(rock, sector.Mineable);

        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var builder = new Ship(9101, hull, new Vector3(-400f, -420f, -430f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(builder, sector);

        // The build on the wrong-type rock is refused outright — no effect.
        Assert.Throws<InvalidOperationException>(
            () => sector.StartBuildingEffect(builder, rock, stationType, mission.LastUpdate));
        Assert.Empty(sector.BuildingEffects);           // nothing registered
        Assert.Contains(rock, sector.Mineable);         // rock untouched
        Assert.False(builder.IsBuilding);               // builder not committed

        Console.WriteLine($"P280-REJECT-DIRECT: StartBuildingEffect threw for '{stationType.Name}' (AabmBuild 0x{stationType.AabmBuild:x}) on wrong-type rock {rock.Data.AsteroidId} (cap 0x{rock.Data.AabmCapabilities:x}) — rejected, no effect");
    }
}
