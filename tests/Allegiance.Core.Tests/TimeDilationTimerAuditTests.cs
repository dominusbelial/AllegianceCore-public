using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using LS = global::Allegiance.Server.LiveSettingsConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P618 — the timer-audit regression test: with the sim time scale at 0.5, a
/// game-timed cooldown (here a real ripcord countdown, the port of native
/// <c>shipIGC.cpp:277-294</c> driven by the sim clock's per-tick chunk) takes
/// TWICE as long in wall time (ticks) and the SAME amount of sim time to
/// complete. This is the invariant that makes EVE-style time dilation — and
/// P612's cinematic slow motion — play consistently: the whole game world is
/// timed in SIM time, so slowing the clock slows every cooldown proportionally
/// instead of leaving them on wall time (which would make them finish at full
/// speed while physics crawls).
///
/// Method: two real in-process servers, identical except
/// <c>liveSettings.timeScale</c> = 1.0 vs 0.5, each with one flying requester
/// and an injected ripcord anchor. Activate the ripcord, then pump a FIXED
/// number of sim ticks and measure how much the countdown fell. At scale 0.5
/// each tick advances half the sim time, so the same 60 ticks consume 2× less
/// sim time — hence the SAME cooldown amount takes 2× as many ticks (2× wall
/// time) to elapse, while the total sim time to a given countdown is identical.
///
/// Contrast with what MUST NOT happen: a wall-clock-timed cooldown would fall
/// by the same amount per wall tick regardless of scale, giving 1× — the
/// regression this guards against (the pre-audit behaviour of any timer that
/// deducted a fixed per-tick amount instead of the sim chunk).
/// </summary>
[Collection("LiveNetwork")]
public class TimeDilationTimerAuditTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private const short FlySector = 1052;     // Borasisi — no station, requester stays flying
    private const short TargetA = 1054;       // Deucalion — gets the injected side-0 ripcord anchor

    private static Cfg Config(double timeScale) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,                 // period = 0.05 s
        LiveSettings = new LS { TimeScale = timeScale },
        Games =
        [
            new Gm { Name = "TiDi Audit", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 613u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7811000000000000000ul, DrmHash = "tidi",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void AddStationToSector(Sector sector, DataStationIGC station)
    {
        var mission = sector.Mission!;
        mission.PlaceStation(new Station(station, mission.Core.StationTypes[station.StationTypeId]), sector);
    }

    /// <summary>Boot a server + one flying requester with an injected ripcord
    /// anchor, connect the bot, and activate the ripcord. Returns the server,
    /// the bot, the anchored ship, and the requester's ripcord state so the test
    /// can pump ticks and measure countdown decay.</summary>
    private static (Srv server, FltBot bot, Ship ship) BootAndActivate(double timeScale, int activationSeed = 9900)
    {
        var server = new Srv();
        int n = 0;
        DataStationIGC anchor = default;
        Ship requester = null!;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var sectorA = mission.Sectors.First(s => s.Id == TargetA);
            if (n == 0)
            {
                var anchorType = core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRipcord)).StationTypeId;
                anchor = new DataStationIGC(
                    Position: [1000f, 0f, 0f], Up: [0f, 0f, 1f], Forward: [1f, 0f, 0f], Rotation: [1f, 0f, 0f, 0f],
                    ClusterId: TargetA, SideId: 0, StationId: (short)activationSeed, StationTypeId: anchorType, BpHull: 1, BpShield: 1, Name: "AnchA");
                AddStationToSector(sectorA, anchor);
            }
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            var ship = new Ship(shipId, hull, new Vector3(300f + n * 10f, 650f, 890f), Vector3.Zero, Orientation.Identity)
            { SideId = 0 };
            mission.PlaceShip(ship, fly);
            if (n == 0) requester = ship;
            n++;
            return (ship, fly);
        };
        server.Boot(Config(timeScale));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180 — flyable immediately, bypass ready-up

        var bot = new FltBot();
        bot.Connect(server.BoundPort, "REQR");
        bot.SendLogonRequest(Request("REQR"));
        PumpUntil(server, () => bot.ShipAssignment is not null, 4000);

        bot.ResetRipcordActivates();
        bot.SendRipcordRequest(TargetA);
        PumpUntil(server, () => bot.RipcordActivates.Count >= 1, 4000);
        Assert.True(requester.IsRipcordActive);
        return (server, bot, requester);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cooldown_TakesTwiceAsLongInWallTime_AtScaleHalf_ButSameSimTime()
    {
        Require.Artwork();

        // --- Scale 1.0: each tick advances 0.05 s of sim time. ---
        var (s1, _, ship1) = BootAndActivate(1.0, activationSeed: 9010);
        float t0a = ship1.RipcordTimeLeft;
        const int M = 60;
        for (int i = 0; i < M; i++) s1.TickSim();           // 60 × 0.05 = 3.0 s sim
        float t1a = ship1.RipcordTimeLeft;
        float fellNormal = t0a - t1a;
        Assert.True(ship1.IsRipcordActive, "ripcord should still be legal/active after 60 ticks");

        // --- Scale 0.5: each tick advances 0.025 s of sim time. ---
        var (s05, _, ship05) = BootAndActivate(0.5, activationSeed: 9020);
        float t0b = ship05.RipcordTimeLeft;
        for (int i = 0; i < M; i++) s05.TickSim();          // 60 × 0.025 = 1.5 s sim
        float t1b = ship05.RipcordTimeLeft;
        float fellHalf = t0b - t1b;
        Assert.True(ship05.IsRipcordActive, "ripcord should still be legal/active after 60 ticks");

        // The SAME 60 ticks consume ~2× the sim time at scale 1.0 vs 0.5, so
        // the countdown (a sim-time cooldown) fell ~2× as far at scale 1.0.
        // Equivalently: the SAME cooldown amount takes 2× the wall ticks at 0.5.
        Assert.True(fellNormal > 0.001f, "no decay at scale 1.0 — cooldown not ticking");
        Assert.True(fellHalf > 0.001f, "no decay at scale 0.5 — cooldown not ticking");
        // Each tick's chunk scales exactly: fellNormal ≈ M×0.05, fellHalf ≈ M×0.025.
        Assert.True(Math.Abs(fellNormal - M * 0.05) < 0.551, $"{fellNormal:F3} !≈ {M * 0.05:F3}");
        Assert.True(Math.Abs(fellHalf - M * 0.025) < 0.601, $"{fellHalf:F3} !≈ {M * 0.025:F3}");

        // THE AUDIT CLAIM: twice the wall ticks for the same sim time.
        double ratio = fellNormal / fellHalf;
        Assert.True(ratio > 1.750 && ratio < 2.350,
            $"scale-0.5 cooldown did not take ~2× wall time: fell@{1.0}={fellNormal:F3}s vs @0.5={fellHalf:F3}s ratio {ratio:F3}");

        // And the invariant in sim-time terms: the number of ticks to consume a
        // fixed sim-time cooldown doubles when the scale halves.
        double ticksForSame = fellNormal / fellHalf * M;
        Assert.True(ticksForSame > 100 && ticksForSame < 140,
            $"to consume the same sim-time cooldown, scale 0.5 needs ~{ticksForSame:F0} ticks (vs {M} at 1.0)");

        Console.WriteLine($"TIDI-AUDIT: ripcord cooldown over {M} ticks — scale 1.0 fell {fellNormal:F3}s sim, scale 0.5 fell {fellHalf:F3}s sim; " +
                          $"ratio {ratio:F3} (≈2× wall time at half scale, SAME sim time per cooldown)");

        s1.Dispose(); s05.Dispose();
    }

    /// <summary>Opposite-direction guard: at scale 1.0 a game-timed cooldown is
    /// NOT sped up or distorted — it simply matches sim time (the control that
    /// proves the audit isn't an artefact of a particular scale).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void AtFullSpeed_CooldownMatchesSimTimeExactly()
    {
        Require.Artwork();
        var (s1, _, ship) = BootAndActivate(1.0, activationSeed: 9030);
        float t0 = ship.RipcordTimeLeft;
        const int M = 40;
        for (int i = 0; i < M; i++) s1.TickSim();
        float fell = t0 - ship.RipcordTimeLeft;
        Assert.True(Math.Abs(fell - M * 0.05) < 0.451, $"{fell:F3} !≈ {M * 0.05:F3} (full speed should be 1:1 with sim time)");
        s1.Dispose();
    }
}
