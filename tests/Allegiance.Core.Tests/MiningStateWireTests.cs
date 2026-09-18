using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P255 — client-visible mining state. Unit: the <c>OwnShipMiningUpdate(206)</c>
/// codec + the faithful <c>miningMaskIGC</c> state-bit stamping on
/// <see cref="ServerShipUpdate"/>. Live: over real TCP, a bot's ship spawned
/// atop a synthetic He3 rock mines it, the OWN client is told exactly which
/// rock (<c>OwnShipMiningUpdate(206)</c> ids) and the native <c>miningMaskIGC</c>
/// bit (0x2000) rides its own <c>SingleShipUpdate(78)</c> — and BOTH clear the
/// moment the ship leaves the rock.
/// </summary>
[Collection("LiveNetwork")]
public class MiningStateWireTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, short radius, short sectorId)
    {
        float ore = oreMax;
        return new DataAsteroidIGC(
            Signature: 1.0f, Position: [pos.X, pos.Y, pos.Z],
            Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            Ore: ore, OreMax: oreMax, AabmCapabilities: Asteroid.CapMineHe3,
            AsteroidId: id, HitPoints: 0f, Radius: radius,
            ModelName: "roid.mdl", TextureName: "", IconName: "",
            ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);
    }

    // ── codec / unit ─────────────────────────────────────────────────────────

    [Fact]
    public void Codec_roundtrips_mining_and_not_mining()
    {
        var m = new OwnShipMiningUpdate { ClusterId = 3, AsteroidId = 7100 };
        var back = OwnShipMiningUpdate.Decode(m.Encode());
        Assert.Equal(3, back.ClusterId);
        Assert.Equal(7100, back.AsteroidId);
        Assert.True(back.IsMining);

        var na = new OwnShipMiningUpdate { ClusterId = -1, AsteroidId = -1 };
        var naBack = OwnShipMiningUpdate.Decode(na.Encode());
        Assert.Equal(-1, naBack.ClusterId);
        Assert.Equal(-1, naBack.AsteroidId);
        Assert.False(naBack.IsMining);
    }

    [Fact]
    public void FromMining_null_is_not_mining_else_names_the_rock()
    {
        var na = OwnShipMiningUpdate.FromMining(null);
        Assert.False(na.IsMining);
        Assert.Equal(-1, na.AsteroidId);

        var rock = new Asteroid(MkAsteroid(7101, new Vector3(1, 2, 3), 500f, 60, sectorId: 4));
        var m = OwnShipMiningUpdate.FromMining(rock);
        Assert.True(m.IsMining);
        Assert.Equal(7101, m.AsteroidId);
        Assert.Equal(4, m.ClusterId);
    }

    [Fact]
    public void ServerShipUpdate_extraStateBits_stamps_the_faithful_mining_bit()
    {
        var hull = new DataHullTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "t",
            Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
            Mass: 550f, Signature: 0f, Speed: 120f, MaxTurnRates: [4.5f, 4.5f, 4.5f],
            TurnTorques: [5000f, 5000f, 5000f], Thrust: 52500f, SideMultiplier: 4f,
            BackMultiplier: 4f, ScannerRange: 1000f, MaxFuel: 100f, Ecm: 0f, Length: 30f,
            MaxEnergy: 0f, RechargeRate: 0f, RipcordSpeed: 0f, RipcordCost: 0f,
            MaxAmmo: 0, HullId: 502, SuccessorHullId: -1, MaxWeapons: 0,
            MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 540, DefenseType: 0,
            CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
            PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
            InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
            MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
            ManuveringThrusterExteriorSound: 0, Hardpoints: []);
        var ship = new Ship(900, hull, new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity);

        var mining = ServerShipUpdate.FromModel(ship.Id, ship, 1u, ShipControlState.MiningMask);
        Assert.NotEqual(0, mining.StateM & ShipControlState.MiningMask);

        var idle = ServerShipUpdate.FromModel(ship.Id, ship, 2u, 0);
        Assert.Equal(0, idle.StateM & ShipControlState.MiningMask);
    }

    // ── live loopback over real TCP ──────────────────────────────────────────

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Mining State", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 4242u, DwCookie = 0x42u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 42600000000000000ul, DrmHash = "miningstate",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        Thread.Sleep(60);
        return n;
    }

    /// <summary>⭐ Live: a bot's ship parked atop a synthetic He3 rock mines it;
    /// the OWN client is told the exact rock via OwnShipMiningUpdate(206) and the
    /// native miningMaskIGC bit (0x2000) rides its own SingleShipUpdate(78); both
    /// clear when the ship leaves the rock.</summary>
    [Fact]
    public void Loopback_own_mining_state_reaches_client_and_clears_on_departure()
    {
        using var server = new Srv();
        Asteroid? placedRock = null;
        Vector3 apos = new(120f, 80f, -40f);
        short rockId = 7200;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var sector = mission.Sectors[0];
            placedRock = new Asteroid(MkAsteroid(rockId, apos, oreMax: 1000f, radius: 60, sectorId: sector.Id));
            mission.PlaceAsteroid(placedRock, sector);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, apos, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby (P180)
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "mine_state");
        bot.SendLogonRequest(Request("mine_state"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;
        var live = () => server.Ships.First(s => s.Ship.Id == shipId).Ship;

        bot.ResetOwnShipMining();
        bot.ResetObservations();

        // Let it mine for a while, then assert the OWN client learned the rock id.
        PumpUntil(server, () => bot.OwnShipMiningUpdates.Any(u => u.IsMining && u.AsteroidId == rockId), 400);
        Assert.True(live().IsMining, "ship should be flagged mining");
        Assert.Contains(bot.OwnShipMiningUpdates, u => u.IsMining && u.AsteroidId == rockId);

        // Force a dirty fmid-78 (nudge the parked ship's POSITION, not velocity —
        // the mining gate's <1.0 velocity constraint is untouched) so the
        // faithful native mining bit rides a decoded own-ship update. A perfectly
        // stationary miner would otherwise send no 78 after the join burst — the
        // same technique MiningTests uses to push ore onto the wire.
        bot.ResetObservations();
        live().Position = apos + new Vector3(2f, 0f, 0f);
        PumpUntil(server, () => bot.Observations.Any(o => (o.StateM & ShipControlState.MiningMask) != 0), 400);
        Assert.Contains(bot.Observations,
            o => (o.StateM & ShipControlState.MiningMask) != 0);

        // Now LEAVE the rock: move far away (no other He3 rock in the demo sector).
        live().Position = apos + new Vector3(100000f, 0f, 0f);
        PumpUntil(server, () =>
            bot.OwnShipMiningUpdates.Count > 0 &&
            bot.OwnShipMiningUpdates[^1].AsteroidId == -1, 400);

        // Mining has stopped server-side (per-tick MiningAsteroid cleared) and the
        // wire reflects it (NA on 206; the departure's position-dirty 78 has the
        // bit cleared). NOTE: IsMining is the LATCHED transition flag (P156/P254)
        // and intentionally never auto-clears — the per-tick "mining right now"
        // signal is MiningAsteroid, which is what the wire (and the client's cue)
        // tracks.
        Assert.Null(live().MiningAsteroid);
        Assert.False(bot.OwnShipMiningUpdates[^1].IsMining);
        Assert.Contains(bot.Observations,
            o => (o.StateM & ShipControlState.MiningMask) == 0);
    }
}
