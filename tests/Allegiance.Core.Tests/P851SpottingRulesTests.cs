using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P851 — the five behavioural branches of <c>ThingSiteImpl</c> /
/// <c>CmissionIGC</c> that the event-driven-mission-rules batch (SIM-0823 …
/// SIM-0834) was missing:
/// <list type="number">
/// <item><b>First-sight score credits</b> — native books them inside
/// <c>UpdateSideVisibility</c>'s scanner loop: <c>AddTargetSpot</c> for a
/// high-value enemy hull (<c>FedSrv.CPP:10952-10982</c>), <c>SpotWarp</c> /
/// <c>SpotSpecialAsteroid</c> for an aleph / special rock
/// (<c>FedSrv.CPP:11103-11111</c>), raised through
/// <see cref="IIgcSite.SpotterCreditEvent"/> on the same false→true edge.</item>
/// <item><b>High-value 50 s latch</b> — <c>MarkPreviouslySpotted</c> /
/// <c>RecentlySpotted</c> (<c>shipIGC.h:1887-1895</c>), stamped on the target
/// "no matter by whom" so a second spotter inside the window earns nothing.</item>
/// <item><b>HandleNewEnemyCluster</b> — <c>CsideIGC::HandleNewEnemyCluster</c>
/// (<c>Igc/sideigc.cpp:361-419</c>): a side's miners drop orders that point into
/// a cluster the moment one of that side's scanners first spots a station
/// there.</item>
/// <item><b>Docked / boarded ships are not participants</b> — native removes a
/// docked ship from its cluster (<c>stationIGC.cpp:282-285</c>) and rebuilds a
/// boarded ship's ThingSite without <c>c_mtSeenBySide</c>
/// (<c>shipIGC.cpp:3632-3643</c>), so it is neither a target nor a scanner; the
/// sweep now reconciles it hidden for every non-own, non-allied side
/// (<c>ThingSiteImpl::SetCluster</c>, FedSrv.CPP:10711-10728).</item>
/// <item><b>GetSeenSides flag bypass</b> — a flag/artifact-carrying builder is
/// seen by any side with a scanner in the sector
/// (<c>Igc/missionigc.cpp:3658-3681</c> → <c>shipIGC.h:351-369</c>).</item>
/// </list>
/// Real corpus hulls/scanners, deterministic geometry (the same "clean base"
/// noise control <see cref="CrossSideVisibilitySweepTests"/> uses).
/// </summary>
public sealed class P851SpottingRulesTests
{
    private const short Side0 = 0;
    private const short Side1 = 1;

    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    private static DataHullTypeIGC Hull(string modelName) =>
        PayloadDecodeTests.Hulls().First(h => h.V.ModelName == modelName).V;

    private static readonly DataHullTypeIGC ScannerHull = Hull("faohshcar");   // ScannerRange 3600, Length 300
    private static readonly DataHullTypeIGC TargetHull = Hull("dreghpod");     // tiny, signature 0.5

