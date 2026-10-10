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
/// P493 — LIVE proof that a side's probe/scout is "eyes for the team": a
/// side-0 probe in an empty sector spots a side-1 ship, and side-0's seated
/// peer (in a DIFFERENT sector) receives that ship's SHIP_STATUS(90) with the
/// correct last-known sector (the port of native ShowObject →
/// CFSShip::ShipStatusSpotted, fsship.cpp:237), then receives the HIDDEN
/// transition (Unknown=true, the port of HideObject → ShipStatusHidden,
/// fsship.cpp:287) when the ship leaves the probe's scan range. Over the real
/// wire to a real <see cref="FltBot"/> peer, whose <c>ShipStatuses</c>
/// accumulator is exactly what the native strategy layer (ScoutExploreMap)
/// gates its sector tracking on.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P493EnemySpottingLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static DataHullTypeIGC FighterHull()
        => Core().HullTypes.Values
            .Where(h => h.HitPoints > 0f && !h.HasCapability(DataHullTypeIGC.HabmLifepod))
            .OrderBy(h => h.HitPoints).First();

    /// <summary>The LONGEST-ranged UNARMED (sensor/recon) probe type — the
    /// canonical "eyes" that spots an enemy entering the sector.</summary>
    private static DataProbeTypeIGC ReconProbe(StaticCore core)
        => core.ProbeTypes.Values
            .Where(p => p.ProjectileTypeId <= 0 && p.ScannerRange > 0f)
            .OrderByDescending(p => p.ScannerRange).First();

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P493 Spotting", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name, ulong steam) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 933u, DwCookie = 0x933u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = steam, DrmHash = "p493",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ A side-0 probe in an empty sector spots a side-1 ship entering
    /// probe range: the side-0 peer (in a different sector) receives that ship's
    /// status with the correct last-known sector, then the hidden transition when
    /// the ship leaves the probe's range.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_spot_of_cross_side_ship_delivers_status_and_hidden_to_side0_peer()
    {
        Require.Artwork();
        using var server = new Srv();
        var core = Core();
        var hull = FighterHull();

        Ship? alpha = null, bravo = null;
        Sector? sec0 = null, probeSector = null;
        short alphaSide = 0, bravoSide = 1;
        int placed = 0;
        server.ShipSpawner = (_, mission, id) =>
        {
            sec0 = mission.Sectors[0];
            // An empty (station-free) sector, distinct from the home sector, to
            // host the side-0 probe — the ONLY scanner there.
            probeSector ??= mission.Sectors
                .First(s => s.Id != mission.Sectors[0].Id && s.StationRuntimes.Count == 0);
            if (placed == 0)
            {
                placed++;
                alphaSide = mission.Sides[0].Id;
                var s = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = alphaSide };
                mission.PlaceShip(s, sec0);
                alpha = s;
                return (s, sec0);
            }
            // Bravo (side 1) sits in the probe sector but FAR from the probe
            // location (origin), so side 0 does not see it until we move it in.
            bravoSide = mission.Sides[1].Id;
            var b = new Ship(id, hull, new Vector3(50_000f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = bravoSide };
            mission.PlaceShip(b, probeSector);
            bravo = b;
            placed++;
            return (b, probeSector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;   // bypass the ready-up lobby
        var port = server.BoundPort;

        using var alphaBot = new FltBot();
        using var bravoBot = new FltBot();
        alphaBot.Connect(port, "p493_alpha"); alphaBot.SendLogonRequest(Request("p493_alpha", 99000000000000001ul));
        bravoBot.Connect(port, "p493_bravo"); bravoBot.SendLogonRequest(Request("p493_bravo", 99000000000000002ul));
        int seated = PumpUntil(server, () => alphaBot.ShipAssignment is not null && bravoBot.ShipAssignment is not null, 4000);
        Assert.True(seated < 4000, "both bots seated");
        Assert.NotNull(alpha); Assert.NotNull(bravo); Assert.NotNull(probeSector);
        Assert.NotEqual(alphaSide, bravoSide);

        // Deploy the side-0 recon probe at the probe sector's origin (the sole
        // side-0 scanner there; Alpha's own ship is in a different sector).
        var probeType = ReconProbe(core);
        var probe = new Probe(9001, probeSector, probeType, Vector3.Zero, alpha!.Mission!.LastUpdate, alphaSide, launcher: null);
        probeSector.Add(probe);

        // Calibrate the detection distance from the real probe/ship geometry
        // (native DetectionRadius = scannerRadius + targetRadius + range·sig).
        float R = VisibilityMath.DetectionRadius(
            probeType.Radius, probeType.ScannerRange,
            0.5f * hull.Length, hull.Signature);

        // Sanity: out-of-range initially, so no premature spot.
        int settle = PumpUntil(server, () => !bravo!.SeenBySide(alphaSide), 200);
        Assert.True(settle < 200, "intruder starts out of side-0's sight (probe is the only eye)");
        Assert.DoesNotContain(alphaBot.ShipStatuses, s => s.ShipId == bravo!.Id && !s.Unknown);

        // ══ SPOT: fly the side-1 ship into probe range. The side-0 peer (in a
        // different sector) must receive the enemy's status with the correct
        // last-known sector (Unknown=false, State=flying, SectorId == probe sector).
        // Reset the accumulator BEFORE the move so the status the edge fires is
        // preserved for the assertion below (it arrives during the `seen` pump).
        alphaBot.ResetShipStatuses();
        lock (server) { bravo!.Position = new Vector3(0.3f * R, 0f, 0f); bravo.Velocity = Vector3.Zero; }
        int seen = PumpUntil(server, () => bravo!.SeenBySide(alphaSide), 4000);
        Assert.True(seen < 4000, "probe must come to see the cross-side ship");

        int spotted = PumpUntil(server, () =>
            alphaBot.ShipStatuses.Any(s => s.ShipId == bravo!.Id && !s.Unknown), 4000);
        Assert.True(spotted < 4000, "side-0 peer must receive the spotted status of the enemy ship");
        var spotStatus = alphaBot.ShipStatuses.First(s => s.ShipId == bravo!.Id && !s.Unknown);
        Assert.Equal(probeSector.Id, spotStatus.SectorId);
        Assert.Equal(ShipStatusMessage.StateFlying, spotStatus.State);
        Assert.Equal<short>(hull.HullId, spotStatus.HullId);

        // ══ HIDE: fly the ship out of probe range. The side-0 peer must receive
        // the hidden transition (Unknown=true) for the same ship.
        lock (server) { bravo!.Position = new Vector3(10f * R, 0f, 0f); bravo.Velocity = Vector3.Zero; }
        int unseen = PumpUntil(server, () => !bravo!.SeenBySide(alphaSide), 4000);
        Assert.True(unseen < 4000, "probe must lose sight of the cross-side ship");
        int hidden = PumpUntil(server, () =>
            alphaBot.ShipStatuses.Any(s => s.ShipId == bravo!.Id && s.Unknown), 4000);
        Assert.True(hidden < 4000, "side-0 peer must receive the hidden transition for the enemy ship");

        Console.WriteLine($"P493-LIVE: probe(id {probe.Id}, range {probeType.ScannerRange}) in sector {probeSector.Id} " +
                          $"spotted side-{bravoSide} ship {bravo.Id} → side-{alphaSide} peer got status (sector {spotStatus.SectorId}, " +
                          $"state {spotStatus.State}, hull {spotStatus.HullId}); hidden after {unseen}+{hidden} pumps");
    }
}
