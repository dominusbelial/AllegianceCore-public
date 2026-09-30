using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P461 — the ship's in-flight <b>energy recharge</b> (<c>CshipIGC::Update</c>,
/// <c>src/Igc/shipIGC.cpp:295-303</c>). Before this task the port had NO
/// recharge: <c>Energy</c> was written only at spawn / power-up / parent-copy /
/// station-refit / cloak-drain / weapon-spend, so energy-per-shot guns drained
/// the pool and stayed empty until the ship docked, and a cloak's drain was
/// permanent. <see cref="Ship.OnUpdate"/> now adds <c>RechargeRate·dt</c> to
/// the pool every tick and clamps it to <c>[0, HullType.MaxEnergy]</c>, gated
/// on <c>(rechargeRate &gt; 0 || energy != 0)</c>, placed <b>before</b> the
/// mounted-parts sims exactly as native recharges before cloak-then-weapons
/// (shipIGC.cpp:306-343).
/// <para>
/// The <c>ParentShip</c>/<c>StationId</c> conjuncts reproduce native's Update
/// early-outs: native skips the WHOLE ship update for a boarded ship
/// (<c>m_pshipParent != NULL</c>, shipIGC.cpp:271) and never sims a docked
/// ship in a cluster, so neither recharges here — while a lifepod is just a
/// normal in-cluster hull and recharges like any other.
/// </para>
/// </summary>
public class EnergyRechargeTests
{
    private const short Side0 = 0;

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

    /// <summary>A hull with a real energy pool AND a real non-zero recharge so
    /// the recharge step is actually observable. Prefers a LOW recharge (a
    /// cheap energy weapon can then genuinely out-drain it — see
    /// <see cref="EnergyWeapon"/>), which is what lets the pool be spent toward
    /// zero by firing rather than sitting topped-up at max.</summary>
    private static readonly Lazy<DataHullTypeIGC> Hull = new(() =>
    {
        var core = RepresentativeCore.Value;
        return core.HullTypes.Values
            .Where(h => h.MaxEnergy > 0 && h.RechargeRate > 0f)
            .OrderBy(h => h.RechargeRate)
            .ThenBy(h => h.HullId)
            .First();
    });

