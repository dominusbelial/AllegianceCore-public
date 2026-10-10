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
/// P459 — investing in a <b>drone type</b> spawns that drone (the port of
/// native <c>FedSrvSiteBase::DroneTypeCompleted</c>, <c>FedSrv.CPP:11518-11546</c>).
/// A real drone-type bucket, once fully funded (the P189 <c>FundAndComplete</c>
/// loopback pattern), must spawn a NEW NPC on that side at the home station
/// carrying the drone type's <b>real hull</b> and <b>pilot type</b> (after
/// P458), and — per pilot type:
/// <list type="bullet">
/// <item><b>Wingman</b> — picks the default order: engages a nearby enemy ship
/// (<see cref="Ship.AttackTarget"/>), the P162 <c>c_cidAttack</c> default.</item>
/// <item><b>Miner</b> — enters the mine cycle (<see cref="Ship.MiningAsteroidTarget"/>).</item>
/// <item><b>Layer</b> — records its laid expendable (<see cref="Ship.LayerLaidExpendableTypeId"/>),
/// the team hears the native where-to-lay chat, and it parks at a rock
/// (<see cref="Ship.LayerAsteroidTarget"/>, the P161 default; it does not deploy).</item>
/// <item><b>Carrier</b> — spawned so the investment isn't lost, but with no
/// behaviour arm (documented gap).</item>
/// <item><b>Builder</b> — logged and ignored (native asserts builders never
/// complete via a drone-type bucket; they come only from station buckets, P458).</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public sealed class DroneBucketSpawnsDroneLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int ups = 20) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        Games =
        [
            new Gm { Name = "P459 Drone Bucket", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 882u, DwCookie = 0x882u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 88700000000000001ul, DrmHash = "p459drone",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void SeatOne(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int n = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(n < 4000, $"{name} did not seat");
    }

    private static short SideOf(Srv server, FltBot bot)
    {
        var shipId = bot.ShipAssignment!.Ship.ShipId;
        return server.Ships.Single(s => s.Ship.Id == shipId).Ship.SideId!.Value;
    }

    /// <summary>Deterministically route the single funder peer to mission-0
    /// side 0 (the owning side we invest a drone bucket on).</summary>
    private static void InjectSingleMissionSpawn(Srv server)
    {
        int seq = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(950f, 550f, 520f), Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            seq++;
            return (ship, mission.Sectors[0]);
        };
    }

    /// <summary>Fully fund a bucket (money + build time) via the donor bot,
    /// pumping until the server records the completion (P189 pattern).</summary>
    private static void FundAndComplete(Srv server, FltBot funder, Bucket bucket, int budget)
    {
        server.Ships.First(s => s.Ship.Id == funder.ShipAssignment!.Ship.ShipId).Wallet.Money = 50_000_000;
        funder.ResetChatReceived();
        funder.SendDonate(bucket.Id, bucket.Price);
        int done = PumpUntil(server, () => server.BucketCompletions.Contains(bucket.Id), budget);
        Assert.True(done < budget, $"drone-type bucket {bucket.Id} did not complete after {budget} ticks");
    }

    /// <summary>The id set of every NPC ship currently on a side — snapshotted
    /// before/after funding so the freshly-bucket-spawned drone (the delta) is
    /// isolated from any initial miner / earlier constructor.</summary>
    private static HashSet<int> NpcShipIdsOnSide(Srv server, short sideId) => new(
        server.Missions[0].Sectors
            .SelectMany(s => s.Models.OfType<Ship>())
            .Where(s => s.IsNpc && s.SideId == sideId)
            .Select(s => (int)s.Id));

    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, short radius, short sectorId) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: oreMax, OreMax: oreMax,
        AabmCapabilities: Asteroid.CapMineHe3,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    /// <summary>Boot the in-process server + one seated funder on side 0, and
    /// hand back the owning side, its mission and the drone-type buckets of the
    /// wanted pilot types (searched dynamically, never hard-coding a core id).</summary>
    private static (Srv Server, FltBot Bot, Side Side, Mission Mission)
        BootOwnedSide()
    {
        var server = new Srv();
        InjectSingleMissionSpawn(server);
        server.SpawnInitialMinersAtBoot = false;   // P382: pristine arena — no initial miner to perturb the drone-cycle assertions
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        var bot = new FltBot();
        SeatOne(server, bot, "P459D");
        short sideId = SideOf(server, bot);
        var mission = server.Missions[0];
        var side = mission.Sides.Single(s => s.Id == sideId);
        return (server, bot, side, mission);
    }

    private static Bucket DroneBucketOfPilot(Side side, byte pilotType) =>
        side.Buckets.First(b => b.ItemType == BucketItemType.BitDroneType
                                && b.DroneType is { } dt && dt.PilotType == pilotType);

    /// <summary>
    /// ⭐ Wingman default-order behaviour — the engage path a completed Wingman
    /// drone-type bucket feeds (via <see cref="CompleteDroneType"/> →
    /// <see cref="Srv.SpawnNpcShip"/>). NOTE: PCore001's Wingman drone types
    /// (id 40 'Wingman' and the .Atk* 47-50) require tech bit 7, which nothing
    /// in the core grants, so <b>no civ ever produces a reachable Wingman
    /// drone-type bucket</b> (measured, P459). The bucket→spawn wiring is
    /// therefore exercised by the Miner/Layer/Carrier bucket tests (the identical
    /// <see cref="CompleteDroneType"/> path), and this test pins the one
    /// Wingman-specific behaviour — the P162 c_cidAttack default order — using
    /// the real production spawn primitive with the real Wingman drone type
    /// (id 40, hull 212 'Interceptor'): the spawned ship carries
    /// <c>PilotType == Wingman</c> and the drone type's real hull, and picks a
    /// nearby enemy as its <see cref="Ship.AttackTarget"/>.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Wingman_drone_type_spawns_real_hulled_wingman_that_engages_a_nearby_enemy()
    {
        Require.Artwork();
        var (server, bot, side, mission) = BootOwnedSide();
        using (server)
        using (bot)
        {
            // The real Wingman drone type from the core (id 40 'Wingman').
            var wing = mission.Core.DroneTypes.First(kv => kv.Value.PilotType == Ship.PilotTypeWingman);
            var droneType = wing.Value;
            var enemySideId = mission.Sides.First(s => s.Id != side.Id).Id;
            var sector = mission.Sectors[0];

            // The production spawn primitive CompleteDroneType uses, with the
            // real Wingman drone type id — the exact end state a completed
            // wingman bucket would produce.
            var spawned = server.SpawnNpcShip(mission, sector, side.Id, new Vector3(220f, 540f, 490f), wing.Key);
            Assert.Equal(Ship.PilotTypeWingman, spawned.PilotType);
            Assert.Equal(droneType.HullTypeId, spawned.HullType.HullId);   // real wingman hull (212 Interceptor)
            Console.WriteLine($"[P459] wingman drone type {wing.Key} '{droneType.Name}' → ship {spawned.Id}, hull {spawned.HullType.HullId} '{spawned.HullType.Name}', pilotType {spawned.PilotType}");

            // A nearby enemy in the wingman's sector → it picks the default order.
            var enemyHull = mission.Core.HullTypes.Values.OrderByDescending(h => h.Length).First();
            var enemy = new Ship((short)5001, enemyHull, new Vector3(640f, 545f, 510f), Vector3.Zero, Orientation.Identity) { SideId = enemySideId };
            mission.PlaceShip(enemy, sector);

            int p = PumpUntil(server, () => spawned.AttackTarget is not null, 1500);
            Assert.True(p < 1500, "wingman did not pick the nearby enemy as its default-order attack target");
            Assert.Same(enemy, spawned.AttackTarget);
            Assert.Null(spawned.MiningAsteroidTarget);
            Assert.Null(spawned.LayerAsteroidTarget);
            Console.WriteLine($"[P459] wingman {spawned.Id} engaged enemy {enemy.Id} (P162 default order)");
        }
    }

    /// <summary>⭐ Miner: funding the real Miner drone-type bucket spawns a NEW
    /// Miner-pilot NPC on the real drone hull and it enters the mine cycle
    /// (acquires a <see cref="Ship.MiningAsteroidTarget"/> on a mineable rock).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Completing_miner_drone_type_bucket_spawns_real_hulled_miner_that_enters_the_mine_cycle()
    {
        Require.Artwork();
        var (server, bot, side, mission) = BootOwnedSide();
        using (server)
        using (bot)
        {
            var bucket = DroneBucketOfPilot(side, Ship.PilotTypeMiner);
            var droneType = bucket.DroneType!.Value;
            Console.WriteLine($"[P459] investing in miner drone type {droneType.DroneTypeId} '{droneType.Name}' (hull {droneType.HullTypeId}, price {bucket.Price})");

            var before = NpcShipIdsOnSide(server, side.Id);
            FundAndComplete(server, bot, bucket, budget: 8000);

            var spawned = FindDelta(server, side.Id, before, "miner");
            Assert.Equal(Ship.PilotTypeMiner, spawned.PilotType);
            Assert.Equal(droneType.HullTypeId, spawned.HullType.HullId);
            Console.WriteLine($"[P459] miner spawned: ship {spawned.Id}, hull {spawned.HullType.HullId} '{spawned.HullType.Name}', pilotType {spawned.PilotType} in sector {spawned.Sector?.Id}");

            // A mineable asteroid in the miner's sector → it enters the mine cycle.
            var sector = spawned.Sector!;
            var rock = new Asteroid(MkAsteroid((short)(6000 + side.Id), new Vector3(spawned.Position.X + 300f, spawned.Position.Y, spawned.Position.Z), oreMax: 999f, 60, sector.Id));
            mission.PlaceAsteroid(rock, sector);

            int p = PumpUntil(server, () => spawned.MiningAsteroidTarget is not null, 1500);
            Assert.True(p < 1500, "miner did not acquire a mining target (mine cycle)");
            Assert.Same(rock, spawned.MiningAsteroidTarget);
            Assert.Null(spawned.LayerAsteroidTarget);
            Console.WriteLine($"[P459] miner {spawned.Id} entered the mine cycle → targeting rock {rock.Data.AsteroidId}");
        }
    }

    /// <summary>⭐ Layer: funding a real Layer drone-type bucket spawns a NEW
    /// Layer-pilot NPC on the real drone hull, records its laid expendable
    /// (native CreateStockDrone's GetLaidExpendable), the team hears the native
    /// where-to-lay chat (tower text, since these PCore001 layers lay tower
    /// expendables, not mines), and it parks at a rock
    /// (<see cref="Ship.LayerAsteroidTarget"/>, the P161 default; no deploy).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Completing_layer_drone_type_bucket_spawns_real_hulled_layer_sends_chat_and_parks_at_a_rock()
    {
        Require.Artwork();
        var (server, bot, side, mission) = BootOwnedSide();
        using (server)
        using (bot)
        {
            var bucket = DroneBucketOfPilot(side, Ship.PilotTypeLayer);
            var droneType = bucket.DroneType!.Value;
            Console.WriteLine($"[P459] investing in layer drone type {droneType.DroneTypeId} '{droneType.Name}' (hull {droneType.HullTypeId}, laysExpendable {droneType.EtidLaid}, price {bucket.Price})");

            var before = NpcShipIdsOnSide(server, side.Id);
            FundAndComplete(server, bot, bucket, budget: 8000);

            var spawned = FindDelta(server, side.Id, before, "layer");
            Assert.Equal(Ship.PilotTypeLayer, spawned.PilotType);
            Assert.Equal(droneType.HullTypeId, spawned.HullType.HullId);
            Assert.Equal(droneType.EtidLaid, spawned.LayerLaidExpendableTypeId);   // base data = the laid expendable
            Console.WriteLine($"[P459] layer spawned: ship {spawned.Id}, hull {spawned.HullType.HullId} '{spawned.HullType.Name}', laysExpendable {spawned.LayerLaidExpendableTypeId} in sector {spawned.Sector?.Id}");

            // The team chat arrives with the native tower voice id (these layers
            // lay tower expendables, so NOT a mine → "New tower requesting location.").
            int cx = PumpUntil(server, () => bot.Chats.Any(c => c.Message.Contains("New tower requesting location.")), 800);
            Assert.True(cx < 800, "team did not receive the 'New tower requesting location.' chat");
            var chat = bot.Chats.Single(c => c.Message.Contains("New tower requesting location."));
            Assert.Equal((int)ChatTarget.Team, chat.ChatTarget);
            Assert.Equal(331, chat.VoiceOver);   // droneWhereToLayTowerSound
            Console.WriteLine($"[P459] team chat '{chat.Message}' (voice {chat.VoiceOver}) received");

            // A neutral asteroid in its sector → it parks at a rock (P161).
            var sector = spawned.Sector!;
            var rock = new Asteroid(MkAsteroid((short)(7000 + side.Id), new Vector3(spawned.Position.X + 300f, spawned.Position.Y, spawned.Position.Z), oreMax: 998f, 60, sector.Id));
            mission.PlaceAsteroid(rock, sector);

            int p = PumpUntil(server, () => spawned.LayerAsteroidTarget is not null, 1500);
            Assert.True(p < 1500, "layer did not pick a rock to park at");
            Assert.Same(rock, spawned.LayerAsteroidTarget);
            Assert.Null(spawned.MiningAsteroidTarget);
            Console.WriteLine($"[P459] layer {spawned.Id} parked at rock {rock.Data.AsteroidId} (P161; no deploy — known gap)");
        }
    }

    /// <summary>⭐ Carrier: funding a real Carrier drone-type bucket spawns the
    /// carrier (so the investment isn't lost), but it has no behaviour arm — it
    /// sits idle (documented gap). No chat (native sends none for a carrier).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Completing_carrier_drone_type_bucket_spawns_carrier_without_behaviour()
    {
        Require.Artwork();
        var (server, bot, side, mission) = BootOwnedSide();
        using (server)
        using (bot)
        {
            var bucket = DroneBucketOfPilot(side, Ship.PilotTypeCarrier);
            var droneType = bucket.DroneType!.Value;
            Console.WriteLine($"[P459] investing in carrier drone type {droneType.DroneTypeId} '{droneType.Name}' (hull {droneType.HullTypeId}, price {bucket.Price})");

            var before = NpcShipIdsOnSide(server, side.Id);
            FundAndComplete(server, bot, bucket, budget: 8000);

            var spawned = FindDelta(server, side.Id, before, "carrier");
            Assert.Equal(Ship.PilotTypeCarrier, spawned.PilotType);
            Assert.Equal(droneType.HullTypeId, spawned.HullType.HullId);
            Console.WriteLine($"[P459] carrier spawned: ship {spawned.Id}, hull {spawned.HullType.HullId} '{spawned.HullType.Name}', pilotType {spawned.PilotType} (no behaviour arm — sits idle)");
        }
    }

    /// <summary>
    /// ⭐ Builder ignored: a Builder-pilot drone-type bucket's completion is
    /// logged and ignored (native <c>assert(pilotType != c_ptBuilder)</c> —
    /// builders come only from station-type buckets, P458). Exercising this via
    /// a real bucket is impossible in PCore001: builders require tech bit 7,
    /// which nothing in the core grants, so no Builder drone-type bucket is ever
    /// reachable (measured). The guard is therefore defensive against malformed
    /// core data; its code path is documented in the P459 report. This test is
    /// intentionally absent — see the report's 'Behaviour each type gets today'
    /// table for the Builder row's provenance.
    /// </summary>

    /// <summary>Return the single newly-spawned NPC (the before→after delta) on
    /// the side; fail loudly if it is not exactly one.</summary>
    private static Ship FindDelta(Srv server, short sideId, HashSet<int> before, string what)
    {
        var after = NpcShipIdsOnSide(server, sideId);
        after.ExceptWith(before);
        Assert.Single(after);
        int newId = after.Single();
        return server.Missions[0].Sectors
            .SelectMany(s => s.Models.OfType<Ship>())
            .Single(s => s.Id == newId);
    }
}
