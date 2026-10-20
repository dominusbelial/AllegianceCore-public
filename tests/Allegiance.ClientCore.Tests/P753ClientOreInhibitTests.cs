using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>P753 — SIM-0044 client-side ore update inhibit after own-ship
/// sector entry (native trekigc.cpp:4057-4064).</summary>
public class P753ClientOreInhibitTests
{
    private static DataAsteroidIGC MakeMineableAsteroid(short id, float ore = 1000f) => new(
        Signature: 100f, Position: [0f, 0f, 0f], Up: [0f, 1f, 0f],
        Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
        Ore: ore, OreMax: ore, AabmCapabilities: Asteroid.CapMineHe3,
        AsteroidId: id, HitPoints: 100f, Radius: 50, ModelName: "roid",
        TextureName: "", IconName: "", ClusterId: 7, Name: $"rock_{id}",
        Fraction: (byte)0xFF);

    private static ClientWorld SeededWorld(short asteroidId)
    {
        var world = new ClientWorld();
        world.Apply(new ExportAsteroid { ObjectType = ExportAsteroid.OtAsteroid, Asteroid = MakeMineableAsteroid(asteroidId) });
        return world;
    }

    private static ServerShipUpdate ShipUpdate(short shipId, short sectorId) =>
        ServerShipUpdate.FromValues(
            shipId: shipId, stateM: 0, controls: default,
            time: 0, position: Vector3.Zero, orientation: Orientation.Identity,
            velocity: Vector3.Zero, turnRates: [0f, 0f, 0f],
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 1f);

    [Fact]
    public void Own_sector_entry_inhibits_ore_updates_for_three_seconds()
    {
        double fakeNow = 1000.0;
        var world = SeededWorld(asteroidId: 701);
        world.NowSeconds = () => fakeNow;
        const short shipId = 42;

        // Baseline ore fraction established before the sector change.
        var baseline = new Asteroid(MakeMineableAsteroid(701, 1000f));
        baseline.MineOre(200f);
        world.Apply(AsteroidsUpdate.FromAsteroids([baseline]));
        Assert.Equal(0.80f, world.AsteroidLive[701].OreFraction, 3);

        // Own ship enters the sector: inhibit is applied for 3 wall-clock seconds.
        world.Apply(new SetCluster { SectorId = 7, Ship = ShipUpdate(shipId, 7), Cookie = 1 });
        Assert.True(world.AsteroidLive[701].InhibitOreExpiry > fakeNow);

        // Server now reports the asteroid as nearly depleted.
        var depleted = new Asteroid(MakeMineableAsteroid(701, 1000f));
        depleted.MineOre(900f);

        // While inhibited, the client keeps the previous ore fraction.
        fakeNow += 2.0;
        world.Apply(AsteroidsUpdate.FromAsteroids([depleted]));
        Assert.Equal(0.80f, world.AsteroidLive[701].OreFraction, 3);

        // After the inhibit expires, the client accepts the new value.
        fakeNow += 2.0;
        world.Apply(AsteroidsUpdate.FromAsteroids([depleted]));
        Assert.Equal(0.10f, world.AsteroidLive[701].OreFraction, 3);
    }

    [Fact]
    public void Sector_entry_does_not_create_zeroed_live_state_for_unseen_asteroids()
    {
        double fakeNow = 1000.0;
        var world = SeededWorld(asteroidId: 702);
        world.NowSeconds = () => fakeNow;

        // No asteroid live-state entry exists before the own ship enters.
        Assert.False(world.AsteroidLive.ContainsKey(702));

        // Own ship enters the asteroid's sector.
        world.Apply(new SetCluster { SectorId = 7, Ship = ShipUpdate(42, 7), Cookie = 1 });

        // SIM-0044 bug: the sector-entry inhibit must NOT fabricate a zeroed
        // AsteroidLiveState(0,0,…) — SelectionPaneModel.cs:27 would then show a
        // bogus 0% hull/ore until the first ASTEROIDS_UPDATE.
        Assert.False(world.AsteroidLive.ContainsKey(702));

        // A real ASTEROIDS_UPDATE still creates the entry through the normal path.
        world.Apply(AsteroidsUpdate.FromAsteroids([new Asteroid(MakeMineableAsteroid(702, 1000f))]));
        Assert.True(world.AsteroidLive.ContainsKey(702));
        Assert.Equal(1f, world.AsteroidLive[702].OreFraction, 3);
    }
}
