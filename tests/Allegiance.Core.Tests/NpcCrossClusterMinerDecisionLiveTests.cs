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
/// P158 — cross-cluster miner decision, live loopback proof that an idle NPC
/// whose OWN sector has nothing mineable/sellable now travels on its own
/// initiative into a neighbour sector across a warp to find it — the loop the
/// P157 stage explicitly deferred. Unlike P157 (where the test handed the NPC a
/// cross-sector <see cref="Ship.PlanTarget"/>), here the ship's own
/// <see cref="Ship.DecideMiningOrSelling"/> cycle makes the decision via the new
/// any-cluster searches, and the newly-fixed asteroid autopilot leg carries it
/// across. This is the first time this port's miner decision cycle travels
/// somewhere without a test explicitly setting the target.
///
/// Two mirrored legs:
///   • Mining — an idle NPC in a sector with NO mineable asteroid of its own,
///     connected by a warp to a sector that DOES have one, autonomously targets
///     it, steers to the connecting warp, transits, and begins mining
///     (<see cref="Ship.Ore"/> grows via the pre-existing
///     <see cref="Model.Sector.ResolveMining"/>).
///   • Selling — a full NPC in a sector with NO unload-capable friendly station,
///     with one across the warp, autonomously targets it, transits, docks, and
///     sells (ore → 0, side treasury credited via the pre-existing ServerHost
///     dock-sell path).
///
/// Harness mirrors P147/P155/P156/P157 (booted in-process server + one seated
/// bot, no Windows VM); the mission advances with the server's own deterministic
/// <see cref="Srv.TickSim"/> (one 0.05 s chunk each).
/// </summary>
/// <remarks>
/// <para><b>Deterministic placement, no map-luck on the search side.</b> The
/// multi-sector <c>XXV_simple.igc</c> map (same one WarpTransitServerTests /
/// NpcCrossClusterLiveTests use) provides real cross-cluster warp pairs; the
/// test discovers the first pair whose <b>start</b> sector carries no stations
/// (so the same-sector search genuinely finds nothing — proving the cross-cluster
/// fallback is what fired) and whose warp has a resolvable partner (so the
/// transit has somewhere to put the ship). The demo map has <em>zero</em>
/// asteroids (P156), so the single rock the mining leg injects is the ONLY
/// mineable anywhere reachable — the mining leg's cross-sector provenance is
/// guaranteed by construction, not asserted post-hoc. The sell leg's unload
/// station is likewise the only reachable unload-capable station.</para>
/// </remarks>
[Collection("LiveNetwork")]
public sealed class NpcCrossClusterMinerDecisionLiveTests
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
            new Gm { Name = "P158 Cross-Cluster Miner Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 615u, DwCookie = 0x615u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 67800000000000006ul, DrmHash = "p158miner",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Discover the first genuine cross-cluster warp pair whose start
    /// sector carries no stations (so the same-sector searches find nothing and
    /// only the cross-cluster fallback can succeed) and whose warp has a
    /// resolvable partner (so the transit has somewhere to put the ship).</summary>
    private static (Sector A, ResolvedWarp Warp, Sector B) DiscoverPair(Mission m)
    {
        foreach (var sec in m.Sectors)
        {
            if (sec.Stations.Count != 0) continue;                 // start sector must have NO local stations
            var w = sec.Warps.FirstOrDefault(warp =>
                warp.Destination is not null && !ReferenceEquals(warp.Destination, sec) && warp.Partner() is not null);
            if (w is not null) return (sec, w, w.Destination!);
        }
        throw new InvalidOperationException("XXV_simple.igc must contain a station-free cross-cluster warp pair for this test");
    }

    /// <summary>A position <paramref name="distance"/> units from
    /// <paramref name="anchor"/> maximizing clearance to every warp in
    /// <paramref name="sector"/> (sampled over 26 axes) — so spawns/stations
    /// never start inside an unrelated warp (same helper P157 used).</summary>
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
    private static float WarpClearance(ResolvedWarp w) => Sector.WarpVisualScale * w.Warp.Radius + 405f;

    // He3-capable rock (bit CapMineHe3, igc.h:761) — same synthetic builder
    // MiningTests/MiningSellTests/P156 use, so ResolveMining treats it as a real pool.
    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, short radius, short sectorId) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: oreMax, OreMax: oreMax,
        AabmCapabilities: Asteroid.CapMineHe3,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    private static float DistToRock(Ship s, Asteroid rock) =>
        (new Vector3(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]) - s.Position).Length();

    private static float DistToStation(Ship s, DataStationIGC t) =>
        (new Vector3(t.Position[0], t.Position[1], t.Position[2]) - s.Position).Length();

    /// <summary>The B-side partner-warp forward-axis position (the trajectory
    /// HitWarpEvent launches a crossing ship along, ServerHost.cs:639-646), used
    /// to place the mined/sold-at target so the ship burns out of the exit
    /// warp's collision volume straight at it rather than re-transiting.</summary>
    private static (Vector3 Pos, Vector3 Fwd) PartnerExit(ResolvedWarp crossWarp)
    {
        var partner = crossWarp.Partner()!;
        var exit = new Vector3(partner.Warp.Position![0], partner.Warp.Position[1], partner.Warp.Position[2]);
        float[] pf = partner.Warp.Forward is { Length: 3 } v && (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) > 0f
            ? v : [1f, 0f, 0f];
        return (exit, new Vector3(pf[0], pf[1], pf[2]).Normalize());
    }

    /// <summary>⭐ Mining leg — the headline P158 proof: an idle NPC in a sector
    /// with no mineable asteroid, connected by a warp to one that HAS the rock,
    /// autonomously (1) targets it via
    /// <see cref="Ship.FindNearestMinableAsteroidAnyCluster"/> (its own-sector
    /// search returns null first — the cross-cluster search is what fires),
    /// (2) steers to the connecting warp via the newly-fixed asteroid
    /// cross-cluster autopilot leg (<see cref="Model.Sector.FindPathToward"/>),
    /// (3) transits, and (4) begins mining. Nothing here hands the NPC a target —
    /// the decision cycle finds the far rock by itself.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Idle_npc_in_empty_sector_finds_and_mines_cross_sector_asteroid_on_its_own_initiative()
    {
        Require.Artwork();
        using var server = new Srv();
        Mission? mission = null;
        Sector? sectorA = null, sectorB = null;
        ResolvedWarp? crossWarp = null;
        Asteroid? rock = null;
        short npcSide = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            // P864c fills authored empty sectors. This isolated NPC-order
            // fixture supplies its own only reachable rock(s).
            foreach (var arena in m.Sectors)
                foreach (var generated in arena.Mineable.ToArray()) arena.RemoveAsteroid(generated);
            mission = m;
            (sectorA, crossWarp, sectorB) = DiscoverPair(m);
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;   // NPC on the OTHER side (no players)

            // The single He3 rock, in sector B along the exit warp's forward axis
            // (the exact trajectory the crossing ship is launched along), so it
            // burns out of the exit warp straight at the rock.
            var (exit, fwd) = PartnerExit(crossWarp);
            rock = new Asteroid(MkAsteroid(7761, exit + fwd * WarpClearance(crossWarp.Partner()!), oreMax: 500f, radius: 60, sectorB!.Id));
            m.PlaceAsteroid(rock, sectorB);

            // Park the peer bot's ship in sector B clear of every warp so it never
            // transits or interferes with the NPC's approach.
            var peerHull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var peerShip = new Ship(id, peerHull, ClearPoint(sectorB, exit, WarpClearance(crossWarp!)), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peerShip, sectorB);
            return (peerShip, sectorB);
        };

        server.SpawnInitialMinersAtBoot = false;   // P382: pristine arena — no initial miner to perturb precise assertions
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        // Native GotoPlan travels only along routes/targets the side has seen
        // (common.cpp:899-927 + igc.h:4909-4913: GetCluster(pship, model) is null when
        // !pship->CanSee(model)). The decision cycle (P652 domain) stays
        // visibility-blind, but the travel leg needs the route (bShowMap reveal,
        // missionigc.cpp:4056-4071 + mapmakerigc.cpp:1002-1017) and the far rock
        // marked the way a scouted field is — the same seeding P528b's economic-cycle
        // fixture documents. The direct Stage write bypasses the lobby's
        // Started-transition seed (Server.Lobby.cs:2132), so seed explicitly. The
        // rock the fixture places (below, via ShipSpawner) is marked seen after it
        // exists, in Phase 1.
        foreach (var __m in server.Missions) __m.SeedStartingVisibility();
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p158_miner");
        bot.SendLogonRequest(Request("p158_miner"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);
        Assert.NotNull(mission); Assert.NotNull(sectorA); Assert.NotNull(sectorB); Assert.NotNull(rock);

        // Spawn the idle NPC in sector A, a clear run away from the connecting
        // warp so it must STEER to it (not be born inside its collision volume).
        var waPos = new Vector3(crossWarp!.Warp.Position![0], crossWarp.Warp.Position[1], crossWarp.Warp.Position[2]);
        var npcSpawn = ClearPoint(sectorA!, waPos, WarpClearance(crossWarp!));
        var npc = server.SpawnNpcShip(mission!, sectorA!, npcSide, npcSpawn);
        Assert.True(npc.IsNpc, "the test ship must be an NPC");

        // Premise: the ship's OWN sector has no mineable rock, and the found rock
        // is genuinely cross-sector. The map has zero asteroids, so this same-sector
        // null + cross-sector target is exactly the P158 situation.
        Assert.Null(npc.FindNearestMinableAsteroid());
        Assert.NotEqual(sectorA!.Id, rock!.Data.ClusterId);
        Assert.Same(rock, npc.FindNearestMinableAsteroidAnyCluster());

        // ── Phase 1: the decision cycle autonomously targets the far rock ─────
        Pump(server, () => npc.MiningAsteroidTarget is not null, 600);
        Assert.NotNull(npc.MiningAsteroidTarget);
        Assert.Equal(rock.Data.AsteroidId, npc.MiningAsteroidTarget!.Data.AsteroidId);
        Assert.Null(npc.PlanTarget);                    // mutual exclusion: mining XOR selling

        // Mark the placed rock seen the way a scouted field is (P528b seeding
        // pattern): a rock is c_mtSeenBySide (asteroidigc.cpp:36) and native
        // GotoPlan only flies toward targets pship->CanSee (igc.h:4909-4913),
        // which the side gains here exactly as a scout's sweep would grant it.
        foreach (var side in mission!.Sides) rock!.SideVisibility.SetSideVisibility(side.Id, true);

        // ── Phase 2: travels to and transits through the connecting warp ──────
        int ti = 0;
        while (ti < 60000 && npc.Sector?.Id != sectorB!.Id) { server.TickSim(); ti++; }
        Assert.True(npc.Sector?.Id == sectorB!.Id,
            $"NPC never crossed into the rock's sector {sectorB!.Name}/{sectorB.Id} (transit ticks {ti}, still in {npc.Sector?.Name}/{npc.Sector?.Id})");

        // ── Phase 3: begins mining the cross-sector rock ──────────────────────
        Pump(server, () => npc.Ore >= 1f, 30000);
        Assert.True(npc.Ore >= 1f, $"NPC should have begun mining the cross-sector rock, got ore {npc.Ore}");
        Assert.True(DistToRock(npc, rock) < 140f, $"NPC must have flown to the rock: dist {DistToRock(npc, rock):F1}");

        Console.WriteLine($"P158-MINE: NPC {npc.Id} (side {npcSide}) {sectorA!.Name}→{sectorB!.Name} autonomously found " +
                          $"cross-sector rock {rock.Data.AsteroidId} '{rock.Data.Name}', transited at t≈{ti}×0.05s, " +
                          $"mining ore {npc.Ore:F1}; sector now {npc.Sector?.Name}/{npc.Sector?.Id}");
    }

    /// <summary>⭐ Selling leg — the mirror: a full NPC in a sector with no
    /// unload-capable friendly station, connected by a warp to one that HAS the
    /// unload station, autonomously targets it via
    /// <see cref="Ship.FindNearestFriendlyStationAnyCluster"/> (own-sector search
    /// null first), travels across the warp, docks, and sells (ore → 0, side
    /// treasury credited).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Full_npc_in_empty_sector_finds_cross_sector_unload_station_and_sells_on_its_own_initiative()
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
            (sectorA, crossWarp, sectorB) = DiscoverPair(m);
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;

            // The sole unload-capable friendly station, in sector B along the exit
            // warp's forward axis, on the NPC's own side so it can dock & sell.
            var core = m.Core;
            var unloadType = core.StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmUnload) != 0);
            var (exit, fwd) = PartnerExit(crossWarp);
            var stationPos = exit + fwd * WarpClearance(crossWarp.Partner()!);
            station = new DataStationIGC(
                Position: [stationPos.X, stationPos.Y, stationPos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
                Rotation: [0f, 0f, 0f, 1f], ClusterId: sectorB!.Id, SideId: npcSide, StationId: 9951,
                StationTypeId: (short)unloadType.StationTypeId, BpHull: 1, BpShield: 1, Name: "SELL_FAR");
            m.PlaceStation(new Station(station, unloadType), sectorB);

            var peerHull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var peerShip = new Ship(id, peerHull, ClearPoint(sectorB, exit, WarpClearance(crossWarp!)), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peerShip, sectorB);
            return (peerShip, sectorB);
        };

        server.SpawnInitialMinersAtBoot = false;   // P382: pristine arena — no initial miner to perturb precise assertions
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        // Same route-visibility seeding as the mining-leg test (native cites there).
        foreach (var __m in server.Missions) __m.SeedStartingVisibility();
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p158_seller");
        bot.SendLogonRequest(Request("p158_seller"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);
        Assert.NotNull(mission); Assert.NotNull(sectorA); Assert.NotNull(sectorB);

        var side = mission!.Sides.First(s => s.Id == npcSide);
        var waPos = new Vector3(crossWarp!.Warp.Position![0], crossWarp.Warp.Position[1], crossWarp.Warp.Position[2]);
        var npcSpawn = ClearPoint(sectorA!, waPos, WarpClearance(crossWarp!));
        var npc = server.SpawnNpcShip(mission!, sectorA!, npcSide, npcSpawn);
        Assert.True(npc.IsNpc, "the test ship must be an NPC");

        // Start FULL with nothing local to sell at: the sell leg's premise.
        npc.Ore = npc.EffectiveMineCapacity();   // P528: the native/core capacity (was the retired flat 150)
        Assert.Null(npc.FindNearestFriendlyStation(DataStationTypeIGC.SabmUnload));   // no local unload
        Assert.NotEqual(sectorA!.Id, station.ClusterId);                               // the unload really is cross-sector
        var anyUnload = npc.FindNearestFriendlyStationAnyCluster(DataStationTypeIGC.SabmUnload);
        Assert.True(anyUnload.HasValue, "the widening search must find the far unload station");
        Assert.Equal(station.StationId, anyUnload.Value.StationId);

        // ── Phase 1: the decision cycle autonomously targets the far unload ────
        Pump(server, () => npc.PlanTarget is not null && npc.MiningAsteroidTarget is null, 600);
        Assert.NotNull(npc.PlanTarget);
        Assert.Null(npc.MiningAsteroidTarget);          // mutual exclusion: mining XOR selling
        Assert.Equal(station.StationId, npc.PlanTarget.Value.StationId);

        // ── Phase 2: transits to the station's sector ─────────────────────────
        int ti = 0;
        while (ti < 60000 && npc.Sector?.Id != sectorB!.Id) { server.TickSim(); ti++; }
        Assert.True(npc.Sector?.Id == sectorB!.Id,
            $"NPC never crossed into the unload sector {sectorB!.Name}/{sectorB.Id} (transit ticks {ti}, still in {npc.Sector?.Name}/{npc.Sector?.Id})");

        // ── Phase 3: docks and sells — treasury credited by ore × rate ────────
        long prevMoney = side.Money;
        float prevOre = npc.Ore;
        bool sold = false;
        int tc = 0;
        while (tc < 30000 && !sold)
        {
            server.TickSim();
            if (npc.Ore <= 0f && prevOre > 0f && npc.StationId is not null)
            {
                sold = true;
                long expected = (long)(npc.EffectiveOreCreditPerUnit() * prevOre);   // P528: native/core value × GA (was the flat 5)
                long delta = side.Money - prevMoney;
                Assert.True(delta >= expected,
                    $"sell must credit the treasury: expected ≥{expected}, sampled single-tick delta {delta}");
                Assert.Equal(0f, npc.Ore);
                Console.WriteLine($"P158-SELL: NPC {npc.Id} (side {npcSide}) {sectorA!.Name}→{sectorB!.Name} autonomously " +
                                  $"found far unload station {station.StationId}, transited at t≈{ti}×0.05s, sold {prevOre:F1} ore " +
                                  $"= {expected} credits (side-money delta {delta}); docked at {npc.StationId}");
            }
            prevMoney = side.Money;
            prevOre = npc.Ore;
            tc++;
        }
        Assert.True(sold, "NPC should have docked and sold at the cross-sector unload within the tick budget");
    }
}