    /// <summary>A started two-side mission over a real map, bound to a recording site.</summary>
    private static Mission NewStartedMission(RecordingIgcSite site)
    {
        var civs = RepresentativeCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId).Take(2).ToList();
        IReadOnlyList<SideSpec> specs =
            [new(Side0, "Alpha", civs[0].CivilizationId), new(Side1, "Bravo", civs[1].CivilizationId)];
        var mission = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, specs, site);
        mission.Stage = MissionStage.Started;
        return mission;
    }

    /// <summary>A sector plus a base point 8000+ units beyond every real asteroid
    /// and station in it, so only the test's own scanners can reach the base.</summary>
    private static (Sector Sector, Vector3 Base) CleanBase(Mission m, float clearance = 8000f)
    {
        foreach (var sector in m.Sectors)
        {
            var content = new List<Vector3>();
            foreach (var a in sector.Asteroids)
                if (a.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));
            foreach (var st in sector.Stations)
                if (st.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));

            float cy = content.Count == 0 ? 0f : content.Average(v => v.Y);
            float cz = content.Count == 0 ? 0f : content.Average(v => v.Z);
            float maxX = content.Count == 0 ? 0f : content.Max(v => v.X);
            return (sector, new Vector3(maxX + clearance, cy, cz));
        }
        throw new InvalidOperationException("no sector available in corpus map");
    }

    /// <summary>
    /// A real ship whose model clock already sits at t=100 s. The clock matters for
    /// the high-value latch: native seeds <c>m_timePreviouslySpotted = 0</c>
    /// (<c>shipIGC.cpp:200</c>) and tests <c>GetMyLastUpdate() &lt;
    /// m_timePreviouslySpotted + 50.0f</c> (<c>shipIGC.h:1892-1895</c>), so NO
    /// high-value credit is bookable in a mission's first 50 seconds — a native
    /// quirk the port reproduces. Tests therefore run "after the first 50 s".
    /// </summary>
    private static Ship NewShip(short id, DataHullTypeIGC hull, short side, Vector3 pos) =>
        new(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = side };

    /// <summary>The mission clock the tests run at: past the first 50 s, so the
    /// native high-value latch window (<c>shipIGC.h:1892-1895</c>) is open.</summary>
    private const double MissionClock = 100;

    /// <summary><see cref="Mission.PlaceShip"/> reseeds a ship's model clock to the
    /// mission's <see cref="Mission.LastUpdate"/> (0 here), so stamp the test clock
    /// AFTER placement.</summary>
    private static void Place(Mission mission, Sector sector, Ship ship)
    {
        mission.PlaceShip(ship, sector);
        ship.InitClock(MissionClock);
    }

    private static List<string> Credits(RecordingIgcSite site) =>
        site.Calls.Where(c => c.StartsWith(nameof(IIgcSite.SpotterCreditEvent), StringComparison.Ordinal)).ToList();

    // ── 1. high-value target spot + the 50 s latch ─────────────────────────

    /// <summary>
    /// A side-0 <b>player</b> ship whose scanner first sees a side-1 AI miner books
    /// exactly one <see cref="SpotCredit.Target"/> credit — native
    /// <c>FedSrv.CPP:10952-10982</c>. The miner's own thing-site latch then blocks a
    /// second credit inside the 50 s window even for a DIFFERENT spotter, and
    /// blocks it again when the same contact is re-acquired after a break
    /// ("no matter by whom", <c>FedSrv.CPP:10979-10981</c>).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void High_value_first_sight_credits_the_player_once_per_50s_window()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var mission = NewStartedMission(site);
        var (sector, basePos) = CleanBase(mission);

        var scanner = NewShip(9701, ScannerHull, Side0, basePos);
        var secondScanner = NewShip(9702, ScannerHull, Side0, basePos + new Vector3(0f, 400f, 0f));
        var miner = new Ship(9703, TargetHull, basePos + new Vector3(300f, 0f, 0f), Vector3.Zero, Orientation.Identity)
        { SideId = Side1, IsNpc = true, PilotType = Ship.PilotTypeMiner };
        Place(mission, sector, scanner);
        Place(mission, sector, secondScanner);
        Place(mission, sector, miner);

        sector.ResolveCrossSideVisibility();

        Assert.True(miner.SeenBySide(Side0), "the player scanner must spot the miner first (precondition)");
        var credits = Credits(site);
        Assert.Single(credits);
        Assert.Equal($"SpotterCreditEvent(Ship, {nameof(SpotCredit.Target)})", credits[0]);
        Assert.True(miner.RecentlySpotted(), "native stamps m_timePreviouslySpotted on every high-value first sight");

        // A second spotting player inside the window earns nothing: the latch is
        // on the TARGET, not on the (side, spotter) pair.
        miner.Position = basePos + new Vector3(50_000f, 0f, 0f);
        sector.ResolveCrossSideVisibility();
        Assert.False(miner.SeenBySide(Side0), "moved far out of range (precondition for a fresh edge)");
        miner.Position = basePos + new Vector3(0f, 0f, 200f);   // now only the SECOND scanner can see it
        sector.ResolveCrossSideVisibility();
        Assert.True(miner.SeenBySide(Side0));
        Assert.Single(Credits(site));   // still one — RecentlySpotted() suppressed the re-credit
    }

    /// <summary>A fighter first sight is <b>not</b> a high-value target
    /// (<c>GetAssetMask</c> gives <c>c_amEnemyFighter</c>, outside native's
    /// <c>hvMask</c>, <c>FedSrv.CPP:10957-10959</c>) — no credit, and no latch.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ordinary_fighter_first_sight_books_no_target_credit()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var mission = NewStartedMission(site);
        var (sector, basePos) = CleanBase(mission);

        var scanner = NewShip(9711, ScannerHull, Side0, basePos);
        var fighter = new Ship(9712, TargetHull, basePos + new Vector3(300f, 0f, 0f), Vector3.Zero, Orientation.Identity)
        { SideId = Side1, PilotType = Ship.PilotTypePlayer };
        Place(mission, sector, scanner);
        Place(mission, sector, fighter);

        sector.ResolveCrossSideVisibility();

        Assert.True(fighter.SeenBySide(Side0));
        Assert.Empty(Credits(site));
        Assert.False(fighter.RecentlySpotted(), "a non-high-value hull is never stamped");
    }

    // ── 2. aleph / special asteroid spot credits ───────────────────────────

    /// <summary>
    /// Native's <c>if (bSent) { ... if (otModel == OT_warp) ppso->SpotWarp(); ... }</c>
    /// (<c>FedSrv.CPP:11103-11111</c>): a player <b>ship</b> that first spots an
    /// aleph or a special (non-plain-buildable) asteroid books the matching
    /// counter; an ordinary buildable rock books nothing.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Aleph_and_special_asteroid_first_sights_credit_the_spotting_ship()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var mission = NewStartedMission(site);
        var (sector, basePos) = CleanBase(mission);

        var scanner = NewShip(9721, ScannerHull, Side0, basePos);
        Place(mission, sector, scanner);
        sector.ResolveCrossSideVisibility();
        site.Reset();

        // An aleph dropped right on the scanner: the warp model carries
        // c_mtSeenBySide|c_mtPredictable and a signature that puts it in range.
        var destination = mission.Sectors.First(s => s != sector);
        sector.AttachWarp(new ResolvedWarp(new DataWarpIGC(
            WarpId: 9721, DestinationId: 9722, Radius: 200,
            TextureName: "", IconName: "", Name: "TESTALEPH",
            Position: [basePos.X + 100f, basePos.Y, basePos.Z],
            Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            Signature: 1f, ClusterId: sector.Id), destination));
        var warp = sector.Warps.Last();
        sector.ResolveCrossSideVisibility();
        Assert.True(warp.SeenBySide(Side0), "the scanner must see the aleph (precondition)");
        Assert.Contains($"SpotterCreditEvent(Ship, {nameof(SpotCredit.Warp)})", Credits(site));

        // A special rock (any capability bit besides c_aabmBuildable) books the
        // asteroid counter; a plain buildable rock books nothing.
        site.Reset();
        PlaceRock(sector, basePos + new Vector3(200f, 0f, 0f), id: 9722, aabm: DataAsteroidIGC.AabmSpecial);
        sector.ResolveCrossSideVisibility();
        Assert.Contains($"SpotterCreditEvent(Ship, {nameof(SpotCredit.SpecialAsteroid)})", Credits(site));

        site.Reset();
        PlaceRock(sector, basePos + new Vector3(-200f, 0f, 0f), id: 9723, aabm: DataAsteroidIGC.AabmBuildable);
        sector.ResolveCrossSideVisibility();
        Assert.DoesNotContain($"SpotterCreditEvent(Ship, {nameof(SpotCredit.SpecialAsteroid)})", Credits(site));
    }

    private static void PlaceRock(Sector sector, Vector3 position, short id, short aabm) =>
        sector.Mission!.PlaceAsteroid(new Asteroid(new DataAsteroidIGC(
            Signature: 1.0f,
            Position: [position.X, position.Y, position.Z],
            Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            Ore: 0f, OreMax: 0f, AabmCapabilities: aabm, AsteroidId: id,
            HitPoints: 1000f, Radius: 40, ModelName: "m2xspiderw.igc",
            TextureName: "", IconName: "", ClusterId: sector.Id, Name: $"ROCK{id}", Fraction: 0)),
            sector);

    // ── 3. HandleNewEnemyCluster ──────────────────────────────────────────

    /// <summary>
    /// <c>CsideIGC::HandleNewEnemyCluster</c> (<c>Igc/sideigc.cpp:361-419</c>),
    /// reached from the station branch of <c>UpdateSideVisibility</c>
    /// (<c>FedSrv.CPP:11087</c>): the tick a side's scanner first spots a station,
    /// that side's miners drop every order whose target sits in the newly
    /// discovered cluster — the queued slot always, the plan slot when the miner
    /// is elsewhere, and the accepted slot when both are bad. Orders into other
    /// clusters are untouched.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Spotting_a_station_makes_the_seeing_sides_miners_abandon_that_cluster()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var mission = NewStartedMission(site);
        var (sector, basePos) = CleanBase(mission);
        var otherSector = mission.Sectors.First(s => s != sector);

        // A side-1 station where the scanner can see it.
        var station = SyntheticStation(sector, basePos + new Vector3(400f, 0f, 0f), id: 9731, Side1);
        mission.PlaceStation(station, sector);

        var scanner = NewShip(9732, ScannerHull, Side0, basePos);
        Place(mission, sector, scanner);

        // The miner sits in ANOTHER sector with a plan order into the station's
        // cluster (native's `else` branch: plan bad, accepted fine).
        var miner = new Ship(9733, TargetHull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity)
        { SideId = Side0, IsNpc = true, PilotType = Ship.PilotTypeMiner };
        Place(mission, otherSector, miner);
        var planStation = station.Data;
        miner.PlanTarget = planStation;
        miner.PlanCommandId = Allegiance.Core.Wire.OrderChange.CidGoto;
        // …and a queued order into the same cluster, which native always clears.
        miner.QueuedTarget = planStation;
        miner.QueuedCommandId = Allegiance.Core.Wire.OrderChange.CidGoto;

        // A second miner with a plan order into an UNRELATED cluster: untouched.
        var farStation = mission.Sectors.SelectMany(s => s.StationRuntimes).FirstOrDefault(s => s.Data.ClusterId != sector.Id);
        var untouched = new Ship(9734, TargetHull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity)
        { SideId = Side0, IsNpc = true, PilotType = Ship.PilotTypeMiner };
        Place(mission, otherSector, untouched);
        short? untouchedPlanCluster = null;
        if (farStation is not null)
        {
            untouched.PlanTarget = farStation.Data;
            untouchedPlanCluster = farStation.Data.ClusterId;
        }

        sector.ResolveCrossSideVisibility();

        Assert.True(station.SeenBySide(Side0), "the station must be spotted first (precondition)");
        Assert.Null(miner.PlanTarget);
        Assert.Equal(Allegiance.Core.Wire.OrderChange.CidNone, miner.PlanCommandId);
        Assert.Null(miner.QueuedTarget);
        Assert.Equal(Allegiance.Core.Wire.OrderChange.CidNone, miner.QueuedCommandId);
        if (untouchedPlanCluster is not null)
            Assert.Equal(untouchedPlanCluster, untouched.PlanTarget?.ClusterId);
    }

    /// <summary>A runtime station of a resolved corpus type, placed where the
    /// scanner can see it (the sweep needs a real type for radius/scanner range).</summary>
    private static Station SyntheticStation(Sector sector, Vector3 position, short id, short side)
    {
        var type = sector.Mission!.Core.StationTypes
            .OrderBy(kv => kv.Key).First().Value;
        return new Station(new DataStationIGC(
            Position: [position.X, position.Y, position.Z],
            Forward: [1f, 0f, 0f], Up: [0f, 1f, 0f], Rotation: [0f, 0f, 0f, 1f],
            ClusterId: sector.Id, SideId: side, StationId: id,
            StationTypeId: type.StationTypeId,
            BpHull: 255, BpShield: 255, Name: $"STATION{id}"), type);
    }

    // ── 4. docked / boarded ships leave the visibility sweep ──────────────

    /// <summary>
    /// Native keeps a docked ship out of its cluster (docking calls
    /// <c>SetCluster(NULL)</c>, <c>stationIGC.cpp:282-285</c>), which both
    /// unregisters it as a scanner (<c>clusterIGC.h:145-148</c>) and hides it from
    /// every other side (<c>ThingSiteImpl::SetCluster</c>, FedSrv.CPP:10711-10728).
    /// A boarded ship's ThingSite is rebuilt without <c>c_mtSeenBySide</c>
    /// (<c>shipIGC.cpp:3632-3643</c>). The port keeps docked ships in
    /// <c>Sector.Models</c> for bookkeeping, so the sweep must exclude them.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docked_and_boarded_ships_are_neither_scanners_nor_targets()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var mission = NewStartedMission(site);
        var (sector, basePos) = CleanBase(mission);

        // (a) A side-1 ship docked NEXT TO a side-0 scanner must stay unseen.
        var scanner = NewShip(9741, ScannerHull, Side0, basePos);
        var docked = NewShip(9742, TargetHull, Side1, basePos + new Vector3(300f, 0f, 0f));
        docked.StationId = 1;
        Place(mission, sector, scanner);
        Place(mission, sector, docked);
        sector.ResolveCrossSideVisibility();
        Assert.False(docked.SeenBySide(Side0), "a docked ship is out of the cluster and cannot be spotted");
        // Native never runs UpdateSideVisibility for a docked ship, so its own
        // side's flag is whatever it was before docking — false for a ship that
        // has never flown (its own side renders docked ships through the hangar,
        // not through ThingSite).
        Assert.False(docked.SeenBySide(Side1), "a never-flown docked ship has no ThingSite visibility yet");

        // (b) A docked side-1 ship must not scan for side 1: a side-0 target far
        // outside every other scanner's range stays unseen by side 1.
        var hidden = NewShip(9743, TargetHull, Side0, basePos + new Vector3(20_000f, 0f, 0f));
        Place(mission, sector, hidden);
        var side1Docked = NewShip(9744, ScannerHull, Side1, basePos + new Vector3(20_300f, 0f, 0f));
        side1Docked.StationId = 1;
        Place(mission, sector, side1Docked);
        sector.ResolveCrossSideVisibility();
        Assert.False(hidden.SeenBySide(Side1), "a docked ship is not a scanner (clusterIGC.h:145-148)");

        // (c) Once docked after having been spotted, the enemy side loses contact
        // on the next sweep (native SetCluster's hide loop).
        docked.StationId = null;
        scanner.Position = basePos + new Vector3(300f, 0f, 0f);
        sector.ResolveCrossSideVisibility();
        Assert.True(docked.SeenBySide(Side0), "flying again, in range (precondition)");
        docked.StationId = 1;
        sector.ResolveCrossSideVisibility();
        Assert.False(docked.SeenBySide(Side0), "docking hides the ship from the enemy side again");
    }

    // ── 5. the object factory's S_FALSE rejection ─────────────────────────

    /// <summary>
    /// <c>CmissionIGC::CreateObject</c>'s failure path
    /// (<c>Igc/missionigc.cpp:3370-3378</c>): <c>CprobeIGC::Initialize</c> returns
    /// <c>S_FALSE</c> for a teleport probe dropped inside an asteroid
    /// (<c>probeigc.cpp:200-212</c>), so the factory Terminates/Releases it and
    /// hands back NULL — the probe never exists in the world. The same drop for a
    /// NON-teleport probe is allowed, and a teleport probe beside the rock is too.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Teleport_probe_dropped_inside_an_asteroid_is_rejected_by_the_object_factory()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var mission = NewStartedMission(site);
        var (sector, basePos) = CleanBase(mission);
        var core = mission.Core;

        var rock = new Asteroid(new DataAsteroidIGC(
            Signature: 1.0f, Position: [basePos.X, basePos.Y, basePos.Z],
            Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            Ore: 0f, OreMax: 0f, AabmCapabilities: 0, AsteroidId: 9761,
            HitPoints: 1000f, Radius: 600, ModelName: "m2xspiderw.igc",
            TextureName: "", IconName: "", ClusterId: sector.Id, Name: "PROBEROCK", Fraction: 0));
        mission.PlaceAsteroid(rock, sector);

        var teleportType = core.ProbeTypes.Values.First(p => p.DtRipcord >= 0);
        var plainType = core.ProbeTypes.Values.First(p => p.DtRipcord < 0);

        // Inside the rock: rejected (native S_FALSE → NULL, nothing created).
        var inside = NewShip(9762, TargetHull, Side0, basePos);
        Place(mission, sector, inside);
        Assert.Null(inside.DeployProbe(teleportType, 100));
        Assert.DoesNotContain(sector.Models, m => m is Probe);
        Assert.DoesNotContain(site.Calls, c => c.StartsWith(nameof(IIgcSite.ProbeCreatedEvent), StringComparison.Ordinal));

        // The same spot with a plain probe: created normally.
        Assert.NotNull(inside.DeployProbe(plainType, 100));
        Assert.Single(sector.Models.OfType<Probe>());

        // A teleport probe well outside the rock: created normally.
        var outside = NewShip(9763, TargetHull, Side0, basePos + new Vector3(5000f, 0f, 0f));
        Place(mission, sector, outside);
        Assert.NotNull(outside.DeployProbe(teleportType, 100));
        Assert.Equal(2, sector.Models.OfType<Probe>().Count());
    }

    // ── 6. GetSeenSides flag bypass ───────────────────────────────────────

    /// <summary>
    /// <c>CmissionIGC::GetSeenSides</c> (<c>Igc/missionigc.cpp:3658-3681</c>) asks
    /// each candidate scanner <c>InScannerRange(builder)</c>, which returns
    /// <b>true</b> for a model whose <c>GetFlag() != NA</c> regardless of range,
    /// signature and occlusion (<c>shipIGC.h:351-369</c>). So a flag-carrying
    /// builder's construction is announced to a side whose scanner is far away,
    /// as long as that side has already seen the rock.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Flag_carrying_builder_is_seen_by_a_distant_scanner_in_get_seen_sides()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var mission = NewStartedMission(site);
        var (sector, basePos) = CleanBase(mission);

        var scanner = NewShip(9751, ScannerHull, Side0, basePos);
        var builder = NewShip(9752, TargetHull, Side1, basePos + new Vector3(30_000f, 0f, 0f));
        Place(mission, sector, scanner);
        Place(mission, sector, builder);
        var rock = new Asteroid(new DataAsteroidIGC(
            Signature: 1.0f, Position: [basePos.X, basePos.Y, basePos.Z],
            Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            Ore: 0f, OreMax: 0f, AabmCapabilities: DataAsteroidIGC.AabmBuildable, AsteroidId: 9753,
            HitPoints: 1000f, Radius: 40, ModelName: "m2xspiderw.igc",
            TextureName: "", IconName: "", ClusterId: sector.Id, Name: "BUILDROCK", Fraction: 0));
        mission.PlaceAsteroid(rock, sector);

        // The rock is known to side 0 (its scanner sits on it); the builder is
        // 30 km away — far outside the 1952-unit faohshcar→dreghpod radius.
        rock.SideVisibility.SetSideVisibility(Side0, true);
        var seen = sector.ConstructionSeenSides(builder, rock);
        Assert.False(seen[Side0], "without a flag the distant builder is out of scanner range");

        builder.Flag = 0;                       // a flag / artifact carrier
        seen = sector.ConstructionSeenSides(builder, rock);
        Assert.True(seen[Side0], "a flag-carrying builder is seen by any side with a scanner in the sector");
    }
}
