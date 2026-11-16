using System.Collections;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using Ac = global::Allegiance.Server.AdminCheatsConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P1029 — R1b finding F4: the server-authorized REPOSITIONS must carry the
/// native per-player cookie (<c>NewCookie()</c>) and the native cluster
/// notification (<c>SET_CLUSTER</c>), or the honest client's own-ship update
/// stream dies. Three reposition paths, one proof each:
/// <list type="number">
/// <item><b>Carrier relaunch</b> — <c>RELAUNCH_SHIP(192)</c> for a PLAYER must
/// stamp a fresh cookie (<c>FedSrv.CPP:13015-13016</c>); the port sent 0 while
/// the server's peer cookie stayed at the join value, so the next
/// <c>FM_C_SHIP_UPDATE</c> was dropped whole by the native cookie gate
/// (<c>FedSrv.CPP:4851-4864</c>) and the landed player lost flight control.</item>
/// <item><b>Ripcord teleport</b> — <c>UseRipcord</c> ends in
/// <c>pship-&gt;SetCluster(pcluster)</c> (<c>FedSrv.CPP:13482</c>) →
/// <c>CFSPlayer::SetCluster</c>: a guaranteed <c>SET_CLUSTER(7)</c> with a fresh
/// cookie, sent even for a SAME-sector move (<c>Igc/shipIGC.h:233-240</c>). The
/// port moved the model and left the client in the old sector with the old
/// cookie.</item>
/// <item><b>Admin relocate</b> — the cheat must refuse a docked / boarded /
/// team-lobby / dead ship (this port keeps <c>Ship.Sector</c> non-null on a
/// docked ship, so the old sector test alone let a stale dock travel), and a
/// flying relocate must still deliver <c>SET_CLUSTER</c> + a rotated cookie
/// (it already did: <c>RelocateShipToSector</c> raises <c>ShipWarped</c> —
/// asserted here so the report can state the report's claim was wrong).</item>
/// </list>
/// All live loopback: real corpus hulls, a real in-process server, real
/// <see cref="FltBot"/> peers over TCP.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P1029RepositionSyncTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // XXV_simple (measured, P111/P117): 1052 = Borasisi, no station.
    private const short FlySector = 1052;
    private const short OtherSector = 1054;      // Deucalion — the relocate destination

    // cap502's slot-0 bay interior for the fig22 lander (measured, P117).
    private static readonly Vector3 Cap502Inside = new(-60f, -32f, 70f);

    private static readonly FieldInfo PeerShipsField =
        typeof(Srv).GetField("_peerShips", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo PeerUpdateField =
        typeof(Srv).GetField("_peerShipUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static Cfg Config(int port = 0, Ac? adminCheats = null) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        LiveSettings = new Ls { DebugMode = adminCheats is not null, AdminCheats = adminCheats },
        Games =
        [
            new Gm { Name = "P1029 Reposition", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 1029u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9102900000000000000ul, DrmHash = "p1029",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship ServerShip(Srv server, short id) => server.Ships.First(s => s.Ship.Id == id).Ship;

    /// <summary>The peer connection owning <paramref name="ship"/> (the
    /// <c>_peerShips</c> key — the port's "is this a player ship" test).</summary>
    private static ConnectionId? ConnOf(Srv server, Ship ship)
    {
        var peers = (IDictionary)PeerShipsField.GetValue(server)!;
        foreach (DictionaryEntry e in peers)
        {
            var live = e.Value!;
            var owned = live.GetType().GetProperty("Ship")!.GetValue(live);
            if (ReferenceEquals(owned, ship)) return (ConnectionId)e.Key!;
        }
        return null;
    }

    /// <summary>The P507 per-peer cookie/acceptance state (native
    /// <c>CFSPlayer::m_cookie</c> + the rolling counters).</summary>
    private static (byte Cookie, long Accepted, long DroppedCookie) PeerState(Srv server, Ship ship)
    {
        var conn = ConnOf(server, ship) ?? throw new InvalidOperationException($"ship {ship.Id} has no peer");
        var dict = (IDictionary)PeerUpdateField.GetValue(server)!;
        var pu = dict[conn] ?? throw new InvalidOperationException($"no peer-update state for {conn}");
        var t = pu.GetType();
        return ((byte)t.GetField("Cookie")!.GetValue(pu)!,
                (long)t.GetField("Accepted")!.GetValue(pu)!,
                (long)t.GetField("DroppedCookie")!.GetValue(pu)!);
    }

    /// <summary>Read a private instance field on the server (the established
    /// reflection seam the sibling live tests use).</summary>
    private static object? Call(Srv server, string method, params object?[] args) =>
        typeof(Srv).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(server, args);

    private sealed class Landed
    {
        public Ship Carrier = null!, Lander = null!;
    }

    /// <summary>Two-ship spawn injection: bot A flies the real carrier hull
    /// (cap502), bot B a dreghpod cloned with exactly <c>c_habmLandOnCarrier</c>,
    /// both side 0 in the station-less fly sector (the P117 recipe).</summary>
    private static Landed Inject(Srv server, Vector3 carrierPos, Vector3 landerPos)
    {
        var sp = new Landed();
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            if (n == 0)
            {
                sp.Carrier = new Ship(shipId,
                    core.HullTypes.Values.First(h => h.ModelName == "cap502"),
                    carrierPos, Vector3.Zero, Orientation.Identity) { SideId = 0 };
                mission.PlaceShip(sp.Carrier, fly);
            }
            else
            {
                var fig = core.HullTypes.Values.First(h => h.ModelName == "fig22");
                sp.Lander = new Ship(shipId,
                    fig with { HabmCapabilities = DataHullTypeIGC.HabmLandOnCarrier },
                    landerPos, Vector3.Zero, Orientation.Identity) { SideId = 0 };
                mission.PlaceShip(sp.Lander, fly);
            }
            n++;
            return n == 1 ? (sp.Carrier!, fly) : (sp.Lander!, fly);
        };
        return sp;
    }

    private static void ParkInBay(Srv server, Landed sp, Vector3 insideOffset)
    {
        sp.Lander.Position = sp.Carrier.Position + insideOffset;
        sp.Lander.Velocity = Vector3.Zero;
        var bay = sp.Carrier.Sector!.LandingBays!.Bay(sp.Carrier.HullType);
        Assert.NotNull(bay);
        Assert.True(sp.Carrier.InGarage(sp.Lander, 0f, bay), "lander not inside the carrier bay");
    }

    /// <summary>⭐ P1029 (F4.1) — a PLAYER landing on a carrier receives
    /// <c>RELAUNCH_SHIP(192)</c> stamped with the server's fresh per-player
    /// cookie (native <c>NewCookie()</c>, FedSrv.CPP:13015-13016), the honest
    /// client adopts it, and the very next normal own-ship SHIP_UPDATE is
    /// ACCEPTED. Pre-fix the message carried 0 while the server expected the
    /// join cookie, so every subsequent update was dropped whole.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Relaunched_player_cookie_rotates_and_the_next_ship_update_is_accepted()
    {
        Require.Artwork();
        var server = new Srv();
        var sp = Inject(server, new Vector3(500f, 950f, 930f), new Vector3(500f, 1350f, 930f));
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var carBot = new FltBot();
        using var lanBot = new FltBot();
        carBot.Connect(server.BoundPort, "P1029C");
        carBot.SendLogonRequest(Request("P1029C"));
        PumpUntil(server, () => carBot.ShipAssignment is not null, 4000);
        lanBot.Connect(server.BoundPort, "P1029L");
        lanBot.SendLogonRequest(Request("P1029L"));
        PumpUntil(server, () => lanBot.ShipAssignment is not null, 4000);

        var lander = sp.Lander;
        Assert.Equal(lander.Id, lanBot.ShipAssignment!.Ship.ShipId);
        byte joinCookie = lanBot.ShipAssignment.Cookie;

        ParkInBay(server, sp, Cap502Inside);
        int seen = PumpUntil(server, () => lanBot.RelaunchShips.Count >= 1, 4000);
        Assert.True(seen < 4000, "no RELAUNCH_SHIP(192) reached the lander's own peer");
        var rel = Assert.Single(lanBot.RelaunchShips);

        // Native: `if (pfsShip->IsPlayer()) pfmLaunch->cookie = NewCookie()`.
        Assert.NotEqual(0, rel.Cookie);
        Assert.NotEqual(joinCookie, rel.Cookie);
        // The honest client consumes it (ClientWorld.Apply(RelaunchShip) →
        // Apply(ShipReset) with the message cookie; the harness mirrors that).
        Assert.Equal(rel.Cookie, lanBot.PoseCookie);
        // …and it is exactly the cookie the server now demands.
        var before = PeerState(server, lander);
        Assert.Equal(rel.Cookie, before.Cookie);

        // The player's next NORMAL update, echoing that cookie and a pose a few
        // metres from the server's own, must be accepted (it is what a flying
        // client sends every tick).
        var live = ServerShip(server, lander.Id);
        var target = live.Position + new Vector3(0f, 0f, 10f);
        uint nowMs = (uint)(server.SimNow * 1000.0) + 5;
        lanBot.SendOwnShipPose(nowMs, rel.Cookie, target, live.Velocity, live.Orientation,
            ControlData.Neutral, 0, new float[3]);
        int pumped = PumpUntil(server, () => PeerState(server, lander).Accepted > before.Accepted, 200);
        var after = PeerState(server, lander);
        Assert.True(after.Accepted > before.Accepted,
            $"the post-relaunch SHIP_UPDATE was not accepted (accepted {before.Accepted} -> {after.Accepted}, " +
            $"droppedCookie {after.DroppedCookie}); cookie expected 0x{before.Cookie:X2}, client echoed 0x{rel.Cookie:X2}");
        Assert.Equal(before.DroppedCookie, after.DroppedCookie);
        Assert.True((ServerShip(server, lander.Id).Position - target).Length() < 12f,
            $"accepted pose not applied: {ServerShip(server, lander.Id).Position} vs {target}");

        Console.WriteLine($"P1029-RELAUNCH-LIVE: ship {lander.Id} landed on carrier {sp.Carrier.Id}; RELAUNCH_SHIP cookie 0x{rel.Cookie:X2} (join 0x{joinCookie:X2}) adopted by the client; next SHIP_UPDATE ACCEPTED after {pumped} ticks");
    }

    /// <summary>⭐ P1029 (F4.1, drone branch) — an NPC/drone lander keeps
    /// <c>cookie = 0</c>, exactly native (<c>pfsShip-&gt;IsPlayer()</c> is false,
    /// so nothing is stamped).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Drone_lander_relaunch_keeps_cookie_zero()
    {
        Require.Artwork();
        var server = new Srv();
        var sp = Inject(server, new Vector3(800f, 950f, 930f), new Vector3(800f, 1350f, 930f));
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var carBot = new FltBot();
        carBot.Connect(server.BoundPort, "P1029D");
        carBot.SendLogonRequest(Request("P1029D"));
        PumpUntil(server, () => carBot.ShipAssignment is not null, 4000);
        carBot.ResetRelaunchShips();

        // An ownerless NPC lander in the same bay (no peer ⇒ CFSShip::IsPlayer()
        // is false, the native gate that suppresses the stamp).
        var mission = sp.Carrier.Mission!;
        var fly = mission.Sectors.First(s => s.Id == FlySector);
        var fig = mission.Core.HullTypes.Values.First(h => h.ModelName == "fig22")
            with { HabmCapabilities = DataHullTypeIGC.HabmLandOnCarrier };
        var drone = new Ship((short)9101, fig, sp.Carrier.Position + Cap502Inside, Vector3.Zero, Orientation.Identity)
        { SideId = 0 };
        mission.PlaceShip(drone, fly);
        drone.InitClock(fly.LastUpdate);
        Assert.Null(ConnOf(server, drone));

        int seen = PumpUntil(server, () => carBot.RelaunchShips.Any(r => r.ShipId == drone.Id), 4000);
        Assert.True(seen < 4000, "the NPC lander never relaunched");
        var rel = carBot.RelaunchShips.Single(r => r.ShipId == drone.Id);
        Assert.Equal(0, rel.Cookie);   // no player cookie to stamp (FedSrv.CPP:13015)

        Console.WriteLine($"P1029-RELAUNCH-DRONE-LIVE: NPC lander {drone.Id} relaunched off carrier {sp.Carrier.Id} with cookie 0 (native player-only stamp)");
    }

    // ─────────────────────────── ripcord ───────────────────────────

    /// <summary>Boot a one-bot server whose ship flies in <see cref="FlySector"/>
    /// (station-less), with a synthetic friendly <c>c_sabmRipcord</c> station
    /// injected into <paramref name="anchorSector"/>. Pass
    /// <see cref="FlySector"/> for a SAME-sector ripcord, <see cref="OtherSector"/>
    /// for a cross-sector one.</summary>
    private static (Srv server, FltBot bot, Ship ship, short stationId) RipcordSetup(short anchorSector)
    {
        var server = new Srv();
        Ship ship = null!;
        short stationId = 8300;
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            if (n == 0)
            {
                var anchorType = core.StationTypes.Values
                    .First(t => t.HasCapability(DataStationTypeIGC.SabmRipcord)).StationTypeId;
                var anchor = new DataStationIGC(
                    Position: [1000f, 0f, 0f], Up: [0f, 0f, 1f], Forward: [1f, 0f, 0f], Rotation: [1f, 0f, 0f, 0f],
                    ClusterId: anchorSector, SideId: 0, StationId: stationId, StationTypeId: anchorType,
                    BpHull: 1, BpShield: 1, Name: "RipAnchor");
                var sector = mission.Sectors.First(s => s.Id == anchorSector);
                mission.PlaceStation(new Station(anchor, mission.Core.StationTypes[anchor.StationTypeId]), sector);
            }
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId)
                .First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            ship = new Ship(shipId, hull, new Vector3(300f, 620f, 880f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            ship.Controls = new ControlData(0f, 0f, 0f, -1f);   // kill-throttle: hold position
            mission.PlaceShip(ship, fly);
            n++;
            return (ship, fly);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        var bot = new FltBot();
        bot.Connect(server.BoundPort, "P1029R");
        bot.SendLogonRequest(Request("P1029R"));
        PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.Null(ship.StationId);
        Assert.Equal(FlySector, ship.Sector!.Id);
        return (server, bot, ship, stationId);
    }

    /// <summary>Nudge the ripcord countdown to <paramref name="v"/> seconds so the
    /// next real tick crosses zero (the P112/P113 seam, same reflection write).</summary>
    private static void NudgeCountdownTo(Ship ship, float v) =>
        typeof(Ship).GetField("_dtRipcordCountdown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ship, v);

    private static void ArmRipcord(Srv server, FltBot bot, Ship ship, short target)
    {
        bot.SendRipcordRequest(target);
        int seen = PumpUntil(server, () => bot.RipcordActivates.Count >= 1, 4000);
        Assert.True(seen < 4000, $"no RIPCORD_ACTIVATE for sector {target}");
        Assert.True(ship.IsRipcordActive);
    }

    /// <summary>⭐ P1029 (F4.2) — a CROSS-SECTOR ripcord teleport sends the
    /// ripper's own peer the native <c>SET_CLUSTER(7)</c>: destination sector,
    /// the launch pose, and a rotated cookie (<c>CFSPlayer::SetCluster</c>,
    /// fsship.cpp:758-767 via <c>UseRipcord</c>'s <c>SetCluster</c>,
    /// FedSrv.CPP:13482). Pre-fix no SET_CLUSTER was sent at all: the client kept
    /// the old sector's ships/treasures and the server kept the old cookie.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cross_sector_ripcord_sends_SET_CLUSTER_with_a_rotated_cookie()
    {
        Require.Artwork();
        var (server, bot, ship, _) = RipcordSetup(OtherSector);
        using var __ = bot;
        var sent = new List<(ConnectionId Peer, byte[] Msg)>();
        server._testSendCapture = (c, p) => sent.AddRange(FedPacket.Unpack(p).Select(m => (c, m)));

        byte joinCookie = bot.ShipAssignment!.Cookie;
        var joinState = PeerState(server, ship);
        ArmRipcord(server, bot, ship, OtherSector);

        int assignmentsBefore = bot.Assignments.Count;
        NudgeCountdownTo(ship, 0.011f);
        server.TickSim();
        Assert.Equal(OtherSector, ship.Sector!.Id);   // the teleport still happened

        int seen = PumpUntil(server,
            () => bot.Assignments.Count > assignmentsBefore && bot.Assignments[^1].SectorId == OtherSector, 4000);
        Assert.True(seen < 4000,
            $"no SET_CLUSTER(7) for sector {OtherSector} reached the ripper's client (assignments {assignmentsBefore} -> {bot.Assignments.Count})");

        var set = bot.Assignments[^1];
        Assert.Equal(OtherSector, set.SectorId);
        Assert.NotEqual(joinCookie, set.Cookie);
        Assert.NotEqual(0, set.Cookie);
        // The frame is the ripper's own, carrying its launch pose and the new cookie.
        Assert.Equal(ship.Id, set.Ship.ShipId);
        Assert.True((set.Ship.Position - ship.Position).Length() < 25f,
            $"SET_CLUSTER pose {set.Ship.Position} is not the ripper's launch pose {ship.Position}");
        Assert.Equal(set.Cookie, bot.PoseCookie);
        Assert.Equal(set.Cookie, PeerState(server, ship).Cookie);

        // The old cookie is now dead (native cookie gate), the new one works.
        var before = PeerState(server, ship);
        bot.SendOwnShipPose((uint)(server.SimNow * 1000.0) + 5, joinCookie, ship.Position,
            ship.Velocity, ship.Orientation, ControlData.Neutral, 0, new float[3]);
        int dropped = PumpUntil(server, () => PeerState(server, ship).DroppedCookie > before.DroppedCookie, 200);
        Assert.True(dropped < 200, "a pre-ripcord cookie was still accepted after the SET_CLUSTER rotated it");

        var live = ServerShip(server, ship.Id);
        var target = live.Position + new Vector3(0f, 0f, 10f);
        var before2 = PeerState(server, ship);
        bot.SendOwnShipPose((uint)(server.SimNow * 1000.0) + 10, set.Cookie, target, live.Velocity,
            live.Orientation, ControlData.Neutral, 0, new float[3]);
        int accepted = PumpUntil(server, () => PeerState(server, ship).Accepted > before2.Accepted, 200);
        Assert.True(accepted < 200,
            $"a SHIP_UPDATE echoing the ripcord SET_CLUSTER cookie was not accepted (droppedCookie {PeerState(server, ship).DroppedCookie})");

        Console.WriteLine($"P1029-RIPCORD-CROSS-LIVE: ship {ship.Id} ripcorded {FlySector} -> {OtherSector}; SET_CLUSTER cookie 0x{set.Cookie:X2} (join 0x{joinCookie:X2}); old cookie dropped, new cookie accepted");
    }

    /// <summary>⭐ P1029 (F4.2, same-cluster edge) — a ripcord to a station in the
    /// ripper's OWN sector still emits <c>SET_CLUSTER</c> with a fresh cookie:
    /// native runs the <c>if (cluster)</c> branch "even if the cluster does not
    /// change: the ship mostly likely teleported to a new position"
    /// (<c>Igc/shipIGC.h:233-240</c>). Without the cookie rotation the client
    /// would keep flying the pre-teleport pose forever.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Same_sector_ripcord_still_sends_SET_CLUSTER_and_rotates_the_cookie()
    {
        Require.Artwork();
        var (server, bot, ship, _) = RipcordSetup(FlySector);
        using var __ = bot;
        byte joinCookie = bot.ShipAssignment!.Cookie;
        ArmRipcord(server, bot, ship, FlySector);

        int before = bot.Assignments.Count;
        NudgeCountdownTo(ship, 0.011f);
        server.TickSim();
        Assert.Equal(FlySector, ship.Sector!.Id);    // stayed in the same cluster

        int seen = PumpUntil(server, () => bot.Assignments.Count > before, 4000);
        Assert.True(seen < 4000, "no SET_CLUSTER(7) after a SAME-sector ripcord (native sends one anyway)");
        var set = bot.Assignments[^1];
        Assert.Equal(FlySector, set.SectorId);
        Assert.NotEqual(joinCookie, set.Cookie);
        Assert.Equal(set.Cookie, PeerState(server, ship).Cookie);

        Console.WriteLine($"P1029-RIPCORD-SAME-LIVE: ship {ship.Id} ripcorded within {FlySector}; SET_CLUSTER sent anyway (Igc/shipIGC.h:233-240), cookie 0x{joinCookie:X2} -> 0x{set.Cookie:X2}");
    }

    // ─────────────────────────── admin relocate ───────────────────────────

    private static (Srv server, FltBot bot, Ship ship) BootFlyingShip(Ac? admin)
    {
        var server = new Srv();
        Ship ship = null!;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId)
                .First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            ship = new Ship(shipId, hull, new Vector3(300f, 620f, 880f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            ship.Controls = new ControlData(0f, 0f, 0f, -1f);
            mission.PlaceShip(ship, fly);
            return (ship, fly);
        };
        server.Boot(Config(adminCheats: admin ?? new Ac { Enabled = true, AllowNames = ["admin"] }));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        var bot = new FltBot();
        bot.Connect(server.BoundPort, "admin");
        bot.SendLogonRequest(Request("admin"));
        PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.Equal(ship.Id, bot.ShipAssignment!.Ship.ShipId);
        Assert.Null(ship.StationId);
        Assert.Equal((short)0, ship.SideId);
        return (server, bot, ship);
    }

    private static List<string> RelocateReplies(ConnectionId to, List<(ConnectionId Peer, byte[] Msg)> sent)
    {
        var replies = new List<string>();
        foreach (var (peer, msg) in sent)
        {
            if (peer != to || FedMessageHeader.Read(msg).Fmid != ChatMessage.Fmid) continue;
            var chat = ChatMessage.Decode(msg);
            if (chat.ChatTarget == (int)ChatTarget.IndividualNoFilter && chat.Message is { } text)
                replies.Add(text);
        }
        return replies;
    }

    /// <summary>⭐ P1029 (F4.3) — the relocate cheat refuses a boarded,
    /// team-lobby or dead ship, and a DOCKED ship is relocated with its dock
    /// cleared. This port keeps <c>Ship.Sector</c> non-null on all of them, so
    /// the old `target.Sector is null` test let them be moved with
    /// <c>StationId</c>/<c>ParentShip</c> left behind (a stale dock in the wrong
    /// sector, from which it could buy a hull at a station it is no longer at).
    /// A docked target stays allowed because the port's MCP staging cheat
    /// relocates auto-seated bots, which spawn docked
    /// (<c>P554AdminCheatLiveTests</c>); what it must not do is keep the dock.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Relocate_refuses_boarded_team_lobby_and_dead_and_clears_a_docked_ships_dock()
    {
        Require.Artwork();
        var (server, bot, ship) = BootFlyingShip(null);
        using var __ = bot;
        var sent = new List<(ConnectionId Peer, byte[] Msg)>();
        server._testSendCapture = (c, p) => sent.AddRange(FedPacket.Unpack(p).Select(m => (c, m)));
        var conn = ConnOf(server, ship)!.Value;
        var home = ship.Sector!.Id;
        var homePos = ship.Position;
        // The chat handler passes the tokens AFTER the "relocate" keyword.
        string cmd = $"{ship.Id} {OtherSector} 4000 4000 4000";

        void ExpectRefused(string state)
        {
            sent.Clear();
            Call(server, "HandleRelocateCheat", conn, ship, cmd);
            Assert.Equal(home, ship.Sector!.Id);
            Assert.Equal(homePos, ship.Position);
            var replies = RelocateReplies(conn, sent);
            Assert.True(replies.Any(r => r.Contains("cannot relocate", StringComparison.Ordinal)),
                $"relocate of a {state} ship was not refused (replies: [{string.Join(" | ", replies)}])");
        }

        // (a) boarded on another ship.
        var parent = new Ship((short)9102, ship.HullType, ship.Position, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        ship.Mission!.PlaceShip(parent, ship.Sector!);
        ship.ParentShip = parent;
        parent.AddChildShip(ship);
        ExpectRefused("boarded");
        ship.ParentShip = null;

        // (b) team lobby (side -2).
        ship.SideId = -2;
        ExpectRefused("team-lobby");
        ship.SideId = 0;

        // (c) dead.
        ship.SetFraction(0f);
        ExpectRefused("dead");
        ship.SetFraction(1f);

        // (d) docked — moved, and the dock is cleared (no stale station in the
        // destination sector; the client's SET_CLUSTER does SetStation(NULL)).
        ship.StationId = 8380;
        sent.Clear();
        Call(server, "HandleRelocateCheat", conn, ship, cmd);
        Assert.Equal(OtherSector, ship.Sector!.Id);
        Assert.Null(ship.StationId);
        Assert.True((ship.Position - new Vector3(4000f, 4000f, 4000f)).Length() < 1f);
        Assert.True(RelocateReplies(conn, sent).All(r => !r.Contains("cannot relocate", StringComparison.Ordinal)),
            "the docked relocate must not be refused");

        Console.WriteLine($"P1029-RELOCATE-GATES-LIVE: boarded / team-lobby / dead ship {ship.Id} refused; the docked ship was moved to {OtherSector} with StationId cleared");
    }

    /// <summary>⭐ P1029 (F4.3, the report's second claim — NOT REAL in this
    /// port): a flying relocate already sends <c>SET_CLUSTER(7)</c> with a
    /// rotated cookie, because <c>RelocateShipToSector</c> raises
    /// <c>ShipWarped</c> → <c>Server.OnShipWarped</c> → the shared
    /// <c>CFSPlayer::SetCluster</c> half (<c>Server.Combat.cs</c>
    /// <c>SendOwnerSetCluster</c>). Pinned so no later change regresses it.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Relocate_of_a_flying_ship_sends_SET_CLUSTER_with_a_new_cookie()
    {
        Require.Artwork();
        var (server, bot, ship) = BootFlyingShip(null);
        using var __ = bot;
        var sent = new List<(ConnectionId Peer, byte[] Msg)>();
        server._testSendCapture = (c, p) => sent.AddRange(FedPacket.Unpack(p).Select(m => (c, m)));
        var conn = ConnOf(server, ship)!.Value;
        byte joinCookie = bot.ShipAssignment!.Cookie;
        var dest = new Vector3(4000f, 4000f, 4000f);

        sent.Clear();
        Call(server, "HandleRelocateCheat", conn, ship, $"{ship.Id} {OtherSector} {dest.X} {dest.Y} {dest.Z}");

        Assert.Equal(OtherSector, ship.Sector!.Id);
        var sets = sent.Where(s => s.Peer == conn && FedMessageHeader.Read(s.Msg).Fmid == SetCluster.Fmid)
            .Select(s => SetCluster.Decode(s.Msg)).ToList();
        var set = Assert.Single(sets);
        Assert.Equal(OtherSector, set.SectorId);
        Assert.NotEqual(joinCookie, set.Cookie);
        Assert.Equal(set.Cookie, PeerState(server, ship).Cookie);
        Assert.True((set.Ship.Position - dest).Length() < 1f,
            $"SET_CLUSTER pose {set.Ship.Position} is not the relocation pose {dest}");

        Console.WriteLine($"P1029-RELOCATE-SETCLUSTER-LIVE: flying relocate {ship.Id} -> {OtherSector} sent SET_CLUSTER cookie 0x{joinCookie:X2} -> 0x{set.Cookie:X2} (claim 3's 'no SetCluster/NextCookie' does not reproduce)");
    }
}
