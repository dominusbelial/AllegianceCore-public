using System;
using System.IO;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P591d — the minefield EXPORT audience. On today's (pre-P591d) code an NPC
/// layer drone (the showcase's drone-bucket spawn — NOT a connected peer) that
/// lays a minefield sent its <c>FM_S_EXPORT(26)/OT_mine</c> to <b>0 same-side
/// peer(s)</b>, because <c>Server.BroadcastExportMine</c> gated the recipients on
/// the <c>_teammateExportSent[(peer, layer)]</c> latch, which is only ever set
/// for a CONNECTED PEER ship (its ExportShip sent to teammates). An NPC layer has
/// no peer, so the field was invisible even to its own side — the live P591d
/// symptom. This fails-first test boots a REAL in-process server, seats a
/// same-side peer (a genuine <see cref="FltBot"/> with a seated ship), drives an
/// NPC layer drone to actually LAY a minefield beside that peer, and asserts the
/// peer's <c>Bot.MineExports</c> received the OT_mine EXPORT. On today's code the
/// peer receives nothing (the audience collapses to 0); after the P591d fix
/// (export to the owning side's seated peers, native: a mine is a side-visible
/// static object, mineigc.cpp:103-107) the peer receives it.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P591dMinefieldExportAudienceTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(string map, int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P591d Test", Map = map, Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 615u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 67800000000000006ul, DrmHash = "p591d",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
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

    /// <summary>
    /// ⭐ Headline: an NPC layer drone lays a minefield beside a same-side seated
    /// peer, and the peer's client receives the OT_mine EXPORT (the field is the
    /// side's own hazard). On today's code the NPC launcher's absence from the
    /// peer-export latch zeroes the audience.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Npc_layer_drone_minefield_reaches_same_side_seated_peer()
    {
        Require.Artwork();
        using var server = new Srv();
        Mission? mission = null; Sector? sector = null; Ship? layer = null;
        DataMineTypeIGC mine = default; DataHullTypeIGC hull = default;
        short peerSide = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            mine = m.Core.MineTypes.Values.Where(mm => mm.MineRadius > 0f && mm.Power > 0f && mm.Lifespan >= 60f)
                .OrderBy(mm => mm.MineRadius).First();
            hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
            sector = m.Sectors[0];
            peerSide = m.Sides[0].Id;
            var anchor = m.Sectors[0].Warps.FirstOrDefault() is { } w0 && w0.Warp.Position is { Length: 3 } p0
                ? new Vector3(p0[0], p0[1], p0[2])
                : new Vector3(0f, 0f, 0f);
            var peerShip = new Ship(id, hull, ClearPoint(sector, anchor, 800f), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peerShip, sector);
            return (peerShip, sector);
        };

        server.SpawnInitialMinersAtBoot = false;
        server.Boot(Config("XXV_simple.igc"));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        // Seat a real same-side peer (it gets a ship via the ShipSpawner above).
        using var peer = new FltBot();
        peer.Connect(server.BoundPort, "p591d_peer");
        peer.SendLogonRequest(Request("p591d_peer"));
        Pump(server, () => mission is not null && peer.ShipAssignment is not null, 6000);
        Assert.NotNull(mission); Assert.NotNull(sector);

        // Spawn an NPC layer drone (a drone-bucket spawn — NOT a connected peer)
        // on the PEER's side and order it to lay a minefield in the peer's sector.
        var layerDroneTypes = mission!.Core.DroneTypes.Values.Where(d => d.PilotType == Ship.PilotTypeLayer).ToList();
        if (layerDroneTypes.Count == 0)
            throw new InvalidOperationException("core must have a layer drone type");
        var anchor2 = sector!.Warps.FirstOrDefault() is { } w2 && w2.Warp.Position is { Length: 3 } p2
            ? new Vector3(p2[0], p2[1], p2[2])
            : new Vector3(0f, 0f, 0f);
        var spawn = ClearPoint(sector, anchor2, 1000f);
        layer = server.SpawnNpcShip(mission, sector, peerSide, spawn, layerDroneTypes[0].DroneTypeId);
        layer.SideId = peerSide;
        layer.LayerLaidExpendableTypeId = mine.ExpendableTypeId;
        // Lay point: well clear of the warp transit volume, in the same sector.
        var layPos = ClearPoint(sector, new Vector3(spawn.X + 400f, spawn.Y, spawn.Z), 500f);
        layer.SetLayerBuild(new Ship.LayerBuildGoal(sector.Id, layPos, 120f));

        peer.ResetMineExports();
        // Drive until the layer lays (is consumed -> Dead) — native LayExpendable
        // kills the drone after deploying.
        int steps = 0;
        while (steps < 60000 && !layer.Dead && layer.Sector is not null)
        {
            server.TickSim(); steps++;
            // Give the peer's transport pump a moment to ingest the export.
            if (steps % 20 == 0) Thread.Sleep(1);
        }
        Assert.True(layer.Dead, $"layer must lay and be consumed (sector {layer.Sector?.Id}, steps {steps})");

        // The layer-death loop above stops as soon as layer.Dead goes true, but
        // the peer's mine EXPORT packet may still be in flight; condition-wait
        // until the peer has actually ingested a mine export carrying OUR mine.
        Pump(server, () => peer.MineExports.Any(e => e.Mine.MinetypeId == mine.ExpendableTypeId), 20000);

        var mineExports = peer.MineExports;
        Assert.NotEmpty(mineExports);   // FAILS-FIRST on pre-P591d code (audience 0)
        Assert.Contains(mineExports, e => e.Mine.MinetypeId == mine.ExpendableTypeId);
        var got = mineExports[0];
        Console.WriteLine($"P591d-EXPORT: NPC layer {layer.Id} (side {layer.SideId}) laid mine type {got.Mine.MinetypeId} " +
                          $"at {got.Mine.P0}; same-side peer '{peer.ShipAssignment}' received {mineExports.Count} OT_mine EXPORT(s)");
    }

    /// <summary>
    /// Negative control: the SAME proven harness (peer placed via ShipSpawner in
    /// the layer's sector, mission Started) but with the LAYER on the opposite
    /// side from the seated peer — so the peer is the FOREIGN side relative to
    /// the field. The layer still lays, and the foreign-side peer receives NO
    /// deploy-time <c>OT_mine</c> export (cross-side reach is the P453 sweep's
    /// job once the enemy's scanner spots it — not the deploy broadcast).
    /// Guards that the fix widens the deploy audience to the OWNING side without
    /// leaking the field to a foreign side.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Opposite_side_peer_does_not_receive_the_deploy_mine_export()
    {
        Require.Artwork();
        using var server = new Srv();
        Mission? mission = null; Sector? sector = null;
        DataMineTypeIGC mine = default; DataHullTypeIGC hull = default;
        short peerSide = 0, layerSide = 1;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            mine = m.Core.MineTypes.Values.Where(mm => mm.MineRadius > 0f && mm.Power > 0f && mm.Lifespan >= 60f)
                .OrderBy(mm => mm.MineRadius).First();
            hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
            sector = m.Sectors[0];
            peerSide = m.Sides[0].Id;
            layerSide = m.Sides[1].Id;
            var anchor = m.Sectors[0].Warps.FirstOrDefault() is { } w0 && w0.Warp.Position is { Length: 3 } p0
                ? new Vector3(p0[0], p0[1], p0[2]) : new Vector3(0f, 0f, 0f);
            var peerShip = new Ship(id, hull, ClearPoint(sector, anchor, 800f), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peerShip, sector);
            return (peerShip, sector);
        };
        server.SpawnInitialMinersAtBoot = false;
        server.Boot(Config("XXV_simple.igc"));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var peer = new FltBot();
        peer.Connect(server.BoundPort, "p591d_xpeer");
        peer.SendLogonRequest(Request("p591d_xpeer"));
        Pump(server, () => mission is not null && peer.ShipAssignment is not null, 6000);
        Assert.NotNull(mission); Assert.NotNull(sector);
        peer.ResetMineExports();

        var layerDroneTypes = mission!.Core.DroneTypes.Values.Where(d => d.PilotType == Ship.PilotTypeLayer).ToList();
        var anchor2 = sector!.Warps.FirstOrDefault() is { } w2 && w2.Warp.Position is { Length: 3 } p2
            ? new Vector3(p2[0], p2[1], p2[2]) : new Vector3(0f, 0f, 0f);
        var spawn = ClearPoint(sector, anchor2, 1000f);
        var layer = server.SpawnNpcShip(mission, sector, layerSide, spawn, layerDroneTypes[0].DroneTypeId);
        layer.SideId = layerSide;
        layer.LayerLaidExpendableTypeId = mine.ExpendableTypeId;
        var layPos = ClearPoint(sector, new Vector3(spawn.X + 400f, spawn.Y, spawn.Z), 500f);
        layer.SetLayerBuild(new Ship.LayerBuildGoal(sector.Id, layPos, 120f));

        int steps = 0;
        while (steps < 60000 && !layer.Dead && layer.Sector is not null)
        {
            server.TickSim(); steps++;
            if (steps % 20 == 0) Thread.Sleep(1);
        }
        Assert.True(layer.Dead, $"layer must lay (steps {steps})");
        Assert.Empty(peer.MineExports);   // a foreign side never gets the deploy-time same-side export
        Console.WriteLine($"P591d-EXPORT-CTRL: NPC layer (side {layerSide}) laid; opposite-side peer (side {peerSide}) received {peer.MineExports.Count} deploy mine EXPORT(s)");
    }
}
