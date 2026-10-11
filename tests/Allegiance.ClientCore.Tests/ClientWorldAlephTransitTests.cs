using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P585 — client-side aleph-transit-signal detection in <see cref="ClientWorld"/>:
/// a ship crossing an aleph in the viewer's sector is turned into an
/// <see cref="AlephTransitSignal"/> that the renderer drains to drive the opt-in
/// transit pulse. Signals come from three wire edges: SHIP_DELETE SDR_LEFTSECTOR
/// (a ship warped OUT of the viewer's sector), SET_CLUSTER (the own ship's warp
/// ARRIVAL into a new sector), and re-sighting near an aleph (another ship's
/// arrival). Failing-first: none of these existed before P585.
/// </summary>
public class ClientWorldAlephTransitTests
{
    private static DataWarpIGC Warp(short id, short cluster, Vector3 pos, short radius = 60)
        => new(id, DestinationId: (short)(id + 900), Radius: radius,
            TextureName: "aleph", IconName: "", Name: "w",
            Position: new[] { pos.X, pos.Y, pos.Z }, Forward: new[] { 0f, 0f, -1f },
            Rotation: new[] { 0f, 0f, 0f, 1f }, Signature: 0f, ClusterId: cluster);

    private static void SeedWarp(ClientWorld w, DataWarpIGC warp)
        => w.Apply(new ExportWarp { ObjectType = ExportWarp.OtWarp, Warp = warp });

