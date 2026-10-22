using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P484 — the multi-hop, server-side drone flight the P458/P459 drones must
/// make whenever a target is more than one warp away. The P474 live runs
/// stalled in exactly this shape: on the debug arena (Omega(1053) ↔
/// Alderan(1052) ↔ Plato(1060), sealed by P379) a drone had to cross TWO
/// alephs — Plato → Alderan → Omega — to reach a station, and the
/// MCP combat bot parked at the SECOND aleph. This test asks whether the
/// server-side NPC autopilot (<see cref="Ship.CrossClusterWarpGoal"/> +
/// <see cref="Model.Sector.FindPathToward"/> + the pre-existing
/// <see cref="Model.Sector.ResolveAlephCollisions"/> transit) carries a real
/// NPC drone across BOTH warps to a station two hops away.
///
/// Harness is the debug arena (m2xspiderw.igc + liveSettings.debugMode=true,
/// the P379 sealed topology), run peerless (debug mode auto-starts at boot,
/// P387) with the boot-time initial miners suppressed (P382 opt-out) so the
/// arena is pristine. The drone starts in Plato (1060), side 1's home; the
/// target station sits in Omega (1053) — exactly two hops. The station is
/// side 1 (friendly) so the miner's <c>CidGoto</c> plan is legal under the
/// native <c>LegalCommand</c> NPC gate (an NPC cannot goto an enemy station,
/// shipIGC.h:1631-1636). The station is placed along the Alderan→Omega partner
/// warp's forward axis so the drone burns out of the final exit warp toward it,
/// the P157 lesson (a drone that must turn inside the exit warp's collision
/// volume risks the 2 s warp-debounce "stall and re-transit" hazard).
/// </summary>
[Collection("LiveNetwork")]
public sealed class MultiHopDroneLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg DebugConfig() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = new Ls { DebugMode = true },
        Games =
        [
            new Gm { Name = "P484 Multi-Hop Drone", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A position <paramref name="distance"/> units from
    /// <paramref name="anchor"/>, in the direction that maximizes clearance to
    /// every warp in <paramref name="sector"/> (P157 pattern).</summary>
    private static Vector3 ClearPoint(Sector sector, Vector3 anchor, float distance)
    {
        var warpPositions = sector.Warps
            .Where(w => w.Warp.Position is { Length: 3 })
            .Select(w => new Vector3(w.Warp.Position[0], w.Warp.Position[1], w.Warp.Position[2]))
            .ToList();
        Vector3 best = anchor;
        float bestMin = -1f;
        for (int xi = -1; xi <= 1; xi++)
        for (int yi = -1; yi <= 1; yi++)
        for (int zi = -1; zi <= 1; zi++)
        {
            if (xi == 0 && yi == 0 && zi == 0) continue;
            var dir = new Vector3(xi, yi, zi).Normalize();
            var cand = anchor + dir * distance;
            float mn = float.MaxValue;
            foreach (var p in warpPositions) mn = MathF.Min(mn, (cand - p).Length());
            if (mn > bestMin) { bestMin = mn; best = cand; }
        }
        return best;
    }

    private static float WarpClearance(ResolvedWarp w) => Sector.WarpVisualScale * w.Warp.Radius + 425f;

    private static DataStationIGC Target(Sector sector, short side, short id, Vector3 pos)
    {
        var type = Core().StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmLand) != 0);
        return new DataStationIGC(
            Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: side, StationId: id,
            StationTypeId: (short)type.StationTypeId, BpHull: 1, BpShield: 1, Name: $"BASE_{id}");
    }

    private static DataStationTypeIGC LandType() =>
        Core().StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmLand) != 0);

    private static float Dist(Ship s, DataStationIGC t) =>
        (new Vector3(t.Position[0], t.Position[1], t.Position[2]) - s.Position).Length();

    /// <summary>⭐ P484 scope 3: a server-side NPC drone whose target sits two
    /// warps away (Plato → Alderan → Omega) must cross BOTH alephs and arrive
    /// in the target's sector, closing on the station. Green only if the
    /// cross-cluster autopilot re-paths after the first transit and the second
    /// transit fires — i.e. server-side drones handle multi-hop routes.
    /// Live tier (P753b): the passing path completes in a few seconds, but the
    /// failure path runs both 60k-tick timeouts (~85 s on this workstation).</summary>
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    [SkippableFact]
    public void Server_drone_crosses_two_alephs_to_a_station_two_hops_away()
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;   // P382: pristine arena — no boot miner to perturb the path
        server.Boot(DebugConfig());

        var mission = server.Missions[0];
        Assert.NotEqual(MissionStage.NotStarted, mission.Stage);   // P387 debug auto-start

        Sector plato = mission.Sectors.First(s => s.Id == 1060);   // side 1 home
        Sector alderan = mission.Sectors.First(s => s.Id == 1052); // sealed hub
        Sector omega = mission.Sectors.First(s => s.Id == 1053);   // side 0 home
        Assert.NotEqual(plato, omega);

        // The Omega-side entry warp (the Alderan→Omega hop's exit), whose
        // forward the transit seam launches the arriving ship along. Place the
        // target station down that axis so the drone burns out of the exit
        // warp toward it (the P157 lesson: a drone that must turn inside the
        // exit warp's collision volume risks the 2 s warp-debounce stall).
        ResolvedWarp omegaEntry = omega.Warps.First(w => w.Partner() is not null
            && !ReferenceEquals(w.Destination, omega));
        var bExit = new Vector3(omegaEntry.Warp.Position![0], omegaEntry.Warp.Position[1], omegaEntry.Warp.Position[2]);
        float[] pf = omegaEntry.Warp.Forward is { Length: 3 } v && (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) > 0f
            ? v : [1f, 0f, 0f];
        var fwd = new Vector3(pf[0], pf[1], pf[2]).Normalize();
        var stationPos = bExit + fwd * WarpClearance(omegaEntry);
        var station = Target(omega, 1, id: 9633, stationPos);
        mission.PlaceStation(new Station(station, LandType()), omega);

        // Sanity: Plato → Omega really is two hops (FindPathToward from Plato
        // returns Plato's warp to Alderan, not straight to Omega).
        var platoWarp = plato.FindPathToward(omega);
        Assert.NotNull(platoWarp);
        Assert.Equal(alderan.Id, platoWarp!.Destination!.Id);

        // The drone: a side-1 NPC spawned in Plato, clear of every warp.
        var platoWarpPos = new Vector3(platoWarp.Warp.Position![0], platoWarp.Warp.Position[1], platoWarp.Warp.Position[2]);
        var npc = server.SpawnNpcShip(mission, plato, sideId: 1, ClearPoint(plato, platoWarpPos, WarpClearance(platoWarp)));
        Assert.True(npc.IsNpc, "the test subject must be a server-side NPC drone");

        npc.PlanTarget = station;
        npc.PlanCommandId = OrderChange.CidGoto;

        float d0 = Dist(npc, station);
        Assert.True(d0 > 800f, $"target must start at least two hops away: d0={d0:F1}");

        // ── Leg A: Plato → Alderan (hop 1) ──────────────────────────────────
        int t1 = PumpUntil(server, () => npc.Sector?.Id == alderan.Id, 60000);
        Assert.True(t1 < 60000,
            $"drone never crossed Plato→Alderan (first hop, ticks {t1}, still in {npc.Sector?.Name}/{npc.Sector?.Id})");
        var pAfterFirst = npc.Position;
        Console.WriteLine($"P484-2HOP: drone crossed hop 1 {plato.Name}({plato.Id})→{alderan.Name}({alderan.Id}) at tick {t1}, pos ({pAfterFirst.X:F0},{pAfterFirst.Y:F0},{pAfterFirst.Z:F0})");

        // ── Leg B: Alderan → Omega (hop 2) — the hop P474 reported parking ──
        int t2 = PumpUntil(server, () => npc.Sector?.Id == omega.Id, 60000);
        Assert.True(t2 < 60000,
            $"drone never crossed Alderan→Omega (SECOND hop — the P474 parking hop; ticks {t2}, still in {npc.Sector?.Name}/{npc.Sector?.Id})");
        Console.WriteLine($"P484-2HOP: drone crossed hop 2 {alderan.Name}({alderan.Id})→{omega.Name}({omega.Id}) after {(t2 - t1) * 50}ms of sim");

        // ── Arrival: close on the station in Omega ──────────────────────────
        float minD = float.MaxValue;
        for (int i = 0; i < 60000 && minD > 200f; i++)
        {
            server.TickSim();
            minD = MathF.Min(minD, Dist(npc, station));
        }
        Console.WriteLine($"P484-2HOP: drone arrived in {omega.Name}({omega.Id}); minD to station {minD:F1}");
        Assert.True(minD < d0 - 300f, $"must be measurably closer overall: d0={d0:F1} → minD={minD:F1}");
    }
}