    /// <summary>An energy-per-shot weapon (no ammo) this hull can afford whose
    /// drain rate (cost ÷ cooldown) comfortably exceeds the hull's recharge, so
    /// a held trigger genuinely spends the pool downward instead of hovering at
    /// max.</summary>
    private static readonly Lazy<DataPartTypeIGC.Weapon> EnergyWeapon = new(() =>
    {
        var core = RepresentativeCore.Value;
        var hull = Hull.Value;
        return core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon)
            .Select(p => (DataPartTypeIGC.Weapon)p.Type)
            .Where(w => w.Dispersion == 0f && w.CAmmoPerShot == 0 &&
                        w.EnergyPerShot > 0f && w.EnergyPerShot <= hull.MaxEnergy)
            .OrderByDescending(w => w.EnergyPerShot / MathF.Max(0.032f, w.DtimeReady))
            .ThenBy(w => w.EnergyPerShot)
            .First();
    });

    private static Mission NewStartedMission()
    {
        var civ = RepresentativeCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId).First();
        IReadOnlyList<SideSpec> specs = [new SideSpec(Side0, "Team 1", civ.CivilizationId)];
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, specs, new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>A base point 8000+ units beyond every real asteroid and station
    /// in a sector (the P132 <see cref="CrossSideVisibilitySweepTests"/> control)
    /// so the test's own ship is undisturbed.</summary>
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

    private static Ship NewShip(short id, DataHullTypeIGC hull, Vector3 pos) =>
        new(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = Side0 };

    /// <summary>Tick the mission in small single-chunk steps, continuing from
    /// its current <see cref="Mission.LastUpdate"/>, until
    /// <paramref name="predicate"/> holds or false after
    /// <paramref name="maxSteps"/>.</summary>
    private static bool TickUntil(Mission m, int maxSteps, Func<bool> predicate)
    {
        double t = m.LastUpdate;
        for (int i = 0; i < maxSteps; i++)
        {
            t += 0.041;
            m.Update(t);
            if (predicate()) return true;
        }
        return false;
    }

    // ── the core recharge behaviour ─────────────────────────────────────────

    /// <summary>
    /// An energy-weapon hull fires its pool down, then (once the trigger is
    /// released) the pool recovers at <c>RechargeRate·Δt</c> and clamps at the
    /// hull's <c>MaxEnergy</c>. MUST-FAIL on unfixed code: before P461 nothing
    /// recharges, so the recovered-energy assertion and the climb-to-max both
    /// fail (the pool stays where firing left it).
    /// </summary>
    [Fact]
    public void Ship_recharges_energy_in_flight_up_to_hull_max()
    {
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);
        var ship = NewShip(9901, Hull.Value, basePos);
        mission.PlaceShip(ship, sector);
        ship.StateM |= ShipControlState.AllWeapons;
        ship.MountWeapon(EnergyWeapon.Value);
        ship.Energy = ship.EffectiveMaxEnergy();   // pool starts full (Ship.cs:168; the GA-scaled ceiling)

        // Fire the energy weapon until the pool is genuinely spent (held
        // trigger; each shot drains EnergyPerShot with no ammo gate).
        Assert.True(TickUntil(mission, 8000, () => ship.Energy <= ship.EffectiveMaxEnergy() * 0.25f),
            "an energy weapon must spend the pool below 25% (recharge must not outpace a held trigger)");
        float drained = ship.Energy;
        Assert.True(drained < ship.EffectiveMaxEnergy() * 0.25f);

        // Release the trigger — no firing for the recharge window.
        ship.StateM &= ~ShipControlState.AllWeapons;

        // Measure the recovery over a short, clamp-free window: the pool must
        // rise by exactly RechargeRate·Δt.
        float recharge = ship.HullType.RechargeRate;
        double t0 = mission.LastUpdate;
        double step = 0.082;              // two 0.041 chunks (< 0.25 ⇒ n == 1 each)
        mission.Update(t0 + step);
        double elapsed = mission.LastUpdate - t0;
        float expected = drained + recharge * (float)elapsed;
        Assert.True(MathF.Abs(ship.Energy - expected) < 0.05f,
            $"energy must rise by RechargeRate·Δt (base {drained:F2} + {recharge:F2} × {elapsed:F3} = {expected:F2}, got {ship.Energy:F2})");
        Assert.True(ship.Energy > drained, "the pool must recover while flying");

        // Keep advancing: the pool climbs and must clamp AT the effective max
        // (no overshoot — shipIGC.cpp:299-302; the ceiling is the side's
        // GA-scaled GetMaxEnergy(), here 1.15× raw for the test side's civ 1).
        Assert.True(TickUntil(mission, 8000, () => ship.Energy >= ship.EffectiveMaxEnergy() - 0.001f),
            "the pool must climb back up to the effective MaxEnergy");
        Assert.Equal(ship.EffectiveMaxEnergy(), ship.Energy, precision: 2);
    }

    // ── the cloak interacts with recharge (native recharge-then-cloak) ─────

    /// <summary>
    /// An ACTIVE cloak whose draw is covered by the hull's recharge must not
    /// bottom the pool out. Start with a deliberately SMALL pool (10 energy,
    /// a few seconds of draw) and hold the cloak active: with recharge
    /// (&gt;= the drain) the pool holds and never hits 0, whereas before P461
    /// the cloak drain was permanent so it collapsed straight to 0. MUST-FAIL
    /// on unfixed code: no recharge ⇒ the small pool drains to 0 and the
    /// positive-energy assertion fails.
    /// </summary>
    [Fact]
    public void An_active_cloak_with_enough_recharge_never_bottoms_out()
    {
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);
        var ship = NewShip(9902, Hull.Value, basePos);
        mission.PlaceShip(ship, sector);

        // A cloak drawing 90% of the hull's recharge, so recharge comfortably
        // covers the drain and the pool holds (net slightly positive) while
        // active — versus a permanent collapse to 0 before P461.
        float consume = Hull.Value.RechargeRate * 0.9f;
        var cloakPart = new DataPartTypeIGC(
            Price: 8900, TimeToBuild: 460, ModelName: "bcl1gncr.igc", IconName: "",
            Name: "P461 Recharge Cloak", Description: "", GroupId: 0,
            TtbmRequired: [], TtbmEffects: [],
            Mass: 957f, Signature: 0.0f, PartId: 9130, SuccessorPartId: -1, PartMask: 813,
            InventoryLineMdl: "", Type: new DataPartTypeIGC.Cloak(
                EnergyConsumption: consume, MaxCloaking: 0.989f, OnRate: 4.0f, OffRate: 4.0f,
                EngageSound: -1, DisengageSound: -1));
        ship.MountPart(cloakPart, 1, mountId: 0, fractionMounted: 255);
        ship.StateM |= ShipControlState.CloakActive;
        ship.Energy = 10f;               // a small pool: a few seconds of draw

        // Hold the cloak active for ~2 s of ticks (100 × 0.02).
        double t = 0;
        for (int i = 0; i < 100; i++) { t += 0.019; mission.Update(t); }

        Assert.True(ship.IsCloaked, "the cloak must still be engaged");
        Assert.True(ship.Energy > 0f,
            $"recharge >= cloak draw must keep the small pool above zero; got {ship.Energy:F2}");
        Assert.True(ship.Energy >= 9.0f,
            $"the pool must HOLD near its start (net positive draw), not collapse; got {ship.Energy:F2}");
    }

    // ── native Update early-outs (scope item 3) ─────────────────────────────

    /// <summary>
    /// A DOCKED ship does not recharge: native never sims a docked ship in a
    /// cluster (it is absent from cluster-&gt;GetShips()), and the port's
    /// docked latch (<c>StationId != null</c>) reproduces that early-out for
    /// the recharge step. The pool stays exactly where it was.
    /// </summary>
    [Fact]
    public void A_docked_ship_does_not_recharge()
    {
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);
        var ship = NewShip(9903, Hull.Value, basePos);
        mission.PlaceShip(ship, sector);
        ship.StationId = 555;             // docked latch
        ship.Energy = 777f;               // an arbitrary non-full pool

        mission.Update(0.040);
        mission.Update(0.080);

        Assert.Equal(777f, ship.Energy, precision: 3);
    }

    /// <summary>
    /// A BOARDED ship (turret gunner / observer, <c>ParentShip != null</c>)
    /// does not recharge: native skips the WHOLE ship update while
    /// <c>m_pshipParent != NULL</c> (shipIGC.cpp:271), and the port's recharge
    /// honours that early-out. (The same guard also gates the cloak's
    /// activation, P269.)
    /// </summary>
    [Fact]
    public void A_boarded_ship_does_not_recharge()
    {
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);
        var ship = NewShip(9904, Hull.Value, basePos);
        var carrier = NewShip(9905, Hull.Value, basePos + new Vector3(200f, 0f, 0f));
        mission.PlaceShip(ship, sector);
        mission.PlaceShip(carrier, sector);
        ship.ParentShip = carrier;        // boarded onto the carrier
        ship.Energy = 999f;

        mission.Update(0.050);
        mission.Update(0.090);

        Assert.Equal(999f, ship.Energy, precision: 3);
    }

    /// <summary>
    /// A LIFEPOD is just a normal in-cluster hull and recharges like any other:
    /// the <c>ParentShip</c>/<c>StationId</c> gates do not special-case it out.
    /// FINDING (for the report): every lifepod-capable hull in the
    /// representative core — including the civilisations' own pod hull
    /// (<c>civ.Lifepod</c>) — carries <c>RechargeRate 0</c> and <c>MaxEnergy 0</c>.
    /// Native recharges a pod by <c>GetRechargeRate()·dt</c> exactly like any
    /// other ship (shipIGC.cpp:296-303 has no lifepod special case), so for a
    /// rate-0 pod that is a no-op — there is no pod energy to observe either
    /// way. To prove the GATE does not exclude a pod, drive a synthetic
    /// lifepod-capable hull that does carry a real pool and recharge.
    /// </summary>
    [Fact]
    public void A_lifepod_recharges_in_flight_like_any_in_cluster_hull()
    {
        var lifepod = new DataHullTypeIGC(
            Price: 8710, TimeToBuild: 6410, ModelName: "lifepod", IconName: "lifepod",
            Name: "P461 Pod", Description: "test", GroupId: 0,
            TtbmRequired: Array.Empty<byte>(), TtbmEffects: Array.Empty<byte>(),
            Mass: 861f, Signature: 872f, Speed: 774f,
            MaxTurnRates: new[] { 3f, 3f, 3f }, TurnTorques: new[] { 3621f, 3641f, 3691f },
            Thrust: 564000f, SideMultiplier: 924f, BackMultiplier: 5841f / 838f,
            ScannerRange: 951f, MaxFuel: 376f, Ecm: 568f, Length: 794f,
            MaxEnergy: 522f, RechargeRate: 823f, RipcordSpeed: 566f, RipcordCost: 641f,
            MaxAmmo: 481, HullId: 447, SuccessorHullId: -1,
            MaxWeapons: (byte)1, MaxFixedWeapons: (byte)1, HitPoints: 806f, HardpointOffset: 862,
            DefenseType: (byte)0, CapacityMagazine: 859, CapacityDispenser: 858,
            CapacityChaffLauncher: 885, PreferredPartsTypes: Array.Empty<short>(),
            HabmCapabilities: DataHullTypeIGC.HabmLifepod, TextureName: "lifepod",
            PmEquipment: new short[8], InteriorSound: 873, ExteriorSound: 884,
            MainThrusterInteriorSound: 892, MainThrusterExteriorSound: 898,
            ManuveringThrusterInteriorSound: 914, ManuveringThrusterExteriorSound: 902,
            Hardpoints: Array.Empty<DataHullTypeIGC.Hardpoint>());

        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);
        var ship = NewShip(9906, lifepod, basePos);
        mission.PlaceShip(ship, sector);
        ship.Energy = 357f;              // below max 522, so recharge has room before clamp

        double t0 = mission.LastUpdate;
        mission.Update(t0 + 0.061);
        double elapsed = mission.LastUpdate - t0;
        float expected = 357f + lifepod.RechargeRate * (float)elapsed;
        Assert.True(MathF.Abs(ship.Energy - expected) < 0.05f,
            $"a lifepod in flight must recharge like any in-cluster hull ({357:F1} + {lifepod.RechargeRate:F1}×{elapsed:F3} = {expected:F1}, got {ship.Energy:F1})");
    }

    // ── the HUD energy gauge is driven by the server's energy fraction ──────

    /// <summary>
    /// Item 4 — the HUD energy gauge (HudRenderer.cs:272,372) reads the
    /// own-ship <see cref="ServerShipUpdate"/>'s energy fraction (byte 44).
    /// Before P461 that byte was hardcoded to <c>1.0</c> (a flight-demo
    /// constant), so the gauge could never show the drain NOR the recharge —
    /// the premise "it's driven by the server's energy fraction" was false for
    /// the own-ship path. P461 wires it to <c>Energy / MaxEnergy</c> (the same
    /// 47-byte slot), so the gauge now reflects a drained pool and, once
    /// recharge exists, visibly climbs back. MUST-FAIL on unfixed code: a
    /// drained pool reads 1.0 (full) instead of low.
    /// </summary>
    [Fact]
    public void Own_ship_update_reports_real_energy_fraction()
    {
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);
        var ship = NewShip(9907, Hull.Value, basePos);
        mission.PlaceShip(ship, sector);

        ship.Energy = ship.EffectiveMaxEnergy() * 0.1f;   // drained to 10%
        float low = ServerShipUpdate.FromModel(ship.Id, ship, 1u).EnergyFraction;
        Assert.True(low <= 0.11f && low >= 0.0f,
            $"a drained pool must read low on the wire (the HUD gauge); got {low:F3}");

        ship.Energy = ship.EffectiveMaxEnergy();          // recharged / refitted full
        float full = ServerShipUpdate.FromModel(ship.Id, ship, 1u).EnergyFraction;
        Assert.Equal(1f, full, precision: 2);
    }
}
