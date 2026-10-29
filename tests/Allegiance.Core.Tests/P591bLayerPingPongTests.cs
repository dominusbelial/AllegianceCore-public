using System;
using System.IO;
using System.Linq;
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
/// P591b — the mine-layer aleph ping-pong and its native-faithful re-entry
/// guard. Live-loopback tests on the REAL in-process server (so the true
/// <c>ServerHost.HitWarpEvent</c> transit, which stamps the arrival warp, runs)
/// and the real <c>m2xspiderw.igc</c> 221 map.
///
/// Failure measured live on 221 (2026-10-09 14:06, commit 937388a8): the
/// &#39;.Caltrop&#39; mine-layer drones (ships 8102/8103) ping-ponged through alephs
/// and never laid — side-0 8102 crossed Rigel(1059)&#8596;Alderan(1052) 34&#215;, side-1
/// 8103 crossed Organia(1051)&#8596;Gaia(1062) 378&#215;. Root cause: with NO valid
/// <see cref="Ship.LayerBuildOrder"/> the P591 suppression (<c>Sector.cs:3301</c>)
/// never applies, and the flat 2&nbsp;s <see cref="Ship.LastWarpTime"/> cooldown
/// alone lets a ship that has turned back inside the arrival aleph&#39;s transit
/// volume re-cross the very aleph it just came through — so a drone whose goal
/// lies back through the arrival aleph bounces forever.
///
/// The fix (native-faithful): the host transit records the warp the ship arrived
/// THROUGH (<c>ServerHost.HitWarpEvent</c>), and <c>Sector.ResolveWarpCollisions</c>
/// refuses to re-transit that warp while the ship is still inside its transit
/// volume, releasing the latch the moment it clears — completing native&#39;s
/// "don&#39;t re-enter the arrival aleph" behaviour (emergent outward velocity +
/// <c>m_warpState</c>, <c>fsship.cpp:71-137, 1185-1191</c>) that the flat
/// cooldown cannot guarantee. Applies to every ship (layers, pods, bots).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P591bLayerPingPongTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        return StaticCore.Load(file);
    }

    private static Cfg Config(string map, int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P591b Test", Map = map, Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], ShowMap = true },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 615u, DwCookie = 0x615u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 67800000000000006ul, DrmHash = "p591b",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static (Sector A, ResolvedWarp Warp, Sector B) DiscoverPair(Mission m)
    {
        foreach (var sec in m.Sectors)
        {
            var w = sec.Warps.FirstOrDefault(warp =>
                warp.Destination is not null && !ReferenceEquals(warp.Destination, sec) && warp.Partner() is not null);
            if (w is not null) return (sec, w, w.Destination!);
        }
        throw new InvalidOperationException("map must contain a cross-cluster warp pair for this test");
    }

    private static Vector3 ClearPoint(Sector sector, Vector3 anchor, float distance)
    {
        var warpPositions = sector.Warps
            .Where(w => w.Warp.Position is { Length: 3 })
            .Select(w => new Vector3(w.Warp.Position[0], w.Warp.Position[1], w.Warp.Position[2]))
            .ToList();
        Vector3 best = anchor; float bestMin = -1f;
        for (int xi = -1; xi <= 1; xi++) for (int yi = -1; yi <= 1; yi++) for (int zi = -1; zi <= 1; zi++)
        {
            if (xi == 0 && yi == 0 && zi == 0) continue;
            var cand = anchor + new Vector3(xi, yi, zi) * distance;
            float mn = float.MaxValue;
            foreach (var p in warpPositions) mn = MathF.Min(mn, (cand - p).Length());
            if (mn > bestMin) { bestMin = mn; best = cand; }
        }
        return best;
    }

    private static float WarpClearance(ResolvedWarp w) => Sector.WarpVisualScale * w.Warp.Radius + 405f;

    /// <summary>
    /// ⭐ P591c correction — the re-entry guard must NOT trap a ship that
    /// INTENTIONALLY re-crosses the aleph it just came through. A ship that
    /// arrives via a warp and whose route then genuinely leads BACK through that
    /// same aleph (e.g. a combat bot pursuing an enemy back across the border it
    /// entered by, or a return-home through the same door) must be permitted to
    /// re-cross once native's ~2 s re-arm lapses — native has no arrival-warp
    /// memory and allows it. Blocking such a ship would freeze it forever (its
    /// goal lies THROUGH the warp, so it can never clear the 2× release volume).
    /// P591c makes the guard precise via <see cref="Ship.IntendedNextWarpId"/>:
    /// it holds only an UNINTENTIONAL drift back through the arrival aleph
    /// (goal is a different warp / none), and permits an intentional one. This
    /// test pins a ship at the arrival aleph with its goal pointing back and
    /// asserts it re-crosses (is NOT trapped).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ship_that_arrives_and_intentionally_recrosses_its_arrival_warp_is_permitted_not_trapped()
    {
        Require.Artwork();
        using var server = new Srv();
        Mission? mission = null; Sector? sectorA = null, sectorB = null; ResolvedWarp? crossWarp = null;
        short npcSide = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            (sectorA, crossWarp, sectorB) = DiscoverPair(m);
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;
            var partner = crossWarp!.Partner()!;
            var exit = new Vector3(partner.Warp.Position![0], partner.Warp.Position[1], partner.Warp.Position[2]);
            var peerHull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var peerShip = new Ship(id, peerHull, ClearPoint(sectorB, exit, WarpClearance(crossWarp)), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peerShip, sectorB);
            return (peerShip, sectorB);
        };

        server.SpawnInitialMinersAtBoot = false;
        server.Boot(Config("XXV_simple.igc"));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        // Native GotoPlan travels only along routes the side has seen (FindPath gates
        // each aleph edge on pship->CanSee, common.cpp:899-927; warps are
        // c_mtSeenBySide, warpIGC.cpp:35). Setting Stage directly bypasses the lobby's
        // Started-transition seed (Server.Lobby.cs:2132), so seed explicitly; bShowMap
        // (igc.h:1123) is native's own reveal-everything mission param
        // (missionigc.cpp:4056-4071, mapmakerigc.cpp:1002-1017 via Mission.SeedStartingVisibility).
        foreach (var __m in server.Missions) __m.SeedStartingVisibility();
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p591b_guard");
        bot.SendLogonRequest(Request("p591b_guard"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);
        Assert.NotNull(mission); Assert.NotNull(sectorA); Assert.NotNull(sectorB);

        // Spawn an NPC in sector A and drive it across the warp into sector B via
        // a waypoint beyond the exit warp's forward axis (real transit runs).
        var waPos = new Vector3(crossWarp!.Warp.Position![0], crossWarp.Warp.Position[1], crossWarp.Warp.Position[2]);
        var npcSpawn = ClearPoint(sectorA!, waPos, WarpClearance(crossWarp!));
        var npc = server.SpawnNpcShip(mission!, sectorA!, npcSide, npcSpawn);
        var partner = crossWarp!.Partner()!;
        var exit = new Vector3(partner.Warp.Position![0], partner.Warp.Position[1], partner.Warp.Position[2]);
        var fwd = partner.Warp.Forward is { Length: 3 } pf && (pf[0] * pf[0] + pf[1] * pf[1] + pf[2] * pf[2]) > 0f
            ? new Vector3(pf[0], pf[1], pf[2]) : new Vector3(1f, 0f, 0f);
        fwd = fwd / fwd.Length();
        npc.SetGoto(new GotoOrder(GotoKind.Waypoint, sectorB!.Id, exit + fwd * WarpClearance(partner)));

        int ti = 0;
        while (ti < 60000 && npc.Sector?.Id != sectorB!.Id) { server.TickSim(); ti++; }
        Assert.True(npc.Sector?.Id == sectorB!.Id, $"NPC never crossed {sectorA!.Name}->{sectorB!.Name} (ticks {ti}, sector {npc.Sector?.Id})");

        // It is now inside the destination (partner) aleph's transit volume. Pin it
        // there (has not cleared the arrival aleph), expire the flat 2 s cooldown,
        // and point its goal back through the very warp it just came through — an
        // INTENTIONAL re-crossing. The guard must permit this (anti-trap): a ship
        // whose route genuinely leads back through its arrival aleph re-crosses
        // once native's ~2 s re-arm lapses, matching native (no arrival-warp memory).
        npc.Position = exit;                        // the arrival aleph centre — not cleared
        npc.Velocity = Vector3.Zero;
        npc.LastWarpTime = npc.Mission!.LastUpdate - 3.0;   // cooldown expired
        npc.SetGoto(new GotoOrder(GotoKind.Waypoint, sectorA!.Id, waPos));

        int tb = 0;
        while (tb < 4000 && npc.Sector?.Id == sectorB!.Id) { server.TickSim(); tb++; }
        // NOT trapped: it re-crosses back to sector A (permitted, native-faithful).
        Assert.Equal(sectorA!.Id, npc.Sector?.Id);
        Console.WriteLine($"P591c-GUARD: NPC {npc.Id} intentionally re-crossed its arrival aleph back to " +
                          $"{sectorA!.Name} ({sectorA.Id}) after {tb} ticks — not trapped by the non-native guard");
    }

    /// <summary>The real 221 map&#39;s Alderan (1052) lay point for the given warp,
    /// faithful to Server.ArmLayerBuild (P591): the warp position shifted onto
    /// the warp's backward side by (transitRadius + MineRadius).</summary>
    private static Ship.LayerBuildGoal AlderanLayPoint(Sector alderan, DataMineTypeIGC mine, DataHullTypeIGC hull, int warpId)
    {
        var w = alderan.Warps.First(x => x.Warp.WarpId == warpId);
        var p = w.Warp.Position;
        var pos = new Vector3(p[0], p[1], p[2]);
        Vector3 forward = w.Warp.Forward is { Length: 3 } f ? new Vector3(f[0], f[1], f[2]) : new Vector3(0f, 0f, 1f);
        if (forward.LengthSquared() < 1e-6f) forward = new Vector3(0f, 0f, 1f);
        float standoff = (w.Warp.Radius + 0.5f * hull.Length) + mine.MineRadius;
        pos -= forward * standoff;
        return new Ship.LayerBuildGoal(w.Warp.ClusterId, pos, standoff + 80f);
    }

    private static void DriveSide(string label, int startSectorId, int warpId)
    {
        using var server = new Srv();
        Mission? mission = null; Sector? alderan = null; Ship? layer = null;
        DataMineTypeIGC mine = default; DataHullTypeIGC hull = default;
        short npcSide = 0;
        int crossings = 0; Sector? last = null;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            mine = m.Core.MineTypes.Values.Where(mm => mm.MineRadius > 0f && mm.Power > 0f && mm.Lifespan >= 60f)
                .OrderBy(mm => mm.MineRadius).First();
            hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
            alderan = m.Sectors.First(s => s.Id == 1052);
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;
            var peerHull = hull;
            var exit = new Vector3(alderan.Warps.First(x => x.Warp.WarpId == warpId).Warp.Position![0],
                                   alderan.Warps.First(x => x.Warp.WarpId == warpId).Warp.Position![1],
                                   alderan.Warps.First(x => x.Warp.WarpId == warpId).Warp.Position![2]);
            var peerShip = new Ship(id, peerHull, ClearPoint(alderan, exit, WarpClearance(alderan.Warps.First(x => x.Warp.WarpId == warpId))),
                Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peerShip, alderan);
            return (peerShip, alderan);
        };

        server.SpawnInitialMinersAtBoot = false;
        server.Boot(Config("m2xspiderw.igc"));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        // See the DriveSide-adjacent note in this class: native GotoPlan needs the
        // route seen (common.cpp:899-927); seed bShowMap explicitly because the
        // direct Stage write bypasses the lobby's Started-transition seed.
        foreach (var __m in server.Missions) __m.SeedStartingVisibility();
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p591b_route");
        bot.SendLogonRequest(Request("p591b_route"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 6000);
        Assert.NotNull(mission); Assert.NotNull(alderan);

        // Spawn the mine-layer in the given start sector and order it (cross-sector)
        // to lay at Alderan — the exact showcase flow, but via the server seam.
        var startSec = mission!.Sectors.First(s => s.Id == startSectorId);
        var home = startSec.Warps.FirstOrDefault(w => w.Destination is not null)
            ?? startSec.Warps[0];
        var homeAnchor = home.Warp.Position is { Length: 3 } hp ? new Vector3(hp[0], hp[1], hp[2]) : Vector3.Zero;
        var spawn = ClearPoint(startSec, homeAnchor, WarpClearance(home));
        var layerDroneTypes = mission.Core.DroneTypes.Values.Where(d => d.PilotType == Ship.PilotTypeLayer).ToList();
        if (layerDroneTypes.Count == 0)
            throw new InvalidOperationException("core must have a layer drone type");
        layer = server.SpawnNpcShip(mission, startSec, npcSide, spawn, layerDroneTypes[0].DroneTypeId);
        layer.SideId = npcSide;
        layer.LayerLaidExpendableTypeId = mine.ExpendableTypeId;
        layer.SetLayerBuild(AlderanLayPoint(alderan, mine, layer.HullType, warpId));
        Console.WriteLine($"[p591b] {label}: spawned in {startSec.Id} '{startSec.Name}', ordered to lay at Alderan via warp {warpId}");

        last = layer.Sector;
        int steps = 0;
        while (steps < 60000 && !layer.Dead && layer.Sector is not null)
        {
            server.TickSim(); steps++;
            if (layer.Sector is { } sec && !ReferenceEquals(sec, last)) { crossings++; last = sec; }
        }
        Console.WriteLine($"[p591b] {label}: FINAL laid={layer.Dead} sector={layer.Sector?.Id} crossings={crossings} steps={steps}");
        Assert.True(layer.Dead, $"{label}: layer must lay and be consumed (crossings {crossings}, sector {layer.Sector?.Id})");
        Assert.True(crossings <= 2, $"{label}: crossings {crossings} must be <= the 2-hop route length (no ping-pong)");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Side0_layer_from_Rigel_reaches_Alderan_and_lays_without_ping_pong()
    {
        Require.Artwork();
        DriveSide("S0-Rigel", 1059, 2187);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Side1_layer_from_Organia_reaches_Alderan_and_lays_without_ping_pong()
    {
        Require.Artwork();
        DriveSide("S1-Organia", 1051, 2152);
    }
}
