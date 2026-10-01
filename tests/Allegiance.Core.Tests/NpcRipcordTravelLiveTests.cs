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
/// P172 — NPC ripcord-as-travel, Stage 1 (safety gate + station-anchor search).
/// Live loopback proof that an <see cref="IsNpc"/> ship with a cross-cluster
/// <see cref="Ship.PlanTarget"/> and a reachable <b>friendly</b>
/// <c>c_sabmRipcord</c>-capable station chooses to <b>ripcord</b> (countdown →
/// teleport) instead of warp-pathing — the alternative cross-cluster travel this
/// port's P157/P158 warp-pathing never had — and that an imminent drift hazard
/// (the <see cref="Ship.IsSafeToRipcord"/> gate) makes it <b>defer and retry</b>
/// rather than ripcord blindly.
///
/// Harness mirrors P147/P155/P157/P158's live tests (booted in-process server +
/// one seated bot, no Windows VM); the mission advances with the server's own
/// deterministic <see cref="Srv.TickSim"/> (one 0.05 s chunk each). The anchor
/// station is injected synthetically (the real XXV_simple garrisons are
/// <b>not</b> <c>c_sabmRipcord</c>-capable — verified in the P172 writeup), the
/// same technique LiveRipcordTests uses.
/// </summary>
[Collection("LiveNetwork")]
public sealed class NpcRipcordTravelLiveTests
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
            new Gm { Name = "P172 NPC Ripcord Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 717u, DwCookie = 0x717u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 82200000000000011ul, DrmHash = "p172rip",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Discover the first genuine cross-cluster warp pair (the same
    /// discovery the P157/P158 harnesses use): a warp whose resolved destination
    /// is a DIFFERENT sector with a resolvable partner, so the transit has
    /// somewhere to put a crossing ship.</summary>
    private static (Sector A, ResolvedWarp Warp, Sector B) DiscoverPair(Mission m)
    {
        foreach (var sec in m.Sectors)
        {
            var w = sec.Warps.FirstOrDefault(warp =>
                warp.Destination is not null && !ReferenceEquals(warp.Destination, sec) && warp.Partner() is not null);
            if (w is not null) return (sec, w, w.Destination!);
        }
        throw new InvalidOperationException("XXV_simple.igc must contain a cross-cluster warp pair for this test");
    }

    /// <summary>A position <paramref name="distance"/> units from
    /// <paramref name="anchor"/> maximizing clearance to every warp in
    /// <paramref name="sector"/> (sampled over 26 axes) — so spawns never start
    /// inside a warp collision volume (same helper P157/P158 use).</summary>
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
    private static float WarpClearance(ResolvedWarp w) => Sector.WarpVisualScale * w.Warp.Radius + 395f;

    /// <summary>A ripcord-capable drone hull (must NOT have c_habmNoRipcord —
    /// the port's default spawn hull does, so the tests swap to a real
    /// ripcord-capable one, the same correction LiveRipcordTests makes).</summary>
    private static DataHullTypeIGC RipcordHull(Mission m) =>
        m.Core.HullTypes.Values
            .Where(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord))
            .OrderBy(h => h.HullId)
            .First();

    /// <summary>A friendly <c>c_sabmRipcord</c>-capable station record at
    /// <paramref name="pos"/> in <paramref name="sector"/> on <paramref name="side"/>
    /// — the ripcord anchor (same real core type LiveRipcordTests injects).</summary>
    private static DataStationIGC RipcordAnchor(Sector sector, short side, short id, Vector3 pos)
    {
        var type = Core().StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRipcord));
        return new DataStationIGC(
            Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: side, StationId: id,
            StationTypeId: (short)type.StationTypeId, BpHull: 1, BpShield: 1, Name: "RIPANCH");
    }

    /// <summary>⭐ The headline P172 proof: an NPC with a cross-cluster
    /// <see cref="Ship.PlanTarget"/> and a reachable friendly
    /// <c>c_sabmRipcord</c> station in the target cluster arms a ripcord
    /// (countdown starts) and — once the countdown completes — its sector
    /// changes via the <b>ripcord teleport</b>, <em>not</em> by physically
    /// flying through the warp. Proof of "ripcord, not warp-path": the ripcord
    /// was genuinely armed (<see cref="Ship.IsRipcordActive"/> true, model
    /// pointing at the target cluster) BEFORE the sector changed, and the ship
    /// never approached the connecting warp (min distance stays far above its
    /// collision radius) — warp-pathing never arms a ripcord.</summary>
    [Fact]
    public void Cross_cluster_npc_rips_to_friendly_station_instead_of_warp_pathing()
    {
        using var server = new Srv();
        Mission? mission = null;
        Sector? sectorA = null, sectorB = null;
        ResolvedWarp? crossWarp = null;
        DataStationIGC anchor = default;
        short npcSide = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            (sectorA, crossWarp, sectorB) = DiscoverPair(m);
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;   // NPC on the OTHER side (no players)

            // The friendly ripcord anchor in the TARGET sector (sector B) on the
            // NPC's own side — reachable in the target cluster itself (hop 0).
            var partner = crossWarp.Partner()!;
            var bExit = new Vector3(partner.Warp.Position![0], partner.Warp.Position[1], partner.Warp.Position[2]);
            float[] pf = partner.Warp.Forward is { Length: 3 } v && (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) > 0f
                ? v : [1f, 0f, 0f];
            var fwd = new Vector3(pf[0], pf[1], pf[2]).Normalize();
            anchor = RipcordAnchor(sectorB, npcSide, id: 8731, bExit + fwd * WarpClearance(partner));
            var anchorType = Core().StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRipcord));
            m.PlaceStation(new Station(anchor, anchorType), sectorB);

            // The peer bot's ship: park it in sector B, clear of every warp.
            var peerHull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var peerShip = new Ship(id, peerHull, ClearPoint(sectorB, bExit, WarpClearance(partner)), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peerShip, sectorB);
            return (peerShip, sectorB);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p172_rip");
        bot.SendLogonRequest(Request("p172_rip"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);
        Assert.NotNull(mission); Assert.NotNull(sectorA); Assert.NotNull(sectorB); Assert.NotNull(crossWarp);

        // Spawn the NPC in sector A, a clear run away from the connecting warp,
        // then give it a REAL ripcord-capable hull (the default spawn hull is
        // c_habmNoRipcord). Velocity 0 → IsSafeToRipcord() is trivially true.
        var waPos = new Vector3(crossWarp!.Warp.Position![0], crossWarp.Warp.Position[1], crossWarp.Warp.Position[2]);
        var npcSpawn = ClearPoint(sectorA!, waPos, WarpClearance(crossWarp!));
        var npc = server.SpawnNpcShip(mission!, sectorA!, npcSide, npcSpawn);
        npc.ChangeHull(RipcordHull(mission!));
        Assert.True(npc.IsNpc, "the test ship must be an NPC");
        Assert.False(npc.HullType.HasCapability(DataHullTypeIGC.HabmNoRipcord), "the test NPC must have a ripcord-capable hull");

        // Explicitly authored cross-sector PlanTarget → the ripcord anchor in B
        // (the decision cycle's own searches are same-sector-only, out of scope).
        Assert.NotEqual(sectorA!.Id, anchor.ClusterId);   // sanity: the target really is cross-sector
        npc.PlanTarget = anchor;
        npc.PlanCommandId = OrderChange.CidGoto;

        float warpSpawnDist = (npcSpawn - waPos).Length();
        Assert.True(warpSpawnDist > 400f, $"spawn must start far from the warp: {warpSpawnDist:F1}");

        // ── Phase 1: the NPC arms a ripcord to the target cluster (not warp-path) ─
        int armed = Pump(server, () => npc.IsRipcordActive, 120);
        Assert.True(armed < 120, "NPC never armed a ripcord to the cross-cluster target");
        Assert.True(npc.IsRipcordActive);
        Assert.NotNull(npc.RipcordModel);
        Assert.Equal(sectorB!.Id, npc.RipcordModel!.Value.ClusterId);   // ripcord targets the plan's sector
        Assert.Equal((short)IgcObjectType.Station, npc.RipcordModel.Value.Ot);
        Assert.Equal(anchor.StationId, npc.RipcordModel.Value.Oid);
        // Still in sector A (the teleport has not happened yet — the countdown is
        // running).
        Assert.Equal(sectorA.Id, npc.Sector!.Id);

        // ── Phase 2: countdown completes → the ship's sector changes via the
        // ripcord teleport, and it NEVER physically approached the warp ────────
        float minWarpDist = float.MaxValue;
        int teleportTicks = 0;
        while (teleportTicks < 1000 && npc.Sector?.Id != sectorB.Id)
        {
            server.TickSim();
            teleportTicks++;
            var wpos = new Vector3(crossWarp.Warp.Position![0], crossWarp.Warp.Position[1], crossWarp.Warp.Position[2]);
            minWarpDist = MathF.Min(minWarpDist, (npc.Position - wpos).Length());
        }
        Assert.True(npc.Sector?.Id == sectorB.Id,
            $"NPC never arrived in the target sector {sectorB!.Name}/{sectorB.Id} via ripcord (teleport ticks {teleportTicks})");
        Assert.False(npc.IsRipcordActive);               // ripcord state cleared by the teleport
        Assert.Null(npc.RipcordModel);
        // The ship never physically flew through the warp (would have come within
        // its ~50 collision radius): it teleported.
        float warpRadius = crossWarp.Warp.Radius;
        Assert.True(minWarpDist > warpRadius + 20f,
            $"ship must have teleported (min distance to warp {minWarpDist:F1} > radius {warpRadius}+20) — it did not physically transit");

        Console.WriteLine($"P172-RIP: NPC {npc.Id} (side {npcSide}) {sectorA!.Name}→{sectorB!.Name} ripcord-armed after {armed} ticks to station {anchor.StationId}; " +
                          $"teleport at t≈{teleportTicks}×0.05s (countdown {npc.HullType.RipcordSpeed}s), min warp distance {minWarpDist:F1}; sector now {npc.Sector?.Name}/{npc.Sector?.Id}");
    }

    /// <summary>⭐ The unsafe-case proof: with something imminent in the drift
    /// path (<see cref="Ship.IsSafeToRipcord"/> false), the NPC does NOT start
    /// ripcording immediately — it defers and retries. Once the hazard clears
    /// and the 5 s retry window lapses, it then ripcords and teleports. The
    /// decoy is a <b>non-He3</b> asteroid placed in the "ripcord-only" band:
    /// close enough that IsSafeToRipcord's long horizon flags it (so the ripcord
    /// genuinely defers), yet far enough that the P168 dodge (short horizon)
    /// does NOT grab the tick — making the deferral observation deterministic.</summary>
    [Fact]
    public void Unsafe_npc_defers_ripcord_and_retries_after_hazard_clears()
    {
        using var server = new Srv();
        Mission? mission = null;
        Sector? sectorA = null, sectorB = null;
        ResolvedWarp? crossWarp = null;
        DataStationIGC anchor = default;
        Asteroid? decoy = null;
        short npcSide = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            (sectorA, crossWarp, sectorB) = DiscoverPair(m);
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;

            var partner = crossWarp.Partner()!;
            var bExit = new Vector3(partner.Warp.Position![0], partner.Warp.Position[1], partner.Warp.Position[2]);
            float[] pf = partner.Warp.Forward is { Length: 3 } v && (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) > 0f
                ? v : [1f, 0f, 0f];
            var fwd = new Vector3(pf[0], pf[1], pf[2]).Normalize();
            anchor = RipcordAnchor(sectorB, npcSide, id: 8761, bExit + fwd * WarpClearance(partner));
            var anchorType = Core().StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRipcord));
            m.PlaceStation(new Station(anchor, anchorType), sectorB);

            var peerHull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var peerShip = new Ship(id, peerHull, ClearPoint(sectorB, bExit, WarpClearance(partner)), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peerShip, sectorB);
            return (peerShip, sectorB);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p172_unsafe");
        bot.SendLogonRequest(Request("p172_unsafe"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);
        Assert.NotNull(mission); Assert.NotNull(sectorA); Assert.NotNull(sectorB);

        var hull = RipcordHull(mission!);
        var waPos = new Vector3(crossWarp!.Warp.Position![0], crossWarp.Warp.Position[1], crossWarp.Warp.Position[2]);
        var npcSpawn = ClearPoint(sectorA!, waPos, WarpClearance(crossWarp!));
        var npc = server.SpawnNpcShip(mission!, sectorA!, npcSide, npcSpawn);
        npc.ChangeHull(hull);
        npc.PlanTarget = anchor;
        npc.PlanCommandId = OrderChange.CidGoto;

        // Self-calibrate the drift-hazard geometry from the chosen hull so the
        // decoy sits in the "ripcord-only" band: flagged by IsSafeToRipcord's long
        // horizon (t <= tMax) but NOT by the P168 dodge's short default horizon
        // (t <= sqrt(4r/accel)) — so the ripcord defers deterministically instead
        // of the tick being grabbed by dodge.
        float v0 = hull.Speed * 0.5f;                    // cruising under max speed
        float myRadius = 0.5f * hull.Length + 1f;
        float rockR = 60f;
        float r = rockR + myRadius;
        float accel = hull.Thrust / hull.Mass;
        float dodgeT = MathF.Sqrt(4f * r / accel);       // dodge fires when t <= dodgeT
        float k = hull.Thrust / (hull.Speed * hull.Mass);
        float s = v0 * (1f - MathF.Exp(-k * hull.RipcordSpeed)) / k;
        float ripT = 1f + s / v0;                        // IsSafeToRipcord fires when t <= ripT
        Assert.True(ripT > dodgeT + 0.5f,
            $"test geometry needs a real gap: ripcord horizon {ripT:F2} must exceed dodge {dodgeT:F2}");
        float tmid = 0.5f * (dodgeT + ripT);             // squarely between the two horizons

        var dir = new Vector3(1f, 0f, 0f);
        var decoyPos = npcSpawn + dir * (v0 * tmid + r); // reaches distance r at time ~tmid
        decoy = new Asteroid(new DataAsteroidIGC(
            Signature: 1f, Position: [decoyPos.X, decoyPos.Y, decoyPos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], Ore: 0f, OreMax: 0f,
            AabmCapabilities: 0,                           // NOT He3-capable → not mineable → miner cycle ignores it
            AsteroidId: 9901, HitPoints: 0f, Radius: (short)rockR,
            ModelName: "roid.mdl", TextureName: "", IconName: "",
            ClusterId: sectorA!.Id, Name: "DECOY", Fraction: 1));   // byte; Alive is governed by Asteroid._fraction (=1.0), not this field
        mission!.PlaceAsteroid(decoy, sectorA);
        Assert.False(decoy.CanMineHe3, "the decoy must not be mineable, else the miner cycle would hijack the plan");
        Assert.True(npc.Sector!.Id == sectorA.Id, "NPC must start in the start sector");

        // Give the NPC a real cross-cluster drift: velocity straight at the decoy.
        npc.Velocity = dir * v0;
        Assert.False(npc.IsSafeToRipcord(),
            $"precondition: with the decoy in the drift path IsSafeToRipcord must be false (v0={v0}, tmid={tmid:F2})");

        // ── Phase 1: an imminent hazard → the NPC does NOT ripcord immediately ──
        // (it defers; even if a dodge occasionally grabs a tick, the ripcord must
        // never arm while the hazard is present).
        for (int i = 0; i < 10; i++) server.TickSim();
        Assert.False(npc.IsRipcordActive, "must NOT start ripcording while something is imminent in the drift path");
        Assert.Null(npc.RipcordModel);
        Assert.Equal(sectorA.Id, npc.Sector!.Id);

        // ── Phase 2: clear the hazard (stop → IsSafeToRipcord true), then the
        // 5 s retry window must lapse before the ripcord arms ──────────────────
        npc.Velocity = Vector3.Zero;                       // hazard cleared
        Assert.True(npc.IsSafeToRipcord(), "stopped ship must be safe to ripcord");
        // The retry latch (set while unsafe) means it should NOT arm instantly on
        // the very next ticks — a real 5 s retry, not an immediate re-fire.
        for (int i = 0; i < 30; i++) server.TickSim();      // ~1.5s — still inside the 5 s window
        Assert.False(npc.IsRipcordActive, "should still be inside the 5 s retry window (did not arm prematurely)");
        // ... then it retries and arms, and completes the travel.
        int armed = Pump(server, () => npc.IsRipcordActive, 220);   // ~11s more — covers the rest of the window
        Assert.True(armed < 220, "NPC never re-attempted the ripcord after the retry window");
        Assert.True(npc.IsRipcordActive, "ripcord should have armed once safe + retry window elapsed");
        Assert.Equal(sectorB!.Id, npc.RipcordModel!.Value.ClusterId);

        int teleportTicks = 0;
        while (teleportTicks < 1000 && npc.Sector?.Id != sectorB.Id) { server.TickSim(); teleportTicks++; }
        Assert.True(npc.Sector?.Id == sectorB.Id,
            $"NPC never reached the target sector {sectorB!.Name}/{sectorB.Id} after the unsafe-retry (teleport ticks {teleportTicks})");
        Assert.False(npc.IsRipcordActive);

        Console.WriteLine($"P172-RIP-UNSAFE: NPC {npc.Id} (side {npcSide}) deferred the ripcord (horizon {ripT:F2} > tmid {tmid:F2} > dodge {dodgeT:F2}), " +
                          $"retried & armed after hazard cleared (re-arm at t≈{armed}×0.05s past clear), teleported to {npc.Sector?.Name}/{npc.Sector?.Id} at t≈{teleportTicks}×0.05s");
    }
}
