using System.Diagnostics;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using FltBot = Allegiance.Bot.Bot;
using Xunit.Abstractions;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P689StationDeathTickTests(ITestOutputHelper output)
{
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(100, false, false)]
    [InlineData(150, false, false)]
    [InlineData(150, true, false)]
    [InlineData(150, true, true)]
    public void Crowded_station_death_keeps_expected_kill_order_and_stays_within_tick_budget(int count, bool lethalSplash, bool loopback)
    {
        Require.Artwork();
        // Warm JIT, real CVH support caches, and the host's station-event path.
        Run(count, lethalSplash, loopback, measured: false);
        for (int sample = 0; sample < 3; sample++) Run(count, lethalSplash, loopback, measured: true);
    }

    private void Run(int count, bool lethalSplash, bool loopback, bool measured)
    {
        using var server = new Srv();
        server.Boot(new Allegiance.Server.ServerConfig
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
            Games = [new Allegiance.Server.GameConfig
            { Name = "P689", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Defenders", "Bombers"] }]
        });
        var m = server.Missions[0];
        m.Stage = MissionStage.Started;
        m.CrowdingPenaltyEnabled = false; // isolate combat from native sector-cost hull drain
        m.AllowFriendlyFire = lethalSplash;
        var sector = m.Sectors[0];
        using var peerA = new FltBot();
        using var peerB = new FltBot();
        FltBot[] peers = loopback ? [peerA, peerB] : [];
        int seated = 0;
        server.ShipSpawner = (_, mission, id) =>
        {
            var observerHull = mission.Core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod));
            var observer = new Ship(id, observerHull, new Vector3(10000, 0, 0), Vector3.Zero, Orientation.Identity)
            { SideId = (short)seated++ };
            mission.PlaceShip(observer, sector);
            return (observer, sector);
        };
        foreach (var peer in peers)
        {
            string name = "p689-observer-" + Array.IndexOf(peers, peer);
            peer.Connect(server.BoundPort, name);
            peer.SendLogonRequest(new LogonRequest
            {
                CharacterName = name, ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
                FedsrvVer = LogonRequest.MsgVer, Time = 689, DwCookie = 689, CrcFileList = -1,
                SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0, SteamID = 68900000000000000, DrmHash = "p689"
            });
        }
        var join = Stopwatch.StartNew();
        while (peers.Any(p => p.ShipAssignment is null) && join.Elapsed < TimeSpan.FromSeconds(5))
        { server.TickSim(); Thread.Sleep(1); }
        Assert.All(peers, p => Assert.NotNull(p.ShipAssignment));
        var observers = server.Ships.Select(s => s.Ship).ToHashSet();
        foreach (var st in sector.StationRuntimes.ToArray()) sector.RemoveStation(st);
        foreach (var ast in sector.Mineable.ToArray()) sector.RemoveAsteroid(ast);
        foreach (var model in sector.Models.Where(model => !observers.Contains(model)).ToArray()) sector.Remove(model);
        double now = sector.LastUpdate;
        var type = m.Core.StationTypes.Values.First(t => t.Name.Contains("Garrison", StringComparison.OrdinalIgnoreCase));
        var station = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        {
            StationId = 30000, ClusterId = sector.Id, SideId = 0, StationTypeId = type.StationTypeId,
            Position = [0, 0, 0], Forward = [0, 0, 1], Up = [0, 1, 0], BpHull = 1, BpShield = 0
        }, type);
        m.PlaceStation(station, sector);
        // Real fighter and bomber hulls, concentrated in the fight outside the base.
        var fighter = m.Core.HullTypes.Values.Where(h => h.Name.Contains("Fighter", StringComparison.OrdinalIgnoreCase)
            && h.HasCapability(DataHullTypeIGC.HabmFighter) && !h.HasCapability(DataHullTypeIGC.HabmLifepod)
            && !h.HasCapability(DataHullTypeIGC.HabmBuilder) && !h.HasCapability(DataHullTypeIGC.HabmMiner))
            .OrderByDescending(h => h.Length).First();
        var bomber = m.Core.HullTypes.Values.Where(h => h.Name.Contains("Bomber", StringComparison.OrdinalIgnoreCase)).OrderByDescending(h => h.Length).First();
        output.WriteLine($"garrison {type.Name} {type.ModelName} radius={type.Radius}; fighter {fighter.Name} {fighter.ModelName} length={fighter.Length}; bomber {bomber.Name} {bomber.ModelName} length={bomber.Length}");
        var rng = new Random(689);
        var ships = new List<Ship>();
        for (short i = 0; i < count; i++)
        {
            var hull = i % 2 == 0 ? fighter : bomber;
            var pos = new Vector3(type.Radius + 400 + (float)rng.NextDouble() * 100,
                (float)rng.NextDouble() * 100, (float)rng.NextDouble() * 100);
            var ship = new Ship((short)(10000 + i), hull, pos, Vector3.Zero, Orientation.Identity) { SideId = (short)(i % 2) };
            m.PlaceShip(ship, sector);
            if (lethalSplash)
            {
                ship.Ammo = hull.MaxAmmo;
                ship.SetFuel(hull.MaxFuel);
                foreach (var equipment in new[] { EquipmentType.Weapon, EquipmentType.Afterburner, EquipmentType.Cloak })
                {
                    var part = m.Core.PartTypes.Values.First(p => p.EquipmentType == equipment);
                    Assert.NotNull(ship.MountPart(part, 1, 0, ExpandedPartData.FullyMounted));
                }
            }
            ships.Add(ship);
        }
        var killed = new List<short>();
        var missileKills = new List<short>();
        server.Host.ShipKilled += (_, ship, _, _, _, _) => killed.Add(((Ship)ship).Id);
        server.Host.MissileDestroyed += missile => missileKills.Add(missile.Id);
        if (lethalSplash)
        {
            // Explicitly arm a loaded missile record for the synthetic secondary-
            // explosion stress, including cores with zero missile BlastPower.
            var missileType = m.Core.MissileTypes.Values.First(t => t.HitPoints > 0) with
            { BlastPower = 20, BlastRadius = 100, ReadyTime = 10, Lifespan = 10 };
            for (short i = 0; i < count; i++)
                sector.Add(new Missile((short)(20000 + i), sector, missileType,
                    ships[i].Position, Vector3.Zero, new Vector3(0, 0, 1), now));
        }
        var projectileType = m.Core.ProjectileTypes.Values.First(p => p.Power > 0
            && m.Core.Constants.DamageConstants[p.DamageType][type.DefenseTypeArmor] > 0
            && m.Core.Constants.DamageConstants[p.DamageType][type.DefenseTypeShield] > 0) with
        { Power = 1e9f, BlastPower = lethalSplash ? 1e9f : 0, BlastRadius = 5000, Radius = 1, Lifespan = 10 };
        sector.Add(new Projectile(29000, sector, projectileType, new Vector3(0, 0, -4 * type.Radius), new Vector3(0, 0, 50 * type.Radius),
            new Vector3(0, 0, 1), now, ships[1]));
        var csv = Path.Combine(Path.GetTempPath(), $"p689-{count}-{Guid.NewGuid():N}.csv");
        try
        {
            TickProfiler.Configure(true, 3600, csv);
            TickProfiler.EnterTick();
            var sw = Stopwatch.StartNew();
            sector.Update(now + .05);
            sw.Stop();
            TickProfiler.EndTick();
            TickProfiler.ForceReport();
            TickProfiler.Shutdown();
            if (measured)
            {
                output.WriteLine($"P689 {count} ships, lethalSplash={lethalSplash}, loopback={loopback}: {sw.Elapsed.TotalMilliseconds:F3} ms");
                string profile = File.ReadAllText(csv);
                output.WriteLine(profile);
                Assert.Equal(lethalSplash, profile.Contains(",blast.secondary,"));
                if (lethalSplash)
                {
                    output.WriteLine($"P689 loot: {sector.Models.OfType<Treasure>().Count()} treasures");
                    Assert.NotEmpty(sector.Models.OfType<Treasure>());
                }
                Assert.True(sw.Elapsed.TotalMilliseconds < 250, $"station death took {sw.Elapsed.TotalMilliseconds:F1} ms (budget 250 ms)");
            }
            if (loopback)
            {
                Assert.True(SpinWait.SpinUntil(() => peers.All(p => p.StationDestroyeds.Any(s => s.StationId == station.StationId)
                    && p.Kills.Count(k => k.ShipId >= 10000 && k.ShipId < 10000 + count) == count), TimeSpan.FromSeconds(5)),
                    "both real peers must receive the station death and every kill");
                foreach (var peer in peers)
                {
                    Assert.Single(peer.StationDestroyeds, s => s.StationId == station.StationId);
                    Assert.Equal(ships.Select(s => s.Id), peer.Kills.Where(k => k.ShipId >= 10000).Select(k => k.ShipId));
                }
            }
            Assert.True(station.Dead, "the shot must kill the real garrison through combat");
            Assert.DoesNotContain(station, sector.StationRuntimes);
            Assert.All(ships, s => { Assert.Equal(lethalSplash, s.Dead); Assert.Equal(lethalSplash ? 0f : 1f, s.Fraction); });
            Assert.Equal((lethalSplash ? 0 : count) + observers.Count, sector.Models.OfType<Ship>().Count());
            Assert.All(ships, s => Assert.NotNull(s.Hull));
            Assert.Equal(lethalSplash ? ships.Select(s => s.Id) : [], killed);
            if (lethalSplash)
            {
                Assert.Equal(Enumerable.Range(20000, count).Select(i => (short)i), missileKills);
                Assert.Empty(sector.Models.OfType<Missile>()); // all secondary blasts drained in this call
            }
        }
        finally { TickProfiler.Shutdown(); File.Delete(csv); }
    }
}