    private static ServerShipUpdate PoseFor(short id, Vector3 pos) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 1u,
            pos, Orientation.Identity, Vector3.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f);

    private static SetCluster SetClusterFor(short myId, short sector, Vector3 pos) => new()
    {
        SectorId = sector,
        Ship = PoseFor(myId, pos),
        Cookie = 0x1,
    };

    private static void FlyShip(ClientWorld w, short id, Vector3 pos, uint time = 100u, Vector3? velocity = null)
    {
        var posRef = new Vector3(pos.X, pos.Y, pos.Z);
        w.Apply(new HeavyShipsUpdate
        {
            TimeReference = time,
            PositionReference = posRef,
            HeavyShipEntries =
            [
                HeavyShipUpdateEntry.From(time, posRef, id, 0,
                    new ControlData(0f, 0f, 0f, 0f), time, posRef,
                    new Orientation(), velocity ?? Vector3.Zero, new[] { 0f, 0f, 0f },
                    1f, 1f, 1f),
            ],
        });
    }

    [Fact]
    public void Departure_ThroughWarpInMySector_SignalsThatWarp()
    {
        var w = new ClientWorld();
        w.Apply(SetClusterFor(5, sector: 1, Vector3.Zero));      // my ship in sector 1, nowhere near a warp
        var warp = Warp(11, cluster: 1, new Vector3(200f, 0f, 0f));
        SeedWarp(w, warp);
        FlyShip(w, 7, new Vector3(200f, 0f, 0f));                // a bot at the aleph

        w.Apply(new ShipDelete { ShipId = 7, Sdr = ShipDelete.SdrLeftSector });

        var signals = w.DrainAlephTransits();
        Assert.Single(signals);
        Assert.Equal(warp.WarpId, signals[0].WarpId);
    }

    [Fact]
    public void Departure_NotNearAWarp_InMySector_DoesNotSignal()
    {
        var w = new ClientWorld();
        w.Apply(SetClusterFor(5, sector: 1, Vector3.Zero));
        SeedWarp(w, Warp(11, cluster: 1, new Vector3(500f, 0f, 0f)));
        FlyShip(w, 7, new Vector3(10f, 10f, 10f));               // far from every aleph

        w.Apply(new ShipDelete { ShipId = 7, Sdr = ShipDelete.SdrLeftSector });

        Assert.Empty(w.DrainAlephTransits());                    // no fake pulse
    }

    [Fact]
    public void Departure_NearAWarpInAnotherSector_DoesNotSignal()
    {
        var w = new ClientWorld();
        w.Apply(SetClusterFor(5, sector: 1, Vector3.Zero));      // I'm in sector 1…
        // …but the only nearby aleph lives in sector 2, which I am NOT in.
        SeedWarp(w, Warp(11, cluster: 2, new Vector3(200f, 0f, 0f)));
        FlyShip(w, 7, new Vector3(200f, 0f, 0f));

        w.Apply(new ShipDelete { ShipId = 7, Sdr = ShipDelete.SdrLeftSector });

        Assert.Empty(w.DrainAlephTransits());                    // other sectors never fire
    }

    [Fact]
    public void Departure_DockingOrLoggingOff_DoesNotSignal()
    {
        var w = new ClientWorld();
        w.Apply(SetClusterFor(5, sector: 1, Vector3.Zero));
        var warp = Warp(11, cluster: 1, new Vector3(200f, 0f, 0f));
        SeedWarp(w, warp);
        FlyShip(w, 7, new Vector3(200f, 0f, 0f));

        w.Apply(new ShipDelete { ShipId = 7, Sdr = ShipDelete.SdrDocked });
        Assert.Empty(w.DrainAlephTransits());                    // docking ≠ warp crossing
        w.Apply(new ShipDelete { ShipId = 7, Sdr = ShipDelete.SdrLoggedOff });
        Assert.Empty(w.DrainAlephTransits());                    // logger ≠ warp crossing
    }

    [Fact]
    public void OwnArrival_IntoANewSectorNearItsAleph_SignalsThatAleph()
    {
        var w = new ClientWorld();
        w.Apply(SetClusterFor(5, sector: 1, Vector3.Zero));      // join sector 1 at origin
        var destWarp = Warp(33, cluster: 2, new Vector3(500f, 0f, 0f));
        SeedWarp(w, destWarp);

        // My ship emerges beside the sector-2 aleph (a warp transit landing).
        w.Apply(SetClusterFor(5, sector: 2, new Vector3(500f, 0f, 0f)));

        var signals = w.DrainAlephTransits();
        Assert.Single(signals);
        Assert.Equal(destWarp.WarpId, signals[0].WarpId);
    }

    [Fact]
    public void OwnSetCluster_WithoutSectorChange_DoesNotSignal()
    {
        var w = new ClientWorld();
        w.Apply(SetClusterFor(5, sector: 1, Vector3.Zero));      // join sector 1
        SeedWarp(w, Warp(33, cluster: 1, new Vector3(20f, 0f, 0f)));

        // A SET_CLUSTER re-seating us in the SAME sector (no warp transit).
        w.Apply(SetClusterFor(5, sector: 1, new Vector3(0f, 0f, 0f)));

        Assert.Empty(w.DrainAlephTransits());
    }

    [Fact]
    public void Arrival_ReSightingNearAnAleph_SignalsIt()
    {
        var w = new ClientWorld();
        w.Apply(SetClusterFor(5, sector: 1, Vector3.Zero));
        var warp = Warp(44, cluster: 1, new Vector3(300f, 0f, 0f));
        SeedWarp(w, warp);
        FlyShip(w, 9, Vector3.Zero);                             // establish far from the aleph
        Assert.Empty(w.DrainAlephTransits());

        w.Apply(new ShipDelete { ShipId = 9, Sdr = ShipDelete.SdrLoggedOff }); // out of view
        Assert.Empty(w.DrainAlephTransits());

        // Re-sighted beside the aleph, moving OUTWARD from it (a warp arrival).
        FlyShip(w, 9, new Vector3(320f, 0f, 0f), time: 200u, velocity: new Vector3(70f, 0f, 0f));
        var signals = w.DrainAlephTransits();
        Assert.Single(signals);
        Assert.Equal(warp.WarpId, signals[0].WarpId);
    }

    // ── P599 — the transit pulse also fires for VISIBLE ENEMY ships ─────────

    /// <summary>
    /// P599 failing-first: an ENEMY crossing into the viewer's sector is a
    /// FIRST-EVER sighting (the server only sends cross-side ships while they
    /// fly in our sector, so there is no prior IsGone for it to re-sight). It
    /// must still flare the arrival aleph when it first appears just past the
    /// aleph moving outward. Fails on P585 code (which only signed re-sightings
    /// after IsGone), passes with the P599 first-sighting widening.
    /// </summary>
    [Fact]
    public void EnemyFirstSighting_AtAlephMovingOutward_SignalsThatAleph()
    {
        var w = new ClientWorld();
        w.Apply(SetClusterFor(5, sector: 1, Vector3.Zero));   // I'm in sector 1
        var warp = Warp(50, cluster: 1, new Vector3(200f, 0f, 0f));  // radius 60 → attribution 120
        SeedWarp(w, warp);

        // Brand-new enemy ship 8: first ever pose in my sector, 20u past the
        // aleph centre, moving straight OUT (right along the warp axis).
        FlyShip(w, 8, new Vector3(220f, 0f, 0f), velocity: new Vector3(60f, 0f, 0f));

        var signals = w.DrainAlephTransits();
        Assert.Single(signals);
        Assert.Equal(warp.WarpId, signals[0].WarpId);
        Assert.Equal((short)8, signals[0].ShipId);   // P599 — the signal names the enemy ship (side evidence)
    }

    [Fact]
    public void FirstSighting_NearAlephMovingInward_DoesNotSignal()
    {
        var w = new ClientWorld();
        w.Apply(SetClusterFor(5, sector: 1, Vector3.Zero));
        var warp = Warp(51, cluster: 1, new Vector3(200f, 0f, 0f));
        SeedWarp(w, warp);

        // New ship PAST the aleph but flying IN TOWARD it (about to depart,
        // not arriving) — the outward gate must suppress the arrival pulse.
        FlyShip(w, 8, new Vector3(220f, 0f, 0f), velocity: new Vector3(-60f, 0f, 0f));

        Assert.Empty(w.DrainAlephTransits());
    }

    [Fact]
    public void FirstSighting_FarFromAnyAleph_DoesNotSignal()
    {
        var w = new ClientWorld();
        w.Apply(SetClusterFor(5, sector: 1, Vector3.Zero));
        SeedWarp(w, Warp(52, cluster: 1, new Vector3(500f, 0f, 0f)));

        // New ship far from every aleph, whatever its velocity.
        FlyShip(w, 8, new Vector3(10f, 10f, 10f), velocity: new Vector3(60f, 0f, 0f));

        Assert.Empty(w.DrainAlephTransits());                    // no fake pulse
    }

    /// <summary>P599 — an enemy that was VISIBLE and vanishes via SDR_LEFTSECTOR
    /// while inside an aleph's radius IS a departure (non-kill): pulse it. This
    /// pins that the departure path serves cross-side ships too, not just the
    /// same-side path P585 keyed on.</summary>
    [Fact]
    public void VisibleEnemyVanishing_InsideAleph_NonKill_SignalsIt()
    {
        var w = new ClientWorld();
        w.Apply(SetClusterFor(5, sector: 1, Vector3.Zero));
        var warp = Warp(53, cluster: 1, new Vector3(200f, 0f, 0f));
        SeedWarp(w, warp);
        FlyShip(w, 8, new Vector3(205f, 0f, 0f));                // enemy 8 visible at the aleph

        w.Apply(new ShipDelete { ShipId = 8, Sdr = ShipDelete.SdrLeftSector });

        var signals = w.DrainAlephTransits();
        Assert.Single(signals);
        Assert.Equal(warp.WarpId, signals[0].WarpId);
        Assert.Equal((short)8, signals[0].ShipId);   // P599 — the departing ship is named too
    }

    /// <summary>P599 — a KILL (SDR_KILLED) inside an aleph's radius must NEVER
    /// pulse: dying is not a warp crossing.</summary>
    [Fact]
    public void Kill_InsideAleph_DoesNotSignal()
    {
        var w = new ClientWorld();
        w.Apply(SetClusterFor(5, sector: 1, Vector3.Zero));
        var warp = Warp(54, cluster: 1, new Vector3(200f, 0f, 0f));
        SeedWarp(w, warp);
        FlyShip(w, 8, new Vector3(205f, 0f, 0f));                // enemy 8 visible at the aleph

        w.Apply(new ShipDelete { ShipId = 8, Sdr = ShipDelete.SdrKilled });

        Assert.Empty(w.DrainAlephTransits());                    // death ≠ transit
    }
}
