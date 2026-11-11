using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void P994_Batch_preserves_production_hit_metrics_and_all_attacker_alerts(bool countOnly)
    {
        Require.Artwork();
        using var legacy = Session(); using var compact = Session();
        short miner = Catalog.RepresentativeCore().HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmMiner)).HullId;
        foreach (var cs in new[] { legacy, compact })
        {
            cs.World.CountOnlyProjectiles = countOnly;
            cs.World.NowSeconds = () => 100;
            cs.World.Apply(new ExportShip { ShipId = 2, HullId = miner, SideId = 0, CharacterName = "miner" });
            cs.World.Apply(new ExportShip { ShipId = 9, HullId = 15, SideId = 1, CharacterName = "enemy-a" });
            cs.World.Apply(new ExportShip { ShipId = 10, HullId = 15, SideId = 1, CharacterName = "enemy-b" });
            cs.World.Apply(new ExportStation { Station = Station(90, 0, 1) });
        }
        var a = Bot(legacy); var b = Bot(compact);
        IFedMessage[] records =
        [
            new ProjectileInstance { TimeCreate = 994, ProjectileTypeId = 41, LauncherId = 1,
                Position = new(0.123f, 0, 0), Velocity = new(1.234f, 2.345f, -1000), Forward = new(0, 0, -1), Lifespan = 2.125f },
            Hit(9, 1, false), Hit(9, 2, true), Hit(10, 90, false), Hit(1, 9, true), Hit(1, 10, false),
        ];
        ((ITransportSite)legacy).OnReceive(ConnectionId.Server, FedPacket.Pack(records.Select(r => r.Encode())));
        ((ITransportSite)compact).OnReceive(ConnectionId.Server,
            FedPacket.Pack(new GunfireBatch { Tick = 994, Records = records }.Encode()));
        Call("SampleCombatStats", null, a, legacy, (short)1, 100.0);
        Call("SampleCombatStats", null, b, compact, (short)1, 100.0);
        foreach (string field in new[] { "ShotsFired", "ShipHits", "EnemyShieldHits", "EnemyHullHits", "FriendlyHits", "NoneHits" })
            Assert.Equal(Get<long>(a, field), Get<long>(b, field));
        Assert.Equal(1, Get<long>(b, "ShotsFired"));
        Assert.Equal(2, Get<long>(b, "ShipHits"));
        foreach (string field in new[] { "RecentAttackers", "RecentAssetAttackers", "RecentMinerAttackSectors" })
            Assert.Equal(Get<Dictionary<short, double>>(a, field).OrderBy(p => p.Key),
                Get<Dictionary<short, double>>(b, field).OrderBy(p => p.Key));
        Assert.Contains((short)9, Get<Dictionary<short, double>>(b, "RecentAttackers").Keys);
        Assert.Contains((short)1, Get<Dictionary<short, double>>(b, "RecentMinerAttackSectors").Keys);
        Assert.Equal(Get<Dictionary<(short, short), double>>(a, "RecentStationAttackers"),
            Get<Dictionary<(short, short), double>>(b, "RecentStationAttackers"));
        Assert.Contains(((short)90, (short)10), Get<Dictionary<(short, short), double>>(b, "RecentStationAttackers").Keys);

        static ProjectileHullHit Hit(short source, short target, bool absorbed) => new()
        {
            SourceId = source, TargetId = target, ClusterId = 1, AbsorbedByShield = absorbed,
            Position = new(12.345f, -23.456f, 34.567f), Normal = new Vector3(1, 2, -3).Normalize(), Radius = 35.25f,
        };
    }
}
