using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Xunit.Abstractions;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P412 — the real fuel pool (<see cref="Ship.Fuel"/>/<see cref="Ship.SetFuel"/>)
/// and the real afterburner thrust boost (<see cref="Ship.UpdateAfterburner"/> +
/// the ExecuteMove thrust branch). Native citations:
///   fuel pool           shipIGC.h:623-651 (GetFuel/SetFuel)
///   afterburner sim     afterburnerIGC.cpp:60-63,103-164 (Update→IncrementalUpdate)
///   ExecuteMove boost   shipIGC.cpp:2939-2959 (thrustRatio + drag)
/// Proved here deterministically on real <see cref="Ship.Update"/> ticks:
///   (1) fuel depletes at EXACTLY native's rate (power·fuelConsumption·maxThrust·dt)
///       while the button is held at full power, and stops draining ("out of
///       gas", deactivated) the moment it hits zero;
///   (2) the afterburner produces a REAL, measurable flight-model difference —
///       an engaged ship cruises faster than an unmounted ship holding the same
///       button (the thrustRatio/target-speed effect, shipIGC.cpp:2992-2993);
///   (3) fuel refills to the real max from each touch point: a fuel-pack RELOAD
///       drain, station RepairAndRefuel, and a c_tcPowerup fuel-treasure pickup.
/// </summary>
public class FuelPoolAndAfterburnerTests
{
    private readonly ITestOutputHelper _output;

    public FuelPoolAndAfterburnerTests(ITestOutputHelper output) => _output = output;

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

    /// <summary>A real hull with PINNED Speed/Thrust/MaxFuel so the afterburner
    /// maths and the cruise-speed expectations are exact and deterministic.</summary>
    private static readonly Lazy<DataHullTypeIGC> PinnedHull = new(() =>
    {
        var core = RepresentativeCore.Value;
        var baseHull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        return baseHull with { Speed = 100f, Thrust = 1000f, MaxFuel = 1000f };
    });

    private static Ship NewShip()
    {
        var hull = PinnedHull.Value;
        return new Ship(shipId: 41, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
    }

    /// <summary>A constructed <c>ET_Afterburner</c> part (<c>DataAfterburnerTypeIGC</c>,
    /// igc.h:1865) with pinned stats — the same construction-only part pattern the
    /// auto-reload/refuel suites use for their crafted parts.</summary>
    private static DataPartTypeIGC AfterburnerType(float fuelConsumption, float maxThrust, float onRate, float offRate) => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ab", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
        PartId: 9300, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
        Type: new DataPartTypeIGC.Afterburner(
            FuelConsumption: fuelConsumption, MaxThrust: maxThrust,
            OnRate: onRate, OffRate: offRate, InteriorSound: 0, ExteriorSound: 0));

