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
/// P475 — end-to-end: completing a REAL development from PCore001 whose
/// <c>Gas</c> changes one of this task's four station attributes actually
/// scales a real station on that side. Dev 167 'GA: Station Hull 1'
/// (<c>gas[4] = 1.1</c> = <c>c_gaMaxArmorStation</c>, and <c>gas[5] = 1.1</c> =
/// <c>c_gaArmorRegenerationStation</c>) is verified against PCore001. The only
/// thing that changes between the two station probes is the side's global
/// attribute set; the station's <see cref="Station.EffectiveMaxArmor"/> (and
/// its regen rate) must track the completed dev's multiplier exactly.
/// </summary>
[Collection("LiveNetwork")]
public class P475E2eStationGaLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int ups = 1) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        Games =
        [
            new Gm { Name = "P475", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 918u, DwCookie = 0x918u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9130000000000000000ul, DrmHash = "p475",
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

    /// <summary>Spawn the single test ship on side 0 (mission 0), giving the bot
    /// a real side to fund a development for.</summary>
    private static void InjectSingleMissionSpawn(Srv server)
    {
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 750f, 910f), Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            return (ship, mission.Sectors[0]);
        };
    }

    private static Bucket DevBucket(Side side, short devId) =>
        side.Buckets.Single(b => b.ItemType == BucketItemType.BitDevelopment
                                 && b.Development is { } dd && dd.DevelopmentId == devId);

    /// <summary>A synthetic station on the given side of the live mission: a real
    /// station type <c>with</c>-modified to chosen capacities, placed via
    /// <see cref="Mission.PlaceStation"/> (which wires <see cref="Station.Mission"/>)
    /// so its effective getters resolve the side's live GA.</summary>
    private static Station NewProbeStation(Mission mission, short side, short id, float maxArmor, float shieldRegen)
    {
        var baseType = mission.Core.StationTypes.Values.First();
        var type = baseType with
        {
            MaxArmorHitPoints = maxArmor,
            MaxShieldHitPoints = 0f,
            DefenseTypeArmor = 8,
            DefenseTypeShield = 9,
            ArmorRegeneration = 50f,
            ShieldRegeneration = shieldRegen,
            SabmCapabilities = 0,
        };
        var spot = new Vector3(0f, 0f, -500f);
        var data = new DataStationIGC(
            Position: [spot.X, spot.Y, spot.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: mission.Sectors[0].Id, SideId: side, StationId: id,
            StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: $"PROBE_{id}");
        var station = new Station(data, type);
        mission.PlaceStation(station, mission.Sectors[0]);
        return station;
    }

    /// <summary>⭐ End-to-end must-pass: complete real dev 167 'GA: Station Hull 1'
    /// (gas[4]=1.1 = c_gaMaxArmorStation, gas[5]=1.1 = c_gaArmorRegenerationStation)
    /// through the real server economy, then show a station on that side's
    /// effective max armor (and armor regen) scale by exactly that multiplier.</summary>
    [Fact]
    public void Completing_station_hull_dev_scales_real_station_armor()
    {
        using var server = new Srv();
        InjectSingleMissionSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var funder = new FltBot();
        SeatOne(server, funder, "p475");               // seq 0 → mission 0 / side 0

        var mission = server.Missions[0];
        var side = mission.Sides[0];
        Assert.Equal(1f, side.GlobalAttributes.Get(GlobalAttributeSet.MaxArmorStation), 6);   // civ 1 Dreghklar base is identity

        // Dev 167 'GA: Station Hull 1' — real PCore001 data: gas[4]=1.1 (max armor), gas[5]=1.1 (armor regen).
        var dev = mission.Core.Developments[167];
        Assert.True(dev.Name.Contains("Station Hull"), $"dev 167 unexpected: '{dev.Name}'");
        Assert.Equal(1.1f, dev.Gas[GlobalAttributeSet.MaxArmorStation], 5);
        Assert.Equal(1.1f, dev.Gas[GlobalAttributeSet.ArmorRegenerationStation], 5);

        // Probe 1: a station on the side BEFORE the dev completes — effective = raw (GA 1.0).
        const float rawArmor = 2500f;
        var probe1 = NewProbeStation(mission, side.Id, 8711, rawArmor, shieldRegen: 10f);
        Assert.Equal(rawArmor, probe1.EffectiveMaxArmor(), 3);
        Assert.Equal(50f, probe1.EffectiveArmorRegen(), 3);
        Console.WriteLine($"P475-E2E: pre-dev EffectiveMaxArmor={probe1.EffectiveMaxArmor():F1} (raw {rawArmor:F0}); GA[4]={side.GlobalAttributes.Get(GlobalAttributeSet.MaxArmorStation):F3}");

        // Complete the real development via the real economy path.
        var bucket = DevBucket(side, dev.DevelopmentId);
        side.DevelopmentTechs = side.DevelopmentTechs.Or(TechTreeBitMask.FromBytes(dev.TtbmRequired));   // P551: unlock dev 167 so the side can buy/fund it (CanBuy gate)
        server.Ships.First(s => s.Ship.Id == funder.ShipAssignment!.Ship.ShipId).Wallet.Money = 10_000_000;
        funder.SendDonate(bucket.Id, bucket.Price);
        int done = PumpUntil(server, () => server.BucketCompletions.Contains(bucket.Id), 600);
        Assert.True(done < 600, $"dev {bucket.Id} did not complete after 600 ticks");

        // The side's station-armor multiplier rose by exactly the dev's 1.1.
        Assert.Equal(1.1f, side.GlobalAttributes.Get(GlobalAttributeSet.MaxArmorStation), 5);
        Assert.Equal(1.1f, side.GlobalAttributes.Get(GlobalAttributeSet.ArmorRegenerationStation), 5);
        Console.WriteLine($"P475-E2E: post-dev side GA[4]={side.GlobalAttributes.Get(GlobalAttributeSet.MaxArmorStation):F3} GA[5]={side.GlobalAttributes.Get(GlobalAttributeSet.ArmorRegenerationStation):F3}");

        // Probe 2: a fresh station on the same side AFTER — effective armor scales by 1.1.
        var probe2 = NewProbeStation(mission, side.Id, 8722, rawArmor, shieldRegen: 10f);
        Assert.Equal(rawArmor * 1.1f, probe2.EffectiveMaxArmor(), 2);
        Assert.Equal(50f * 1.1f, probe2.EffectiveArmorRegen(), 2);
        Console.WriteLine($"P475-E2E: post-dev EffectiveMaxArmor={probe2.EffectiveMaxArmor():F2} ratio={probe2.EffectiveMaxArmor() / rawArmor:F4} (want ~1.1)");
    }
}
