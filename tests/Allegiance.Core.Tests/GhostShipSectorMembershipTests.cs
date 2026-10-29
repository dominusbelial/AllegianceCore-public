using System.Reflection;
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
/// P307 — the ghost-ship sector-membership desync fix's live proof. Three
/// relocation sites in <c>Server.cs</c> (death-respawn in
/// <c>BroadcastKillShip</c>, <c>PerformTeleport</c>, and the two rescue
/// placements) used to reassign only the <see cref="Ship.Sector"/> back-ref
/// without removing the ship from its old sector's <c>_models</c> list and
/// adding it to the new sector's — so the respawned/teleported/rescued ship
/// ended up in the WRONG sector's model list (or none at all), frozen,
/// invisible, never ticked: a server-side ghost. The fix routes all four
/// sites through <see cref="Srv"/>'s <c>MoveShipToSector</c> (P307-hardened
/// to be exact-once-safe).
///
/// The real per-tick passes (<c>Sector.Update</c>,
/// <c>GetShipUpdateCandidates</c>, warp/dock/mining/combat/rescue) walk a
/// sector's <c>_models</c> list, so the INVARIANT that proves the fix is:
/// after a relocation, the ship's <see cref="Ship.Sector"/> back-ref AND its
/// presence in that exact sector's <c>_models</c> list agree — it is in the
/// destination sector's list exactly once and in no other sector's list. This
/// is precisely what the pre-P307 code violated (it only moved the back-ref).
///
/// Each test drives a real in-process <see cref="Srv"/> over loopback TCP
/// with the established <c>ShipSpawner</c> bot harness. These are the
/// DISCRIMINATING tests: revert the P307 wiring (restore the bare
/// <c>X.Sector = destSector;</c> assignments) and every membership assertion
/// here goes red.
/// </summary>
[Collection("LiveNetwork")]
public class GhostShipSectorMembershipTests
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
            new Gm { Name = "Ghost Ship Membership", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 555u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7410000000000000000ul, DrmHash = "ghost",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>The P307 invariant: the ship's <see cref="Ship.Sector"/> points
    /// at <paramref name="expected"/> AND the ship is in exactly that sector's
    /// <c>_models</c> list (once), and in no other sector's list. Any divergence
    /// is the ghost-ship desync.</summary>
    private static void AssertMembershipConsistent(Ship s, Sector expected, string what)
    {
        Assert.Equal(expected, s.Sector);
        int inExpected = expected.Models.Count(m => ReferenceEquals(m, s));
        Assert.True(inExpected == 1,
            $"{what}: ship {s.Id} must be in destination sector {expected.Id} _models exactly once; found {inExpected}");
        foreach (var sec in s.Mission!.Sectors)
            if (!ReferenceEquals(sec, expected))
                Assert.False(sec.Models.Any(m => ReferenceEquals(m, s)),
                    $"{what}: ship {s.Id} must NOT be in sector {sec.Id} _models");
    }

    private static (DataStationIGC station, Sector sector) FirstFriendlyStation(Mission mission)
    {
        foreach (var sector in mission.Sectors)
            foreach (var station in sector.Stations)
                if (station.SideId >= 0)
                    return (station, sector);
        throw new InvalidOperationException("no friendly (SideId>=0) station in mission");
    }

    // ── 1) DEATH-RESPAWN (the worst of the three) ────────────────────────────

    /// <summary>⭐ A player ship dies with a friendly station present and is
    /// respawned as a docked Lifepod — the respawn must leave the ship in the
    /// DESTINATION sector's <c>_models</c> list exactly once (and nowhere
    /// else), not stranded in no list (the P307 bug: frozen/invisible/ghost).
    /// This is the required live proof for the respawn case.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Respawned_player_lands_in_destination_sectors_model_list()
    {
        Require.Artwork();
        using var server = new Srv();
        Sector? noStationSector = null;
        var placedObserver = false;
        server.ShipSpawner = (core, mission, id) =>
        {
            // Both peers spawn in a station-less sector; the friendly station
            // lives in another cluster — so the respawn genuinely crosses
            // sectors and the destination membership is provable.
            noStationSector ??= mission.Sectors.First(s => s.Stations.Count == 0);
            var (station, _) = FirstFriendlyStation(mission);
            var sideHere = mission.Sides.First(s => s.Id == station.SideId);
            var hull = core.HullTypes[sideHere.Civilization.Lifepod];
            Vector3 pos = (placedObserver
                ? new Vector3(3000f, 0f, -300f)
                : new Vector3(3000f, 0f, 0f));
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity)
            { SideId = station.SideId };
            mission.PlaceShip(ship, noStationSector);
            placedObserver = true;
            return (ship, noStationSector);
        };
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var victim = new FltBot();
        using var observer = new FltBot();
        victim.Connect(server.BoundPort, "GM_VICTIM"); observer.Connect(server.BoundPort, "GM_OBS");
        victim.SendLogonRequest(Request("GM_VICTIM")); observer.SendLogonRequest(Request("GM_OBS"));
        PumpUntil(server, () => victim.ShipAssignment is not null && observer.ShipAssignment is not null, 4000);

        short victimId = victim.ShipAssignment!.Ship.ShipId;
        var mission = server.Missions[0];
        var (station, destSector) = FirstFriendlyStation(mission);
        var origSector = noStationSector!;

        // Baseline: the victim starts in the station-less sector's list.
        AssertMembershipConsistent(server.Ships.First(s => s.Ship.Id == victimId).Ship, origSector, "baseline");

        // P653 H-26 restores native's child-or-CAN_CHEAT suicide gate (FedSrv.CPP:6338);
        // enable the configured admin predicate for the test connection.
        server.Config.LiveSettings.AdminCheats = new global::Allegiance.Server.AdminCheatsConfig { Enabled = true, AllowNames = ["GM_VICTIM"] };

        // Real death via the suicide wire message (the P105 path), with a real
        // friendly station present -> bKilled -> the BroadcastKillShip respawn.
        victim.SendSuicide();

        int ticks = PumpUntil(server, () =>
            server.Ships.First(s => s.Ship.Id == victimId).Ship.Sector?.Id == destSector.Id, 2000);
        Assert.True(ticks < 2000, "no respawn relocation to the destination sector");

        var respawned = server.Ships.First(s => s.Ship.Id == victimId).Ship;
        Assert.Equal(station.StationId, respawned.StationId);   // docked at the friendly station

        // ⭐ THE P307 ASSERTION: the respawned ship must live in the destination
        // sector's _models list exactly once (not none, not the old sector's).
        AssertMembershipConsistent(respawned, destSector, "respawn");

        Console.WriteLine($"GHOST-RESPAWN: ship {victimId} died in sector {origSector.Id} -> respawned docked at station {station.StationId} in sector {destSector.Id}; " +
                          $"_models membership: dest {destSector.Models.Count(m => ReferenceEquals(m, respawned))}, elsewhere 0 — consistent (P307)");
    }

    // ── 2) RESCUE (ship-ship, cross-sector: fly sector -> station sector) ────

    /// <summary>⭐ A same-side lifepod rescued by a rescue-capable ship is
    /// placed docked at the friendly station — the rescued pod must end up in
    /// the DESTINATION (station) sector's <c>_models</c> list exactly once, not
    /// stranded in the fly sector's list (the P307 bug: phantom in the old
    /// sector, absent in the new).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rescued_lifepod_lands_in_destination_sectors_model_list()
    {
        Require.Artwork();
        const short FlySector = 1052;          // no station (P111 measurement)
        const short Side0StationSector = 1053;

        using var server = new Srv();
        Ship pod = null!, rescuer = null!;
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var baseHull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            if (n == 0)
            {
                pod = new Ship(shipId, baseHull with { HabmCapabilities = DataHullTypeIGC.HabmLifepod },
                    new Vector3(300f, 620f, 880f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                mission.PlaceShip(pod, fly);
            }
            else
            {
                rescuer = new Ship(shipId, baseHull with { HabmCapabilities = DataHullTypeIGC.HabmRescue },
                    new Vector3(700f, 620f, 860f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                mission.PlaceShip(rescuer, fly);
            }
            n++;
            return n == 1 ? (pod!, fly) : (rescuer!, fly);
        };
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var podBot = new FltBot();
        using var rescBot = new FltBot();
        podBot.Connect(server.BoundPort, "GM_POD"); rescBot.Connect(server.BoundPort, "GM_RES");
        podBot.SendLogonRequest(Request("GM_POD")); rescBot.SendLogonRequest(Request("GM_RES"));
        PumpUntil(server, () => podBot.ShipAssignment is not null && rescBot.ShipAssignment is not null, 4000);

        // Pod baseline: in the fly sector's list.
        var mission = server.Missions.Single();
        var flySector = mission.Sectors.First(s => s.Id == FlySector);
        AssertMembershipConsistent(pod, flySector, "rescue baseline");

        // Drift the pod onto the rescuer -> real rescue (P115 proximity pass).
        pod.Position = rescuer.Position;
        server.TickSim();

        var destSector = mission.Sectors.First(s => s.Id == Side0StationSector);
        int seen = PumpUntil(server,
            () => podBot.PlayerRescueds.Count >= 1 && rescBot.PlayerRescueds.Count >= 1 &&
                  server.Ships.First(s => s.Ship.Id == pod.Id).Ship.Sector?.Id == destSector.Id, 4000);
        Assert.True(seen < 4000, "no PLAYER_RESCUED / relocation reached the station sector");

        // ⭐ THE P307 ASSERTION: the rescued pod lives in the station sector's
        // _models list exactly once, not the fly sector's.
        AssertMembershipConsistent(pod, destSector, "rescue");

        Console.WriteLine($"GHOST-RESCUE: lifepod {pod.Id} rescued in sector {flySector.Id} -> placed docked in sector {destSector.Id}; " +
                          $"_models membership: dest {destSector.Models.Count(m => ReferenceEquals(m, pod))}, fly {flySector.Models.Count(m => ReferenceEquals(m, pod))} — consistent (P307)");
    }

    // ── 3) TELEPORT (cross-sector via an injected restart-capable station) ───

    /// <summary>⭐ A ship teleports (FM_C_DOCKED with a real stationID) to a
    /// restart-capable station in a DIFFERENT sector — it must end up in that
    /// destination sector's <c>_models</c> list exactly once, not stay ticked
    /// by its old sector (the P307 bug: both sectors' geometry passes misfire,
    /// and peers in the new sector never see it).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Teleported_ship_lands_in_destination_sectors_model_list()
    {
        Require.Artwork();
        using var server = new Srv();
        DataStationIGC realStation = default;
        Sector realSector = null!;
        Sector targetSector = null!;
        DataStationIGC fake = default;
        short fakeId = 9100;
        Ship ship = null!;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            realSector = mission.Sectors.First(s => s.Stations.Any(st => st.SideId >= 0));
            realStation = realSector.Stations.First(st => st.SideId >= 0);
            // A second friendly restart-capable station in a DIFFERENT sector
            // (clone the real garrison's type so the restart gate passes).
            targetSector = mission.Sectors.First(s => s.Id != realSector.Id && s.Stations.Count == 0);
            fake = new DataStationIGC(
                Position: [2000f, 0f, 0f], Up: [0f, 0f, 1f], Forward: [1f, 0f, 0f], Rotation: [1f, 0f, 0f, 0f],
                ClusterId: targetSector.Id, SideId: realStation.SideId, StationId: fakeId,
                StationTypeId: realStation.StationTypeId, BpHull: 1, BpShield: 1, Name: "GhostTeleport");
            AddStationToSector(targetSector, fake);

            var side = mission.Sides.First(s => s.Id == realStation.SideId);
            var hull = core.HullTypes.Values.Where(h => SellsHull(core, realStation.StationTypeId, side.Civilization.CivilizationId, h))
                                             .OrderBy(h => h.Price).First();
            ship = new Ship(shipId, hull, DockTestPlacement.Inside(mission, realStation, hull), Vector3.Zero, Orientation.Identity)
            { SideId = realStation.SideId };
            mission.PlaceShip(ship, realSector);
            return (ship, realSector);
        };
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "GM_TP");
        bot.SendLogonRequest(Request("GM_TP"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        // Baseline: the ship is docked in the REAL station's sector's list.
        AssertMembershipConsistent(ship, realSector, "teleport baseline");

        // The FM_C_DOCKED handler gates the teleport on StationId != null
        // (Server.cs:2037-2044) — wait for the spawn tick to latch the dock at
        // the real station (spawned within DockRadius) before sending.
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is not null, 400);

        // Teleport to the injected cross-sector station.
        bot.ResetTeleportAcks();
        bot.SendDocked(fakeId);
        PumpUntil(server, () => bot.TeleportAcks.Count >= 1, 4000);
        var ack = Assert.Single(bot.TeleportAcks);
        Assert.Equal(fakeId, ack.StationId);            // accepted (echoed, not NA)

        int moved = PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.Sector?.Id == targetSector.Id, 4000);
        Assert.True(moved < 4000, "teleport did not relocate the ship to the target sector");

        // ⭐ THE P307 ASSERTION: the teleported ship lives in the DESTINATION
        // sector's _models list exactly once, not the old one's.
        AssertMembershipConsistent(ship, targetSector, "teleport");

        // The teleported ship is ALIVE (teleport isn't a death), so undocking it
        // lets us prove it GENUINELY ticks in the NEW sector: it appears in the
        // destination sector's real per-tick candidate list (the one every pass
        // walks) and is absent from the old sector's — the opposite of the P307
        // phantom (in the old sector's list under new-sector coordinates).
        bot.SendDocked();                                       // stationID = NA = undock
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is null, 400);
        Assert.True(targetSector.GetShipUpdateCandidates().Any(c => ReferenceEquals(c, ship)),
            $"teleported ship {ship.Id} must tick (be a ship-update candidate) in destination sector {targetSector.Id}");
        Assert.False(realSector.GetShipUpdateCandidates().Any(c => ReferenceEquals(c, ship)),
            $"teleported ship {ship.Id} must NOT tick in the old sector {realSector.Id}");

        Console.WriteLine($"GHOST-TELEPORT: ship {shipId} tp station {realStation.StationId} (sector {realSector.Id}) -> station {fakeId} (sector {targetSector.Id}); " +
                          $"_models membership: dest {targetSector.Models.Count(m => ReferenceEquals(m, ship))}, old {realSector.Models.Count(m => ReferenceEquals(m, ship))}; " +
                          $"undocked -> ticks in {targetSector.Id} ship-update candidates, not in {realSector.Id} — consistent (P307)");
    }

    // ── helpers (borrowed from the established harness) ──────────────────────

    private static bool SellsHull(StaticCore core, short stationTypeId, short civId, DataHullTypeIGC hull)
    {
        var st = core.StationTypes[stationTypeId];
        var civ = core.Civilizations[civId];
        var need = hull.TtbmRequired;
        for (int i = 0; i < need.Length && i < st.TtbmLocal.Length; i++)
        {
            byte avail = (byte)(st.TtbmLocal[i] | (i < civ.TtbmBaseTechs.Length ? civ.TtbmBaseTechs[i] : (byte)0));
            if ((need[i] & ~avail) != 0) return false;
        }
        for (int i = st.TtbmLocal.Length; i < need.Length; i++)
        {
            if (need[i] != 0 && (i >= civ.TtbmBaseTechs.Length || (need[i] & ~civ.TtbmBaseTechs[i]) != 0)) return false;
        }
        return true;
    }

    private static void AddStationToSector(Sector sector, DataStationIGC station)
    {
        var mission = sector.Mission!;
        mission.PlaceStation(new Station(station, mission.Core.StationTypes[station.StationTypeId]), sector);
    }
}
