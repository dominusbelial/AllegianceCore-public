using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P423 — the live per-asteroid / per-station condition ClientWorld now keeps
/// from the periodic ASTEROIDS_UPDATE(179) and STATIONS_UPDATE(57) syncs. The
/// headline test proves the wire-decoded per-asteroid ore fraction matches the
/// REAL server-side value: a genuine sim <see cref="Asteroid"/> is mined partway
/// (MineOre), packed by the same <see cref="AsteroidsUpdate.FromAsteroids"/> the
/// server's BroadcastAsteroidsUpdate uses, routed through ClientWorld's dispatch,
/// and the resulting <see cref="ClientWorld.AsteroidLive"/> ore fraction is
/// compared against the sim's own <c>Asteroid.OreFraction</c>. Pure-domain
/// (construct-then-Apply, no sockets — the ClientWorldUnitTests pattern).
/// </summary>
public class ClientWorldLiveAsteroidOreTests
{
    private static DataAsteroidIGC MakeAsteroid(short id, float ore = 1000f) => new(
        Signature: 722f, Position: [748f, 779f, 744f], Up: [793f, 772f, 728f],
        Forward: [771f, 723f, 778f], Rotation: [767f, 770f, 792f, 717f],
        Ore: ore, OreMax: ore, AabmCapabilities: 761, AsteroidId: id,
        HitPoints: 797f, Radius: 752, ModelName: "mdl", TextureName: "tex",
        IconName: "ico", ClusterId: 7, Name: $"rock_{id}", Fraction: (byte)0x61);

    private static DataStationIGC MakeStation(short id) => new(
        Position: [100f, 0f, 0f], Up: [0f, 791f, 703f], Forward: [713f, 714f, 715f],
        Rotation: [1f, 725f, 702f, 726f], ClusterId: 7, SideId: 793, StationId: id,
        StationTypeId: (short)0xA1, BpHull: (byte)0xA3, BpShield: (byte)0xB4, Name: $"garrison_{id}");

    /// <summary>Seed the static asteroid definition (the source of OreMax the
    /// client needs to derive the ore fraction) and the station definition.</summary>
    private static ClientWorld SeededWorld(short asteroidId, short stationId)
    {
        var world = new ClientWorld();
        world.Apply(new ExportAsteroid { ObjectType = ExportAsteroid.OtAsteroid, Asteroid = MakeAsteroid(asteroidId) });
        world.Apply(new ExportStation { ObjectType = ExportStation.OtStation, Station = MakeStation(stationId) });
        return world;
    }

    [Fact]
    public void Decoded_Ore_Fraction_Matches_Real_Server_Asteroid_After_Partial_Mine()
    {
        // A REAL sim asteroid, mid-depletion: mine it partway so its live
        // OreRemaining is genuinely reduced from OreMax (1000 -> 600 = 0.60).
        var ast = new Asteroid(MakeAsteroid(503, ore: 1000f));
        ast.MineOre(400f);
        float expectedOreFrac = ast.OreFraction;            // 0.60, live sim value

        // The exact pack the server's BroadcastAsteroidsUpdate uses each cadence
        // tick (FromAsteroids bakes the raw-ore + health-fraction conversions).
        var update = AsteroidsUpdate.FromAsteroids([ast]);

        var world = SeededWorld(asteroidId: 503, stationId: 789);
        world.Apply(update);                                // routes via Apply(IFedMessage)

        var live = world.AsteroidLive[503];
        Assert.Equal(expectedOreFrac, live.OreFraction, 3);
        // And the structural health fraction came through too (a fresh rock = full).
        Assert.Equal(ast.Fraction, live.Fraction, 3);
    }

    [Fact]
    public void Depleting_More_Decreases_Client_Ore_Fraction()
    {
        var ast = new Asteroid(MakeAsteroid(503, ore: 1000f));
        var world = SeededWorld(asteroidId: 503, stationId: 789);

        ast.MineOre(200f);                                  // 1000 -> 800 (0.80)
        world.Apply(AsteroidsUpdate.FromAsteroids([ast]));
        float fuller = world.AsteroidLive[503].OreFraction;

        ast.MineOre(600f);                                  // 800 -> 200 (0.20)
        world.Apply(AsteroidsUpdate.FromAsteroids([ast]));
        float depleted = world.AsteroidLive[503].OreFraction;

        Assert.True(depleted < fuller, "more mining must lower the client's ore readout");
        Assert.Equal(0.80f, fuller, 3);
        Assert.Equal(0.20f, depleted, 3);
    }

    [Fact]
    public void Decoded_Health_Fraction_Uses_Div240()
    {
        // Directly exercise the div-240 BytePercentage decode on the client side,
        // independent of the sim's own health plumbing.
        var world = new ClientWorld();
        world.Apply(new AsteroidsUpdate
        {
            Entries =
            [
                new AsteroidStateEntry(AsteroidId: 503, CompactOrientation.Identity, Ore: 600, BpFraction: 240),
                new AsteroidStateEntry(AsteroidId: 504, CompactOrientation.Identity, Ore: 600, BpFraction: 120),
                new AsteroidStateEntry(AsteroidId: 505, CompactOrientation.Identity, Ore: 600, BpFraction: 060),
            ],
        });

        Assert.Equal(1.0f, world.AsteroidLive[503].Fraction, 3);
        Assert.Equal(0.5f, world.AsteroidLive[504].Fraction, 3);
        Assert.Equal(0.25f, world.AsteroidLive[505].Fraction, 3);
    }

    [Fact]
    public void Entity_Without_Export_Still_Records_Health_But_Zero_Ore_Fraction()
    {
        // The ore fraction needs the static OreMax (from EXPORT); before that
        // arrives we can still surface the health fraction (from the update alone).
        var world = new ClientWorld();                       // no EXPORT seeded
        world.Apply(new AsteroidsUpdate
        {
            Entries = [new AsteroidStateEntry(AsteroidId: 503, CompactOrientation.Identity, Ore: 050, BpFraction: 200)],
        });

        var live = world.AsteroidLive[503];
        Assert.Equal(0f, live.OreFraction);                  // OreMax unknown -> 0 (guarded)
        Assert.Equal(200f / 240f, live.Fraction, 3);
    }

    [Fact]
    public void Station_Live_Fractions_Decoded_From_StationsUpdate()
    {
        // A station is a damageable target: its live armor + shield fractions
        // (div-240) feed the generic target health bar.
        var world = new ClientWorld();
        world.Apply(new StationsUpdate
        {
            Entries =
            [
                new StationsUpdateEntry(StationId: 789, BpHullFraction: 180, BpShieldFraction: 090),
            ],
        });

        var live = world.StationLive[789];
        Assert.Equal(180f / 240f, live.HullFraction, 3);
        Assert.Equal(90f / 240f, live.ShieldFraction, 3);
    }
}
