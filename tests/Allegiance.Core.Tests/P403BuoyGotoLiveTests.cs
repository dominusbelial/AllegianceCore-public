using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P403 — buoy-targeted "goto" command. Live loopback proof that a real
/// <see cref="FltBot"/> sends a real <c>FM_CS_CHATBUOY(108)</c> carrying a
/// <c>c_buoyCluster</c> buoy addressed to its OWN ship, and the server — as the
/// ship's owner/simulation authority — turns the received order into a real move
/// the ship's update loop acts on: it path-finds the connecting warp toward the
/// target sector, transits through it (the pre-existing warp-collision resolver),
/// arrives in the target sector, and clears the goto on arrival (native deletes a
/// <c>c_buoyCluster</c> "as soon as we are in the cluster", FedSrv.CPP:3302;
/// ResetWaypoint gives an OT_buoy target the plain <c>c_oGoto</c> objective,
/// shipIGC.cpp:4053,4160). Also proves the mutual-exclusion discipline (issuing a
/// goto clears the station <see cref="Ship.PlanTarget"/> — the mining-XOR-selling
/// analogue) and the two refusals (an order addressed to a DIFFERENT ship, and a
/// non-cluster <c>c_buoyWaypoint</c> buoy, must NOT re-route the sender's ship).
///
/// Harness mirrors NpcCrossClusterLiveTests: booted in-process server, mission
/// forced to Started, one seated bot; the map's first real cross-cluster warp pair
/// is discovered at runtime so no map geometry is hard-coded.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P403BuoyGotoLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P403 Goto Demo", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 903u, DwCookie = 0x403u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 74700000000000403ul, DrmHash = "p403goto",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A position <paramref name="distance"/> units from
    /// <paramref name="anchor"/> maximising clearance to every warp in
    /// <paramref name="sector"/> — so a spawned ship must STEER to a warp, not be
    /// born inside its transit volume (P157/P238 clear-placement idiom).</summary>
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

    private static DataStationIGC FakeStation(Sector sector, short side, short id, Vector3 pos) => new(
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: side, StationId: id,
        StationTypeId: 901, BpHull: 1, BpShield: 1, Name: $"FAKE_{id}");

    /// <summary>
    /// ⭐ Headline P403 proof: a real bot sends a <c>c_buoyCluster</c> CHATBUOY
    /// addressed to its own ship, the server sets the ship's
    /// <see cref="Ship.GotoWaypoint"/>, and the ship flies THROUGH the real warp to
    /// the target sector, clearing the goto on arrival — a genuine "goto" journey,
    /// not just a wire round-trip.
    /// </summary>
    [Fact]
    public void Cluster_chatbuoy_to_own_ship_files_through_warp_and_clears_on_arrival()
    {
        using var server = new Srv();
        Mission? mission = null;
        Sector? sectorA = null, sectorB = null;
        ResolvedWarp? crossWarp = null;
        short peerSide = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            foreach (var sec in m.Sectors)
            {
                var w = sec.Warps.FirstOrDefault(warp =>
                    warp.Destination is not null && !ReferenceEquals(warp.Destination, sec) && warp.Partner() is not null);
                if (w is not null) { sectorA = sec; crossWarp = w; sectorB = w.Destination; break; }
            }
            if (sectorA is null || sectorB is null || crossWarp is null)
                throw new InvalidOperationException("XXV_simple.igc must contain a cross-cluster warp pair for this test");
            peerSide = m.Sides[0].Id;

            // The bot's own (player) ship: park it in sector A, clear of every
            // warp, so it must STEER to the connecting warp and transit to B.
            var waPos = new Vector3(crossWarp.Warp.Position![0], crossWarp.Warp.Position[1], crossWarp.Warp.Position[2]);
            var spawn = ClearPoint(sectorA, waPos, WarpClearance(crossWarp));
            var hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(id, hull, spawn, Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(ship, sectorA);
            return (ship, sectorA);
        };

        server.SpawnInitialMinersAtBoot = false;   // pristine arena
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;   // bypass ready-up lobby
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p403_goto");
        bot.SendLogonRequest(Request("p403_goto"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);
        Assert.NotNull(mission); Assert.NotNull(sectorA); Assert.NotNull(sectorB);

        short ownId = bot.ShipAssignment!.Ship.ShipId;
        var botShip = server.Ships.Single(kv => kv.Ship.Id == ownId).Ship;
        Assert.True(sectorA!.Id != sectorB!.Id, "sanity: the target sector must differ from the start sector");

        // ── Issue the goto: a c_buoyCluster CHATBUOY addressed to the sender's
        //    own ship, targeting sector B (the port's map-goto order) ──
        var db = new DataBuoyIGC([0f, 0f, 0f], ClusterId: sectorB.Id, Type: DataBuoyIGC.CBuoyCluster, BuoyId: 9001, Visible: false);
        bot.SendChatBuoy("goto", ChatTarget.Individual, ownId, db);

        // The server applies the order as a real move order on the sender's ship.
        int gotoTicks = Pump(server, () => botShip.GotoWaypoint is { } gw && gw.ClusterId == sectorB.Id, 2000);
        Assert.True(gotoTicks < 2000, "the server never set the sender ship's GotoWaypoint for the self-addressed cluster buoy");
        Assert.Equal(GotoKind.Cluster, botShip.GotoWaypoint!.Value.Kind);

        // ── The ship actually flies: cross the warp into the target sector ──
        int transitTicks = Pump(server, () => botShip.Sector?.Id == sectorB.Id, 60000);
        Assert.True(botShip.Sector?.Id == sectorB.Id,
            $"goto ship never reached sector {sectorB.Name}/{sectorB.Id} (transit ticks {transitTicks}, still in {botShip.Sector?.Name}/{botShip.Sector?.Id})");

        // ── On arrival the cluster goto clears (native: buoy deleted "as soon as
        //    we are in the cluster") ──
        int clearTicks = Pump(server, () => botShip.GotoWaypoint is null, 500);
        Assert.True(clearTicks < 500, "goto was not cleared after arriving in the target sector");

        Console.WriteLine($"P403-GOTO: bot {ownId} {sectorA.Name}→{sectorB.Name}: GotoWaypoint set, filed through warp " +
                          $"(transit@{transitTicks}t), cleared on arrival ({clearTicks}t) — RESULT PASS");
    }

    /// <summary>
    /// Mutual exclusion (the mining-XOR-selling discipline, applied to the new
    /// objective): a ship already holding a station <see cref="Ship.PlanTarget"/>
    /// that is then given a cluster goto ends up with ONLY the goto — the station
    /// plan is cleared, so the ship never navigates to both at once.
    /// </summary>
    [Fact]
    public void Issuing_a_cluster_goto_clears_an_existing_station_plantarget()
    {
        using var server = new Srv();
        Mission? mission = null;
        Sector? sectorA = null, sectorB = null;
        short peerSide = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            foreach (var sec in m.Sectors)
            {
                var w = sec.Warps.FirstOrDefault(warp =>
                    warp.Destination is not null && !ReferenceEquals(warp.Destination, sec) && warp.Partner() is not null);
                if (w is not null) { sectorA = sec; sectorB = w.Destination; break; }
            }
            peerSide = m.Sides[0].Id;
            var hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(ship, sectorA!);
            return (ship, sectorA!);
        };

        server.SpawnInitialMinersAtBoot = false;
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p403_xor");
        bot.SendLogonRequest(Request("p403_xor"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);
        Assert.NotNull(mission); Assert.NotNull(sectorA); Assert.NotNull(sectorB);

        short ownId = bot.ShipAssignment!.Ship.ShipId;
        var botShip = server.Ships.Single(kv => kv.Ship.Id == ownId).Ship;

        // Seed a station plan (simulating the NPC selling/station-target flow).
        var station = FakeStation(sectorA!, peerSide, id: 911, botShip.Position);
        botShip.PlanTarget = station;
        botShip.PlanCommandId = OrderChange.CidGoto;
        Assert.NotNull(botShip.PlanTarget);

        var db = new DataBuoyIGC([0f, 0f, 0f], ClusterId: sectorB!.Id, Type: DataBuoyIGC.CBuoyCluster, BuoyId: 9101, Visible: false);
        bot.SendChatBuoy("goto", ChatTarget.Individual, ownId, db);
        Pump(server, () => botShip.GotoWaypoint is not null, 2000);

        Assert.NotNull(botShip.GotoWaypoint);
        Assert.Null(botShip.PlanTarget);   // the station plan was cleared — XOR
        Console.WriteLine("P403-XOR: issuing a cluster goto cleared the station PlanTarget — RESULT PASS");
    }

    /// <summary>
    /// A <c>c_buoyCluster</c> CHATBUOY addressed to a DIFFERENT ship (a teammate)
    /// does NOT re-route the sender's own ship: v1's "commanded ship" is the
    /// player's own ship (no wingman-selection concept), so only a self-addressed
    /// order sets the goto. This pins the addressing rule and keeps the existing
    /// ChatBuoy relay test's ships from being silently re-routed.
    /// </summary>
    [Fact]
    public void Cluster_chatbuoy_addressed_to_a_teammate_does_not_reroute_the_sender()
    {
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        var bots = new FltBot[3];
        for (int i = 0; i < 3; i++) { bots[i] = new FltBot(); bots[i].Connect(server.BoundPort, $"BOT{i}"); }
        for (int i = 0; i < 3; i++) bots[i].SendLogonRequest(Request($"P{i}"));
        Pump(server, () => bots.All(b => b.ShipAssignment is not null), 4000);

        // Round-robin seats 3 bots onto 2 sides → one same-side pair + a solo.
        var sides = server.Ships.ToDictionary(kv => kv.Ship.Id, kv => kv.Ship.SideId);
        var groups = bots.GroupBy(b => sides[b.ShipAssignment!.Ship.ShipId]).ToList();
        Assert.Equal(2, groups.Count);
        var majority = groups.First(g => g.Count() == 2).ToArray();   // the two teammates
        var sender = majority[0];
        var teammate = majority[1];
        short senderId = sender.ShipAssignment!.Ship.ShipId;
        short teammateId = teammate.ShipAssignment!.Ship.ShipId;
        var senderShip = server.Ships.Single(kv => kv.Ship.Id == senderId).Ship;
        var teammateShip = server.Ships.Single(kv => kv.Ship.Id == teammateId).Ship;

        var db = new DataBuoyIGC([0f, 0f, 0f], ClusterId: 9021, Type: DataBuoyIGC.CBuoyCluster, BuoyId: 9321, Visible: false);
        sender.SendChatBuoy("goto", ChatTarget.Individual, teammateId, db);
        Pump(server, () => teammate.ChatBuoys.Any(c => c.Message == "goto"), 2000);

        // Relay semantics unchanged: the addressed same-side teammate receives it…
        Assert.Contains(teammate.ChatBuoys, c => c.Message == "goto");
        // …but because v1's commanded ship is the player's OWN ship (no wingman-
        // selection concept), addressing a DIFFERENT ship re-routes nobody.
        Assert.Null(senderShip.GotoWaypoint);
        Assert.Null(teammateShip.GotoWaypoint);
        Console.WriteLine("P403-ADDR: cluster buoy to a same-side teammate relayed as chat but re-routes NO ship — RESULT PASS");
    }

    /// <summary>
    /// A <c>c_buoyWaypoint</c> (in-sector point) buoy addressed to the sender's own
    /// ship does NOT set a cross-sector goto: the v1 trigger produces only
    /// <c>c_buoyCluster</c> (cross-sector) orders; the waypoint kind is the
    /// documented, scoped-out follow-up, so it must be inert here.
    /// </summary>
    [Fact]
    public void Waypoint_type_buoy_to_self_does_not_set_a_goto()
    {
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p403_wp");
        bot.SendLogonRequest(Request("p403_wp"));
        Pump(server, () => bot.ShipAssignment is not null, 4000);
        short ownId = bot.ShipAssignment!.Ship.ShipId;
        var botShip = server.Ships.Single(kv => kv.Ship.Id == ownId).Ship;

        var db = new DataBuoyIGC([50f, 60f, 70f], ClusterId: (short)(botShip.Sector?.Id ?? 778), Type: DataBuoyIGC.CBuoyWaypoint, BuoyId: 9401, Visible: true);
        bot.SendChatBuoy("goto", ChatTarget.Individual, ownId, db);
        Pump(server, () => false, 80);

        Assert.Null(botShip.GotoWaypoint);
        Console.WriteLine("P403-WP: waypoint-type buoy to self sets no goto (documented deferred scope) — RESULT PASS");
    }
}
