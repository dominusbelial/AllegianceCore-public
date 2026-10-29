using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P182 — chaff/countermeasures. Three tiers of proof:
/// <list type="number">
/// <item><b>Domain (sim, non-live):</b> <see cref="Ship.DeployChaff"/> creates a
/// real <see cref="Chaff"/> in the ship's cluster at the ship's position/velocity
/// and runs the native missile-retarget (spoof) loop — a missile whose
/// <see cref="Missile.Target"/> is the firing ship gets retargeted onto the chaff
/// (deterministically forced via an injectable RNG, mirroring <see cref="Weapon"/>'s
/// dispersion-RNG pattern), a missile targeting a <em>different</em> ship is never
/// touched, <see cref="Chaff.Radius"/> grows over its lifetime and the chaff expires
/// after <see cref="DataChaffTypeIGC.Lifespan"/>, and a spoofed missile still drops
/// its chaff target when the chaff leaves the cluster (the P179 mechanism).</item>
/// <item><b>Wire:</b> the five new codecs (<see cref="FireExpendableRequest"/> 82,
/// <see cref="FireExpendable"/> 87, <see cref="CreateChaff"/> 109,
/// <see cref="MissileSpoofed"/> 110, <see cref="EndSpoofing"/> 111) round-trip and
/// pin the native <c>sizeof</c> sizes.</item>
/// <item><b>Live (real in-process <see cref="Srv"/> over loopback):</b> the
/// <c>FIRE_EXPENDABLE(82)</c> dispatch — a mounted chaff launcher with
/// <c>Amount &gt; 0</c> creates a real Chaff in the sector, decrements <c>Amount</c>,
/// and broadcasts FE(87)+CREATE_CHAFF(109)+END_SPOOFING(111) to the cluster; an
/// empty launcher is refused (no object, no broadcast, no decrement — the
/// must-fail-if-removed proof); and a missile with <c>ChaffResistance == 0</c>
/// targeting the firer is spoofed over the wire, delivering MISSILE_SPOOFED + the
/// single mandatory END_SPOOFING.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public sealed class ChaffTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static DataChaffTypeIGC RealChaff(StaticCore core)
    {
        var c = core.ChaffTypes.Values
            .Where(cc => cc.ChaffStrength > 0f && cc.Lifespan >= 2f)
            .OrderBy(cc => cc.Lifespan)
            .FirstOrDefault();
        if (c.ExpendableTypeId == 0 && core.ChaffTypes.Count > 0)
            throw new InvalidOperationException("representative core has no chaff with strength>0 and lifespan>=2");
        return c;
    }

    private static DataMineTypeIGC RealMine(StaticCore core)
    {
        var m = core.MineTypes.Values
            .Where(mm => mm.Lifespan >= 2f)
            .OrderBy(mm => mm.Lifespan)
            .FirstOrDefault();
        if (m.ExpendableTypeId == 0 && core.MineTypes.Count > 0)
            throw new InvalidOperationException("representative core has no mine with lifespan>=2");
        return m;
    }

    private static DataProbeTypeIGC RealProbe(StaticCore core)
    {
        var p = core.ProbeTypes.Values
            .Where(pp => pp.Lifespan >= 2f)
            .OrderBy(pp => pp.Lifespan)
            .FirstOrDefault();
        if (p.ExpendableTypeId == 0 && core.ProbeTypes.Count > 0)
            throw new InvalidOperationException("representative core has no probe with lifespan>=2");
        return p;
    }

    /// <summary>A real missile type copied with <c>ChaffResistance = 0</c> — the
    /// deterministic spoof lever (cm = chaff·0 = 0 ⇒ random(0,0)=0 ≤ f always).</summary>
    private static DataMissileTypeIGC ZeroResistMissile(StaticCore core)
    {
        var m = core.MissileTypes.Values.FirstOrDefault(mm => mm.Lifespan >= 2f);
        if (m.ExpendableTypeId == 0 && core.MissileTypes.Count > 0)
            throw new InvalidOperationException("no missile with lifespan>=2");
        return m with { ChaffResistance = 0f };
    }

    /// <summary>A real missile type copied with a huge <c>ChaffResistance</c> — with
    /// an RNG pinned to sample=1.0, roll = cm ≫ f, so it is never spoofed.</summary>
    private static DataMissileTypeIGC HighResistMissile(StaticCore core)
    {
        var m = core.MissileTypes.Values.FirstOrDefault(mm => mm.Lifespan >= 2f);
        if (m.ExpendableTypeId == 0 && core.MissileTypes.Count > 0)
            throw new InvalidOperationException("no missile with lifespan>=2");
        return m with { ChaffResistance = 1e6f };
    }

    /// <summary>A deterministic RNG: <see cref="Random.NextDouble"/> returns exactly
    /// <paramref name="sample"/> every draw (overriding <c>Sample()</c>), so the
    /// chaff roll <c>random(0, cm) = sample·cm</c> is fully predictable.</summary>
    private sealed class FixedRandom : Random
    {
        private readonly double _sample;
        public FixedRandom(double sample) => _sample = sample;
        protected override double Sample() => _sample;
    }

    // ── domain (non-live) helpers ───────────────────────────────────────────────

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

    private sealed class SimSite : IIgcSite { }   // all-default host seam; chaff raises no events

    private sealed record Domain(Mission Mission, Sector Sector, Ship Ship);

    private static Domain NewDomain()
    {
        var core = RepresentativeCore();
        var site = new SimSite();
        var mission = Mission.Assemble(
            core, SomeMap(),
            [new SideSpec(0, "Team", core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId)],
            site);
        mission.Stage = MissionStage.Started;
        var sector = mission.Sectors[0];
        var ship = new Ship(777, core.HullTypes.Values.First(), Vector3.Zero, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(ship, sector);
        return new Domain(mission, sector, ship);
    }

    private static Missile PlaceMissile(Sector sector, double now, DataMissileTypeIGC type, Ship target, short id)
    {
        var missile = new Missile(
            id, sector, type,
            target.Position + new Vector3(100f, 0f, 0f),   // some metres out so it homes from a distance
            Vector3.Zero, new Vector3(0f, 0f, -1f), now,
            launcher: target, target: target);
        sector.Add(missile);
        return missile;
    }

    // ── domain: deploy + spoof mechanics ─────────────────────────────────────────

    /// <summary>⭐ Deploying chaff creates a real <see cref="Chaff"/> model in the
    /// ship's cluster at the ship's plain Position with the ship's Velocity, and a
    /// missile (ChaffResistance 0) whose target is the firing ship is deterministically
    /// spoofed onto it — <c>Target</c> becomes the chaff. A missile targeting a
    /// <em>different</em> ship in the same cluster is never touched. Both spoof and
    /// no-op are forced by the injectable RNG, mirroring Weapon's dispersion-RNG pattern.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void DeployChaff_creates_chaff_and_spoofs_only_the_missile_targeting_the_firer()
    {
        Require.Artwork();
        var (mission, sector, ship) = NewDomain();
        double now = mission.LastUpdate;

        // A second ship (also in the cluster) that an unrelated missile hunts.
        var otherShip = new Ship(778, ship.HullType, new Vector3(0f, 500f, 0f), Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(otherShip, sector);

        var chaffType = RealChaff(mission.Core);
        var spoofMe = PlaceMissile(sector, now, ZeroResistMissile(mission.Core), ship, id: 901);
        var leaveMe = PlaceMissile(sector, now, HighResistMissile(mission.Core), otherShip, id: 902);

        // Deploy chaff with an RNG pinned to 1.0 (would-be-worst for spoofing, but the
        // ChaffResistance==0 missile spoofs regardless; the unrelated missile has huge
        // resistance so it is untouched).
        var result = ship.DeployChaff(chaffType, now, new FixedRandom(1.0));

        // A real Chaff in the ship's cluster at the ship's position/velocity.
        Assert.Contains(result.Chaff, sector.Models);
        Assert.Contains(result.Chaff, sector.Models.OfType<Chaff>());
        Assert.Equal(ship.Position, result.Chaff.Position);
        Assert.Equal(ship.Velocity, result.Chaff.Velocity);
        Assert.Equal(chaffType.ExpendableTypeId, result.Chaff.ChaffType.ExpendableTypeId);

        // The missile aimed at the firer got spoofed onto the chaff...
        Assert.Equal(result.Chaff, spoofMe.Target);
        Assert.Contains(spoofMe, result.Spoofed);
        // ...the missile aimed at the OTHER ship was never touched.
        Assert.Equal(otherShip, leaveMe.Target);
        Assert.DoesNotContain(leaveMe, result.Spoofed);
    }

    /// <summary>⭐ The inverse roll: a missile with huge ChaffResistance, against an RNG
    /// pinned to 1.0 (roll = cm ≫ f), is <em>not</em> spoofed and keeps its target.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void DeployChaff_does_not_spoof_when_the_roll_favors_the_missile()
    {
        Require.Artwork();
        var (mission, sector, ship) = NewDomain();
        double now = mission.LastUpdate;

        var missile = PlaceMissile(sector, now, HighResistMissile(mission.Core), ship, id: 903);
        var result = ship.DeployChaff(RealChaff(mission.Core), now, new FixedRandom(1.0));

        Assert.Equal(ship, missile.Target);           // still hunting the ship
        Assert.Empty(result.Spoofed);                 // nothing retargeted
    }

    /// <summary>A chaff fire with no missile currently targeting the firing ship spoofs
    /// nothing — the returned <see cref="Ship.DeployChaffResult.Spoofed"/> list is empty
    /// (the companion to the live "always one END_SPOOFING" assertion below).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void DeployChaff_with_no_threatening_missile_spoofs_nothing()
    {
        Require.Artwork();
        var (mission, sector, ship) = NewDomain();
        double now = mission.LastUpdate;

        // A missile in the cluster, but hunting a DIFFERENT ship.
        var otherShip = new Ship(779, ship.HullType, new Vector3(0f, 700f, 0f), Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(otherShip, sector);
        PlaceMissile(sector, now, ZeroResistMissile(mission.Core), otherShip, id: 904);

        var result = ship.DeployChaff(RealChaff(mission.Core), now, new FixedRandom(0.0));
        Assert.Empty(result.Spoofed);
    }

    /// <summary>⭐ A Chaff's <see cref="Chaff.Radius"/> grows from its 1.0 floor toward
    /// <c>ChaffType.Radius</c> over its lifetime, and it expires (removed from the
    /// sector) once the mission time passes <c>now + Lifespan</c> — the native
    /// <c>chaffigc.cpp:82-83,78-79</c> behaviour, confirmed by direct time-advance.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Chaff_radius_grows_and_it_expires_after_lifespan()
    {
        Require.Artwork();
        var (mission, sector, ship) = NewDomain();
        double now = mission.LastUpdate;
        var chaffType = RealChaff(mission.Core);

        var chaff = ship.DeployChaff(chaffType, now, new FixedRandom(1.0)).Chaff;
        float startRadius = chaff.Radius;
        float life = chaffType.Lifespan;
        double t = now;

        // Advance ~half the lifespan: radius must have grown (strictly above the floor).
        double mid = now + 0.5 * life;
        while (t < mid) { t += 0.0125; mission.Update(t); }
        Assert.Contains(chaff, sector.Models);                 // still alive
        Assert.True(chaff.Radius > startRadius, $"radius {chaff.Radius} must grow above the {startRadius} floor");

        // Advance past expiry: the chaff removes itself from the sector.
        double past = now + life + 0.06;
        while (t < past) { t += 0.025; mission.Update(t); }
        Assert.DoesNotContain(chaff, sector.Models);           // expired & removed
    }

    /// <summary>⭐ A missile whose target was reassigned onto chaff still drops the
    /// target when the chaff leaves the cluster — reusing P179's existing per-tick
    /// <c>!_sector.Models.Contains(t)</c> mechanism (Missile.cs:205-208), confirming it
    /// isn't accidentally bypassed for a chaff-type target.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Spoofed_missile_drops_its_chaff_target_when_the_chaff_leaves_the_cluster()
    {
        Require.Artwork();
        var (mission, sector, ship) = NewDomain();
        double now = mission.LastUpdate;

        var missile = PlaceMissile(sector, now, ZeroResistMissile(mission.Core), ship, id: 905);
        var result = ship.DeployChaff(RealChaff(mission.Core), now, new FixedRandom(1.0));
        var chaff = result.Chaff;
        Assert.Equal(chaff, missile.Target);                   // spoofed onto chaff

        // The chaff leaves the cluster (simulating expiry/left-behind), then tick:
        // the missile's existing P179 target-drop clears its target.
        sector.Remove(chaff);
        double t = mission.LastUpdate;
        for (int i = 0; i < 40 && missile.Target is not null; i++)
        {
            t += 0.0125;
            mission.Update(t);
        }
        Assert.Null(missile.Target);
    }

    // ── wire: round-trip + native sizeof ─────────────────────────────────────────

    /// <summary>All five new chaff codecs round-trip through their exact wire bytes and
    /// pin the native C++ <c>sizeof</c> (2-aligned short tails, 4-aligned vectors).</summary>
    [Fact]
    public void Chaff_wire_messages_round_trip_with_native_sizes()
    {
        // 82: FireExpendableRequest { et }
        var req = new FireExpendableRequest { Et = EquipmentType.ChaffLauncher };
        Assert.Equal(6, FireExpendableRequest.FixedSize);
        var reqBack = FireExpendableRequest.Decode(req.Encode());
        Assert.Equal(EquipmentType.ChaffLauncher, reqBack.Et);

        // 87: FireExpendable { launcherID, equipmentType }
        var fe = new FireExpendable { LauncherId = 42, EquipmentType = EquipmentType.Dispenser };
        Assert.Equal(8, FireExpendable.FixedSize);
        var feBack = FireExpendable.Decode(fe.Encode());
        Assert.Equal(42, feBack.LauncherId);
        Assert.Equal(EquipmentType.Dispenser, feBack.EquipmentType);

        // 109: CreateChaff { p0, v0, time0, etid }
        var cc = new CreateChaff
        {
            P0 = new Vector3(1f, -2f, 3f),
            V0 = new Vector3(4f, 5f, -6f),
            Time0 = 88123u,
            Etid = 519,
        };
        Assert.Equal(36, CreateChaff.FixedSize);
        var ccBack = CreateChaff.Decode(cc.Encode());
        Assert.Equal(cc.P0, ccBack.P0);
        Assert.Equal(cc.V0, ccBack.V0);
        Assert.Equal(88123u, ccBack.Time0);
        Assert.Equal(519, ccBack.Etid);

        // 110: MissileSpoofed { missileID }
        var ms = new MissileSpoofed { MissileId = 909 };
        Assert.Equal(6, MissileSpoofed.FixedSize);
        Assert.Equal(909, MissileSpoofed.Decode(ms.Encode()).MissileId);

        // 111: EndSpoofing — header only
        Assert.Equal(4, EndSpoofing.FixedSize);
        _ = EndSpoofing.Decode(new EndSpoofing().Encode());
    }

    /// <summary>P224 — the construction-only mine/probe launcher subtypes mount under
    /// <c>EquipmentType.Dispenser</c> (what <c>CmineTypeIGC</c>/<c>CprobeTypeIGC</c>
    /// report, minetypeigc.h:62-64 / probetypeigc.h:92-94), while chaff keeps its own
    /// <c>ChaffLauncher</c> value. Since mine and probe share <c>Dispenser</c>, the
    /// FIRE_EXPENDABLE dispatch distinguishes them by subtype, not equipment type.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mine_and_probe_launcher_subtypes_mount_under_equipment_type_dispenser()
    {
        Require.Artwork();
        Assert.Equal(EquipmentType.Dispenser, new DataPartTypeIGC.MineLauncher(1).EquipmentType);
        Assert.Equal(EquipmentType.Dispenser, new DataPartTypeIGC.ProbeLauncher(1).EquipmentType);
        Assert.Equal(EquipmentType.ChaffLauncher, new DataPartTypeIGC.ChaffLauncher(1).EquipmentType);
    }

    // ── live loopback (3-bot / 2-side) dispatch tests ────────────────────────────

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Chaff Countermeasure Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 851u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 86000000000000000ul, DrmHash = "chaff",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship Shooter = null!, Observer = null!, Enemy = null!;
        public FltBot ShooterBot = null!, ObserverBot = null!, EnemyBot = null!;
        public StaticCore Core = null!;

        public void Dispose()
        {
            foreach (var b in new[] { ShooterBot, ObserverBot, EnemyBot }) b?.Dispose();
            Server?.Dispose();
        }
    }

    /// <summary>The 3-bot / 2-side harness (round-robin A,B,A) with the shooter at the
    /// origin, the observer same-side far off, the enemy cross-side. The shooter's
    /// chaff broadcast goes to the whole cluster (all sides), so both the observer and
    /// the enemy are valid recipients — that is the <c>GetGroupSectorFlying(pcluster)</c>
    /// audience this task mandates for the chaff flow.</summary>
    private static Seated SeatThree()
    {
        var seat = new Seated();
        var core = RepresentativeCore();
        seat.Core = core;
        seat.Server = new Srv();

        var hull = core.HullTypes.Values.First();
        // P653 H-08 clamps launcher rounds to hull launcher capacity / expendable
        // size, min 1 (launchertypeigc.h:130-145). Values.First() is hull 3
        // 'Miner' (capChaff=Dispenser=0), which would clamp every mount to 1.
        // These live tests verify the FIRING flow (decrement + broadcast), so arm
        // the test hull with a generous launcher capacity — 3 rounds fits any
        // real chaff/mine/probe expendable size. (The audit's clamp is preserved;
        // this merely gives the fixture a hull that can hold its test rounds.)
        hull = hull with { CapacityChaffLauncher = 100, CapacityDispenser = 100, CapacityMagazine = 100 };
        ((Dictionary<short, DataHullTypeIGC>)core.HullTypes)[hull.HullId] = hull;
        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            var ship = placed switch
            {
                0 => new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity),
                1 => new Ship(id, hull, new Vector3(0f, 0f, -100f), Vector3.Zero, Orientation.Identity),
                _ => new Ship(id, hull, new Vector3(0f, 850f, 910f), Vector3.Zero, Orientation.Identity),
            };
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;   // bypass ready-up (P180)
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p182_shooter", "p182_observer", "p182_enemy" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            foreach (var bot in bots)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                if (ship.Position.Length() < 1f) { seat.Shooter = ship; seat.ShooterBot = bot; }
                else if (MathF.Abs(ship.Position.Z + 100f) < 2f) { seat.Enemy = ship; seat.EnemyBot = bot; }
                else { seat.Observer = ship; seat.ObserverBot = bot; }
            }
            Assert.NotNull(seat.Shooter); Assert.NotNull(seat.Observer); Assert.NotNull(seat.Enemy);
            Assert.True(seat.Shooter.SideId == seat.Observer.SideId, "observer same-side to shooter");
            Assert.True(seat.Shooter.SideId != seat.Enemy.SideId, "enemy cross-side to shooter");
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    /// <summary>Mount a chaff launcher at mount 0 on <paramref name="ship"/> that names
    /// a real core chaff type, armed with <paramref name="amount"/> rounds. The part is
    /// constructed directly (the <see cref="DataPartTypeIGC.ChaffLauncher"/> subtype has
    /// no corpus part-record to decode — it is the port's construction-only mount case,
    /// see DataPartTypeIGC.cs).</summary>
    private static void MountChaffLauncher(Ship ship, StaticCore core, short amount)
    {
        var chaffType = RealChaff(core);
        var part = new DataPartTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "chaff", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: 660, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.ChaffLauncher(chaffType.ExpendableTypeId));
        ship.MountPart(part, amount, mountId: 0, fractionMounted: 255);
    }

    /// <summary>P224 — mount a mine launcher at mount 0 on <paramref name="ship"/> that
    /// names a real core mine type, armed with <paramref name="amount"/> rounds. Built
    /// directly from the construction-only <see cref="DataPartTypeIGC.MineLauncher"/>
    /// subtype (no corpus part-record to decode), the <see cref="MountChaffLauncher"/>
    /// recipe repeated for mines. Mines mount under <c>EquipmentType.Dispenser</c>, the
    /// value <c>CmineTypeIGC::GetEquipmentType</c> reports (minetypeigc.h:62-64).</summary>
    private static void MountMineLauncher(Ship ship, StaticCore core, short amount)
    {
        var mineType = RealMine(core);
        var part = new DataPartTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "mine", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: 770, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.MineLauncher(mineType.ExpendableTypeId));
        ship.MountPart(part, amount, mountId: 0, fractionMounted: 255);
    }

    /// <summary>P224 — mount a probe launcher at mount 0 on <paramref name="ship"/> that
    /// names a real core probe type, armed with <paramref name="amount"/> rounds. The
    /// <see cref="MountMineLauncher"/> recipe repeated for probes; probes likewise mount
    /// under <c>EquipmentType.Dispenser</c> (probetypeigc.h:92-94).</summary>
    private static void MountProbeLauncher(Ship ship, StaticCore core, short amount)
    {
        var probeType = RealProbe(core);
        var part = new DataPartTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "probe", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: 771, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.ProbeLauncher(probeType.ExpendableTypeId));
        ship.MountPart(part, amount, mountId: 0, fractionMounted: 255);
    }

    /// <summary>⭐ LIVE dispatch: firing a mounted chaff launcher with <c>Amount &gt; 0</c>
    /// creates a real <see cref="Chaff"/> in the ship's sector, decrements the launcher's
    /// <c>Amount</c> by 1, and broadcasts FE(87) + CREATE_CHAFF(109) + END_SPOOFING(111)
    /// to the whole cluster (all sides).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_firing_mounted_chaff_launcher_creates_chaff_decrements_ammo_and_broadcasts()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var shooter = seat.Shooter;
        var observer = seat.ObserverBot;
        var chaffType = RealChaff(seat.Core);
        MountChaffLauncher(shooter, seat.Core, amount: 3);

        Assert.Equal(3, shooter.Parts.First(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.MountId == 0).Amount);
        seat.ShooterBot.SendFireExpendable(EquipmentType.ChaffLauncher);

        PumpUntil(seat.Server, () => observer.CreateChaffs.Count >= 1, 8000);
        Assert.True(observer.CreateChaffs.Count >= 1, "observer must receive CREATE_CHAFF(109)");

        // The chaff really exists in the shooter's sector.
        Assert.Contains(seat.Server.Ships.First(s => s.Ship.Id == shooter.Id).Ship.Sector!.Models.OfType<Chaff>(),
            c => c.ChaffType.ExpendableTypeId == chaffType.ExpendableTypeId);

        // Ammo decremented 3 -> 2.
        Assert.Equal(2, shooter.Parts.First(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.MountId == 0).Amount);

        // FE(87) echo + exactly one END_SPOOFING arrive too.
        Assert.Contains(observer.FireExpendables, fe => fe.LauncherId == shooter.Id && fe.EquipmentType == EquipmentType.ChaffLauncher);
        Assert.Single(observer.EndSpoofings);   // exactly one, no missiles present
    }

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: firing a mounted chaff launcher with
    /// <c>Amount == 0</c> is refused — no Chaff is created, no broadcast is sent, and
    /// the <c>Amount</c> is not decremented. Temporarily removing the <c>Amount &lt;= 0</c>
    /// guard in <c>HandleFireExpendable</c> makes this test fail.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_firing_empty_chaff_launcher_is_refused()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var shooter = seat.Shooter;
        var observer = seat.ObserverBot;
        MountChaffLauncher(shooter, seat.Core, amount: 0);

        seat.ShooterBot.SendFireExpendable(EquipmentType.ChaffLauncher);
        PumpUntil(seat.Server, () => false, 300);   // give the wire time to deliver anything

        Assert.Empty(observer.CreateChaffs);      // no CREATE_CHAFF broadcast
        Assert.Empty(observer.FireExpendables);   // no FE(87) echo
        Assert.Empty(observer.EndSpoofings);      // no END_SPOOFING
        Assert.Empty(observer.MissileSpoofeds);
        Assert.Empty(shooter.Sector!.Models.OfType<Chaff>());   // no chaff in the shooter's sector
        Assert.Equal(0, shooter.Parts.First(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.MountId == 0).Amount);
    }

    /// <summary>⭐ LIVE spoof-over-the-wire: with a <c>ChaffResistance == 0</c> missile in
    /// the shooter's sector whose target is the shooter, firing chaff deterministically
    /// breaks its lock — the observer receives MISSILE_SPOOFED(110) naming that missile,
    /// followed by exactly one END_SPOOFING(111).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_chaff_spoofs_a_zero_resistance_missile_targeting_the_firer_over_the_wire()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var shooter = seat.Shooter;
        var observer = seat.ObserverBot;
        MountChaffLauncher(shooter, seat.Core, amount: 2);

        // Park a real (ChaffResistance==0) missile hunting the shooter in its sector.
        var missile = new Missile(
            911, shooter.Sector!, ZeroResistMissile(seat.Core),
            shooter.Position + new Vector3(150f, 0f, 0f), Vector3.Zero, new Vector3(0f, 0f, -1f),
            shooter.Mission!.LastUpdate, launcher: shooter, target: shooter);
        shooter.Sector!.Add(missile);

        seat.ShooterBot.SendFireExpendable(EquipmentType.ChaffLauncher);
        PumpUntil(seat.Server, () => observer.MissileSpoofeds.Any(ms => ms.MissileId == 911), 8000);

        Assert.Contains(observer.MissileSpoofeds, ms => ms.MissileId == 911);
        Assert.Single(observer.EndSpoofings);          // exactly one END_SPOOFING
        Assert.Single(observer.CreateChaffs);
        // The missile's target must now BE the fresh chaff decoy.
        var chaffInSector = shooter.Sector!.Models.OfType<Chaff>().FirstOrDefault();
        Assert.NotNull(chaffInSector);
        Assert.Same(chaffInSector, missile.Target);
    }

    /// <summary>⭐ P224 LIVE dispatch: firing a mounted <b>mine</b> launcher (the
    /// construction-only <see cref="DataPartTypeIGC.MineLauncher"/> subtype) with
    /// <c>Amount &gt; 0</c> creates a real <see cref="Mine"/> in the ship's sector,
    /// decrements the launcher's <c>Amount</c> by 1, and broadcasts FE(87) + the
    /// auto EXPORT(26)/OT_mine to the <b>same side only</b> (the port's mine-object
    /// audience, BroadcastExportMine) — the cross-side enemy receives nothing.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_firing_mounted_mine_launcher_creates_mine_decrements_ammo_and_broadcasts()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var shooter = seat.Shooter;
        var observer = seat.ObserverBot;
        var enemy = seat.EnemyBot;
        var mineType = RealMine(seat.Core);

        // The observer must already hold the shooter's EXPORT, else the server
        // withholds the same-side mine/probe broadcasts (the _teammateExportSent gate).
        PumpUntil(seat.Server, () => observer.Exports.Any(e => e.ShipId == shooter.Id), 8000);

        MountMineLauncher(shooter, seat.Core, amount: 3);
        Assert.Equal(3, shooter.Parts.First(p => p.EquipmentType == EquipmentType.Dispenser && p.MountId == 0).Amount);
        seat.ShooterBot.SendFireExpendable(EquipmentType.Dispenser);

        PumpUntil(seat.Server, () => observer.MineExports.Count >= 1, 8000);
        Assert.True(observer.MineExports.Count >= 1, "same-side observer must receive EXPORT(26)/OT_mine");

        // The mine really exists in the shooter's sector.
        Assert.Contains(shooter.Sector!.Models.OfType<Mine>(),
            m => m.MineType.ExpendableTypeId == mineType.ExpendableTypeId);

        // Ammo decremented 3 -> 2.
        Assert.Equal(2, shooter.Parts.First(p => p.EquipmentType == EquipmentType.Dispenser && p.MountId == 0).Amount);

        // FE(87) echo arrives same-side, carrying EquipmentType.Dispenser.
        Assert.Contains(observer.FireExpendables,
            fe => fe.LauncherId == shooter.Id && fe.EquipmentType == EquipmentType.Dispenser);

        // The cross-side enemy gets neither the FE(87) echo nor the mine EXPORT.
        Assert.Empty(enemy.FireExpendables);
        Assert.Empty(enemy.MineExports);
    }

    /// <summary>⭐ P224 LIVE dispatch: firing a mounted <b>probe</b> launcher (the
    /// construction-only <see cref="DataPartTypeIGC.ProbeLauncher"/> subtype) creates a
    /// real <see cref="Probe"/> in the ship's sector, decrements the launcher's
    /// <c>Amount</c>, and broadcasts FE(87) + the auto EXPORT(26)/OT_probe to the
    /// same side only — mirroring the mine test with probe's Dispenser mount.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_firing_mounted_probe_launcher_creates_probe_decrements_ammo_and_broadcasts()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var shooter = seat.Shooter;
        var observer = seat.ObserverBot;
        var enemy = seat.EnemyBot;
        var probeType = RealProbe(seat.Core);

        PumpUntil(seat.Server, () => observer.Exports.Any(e => e.ShipId == shooter.Id), 8000);

        MountProbeLauncher(shooter, seat.Core, amount: 3);
        Assert.Equal(3, shooter.Parts.First(p => p.EquipmentType == EquipmentType.Dispenser && p.MountId == 0).Amount);
        seat.ShooterBot.SendFireExpendable(EquipmentType.Dispenser);

        PumpUntil(seat.Server, () => observer.ProbeExports.Count >= 1, 8000);
        Assert.True(observer.ProbeExports.Count >= 1, "same-side observer must receive EXPORT(26)/OT_probe");

        // The probe really exists in the shooter's sector.
        Assert.Contains(shooter.Sector!.Models.OfType<Probe>(),
            p => p.ProbeType.ExpendableTypeId == probeType.ExpendableTypeId);

        // Ammo decremented 3 -> 2.
        Assert.Equal(2, shooter.Parts.First(p => p.EquipmentType == EquipmentType.Dispenser && p.MountId == 0).Amount);

        // FE(87) echo arrives same-side, carrying EquipmentType.Dispenser.
        Assert.Contains(observer.FireExpendables,
            fe => fe.LauncherId == shooter.Id && fe.EquipmentType == EquipmentType.Dispenser);

        // The cross-side enemy gets neither the FE(87) echo nor the probe EXPORT.
        Assert.Empty(enemy.FireExpendables);
        Assert.Empty(enemy.ProbeExports);
    }
}
