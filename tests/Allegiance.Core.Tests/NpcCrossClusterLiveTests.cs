using System.Reflection;
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
/// P157 (NPC cross-cluster movement — <see cref="Model.Sector.FindPathToward"/>
/// + the autopilot's cross-cluster goal leg). Live loopback proof that an
/// <see cref="IsNpc"/> ship given an explicitly test-authored cross-sector
/// <see cref="Ship.PlanTarget"/> now gets THERE: the autopilot path-finds the
/// next warp toward the target's sector and steers at it, the pre-existing
/// <see cref="Model.Sector.ResolveAlephCollisions"/> transits the ship, and the
/// autopilot (re-resolving its goal every tick) continues toward the station in
/// the new sector. Zero new transit code — this task only decides which warp to
/// fly at, exactly the task's scope.
///
/// Harness mirrors P147/P155/P156's live tests (booted in-process server + one
/// seated bot, no Windows VM); the mission is advanced with the server's own
/// deterministic <see cref="Srv.TickSim"/> (one 0.05 s chunk each). The
/// decision-cycle's own searches are same-sector-only (out of scope), so the
/// cross-sector target is set directly on <see cref="Ship.PlanTarget"/>, as the
/// task specifies.
/// </summary>
/// <remarks>
/// The multi-sector <c>XXV_simple.igc</c> map (same one WarpTransitServerTests
/// uses) provides real cross-cluster warp pairs; the test discovers the first
/// one at runtime rather than hard-coding map geometry, so it survives map
/// churn. Synthetic, fully-controlled graphs are used for the direct
/// <see cref="Model.Sector.FindPathToward"/> unit proofs (adjacency, multi-hop
/// first-warp semantics, unreachable) and a synthetic <c>ClusterId</c> not
/// present in the mission drives the autopilot's clean stand-by no-op.
/// </remarks>
[Collection("LiveNetwork")]
public sealed class NpcCrossClusterLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P157 Cross-Cluster Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 815u, DwCookie = 0x815u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 78900000000000007ul, DrmHash = "p157cross",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A position <paramref name="distance"/> units from
    /// <paramref name="anchor"/>, in the direction that maximizes clearance to
    /// every warp in <paramref name="sector"/> (sampled over 26 cardinal/ordinal
    /// axes) — used so spawns/stations never start inside an unrelated warp and
    /// the ship must visibly steer at its intended warp rather than being born
    /// in a collision volume.</summary>
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

    /// <summary>P238 — the scaled radius of a warp's widened crossable sphere
    /// (<see cref="Sector.WarpVisualScale"/> ≈ 9.5 × wire radius) plus a margin.
    /// Placements — spawns, objectives, parked peers — must sit beyond this to be
    /// safely OUTSIDE any warp's transit volume, because P238 made the whole big
    /// vortex crossable (a point nearer than this gets dragged through the warp).</summary>
    private static float WarpClearance(ResolvedWarp w) => Sector.WarpVisualScale * w.Warp.Radius + 425f;

    /// <summary>A genuine land-capable station record (real core type) at the
    /// given position in <paramref name="sector"/> on <paramref name="side"/>.</summary>
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

    /// <summary>⭐ The headline P157 proof: an NPC with a cross-sector
    /// <see cref="Ship.PlanTarget"/> path-finds the connecting warp, steers at
    /// it, is carried through by the pre-existing warp-collision transit, and —
    /// re-resolving its goal each tick — continues toward the station in the
    /// new sector. Asserts the ship's <see cref="Ship.Sector"/> genuinely
    /// changed to the target's sector and that its position is measurably
    /// closer to the station afterward (both overall and, within the new
    /// sector, after the crossing).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cross_cluster_npc_paths_through_warp_and_continues_to_station_in_new_sector()
    {
        Require.Artwork();
        using var server = new Srv();
        Mission? mission = null;
        Sector? sectorA = null, sectorB = null;
        ResolvedWarp? crossWarp = null;
        DataStationIGC station = default;
        short npcSide = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;

            // Discover the first genuine cross-cluster warp pair in the real map
            // (a warp whose resolved destination is a DIFFERENT sector, with a
            // resolvable partner so the transit has somewhere to put the ship).
            foreach (var sec in m.Sectors)
            {
                var w = sec.Warps.FirstOrDefault(warp =>
                    warp.Destination is not null && !ReferenceEquals(warp.Destination, sec) && warp.Partner() is not null);
                if (w is not null) { sectorA = sec; crossWarp = w; sectorB = w.Destination; break; }
            }
            if (sectorA is null || sectorB is null || crossWarp is null)
                throw new InvalidOperationException("XXV_simple.igc must contain a cross-cluster warp pair for this test");

            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;   // NPC on the OTHER side (no players)

            // A real station in sector B (the target sector) on the NPC's own
            // side, placed along the B-side partner warp's FORWARD axis — the
            // exact trajectory HitWarpEvent launches the crossing ship along
            // (ServerHost.cs:639-646: exit velocity = +partner.Forward·speed), so
            // the ship burns straight out of the exit warp's collision volume and
            // toward the station, rather than stalling inside it (turn-then-burn
            // vs. the 2 s warp-debounce window) and re-transiting.
            var partner = crossWarp.Partner()!;
            var bExit = new Vector3(partner.Warp.Position![0], partner.Warp.Position[1], partner.Warp.Position[2]);
            float[] pf = partner.Warp.Forward is { Length: 3 } v && (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) > 0f
                ? v : [1f, 0f, 0f];
            var fwd = new Vector3(pf[0], pf[1], pf[2]).Normalize();
            var stationPos = bExit + fwd * WarpClearance(partner);
            station = Target(sectorB, npcSide, id: 8161, stationPos);
            m.PlaceStation(new Station(station, LandType()), sectorB);

            // The peer bot's ship: park it in sector B, clear of every warp, so it
            // never transits and never interferes with the NPC's approach in A.
            var peerHull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var peerShip = new Ship(id, peerHull, ClearPoint(sectorB, bExit, WarpClearance(partner)), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peerShip, sectorB);
            return (peerShip, sectorB);
        };

        server.SpawnInitialMinersAtBoot = false;   // P382: pristine arena — no initial miner to perturb precise assertions
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p157_cross");
        bot.SendLogonRequest(Request("p157_cross"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);
        Assert.NotNull(mission); Assert.NotNull(sectorA); Assert.NotNull(sectorB); Assert.NotNull(crossWarp);

        // Spawn the NPC in sector A, a good clear run away from the connecting
        // warp (so it must STEER to it, not be born inside its collision volume).
        var waPos = new Vector3(crossWarp!.Warp.Position![0], crossWarp.Warp.Position[1], crossWarp.Warp.Position[2]);
        var npcSpawn = ClearPoint(sectorA!, waPos, WarpClearance(crossWarp!));
        var npc = server.SpawnNpcShip(mission!, sectorA!, npcSide, npcSpawn);
        Assert.True(npc.IsNpc, "the test ship must be an NPC");
        Assert.NotEqual(sectorA!.Id, station.ClusterId);   // sanity: the target really is cross-sector

        // Explicitly authored cross-sector PlanTarget (the decision cycle's own
        // searches are same-sector-only — out of P157 scope).
        npc.PlanTarget = station;
        npc.PlanCommandId = OrderChange.CidGoto;

        float d0 = Dist(npc, station);
        Assert.True(d0 > 480f, $"cross-sector target must start far: d0={d0:F1}");

        // ── Phase 1: steer to the warp and transit into the target sector ────
        int ti = 0;
        while (ti < 60000 && npc.Sector?.Id != sectorB!.Id) { server.TickSim(); ti++; }
        Assert.True(npc.Sector?.Id == sectorB!.Id,
            $"NPC never crossed into sector {sectorB!.Name}/{sectorB.Id} (transit ticks {ti}, still in {npc.Sector?.Name}/{npc.Sector?.Id})");
        var pAfterTransit = npc.Position;
        float dTransit = Dist(npc, station);

        // ── Phase 2: continue toward the station in the new sector ───────────
        // P386: the station goal is CONSUMED on arrival (PlanTarget cleared at the
        // dock — DockWithStationEvent), and the miner then resumes its decision
        // cycle (re-mines) after docking; so measure the MINIMUM approach to the
        // station over Phase 2 (it must close on and reach it), not a late-tick
        // distance after it docked at the station and re-departed.
        float minD = float.MaxValue;
        for (int i = 0; i < 60000 && !(minD < d0 - 300f && minD < dTransit - 60f); i++)
        {
            server.TickSim();
            minD = MathF.Min(minD, Dist(npc, station));
        }

        Console.WriteLine($"P157-CROSS: NPC {npc.Id} (side {npcSide}) {sectorA!.Name}→{sectorB!.Name} " +
                          $"toward station {station.StationId}: d0={d0:F1} → (transit@{ti}t) dTransit={dTransit:F1} → minD={minD:F1}; " +
                          $"crossed at t≈{ti}×0.05s; sector after = {npc.Sector?.Name}/{npc.Sector?.Id}");

        Assert.True(minD < d0 - 300f, $"must be measurably closer overall: d0={d0:F1} → minD={minD:F1}");
        Assert.True(minD < dTransit - 60f,
            $"must keep closing on the station after crossing: right-after-transit {dTransit:F1} → minD={minD:F1}");
    }

    /// <summary>⭐ The clean-no-op proof: an NPC whose cross-sector
    /// <see cref="Ship.PlanTarget"/> names a sector that does not exist in the
    /// mission resolves to "no path" and stands by — the ship never moves, never
    /// crashes, and does not thrash (the whole point of the fallback branch).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cross_cluster_npc_with_unreachable_target_stands_by_without_moving_or_crashing()
    {
        Require.Artwork();
        using var server = new Srv();
        Mission? mission = null;
        Sector? sectorA = null;
        short npcSide = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sectorA = m.Sectors[0];
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;
            var peerHull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var peerShip = new Ship(id, peerHull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peerShip, sectorA);
            return (peerShip, sectorA);
        };

        server.SpawnInitialMinersAtBoot = false;   // P382: pristine arena — no initial miner to perturb precise assertions
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p157_unreach");
        bot.SendLogonRequest(Request("p157_unreach"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);
        Assert.NotNull(mission); Assert.NotNull(sectorA);

        var spawn = ClearPoint(sectorA!, new Vector3(0f, 0f, 0f), 560f);
        var npc = server.SpawnNpcShip(mission!, sectorA!, npcSide, spawn);

        // A target sector id that is NOT in the mission → sector resolution
        // fails → the autopilot logs a stand-by and leaves the ship with no goal.
        short ghostCluster = (short)(mission!.Sectors.Max(s => s.Id) + 777);   // deliberately off-map
        Assert.True(!mission.Sectors.Any(s => s.Id == ghostCluster), "ghost sector must really be absent");
        var ghost = new DataStationIGC(
            Position: [999f, 999f, 999f], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: ghostCluster, SideId: npcSide, StationId: 8261,
            StationTypeId: (short)LandType().StationTypeId, BpHull: 1, BpShield: 1, Name: "GHOST");
        npc.PlanTarget = ghost;
        npc.PlanCommandId = OrderChange.CidGoto;

        var start = npc.Position;
        for (int i = 0; i < 3000; i++) server.TickSim();   // 150 simulated seconds

        Assert.True(npc.Sector?.Id == sectorA!.Id, $"ship must not change sector for an unreachable target (in {npc.Sector?.Name}/{npc.Sector?.Id})");
        float drift = (npc.Position - start).Length();
        Assert.True(drift < 1e-3f, $"ship must stand still for an unreachable target (drift {drift:E2})");
        Console.WriteLine($"P157-NOOP: NPC {npc.Id} given unreachable target sector {ghostCluster}: stood by cleanly " +
                          $"(drift {drift:E2} over 3000 ticks, still in {npc.Sector?.Name}), no crash, no thrash");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Direct unit proofs of FindPathToward on fully-controlled synthetic graphs.
    // Core internals are invisible to tests (only Allegiance.Server declares
    // InternalsVisibleTo), so the private _warps list is reached via reflection,
    // the same mechanism the suite already uses for Ship._fraction et al.
    // ────────────────────────────────────────────────────────────────────────

    private static readonly FieldInfo WarpsField =
        typeof(Sector).GetField("_warps", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void Attach(Sector s, ResolvedWarp w) =>
        ((List<ResolvedWarp>)WarpsField.GetValue(s)!).Add(w);

    private static DataClusterIGC Cluster(short id, string name) => new(
        StarSeed: 1, LightDirection: [1f, 0f, 0f], LightColor: 1, ScreenX: 0, ScreenY: 0,
        ClusterId: id, Debris: 0, Stars: 0, Name: name, PosterName: "", PlanetName: "",
        PlanetSinLatitude: 0, PlanetLongitude: 0, PlanetRadius: 0, Active: true, HomeSector: false);

    private static DataWarpIGC Warp(short id, short clusterId, Vector3 pos) => new(
        WarpId: id, DestinationId: (short)(id + 1000), Radius: 50, TextureName: "", IconName: "",
        Name: $"W{id}", Position: [pos.X, pos.Y, pos.Z], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Signature: 1f, ClusterId: clusterId);

    private static Sector Sec(short id, string name) => new(Cluster(id, name));

    /// <summary>Already there — the target IS this sector, so there is nothing to
    /// fly toward (native <c>FindPath</c> returns NULL, common.cpp:904-905).</summary>
    [Fact]
    public void FindPathToward_same_sector_returns_null()
    {
        var a = Sec(1, "A");
        Assert.Null(a.FindPathToward(a));
    }

    /// <summary>Adjacent — a direct warp to the target returns exactly that warp
    /// (native returns <c>pwarpStart</c> = the seed warp itself).</summary>
    [Fact]
    public void FindPathToward_adjacent_returns_the_connecting_warp()
    {
        var a = Sec(1, "A");
        var b = Sec(2, "B");
        var wa = new ResolvedWarp(Warp(10, 1, new Vector3(100f, 0f, 0f)), b);
        Attach(a, wa);

        Assert.Same(wa, a.FindPathToward(b));
    }

    /// <summary>Multi-hop — with only A→C→B reachable, the returned warp is the
    /// FIRST hop from A (A→C), proving the <c>pwarpStart</c> semantics carry
    /// across levels, not just adjacency.</summary>
    [Fact]
    public void FindPathToward_multi_hop_returns_the_first_warp_of_the_path()
    {
        var a = Sec(1, "A");
        var b = Sec(2, "B");
        var c = Sec(3, "C");
        var wa = new ResolvedWarp(Warp(10, 1, new Vector3(100f, 0f, 0f)), c);   // A→C
        var wc = new ResolvedWarp(Warp(20, 3, new Vector3(0f, 100f, 0f)), b);   // C→B
        Attach(a, wa);
        Attach(c, wc);

        Assert.Same(wa, a.FindPathToward(b));
    }

    /// <summary>Unreachable — the target sector is a real sector but no warp path
    /// reaches it: returns null cleanly (native common.cpp:966-968), no throw.
    /// A dead-end A→C with the target B elsewhere proves the traversal exhausts
    /// the reachable component and gives up rather than looping forever.</summary>
    [Fact]
    public void FindPathToward_unreachable_sector_returns_null_without_throwing()
    {
        var a = Sec(1, "A");
        var b = Sec(2, "B");
        var c = Sec(3, "C");
        var wa = new ResolvedWarp(Warp(10, 1, new Vector3(100f, 0f, 0f)), c);   // A→C (dead end)
        Attach(a, wa);

        Assert.Null(a.FindPathToward(b));   // no exception, no infinite loop
    }

    /// <summary>No warps at all — an empty graph is trivially unreachable and
    /// must return null (a clean stand-by, not a crash).</summary>
    [Fact]
    public void FindPathToward_sector_with_no_warps_returns_null()
    {
        var a = Sec(1, "A");
        var b = Sec(2, "B");
        Assert.Null(a.FindPathToward(b));
    }
}
