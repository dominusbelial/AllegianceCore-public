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
/// P446 — end-to-end: completing a REAL development from PCore001 whose
/// <c>Gas</c> changes one of the four flight attributes actually changes how a
/// ship flies through the real <see cref="Ship.ExecuteMove"/>. Dev 165
/// 'GA: Ship Speed 1' (<c>gas[0] = 1.1</c> = <c>c_gaMaxSpeed</c>) is verified
/// against PCore001. The same side's global attribute set is the only thing
/// that changes between the two probes; each probe is flown at full throttle
/// to convergence by the real <c>ExecuteMove</c>, so the top-speed ratio is
/// exactly the dev's <c>gas[0] = 1.1</c> multiplier.
/// </summary>
[Collection("LiveNetwork")]
public class P446E2eTopSpeedLiveTests
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
            new Gm { Name = "P446", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 919u, DwCookie = 0x919u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9140000000000000000ul, DrmHash = "p446",
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

    /// <summary>Fly a ship at full throttle (cruise) to convergence with the
    /// REAL ExecuteMove; returns the settled top speed.</summary>
    private static float CruiseTopSpeed(Ship probe)
    {
        probe.Controls = new ControlData(0f, 0f, 0f, Throttle: 1f);
        for (int i = 0; i < 40000; i++)
            probe.ExecuteMove(0.008 * i, 0.008 * (i + 1));
        return probe.Velocity.Length();
    }

    /// <summary>⭐ End-to-end must-pass: complete real dev 165 'GA: Ship Speed 1'
    /// (gas[0]=1.1, c_gaMaxSpeed) through the real server economy, then show a
    /// ship on that side's real ExecuteMove top speed rises by that factor.</summary>
    [Fact]
    public void Completing_ship_speed_dev_raises_real_top_speed()
    {
        using var server = new Srv();
        InjectSingleMissionSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var funder = new FltBot();
        SeatOne(server, funder, "p446");               // seq 0 → mission 0 / side 0

        var mission = server.Missions[0];
        var side = mission.Sides[0];
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();

        // Dev 165 'GA: Ship Speed 1' — real PCore001 data, gas[0]=1.1 (max speed).
        var dev = mission.Core.Developments[165];
        Assert.True(dev.Name.Contains("Ship Speed"), $"dev 165 unexpected: '{dev.Name}'");
        Assert.Equal(1.1f, dev.Gas[GlobalAttributeSet.MaxSpeed], 5);

        // Probe 1: same side/mission, flown BEFORE the dev completes.
        var probe1 = new Ship(9901, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { Mission = mission, SideId = side.Id };
        float vBefore = CruiseTopSpeed(probe1);
        float baseMult = side.GlobalAttributes.Get(GlobalAttributeSet.MaxSpeed);   // civ base (no prior devs)
        Console.WriteLine($"P446-E2E: pre-dev top speed={vBefore:R} (hull.Speed={hull.Speed:R}, side GA[0]={baseMult:F3})");

        // Complete the real development via the real economy path.
        var bucket = DevBucket(side, dev.DevelopmentId);
        server.Ships.First(s => s.Ship.Id == funder.ShipAssignment!.Ship.ShipId).Wallet.Money = 10_000_000;
        funder.SendDonate(bucket.Id, bucket.Price);
        int done = PumpUntil(server, () => server.BucketCompletions.Contains(bucket.Id), 600);
        Assert.True(done < 600, $"dev {bucket.Id} did not complete after 600 ticks");

        // The side's max-speed multiplier rose by exactly the dev's 1.1.
        Assert.Equal(baseMult * 1.1f, side.GlobalAttributes.Get(GlobalAttributeSet.MaxSpeed), 5);
        Console.WriteLine($"P446-E2E: post-dev side GA[0]={side.GlobalAttributes.Get(GlobalAttributeSet.MaxSpeed):F3}");

        // Probe 2: a fresh ship on the same side/mission, flown AFTER.
        var probe2 = new Ship(9902, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { Mission = mission, SideId = side.Id };
        float vAfter = CruiseTopSpeed(probe2);
        Console.WriteLine($"P446-E2E: post-dev top speed={vAfter:R} ratio={vAfter / vBefore:F4} (want ~1.1)");

        // The top speed rose by the dev's max-speed multiplier.
        Assert.InRange(vAfter / vBefore, 1.055f, 1.145f);
    }
}
