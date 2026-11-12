using System.Reflection;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P1000CivStrategyReceiptTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void Actual_ore_sale_station_loss_inventory_and_bounded_damage_keep_match_identity(bool teleport)
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter(); Console.SetOut(log);
        try
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false, SimulationRandom = new Random(1000) };
            server.Boot(new ServerConfig
            {
                ArtPath = Catalog.ArtRoot, Port = 0, BenchmarkSeed = 1000, Bots = new() { CivStrategy = true },
                Games = [new() { Teams = 2, CivIds = [18, 4], SideNames = ["A", "B"],
                    MapGen = new() { Type = "SingleRing", Seed = 1000 } }]
            });
            var mission = server.Missions[0]; var host = (ServerHost)mission.Site;
            var own = mission.Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == 0);
            var enemy = mission.Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == 1);
            var ownSector = mission.Sectors.Single(s => s.Id == own.ClusterId);
            var minerType = mission.Core.DroneTypes.Values.First(d => d.PilotType == Ship.PilotTypeMiner);
            var miner = server.SpawnNpcShip(mission, ownSector, 0, new(50000, 0, 0), minerType.DroneTypeId);
            miner.Ore = 10;
            double expectedCredits = miner.Ore * miner.EffectiveOreCreditPerUnit();
            if (teleport)
            {
                own.SetBaseStationType(own.Type with { SabmCapabilities = (short)(own.Type.SabmCapabilities | 0x800) });
                var p = own.Data.Position; miner.Position = new(p[0], p[1], p[2]);
                miner.PlanTarget = own.Data; miner.PlanCommandId = Allegiance.Core.Wire.OrderChange.CidGoto;
                typeof(Ship).GetMethod("TryTeleportUnload", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(miner, null);
            }
            else Assert.True(host.DockWithStationEvent(miner, new DockStation(own.Data)));
            Assert.Equal(0, miner.Ore);
            // A remount changes no granted inventory. Batched null-part events still
            // see all newly granted parts; the receipt observes the complete inventory.
            var part = mission.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Weapon);
            miner.MountPart(part, 1, 0, 255);
            host.LoadoutChangeEvent(miner, null!, LoadoutChange.Mounted);
            var launcher = new ResearchTree(mission.Core, 18).Parts.First(p => p.Type is DataPartTypeIGC.MissileLauncher);
            var expendable = mission.Core.MissileTypes[((DataPartTypeIGC.MissileLauncher)launcher.Type).ExpendableTypeId];
            var supplyHull = mission.Core.HullTypes.Values.First(h => h.GroupId >= 0 && h.CanMountPart(launcher, 0)
                && h.CapacityMagazine >= expendable.LauncherExpendableSize);
            var supply = new Ship(20002, supplyHull, new(80000, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(supply, ownSector);
            var magazine = supply.MountPart(launcher, 3, 0, 255)!;
            int loaded = magazine.Amount; Assert.True(loaded > 0);
            supply.SetPartAmount(magazine, 0);
            supply.SetPartAmount(supply.Parts.Single(p => p.Type.PartId == launcher.PartId), loaded);
            Assert.True(supply.TryMountCargoPart(launcher, (short)loaded, -1, 255));
            supply.SetPartAmount(supply.Parts.Single(p => p.Type.PartId == launcher.PartId), 0);
            Assert.True(supply.TryApplyReloadEntry(-1, -1)); // moving stock from cargo is not another grant
            var victim = new Ship(20001, mission.Core.HullTypes.Values.First(h => h.GroupId >= 0 && h.MaxFixedWeapons > 0),
                new(70000, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 1 };
            mission.PlaceShip(victim, ownSector);
            byte damage = (byte)Enumerable.Range(0, 20).First(d => mission.Core.Constants.DamageConstants[d][victim.HullType.DefenseType] > 0);
            victim.PendingHit = new(HitKind.Gun, null, damage, 1, part.PartId);
            victim.ReceiveDamage(damage, 1, 1, victim.Position, victim.Position, miner);
            // Exercise the bound using the observation seam, with distinct source IDs.
            for (short id = 1000; id < 1300; id++)
                host.StrategyDamageEvent(victim, miner, new(HitKind.Gun, null, damage, 1, id), damage, 1, 1);
            byte stationDamage = (byte)Enumerable.Range(0, 20).First(d =>
                mission.Core.Constants.DamageConstants[d][enemy.Type.DefenseTypeArmor] > 0
                && mission.Core.Constants.DamageConstants[d][enemy.Type.DefenseTypeShield] > 0);
            enemy.ReceiveDamage(stationDamage, 1_000_000_000, 2, Vector3.Zero, Vector3.Zero, miner);
            Assert.True(enemy.Dead);
            host.KillStationEvent(enemy, miner, 1_000_000_000);
            // The ordinary victory path may already have flushed the finished world.
            if (!log.ToString().Contains("\"final\":true"))
                typeof(Srv).GetMethod("FlushCivStrategy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, [mission, 2d, true]);
            var receipts = log.ToString().Split('\n').Where(l => l.StartsWith("[civ-strategy] "))
                .Select(l => JsonDocument.Parse(l[15..]).RootElement.Clone()).ToArray();
            var window = receipts.Last(r => r.GetProperty("kind").GetString() == "window");
            Assert.Equal(mission.Cookie, window.GetProperty("cookie").GetUInt32());
            Assert.Equal(256, window.GetProperty("damage").GetArrayLength());
            Assert.True(window.GetProperty("overflowEvents").GetInt64() >= 44);
            var sides = window.GetProperty("sides").EnumerateArray().ToArray();
            Assert.Equal(0, sides.Sum(s => s.GetProperty("paydays").GetInt64()));
            Assert.True(sides.Single(s => s.GetProperty("side").GetInt16() == 0).GetProperty("miners").GetInt32() >= 1);
            Assert.Equal(10, sides.Single(s => s.GetProperty("side").GetInt16() == 0).GetProperty("ore").GetDouble());
            Assert.Equal(expectedCredits, sides.Single(s => s.GetProperty("side").GetInt16() == 0).GetProperty("oreCredits").GetDouble());
            Assert.Equal(1, sides.Single(s => s.GetProperty("side").GetInt16() == 0).GetProperty("stationKills").GetInt64());
            Assert.Equal(1, sides.Single(s => s.GetProperty("side").GetInt16() == 1).GetProperty("stationLosses").GetInt64());
            var grants = window.GetProperty("grants").EnumerateArray().Where(g => g.GetProperty("kind").GetString() == "part"
                && g.GetProperty("id").GetInt16() == part.PartId).ToArray();
            Assert.Single(grants); Assert.Equal(1, grants[0].GetProperty("amount").GetDouble());
            var refills = Assert.Single(window.GetProperty("grants").EnumerateArray(), g =>
                g.GetProperty("kind").GetString() == "part" && g.GetProperty("id").GetInt16() == launcher.PartId
                && g.GetProperty("hull").GetInt16() == supplyHull.HullId);
            Assert.Equal(3 * loaded, refills.GetProperty("amount").GetDouble());
            output.WriteLine($"ore=10 credits={expectedCredits} stationKills=1 stationLosses=1 damageRows=256 overflow={window.GetProperty("overflowEvents")}");
        }
        finally { Console.SetOut(saved); }
    }
}
