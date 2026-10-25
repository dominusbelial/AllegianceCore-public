using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P803 (SIM-0470) — the CLIENT half of the HEAVY_SHIPS_UPDATE(176)
/// active-turret relay. Native's client walks <c>rgActiveTurretUpdates</c>
/// (<c>clintlib/appmsg.cpp:776-794</c>), requires the ship to be a known
/// BOARD child (<c>pship-&gt;GetParentShip() &amp;&amp; pship-&gt;GetCluster()</c>,
/// :791-792), skips its own ship (the assert at :788) and applies
/// <c>ProcessShipUpdate(timeReference, atu)</c> (<c>shipIGC.cpp:3104-3137</c>) —
/// integrating the turret facing and latching the weapons stateM ("we're
/// shooting", :3359). The port stores both on the child entity, since a
/// boarded child streams no pose of its own and this relay is the only wire
/// source of a remote turret's facing. These tests fail on master (the client
/// dropped <c>ActiveTurretUpdates</c> entirely).
/// </summary>
public class P803TurretRelayTests
{
    private const short Host = 900, Gunner = 901;

    private static ClientWorld BoardedWorld()
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = Host, HullId = 15, SideId = 2, CharacterName = "host" });
        world.Apply(new ExportShip { ShipId = Gunner, HullId = 445, SideId = 2, CharacterName = "gunner" });
        world.Apply(new Allegiance.Core.Wire.LoadoutChange
        {
            SidShip = Host,
            HullId = 16,
            Parts = [],
            Passengers = [new PassengerData(Gunner, TurretId: 2)],
        });
        return world;
    }

    private static HeavyShipsUpdate HeavyWith(params ServerActiveTurretUpdate[] acts) => new()
    {
        TimeReference = 1000,
        PositionReference = Vector3.Zero,
        HeavyShipEntries = [],
        ActiveTurretUpdates = acts,
    };

    [Fact]
    public void Relayed_turret_update_stores_the_facing_and_firing_latch_on_the_boarded_child()
    {
        var world = BoardedWorld();
        var facing = new Orientation();
        facing.Set(new Vector3(0f, 0f, -1f), new Vector3(0f, 1f, 0f));
        world.Apply(HeavyWith(ServerActiveTurretUpdate.From(Gunner, 1000, 990,
            new ControlData(0.1f, -0.2f, 0f, 1f), facing)));

        var e = world.Ships[Gunner];
        Assert.Equal(Host, e.ParentId);          // still the boarded child
        Assert.NotNull(e.TurretFacing);
        Assert.True(e.TurretFiring);
        // The facing survives the wire round trip (CompactOrientation).
        Assert.True(facing.GetForward() * e.TurretFacing!.GetForward() > 0.99f);
    }

    [Fact]
    public void Relay_records_for_unknown_or_flying_ships_are_ignored()
    {
        var world = BoardedWorld();

        // The native gate: pship->GetParentShip() (appmsg.cpp:791) — a ship the
        // client does not know as boarded is not applied.
        const short flying = 903;
        world.Apply(new ExportShip { ShipId = flying, HullId = 445, SideId = 2, CharacterName = "flyer" });
        world.Apply(HeavyWith(
            ServerActiveTurretUpdate.From(flying, 1000, 990, default, new Orientation()),
            ServerActiveTurretUpdate.From(9999, 1000, 990, default, new Orientation())));

        Assert.Null(world.Ships[flying].TurretFacing);
        Assert.False(world.Ships[flying].TurretFiring);
        Assert.False(world.Ships.ContainsKey(9999));
        // The boarded child was NOT in this relay — still clean.
        Assert.Null(world.Ships[Gunner].TurretFacing);
    }

    [Fact]
    public void Unboarding_clears_the_relayed_turret_state()
    {
        var world = BoardedWorld();
        world.Apply(HeavyWith(ServerActiveTurretUpdate.From(Gunner, 1000, 990, default, new Orientation())));
        Assert.NotNull(world.Ships[Gunner].TurretFacing);

        // LEAVESHIP detaches the child (appmsg.cpp:1914-1945); a LOADOUT_CHANGE
        // for the child clears the parent link — and with it the turret state.
        world.Apply(new Allegiance.Core.Wire.LoadoutChange { SidShip = Gunner, HullId = 445, Parts = [], Passengers = [] });
        Assert.Equal((short)-1, world.Ships[Gunner].ParentId);
        Assert.Null(world.Ships[Gunner].TurretFacing);
        Assert.False(world.Ships[Gunner].TurretFiring);
    }
    [Fact]
    public void P788c_LIGHT_stops_a_known_boarded_gunner_preserving_facing_and_ignoring_unknown_ids()
    {
        var world = BoardedWorld();
        world.Apply(HeavyWith(ServerActiveTurretUpdate.From(Gunner, 1000, 990,
            ControlData.Neutral, new Orientation())));
        var facing = world.Ships[Gunner].TurretFacing;
        world.Apply(LightShipsUpdate.Decode(new LightShipsUpdate
            { InactiveTurretShipIds = [Gunner, 32000] }.Encode()));
        Assert.False(world.Ships[Gunner].TurretFiring);
        Assert.Same(facing, world.Ships[Gunner].TurretFacing);
        Assert.False(world.Ships.ContainsKey(32000));
    }
}