    /// <summary>A constructed <c>c_packFuel</c> pack part (<c>igc.h:550</c>).</summary>
    private static DataPartTypeIGC FuelPackType(short amount) => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "fuel", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
        PartId: 9301, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
        Type: new DataPartTypeIGC.Pack(PackType: 1, Amount: amount));   // c_packFuel

    private static void Pump(Ship ship, double start, double dt, int ticks)
    {
        double t = start;
        for (int i = 0; i < ticks; i++)
        {
            t += dt;
            ship.Update(t);
        }
    }

    // ── (1) depletion at native's rate + out-of-gas ─────────────────────────

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: with the afterburner mounted (fully
    /// mounted, so no mount-ramp gate), the button held, and power saturated,
    /// fuel drains at EXACTLY native's <c>power·fuelConsumption·maxThrust·dt</c>
    /// rate (<c>afterburnerIGC.cpp:148-160</c>). We pin onRate to saturate power
    /// to 1.0 on the first tick, so every tick drains <c>cons·maxThrust·dt</c> —
    /// an exact, not approximate, depletion.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fuel_depletes_at_exactly_natives_rate_while_button_held()
    {
        Require.Artwork();
        var ship = NewShip();
        ship.MountPart(AfterburnerType(fuelConsumption: 2f, maxThrust: 10f, onRate: 1e6f, offRate: 1e6f), amount: 1, mountId: 0, fractionMounted: 255);
        ship.StateM |= ShipControlState.AfterburnerButton;

        float start = ship.Fuel;                                   // = MaxFuel = 1000
        const double dt = 0.05;
        const int Ticks = 10;                                      // 0.5 s at full power
        Pump(ship, start: 0.0, dt, Ticks);

        float burnPerSec = 2f * 10f;                               // cons·maxThrust at power 1.0
        float expected = start - burnPerSec * (float)(dt * Ticks); // 1000 - 20·0.5 = 990
        Assert.Equal(expected, ship.Fuel, 3);
        _output.WriteLine($"P412-DEPLETION: start={start}, after 0.5s fuel={ship.Fuel:F3}, expected={expected:F3}");

        // Sanity: it actually moved and stayed within [0, max].
        Assert.InRange(ship.Fuel, 0f, start);
        Assert.True(ship.Fuel < start, "holding the button with fuel must deplete the pool");
    }

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: draining to zero "runs out of gas" —
    /// <c>SetFuel(0)</c> + <c>Deactivate()</c> (<c>afterburnerIGC.cpp:155-159</c>).
    /// The pool hits EXACTLY zero, the afterburner deactivates (no further drain,
    /// no negative), the depleted-notification debounce path records
    /// <c>Depleted</c> (no fuel pack in cargo → native's "Fuel depleted."), and
    /// the powered drag-boost ACCELERATION ceases — the per-tick speed gain
    /// collapses at exhaustion (native's power-gated drag term,
    /// shipIGC.cpp:2952-2957).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fuel_runs_out_of_gas_at_zero_and_stops_draining()
    {
        Require.Artwork();
        var ship = NewShip();
        ship.MountPart(AfterburnerType(fuelConsumption: 2f, maxThrust: 200f, onRate: 1e6f, offRate: 1e6f), amount: 1, mountId: 0, fractionMounted: 255);
        ship.StateM |= ShipControlState.AfterburnerButton;
        ship.Controls = ship.Controls with { Throttle = 1f };

        double t = 0.0;
        const double dt = 0.025;
        float prevSpeed = 0f;
        double lastFuel = ship.Fuel;
        int guard = 0;
        double fueledSumDelta = 0, fueledSamples = 0, drySumDelta = 0, drySamples = 0;
        bool wentDry = false;
        while (guard++ < 4000)
        {
            t += dt;
            ship.Update(t);
            float spd = ship.Velocity.Length();
            float dSpeed = spd - prevSpeed;
            prevSpeed = spd;
            if (wentDry) { drySumDelta += dSpeed; drySamples++; }
            else { fueledSumDelta += dSpeed; fueledSamples++; }
            if (ship.Fuel <= 0f && lastFuel > 0f) wentDry = true;
            lastFuel = ship.Fuel;
            if (wentDry && drySamples > 40) break;
        }

        Assert.Equal(0f, ship.Fuel);                       // out of gas at EXACTLY zero
        Assert.Equal(AutoReloadOutcome.Depleted, ship.LastFuelAutoReloadOutcome);

        // The powered drag-boost punch (avg per-tick speed gain while fueling)
        // drops once fuel is exhausted — that is the faithful "cuts out". NB
        // native's thrustRatio target-speed (shipIGC.cpp:2947-2949) is NOT fuel-
        // gated, so some residual acceleration toward that target persists after
        // exhaustion; the powered boost (the drag term, :2952-2957) is what stops.
        double fueledAvg = fueledSamples > 0 ? fueledSumDelta / fueledSamples : 0;
        double dryAvg = drySamples > 0 ? drySumDelta / drySamples : 0;
        _output.WriteLine($"P412-OUTOFGAS: avg Δspeed/tick fueled={fueledAvg:F4}, dry={dryAvg:F4} (drained to {ship.Fuel} after {guard} ticks)");
        Assert.True(dryAvg < fueledAvg * 0.75,
            $"afterburner acceleration must drop on fuel exhaustion (fueled {fueledAvg:F4} vs dry {dryAvg:F4})");

        // Further ticks with the button held: deactivated, so fuel stays 0 (never negative).
        Pump(ship, t, dt, 20);
        Assert.Equal(0f, ship.Fuel);
    }

    // ── (2) real flight-model difference ─────────────────────────────────────

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: engaging the afterburner produces a real,
    /// measurable speed change in <see cref="Ship.ExecuteMove"/>. Three identical
    /// hulls cruise under the same throttle-cruise decision tree:
    ///   • ENGAGED  — afterburner mounted + button held + power ramped → target
    ///                speed <c>maxSpeed·(1 + thrustRatio)</c> (shipIGC.cpp:2992-2993),
    ///                thrustRatio = maxThrust/thrust &gt; 0;
    ///   • INACTIVE — afterburner mounted + button NOT held, half throttle →
    ///                ordinary throttle cruise at <c>0.5·maxSpeed</c>
    ///                (<c>−0.5·(1+Throttle)·maxSpeed</c>, shipIGC.cpp:2995-2996);
    ///   • UNMOUNTED— no afterburner + button held → the pre-P412 hardcoded
    ///                <c>thrustRatio == 0</c> cruise (≤ maxSpeed).
    /// The engaged ship must cruise measurably faster than the unmounted ship
    /// holding the same button — the actual point of the task.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Engaged_afterburner_cruises_faster_than_unmounted_or_inactive()
    {
        Require.Artwork();
        const float MaxThrust = 5000f;      // >> hull thrust (1000) → clear thrustRatio

        Ship Engage() { var s = NewShip(); s.MountPart(AfterburnerType(0.0001f, MaxThrust, 1e6f, 1e6f), 1, 0, 255); s.StateM |= ShipControlState.AfterburnerButton; return s; }
        Ship Inactive() { var s = NewShip(); s.MountPart(AfterburnerType(0.0001f, MaxThrust, 1e6f, 1e6f), 1, 0, 255); s.Controls = s.Controls with { Throttle = 0f }; return s; }
        Ship Unmounted() { var s = NewShip(); s.StateM |= ShipControlState.AfterburnerButton; return s; }

        var engaged = Engage();
        var inactive = Inactive();
        var unmounted = Unmounted();

        const double dt = 0.006;
        const int Ticks = 2500;            // 15 s of sim — plenty to reach steady cruise
        Pump(engaged, 0.0, dt, Ticks);
        Pump(inactive, 0.0, dt, Ticks);
        Pump(unmounted, 0.0, dt, Ticks);

        float ve = engaged.Velocity.Length();
        float vi = inactive.Velocity.Length();
        float vu = unmounted.Velocity.Length();

        _output.WriteLine($"P412-SPEED: engaged={ve:F1}, inactive={vi:F1}, unmounted={vu:F1} (hull maxSpeed=100)");
        Assert.True(ve > vu, $"engaged ({ve:F1}) must out-cruise the unmounted ship holding the same button ({vu:F1})");
        Assert.True(ve > vi, $"engaged ({ve:F1}) must out-cruise the inactive (button not held) ship ({vi:F1})");
        // A clear, non-noise margin: engaged is well past the plain maxSpeed cruise.
        Assert.True(ve > vu * 1.5f, $"engaged speed must be decisively above the unmounted cruise (ve={ve:F1}, vu={vu:F1})");
    }

    // ── (3) refill to the real max from each touch point ────────────────────

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: draining a <c>c_packFuel</c> pack through
    /// the RELOAD applicator (<c>FedSrv.CPP:6583-6591</c>) tops <see cref="Ship.Fuel"/>
    /// up to the REAL hull max (SetFuel clamps, shipIGC.h:629-631) — not an
    /// arbitrary value.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fuel_pack_reload_drain_refills_to_the_real_max()
    {
        Require.Artwork();
        var ship = NewShip();
        ship.SetFuel(0f);
        ship.MountPart(FuelPackType(amount: 5000), amount: 5000, mountId: 1, fractionMounted: 255);
        Assert.True(ship.TrySwapPartWithCargo(EquipmentType.Pack, 1, -1), "fuel pack must land in cargo");

        // NA (-1) terminates the whole pack and adds its full amount to Fuel;
        // SetFuel clamps to MaxFuel (1000), leaving the excess discarded.
        Assert.True(ship.TryApplyReloadEntry(mount: -1, amountTransfered: -1));

        Assert.Equal(PinnedHull.Value.MaxFuel, ship.Fuel, 3);          // exactly the real max
        Assert.Null(ship.CargoHold[Ship.CargoSlotForMount(-1)]);       // whole pack consumed
    }

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: a station launch top-up
    /// (<c>RepairAndRefuel</c>, <c>stationIGC.cpp:474</c>) restores Fuel to the
    /// real hull max — the previously-documented no-op is now a real refill.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Station_repair_and_refuel_tops_fuel_to_the_real_max()
    {
        Require.Artwork();
        var ship = NewShip();
        ship.SetFuel(0f);
        var garrison = RepresentativeCore.Value.StationTypes[15];     // real Garrison: both sabm bits

        ship.RepairAndRefuel(garrison);

        Assert.Equal(PinnedHull.Value.MaxFuel, ship.Fuel, 3);         // exactly the real max
    }

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: a <c>c_tcPowerup</c> fuel-treasure pickup
    /// (<c>shipIGC.h:1342</c>: <c>if (objectID &amp; c_pcFuel) SetFuel(FLT_MAX)</c>)
    /// refills to the real hull max via <see cref="Ship.ApplyPowerup"/>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fuel_treasure_pickup_refills_to_the_real_max()
    {
        Require.Artwork();
        var ship = NewShip();
        ship.SetFuel(0f);

        ship.ApplyPowerup((int)PowerupCode.Fuel);

        Assert.Equal(PinnedHull.Value.MaxFuel, ship.Fuel, 3);         // exactly the real max
    }

    // ── LIVE proof: a REAL server-authoritative ship, real sim loop ─────────

    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg LiveConfig(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Fuel Live", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    /// <summary>
    /// ⭐ LIVE PROOF (P412): a REAL server-authoritative ship (a real PCore001
    /// hull placed in a real XXV_simple sector of a booted server) holding the
    /// afterburner button, driven by the REAL per-tick sim loop
    /// (<c>Server.TickSim</c> → <c>Mission.Update</c> → <c>Ship.OnUpdate</c> →
    /// ExecuteMove + UpdateAfterburner). Quantitatively confirms:
    ///   (a) a real speed INCREASE — while fuelled the ship climbs well past the
    ///       un-boosted full-throttle cruise (hull.Speed), the real effect of
    ///       the mounted afterburner's thrustRatio/drag (shipIGC.cpp:2939-2959);
    ///   (b) fuel DEPLETES over time at the real sim rate and hits EXACTLY zero
    ///       ("out of gas");
    ///   (c) the powered acceleration CUTS OUT at exhaustion (the per-tick speed
    ///       gain drops), then fuel stays 0 (deactivated).
    /// No network bot is seated (avoiding the LiveNetwork collection) — the ship
    /// is still the server's real authoritative sim object in the real loop.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_real_server_afterburner_flight_depletes_fuel_and_cuts_out()
    {
        Require.Artwork();
        const short FlySector = 1052;   // XXV_simple: no-station fly sector (P111)

        using var server = new Srv();
        server.Boot(LiveConfig());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        // Place a REAL authoritative ship into the booted server's real mission
        // (the same PlaceShip the P411 suite uses on a booted server).
        var mission = server.Missions[0];
        var fly = mission.Sectors.First(s => s.Id == FlySector);
        // A real PCore001 fight hull, with its tank/engine PINNED so the boost is
        // sustained and deterministic (Speed/Thrust/MaxFuel pinned — the same
        // `with` treatment the RepairAndRefuel suite uses; the afterburner MECHANISM
        // is what is under test, not this particular hull's stock tank size).
        var hull = mission.Core.HullTypes.Values
            .OrderBy(h => h.HullId)
            .First(h => h.Hardpoints.Length >= 1 && h.Hardpoints[0].Fixed)
            with { MaxFuel = 200f, Thrust = 1000f };
        var ship = new Ship(997, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 770 };
        mission.PlaceShip(ship, fly);
        // Burn 0.005·5000 = 25 fuel/s at full power → 200 fuel lasts ~8 s of boost.
        ship.MountPart(AfterburnerType(0.005f, 5000f, 1e6f, 1e6f), 1, 0, 255);   // real AB

        // Held on the afterburner + full forward throttle (the server is the
        // authority; this is the StateM the client's held button produces).
        ship.StateM |= ShipControlState.AfterburnerButton;
        ship.Controls = ship.Controls with { Throttle = 1f };

        float maxSpeed = ship.HullType.Speed;
        float fuelStart = ship.Fuel;
        double t = 0.0;
        const double dt = 1.0 / 20.0;   // real 20 Hz cadence
        const int Ticks = 400;          // 20 s of real sim
        float peakWhileFuelled = 0f;
        bool wentDry = false;
        double fueledSumDelta = 0, fueledSamples = 0, drySumDelta = 0, drySamples = 0;
        double lastFuel = ship.Fuel;
        double drainedAt = -1;
        for (int i = 0; i < Ticks; i++)
        {
            float prevSpeed = ship.Velocity.Length();
            server.TickSim();
            t += dt;
            float spd = ship.Velocity.Length();
            float dSpeed = spd - prevSpeed;
            if (wentDry) { drySumDelta += dSpeed; drySamples++; }
            else
            {
                fueledSumDelta += dSpeed; fueledSamples++;
                if (spd > peakWhileFuelled) peakWhileFuelled = spd;
            }
            if (ship.Fuel <= 0f && lastFuel > 0f) { wentDry = true; drainedAt = t; }
            lastFuel = ship.Fuel;
        }

        double fueledAvg = fueledSamples > 0 ? fueledSumDelta / fueledSamples : 0;
        double dryAvg = drySamples > 0 ? drySumDelta / drySamples : 0;

        _output.WriteLine($"P412-LIVE: hull maxSpeed={maxSpeed}, fuelStart={fuelStart:F1}; " +
                          $"peak fuelled speed={peakWhileFuelled:F1}, final speed={ship.Velocity.Length():F1}, " +
                          $"drainedAt={drainedAt:F1}s, final fuel={ship.Fuel:F3}; " +
                          $"avg Δspeed/tick fueled={fueledAvg:F4}, dry={dryAvg:F4}");

        // (a) the afterburner produced a REAL, measurable speed increase past the
        //     hull's own maxSpeed cruise (the point of the task);
        Assert.True(peakWhileFuelled > maxSpeed * 1.5f,
            $"fuelled peak {peakWhileFuelled:F1} must decisively exceed the hull cruise {maxSpeed}");
        // (b) fuel really depletes and hits exactly zero ("out of gas");
        Assert.True(drainedAt > 0, "fuel must deplete to zero while holding the button");
        Assert.Equal(0f, ship.Fuel);
        // (c) the powered acceleration cuts out at exhaustion.
        Assert.True(dryAvg < fueledAvg * 0.75,
            $"powered acceleration must drop at exhaustion (fueled {fueledAvg:F4} vs dry {dryAvg:F4})");
    }
}
