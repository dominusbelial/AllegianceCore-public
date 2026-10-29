using System.Linq;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using SimVec = Allegiance.Core.Collision.Vector3;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P540 — LIVE headless proof of the client EJECT handling end-to-end: a real
/// in-process server seats a real <see cref="ConnectionSession"/> (the ejecting
/// player, own ship, side-bearing) and a real cross-side shooter in the SAME
/// sector, and deals lethal damage. The server's P107 eject broadcast a real
/// <c>FM_S_EJECT(77)</c>, and the victim session's <see cref="ClientWorld"/>
/// must apply it: own ship hull → the side's lifepod, pose/spin from the
/// message, cookie adopted, <see cref="ShipEntity.HasPendingEject"/> latched.
/// We then run the PRODUCTION rescue-target pick
/// (<see cref="Game1.ChooseEjectRescueTarget"/>) against the victim's real
/// world + the real client ShopCatalog, log the chosen station (name + sector),
/// and finally relocate the ejected pod into a real friendly station bay
/// and confirm the server's normal station landing path DOCKs it.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P540EjectLiveProofTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private const short SpawnSector = 1052;   // the no-real-station fly sector (XXV_simple)

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        Games =
        [
            new Gm { Name = "P540 Eject Live Proof", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 540u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 95400000000000002ul, DrmHash = "p540",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship ServerShip(Srv server, short id) =>
        server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models.OfType<Ship>())
            .First(s => s.Id == id);

    private static void ResolveBothSessions(Srv server, ConnectionSession a, ConnectionSession b,
        out ConnectionSession victim, out ConnectionSession shooter)
    {
        // The server AUTO-balances the two peers onto opposite sides (0 and 1).
        // The ejecting player is whichever session's OWN ship sits on side 0.
        // (Scanning the server for "the side-0 ship" is unreliable: the map's
        // initial NPC miners also ride side 0.)
        short SideOf(ConnectionSession s)
        {
            lock (s.Gate)
                return s.World.MyShipId is { } id && s.World.Ships.TryGetValue(id, out var e) ? e.SideId : (short)-1;
        }
        if (SideOf(a) == 0) { victim = a; shooter = b; }
        else if (SideOf(b) == 0) { victim = b; shooter = a; }
        else throw new InvalidOperationException($"neither session on side 0 (a={SideOf(a)}, b={SideOf(b)})");
    }

    /// <summary>A friendly (side 0), rescue-capable station injected into the
    /// victim's spawn sector via the model's synthetic-place seam
    /// (<c>Mission.PlaceStation</c>), so both the client world and the server's
    /// P301 rescue pass see it. Built from a real garrison station type (#15)
    /// with <c>SabmCapabilities</c> given the <c>c_sabmRescue</c> bit.</summary>
    private static Station InjectRescueStation(Mission m, short sectorId, short side, short id, SimVec pos)
    {
        var baseType = m.Core.StationTypes[15];
        var type = baseType with { SabmCapabilities = (short)(baseType.SabmCapabilities | DataStationTypeIGC.SabmRescue) };
        var sector = m.Sectors.First(s => s.Id == sectorId);
        var data = new DataStationIGC(
            Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sectorId, SideId: side, StationId: id,
            StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: "P540_RESQ");
        var st = new Station(data, type);
        m.PlaceStation(st, sector);
        return st;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Client_applies_Eject_and_picks_nearest_friendly_rescue_target_and_pod_docks()
    {
        Require.Artwork();
        using var server = new Srv();
        int spawned = 0;
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short shipId) =>
        {
            // Victim (side 0) at the origin; cross-side shooter (side 1) nearby.
            var sector = mission.Sectors.First(s => s.Id == SpawnSector);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            SimVec pos = spawned == 0 ? new SimVec(0f, 0f, 0f) : new SimVec(0f, 0f, -300f);
            var ship = new Ship(shipId, hull, pos, SimVec.Zero, Orientation.Identity)
            { SideId = (short)(spawned == 0 ? 0 : 1) };
            mission.PlaceShip(ship, sector);
            spawned++;
            return (ship, sector);
        });
        server.Boot(Config());
        var mission = server.Missions[0];
        // Start the mission (the sector/dock/rescue passes only run once
        // Started — the same stage-gate Mission.Update enforces).
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        // Inject a friendly rescue-capable station into the victim's sector, at
        // the eject origin, so it is provably the nearest rescue target.
        // This type offers Land as well as Rescue; native requires bay entry
        // for fighter pods (Igc/shipIGC.cpp:727-729,843,861).
        var rescueStation = InjectRescueStation(mission, SpawnSector, side: 0, id: 9001, pos: new SimVec(0f, 0f, 0f));

        using var sessA = new ConnectionSession();
        using var sessB = new ConnectionSession();
        sessA.Connect(server.BoundPort, "EJCTV"); sessB.Connect(server.BoundPort, "EJCTS");
        sessA.SendLogonRequest(Request("EJCTV")); sessB.SendLogonRequest(Request("EJCTS"));

        int seated = PumpUntil(server,
            () => sessA.World.MyShipId is { } && sessB.World.MyShipId is { }
               && sessA.World.MySideId is { } && sessB.World.MySideId is { },
            4000);
        Assert.True(seated < 4000, "both sessions never seated with a side");

        ResolveBothSessions(server, sessA, sessB, out var victim, out var shooter);
        short victimId = victim.World.MyShipId!.Value;
        short shooterId = shooter.World.MyShipId!.Value;
        short victimSide = victim.World.MySideId!.Value;
        Assert.Equal((short)0, victimSide);
        Assert.NotEqual(victimId, shooterId);

        // Game1 installs the ClientWorld's lifepod-hull resolver in its lifecycle
        // (Game1.Lifecycle: _session.World.LifepodHullResolver = ResolveLifepodHull).
        // A bare ConnectionSession has no Game1, so mirror that wiring here — the
        // REAL catalog + the world's SIDE_ROSTER CivId → Civilization.Lifepod.
        var cat = ShopCatalog.TryLoad(ArtRoot);
        Assert.NotNull(cat);
        InstallLifepodResolver(victim, cat!);
        InstallLifepodResolver(shooter, cat!);

        // Ensure the injected rescue station reached the victim's client world
        // (the server exports every station to every peer at join).
        Assert.True(victim.World.Stations.ContainsKey(rescueStation.StationId),
            $"victim client never received the injected rescue station #{rescueStation.StationId}");

        // ── EJECT ── deal lethal cross-side damage; the victim ejects in-sector.
        var victimShip = ServerShip(server, victimId);
        var shooterShip = ServerShip(server, shooterId);
        var preHull = victimShip.HullType;
        short lifepodId = mission.Sides.First(s => s.Id == victimSide).Civilization.Lifepod;
        SimVec p1 = victimShip.Position;
        victimShip.ReceiveDamage(0, victimShip.HitPoints * 2f, mission.LastUpdate, p1, SimVec.Zero, shooterShip);

        int applied = PumpUntil(server,
            () => { lock (victim.Gate) return victim.World.Ships.TryGetValue(victimId, out var e) && e.HasPendingEject; },
            4000);
        Assert.True(applied < 4000, $"victim client never applied EJECT (pumped {applied} ticks)");

        lock (victim.Gate)
        {
            var ent = victim.World.Ships[victimId];
            Assert.Equal(lifepodId, ent.HullId);                      // hull swapped to the side's lifepod
            Assert.True((ent.Latest!.Position - p1).Length() < 300f,  // pose from the message (pod drifts outward from p1)
                $"pod pose {ent.Latest.Position} not near eject origin {p1}");
            Assert.True(ent.HasPendingEject);
            Console.WriteLine($"[eject] client applied EJECT(77): own ship {victimId} -> lifepod hull {ent.HullId} at {ent.Latest.Position} [cookie 0x{ent.Cookie:X2}]");
        }

        // ── Autopilot target chosen (the production pick on the real world) ──
        SimVec ejectPos;
        lock (victim.Gate) ejectPos = victim.World.Ships[victimId].Latest!.Position;
        short StationSabm(short tid) => cat!.StationTypes is { } sts && sts.TryGetValue(tid, out var st) ? st.SabmCapabilities : (short)0;
        short ProbeEabm(short tid) => cat!.ProbeTypes is { } pts && pts.TryGetValue(tid, out var p) ? p.EabmCapabilities : (short)0;

        var target = Game1.ChooseEjectRescueTarget(victim.World, victimSide, ejectPos, StationSabm, ProbeEabm);
        if (target is not { } t) throw new InvalidOperationException("no rescue target chosen");
        Assert.Equal(Game1.TargetKind.Station, t.Kind);
        Assert.Equal(rescueStation.StationId, t.Id);   // the injected rescue station is nearest
        Console.WriteLine($"[eject] autopilot target chosen: {t.Name} ({t.Kind}#{t.Id}) in sector {SectorName(victim, t)}");

        // ── Pod landing at the rescue station (the server's rescue/land path) ──
        // A pod flown home by the eject autopilot arrives at the friendly
        // rescue-capable station; confirm the server docks (lands) the pod there.
        var pod = ServerShip(server, victimId);
        Assert.True(pod.HullType.HasCapability(DataHullTypeIGC.HabmLifepod));
        pod.Position = DockTestPlacement.Inside(mission, rescueStation.Data, pod.HullType);
        pod.Velocity = SimVec.Zero;

        int docked = PumpUntil(server, () => ServerShip(server, victimId).StationId is not null, 2000);
        Assert.True(docked < 2000, $"ejected pod never docked at the rescue station (pumped {docked} ticks)");
        short dockStation = ServerShip(server, victimId).StationId!.Value;
        Console.WriteLine($"[rescue] ejected pod {victimId} docked (landed) at station {dockStation} '{rescueStation.Data.Name}' via the server's station dock/rescue path");

        // Manual input must be able to disengage — the engaged autopilot is a
        // normal player autopilot (unit-tested); here we only confirm the pod
        // story closed end-to-end.
        Console.WriteLine($"LIVE-EJECT-PROOF: victim {victimId} (side {victimSide}) ejected by cross-side {shooterId} -> lifepod {lifepodId}; target {t.Name} (#{t.Id}) in {SectorName(victim, t)}; pod docked at station {dockStation}");
    }

    private static string SectorName(ConnectionSession s, Game1.AutopilotTarget t)
    {
        lock (s.Gate)
        {
            short? cid = t.Kind switch
            {
                Game1.TargetKind.Station => s.World.Stations.TryGetValue(t.Id, out var st) ? st.ClusterId : null,
                Game1.TargetKind.Probe => s.World.Probes.TryGetValue(t.Id, out var pb) ? pb.ClusterId : null,
                _ => null,
            };
            if (cid is { } id && s.World.Clusters.TryGetValue(id, out var cl)) return $"{cl.Name} ({id})";
            return cid?.ToString() ?? "unknown";
        }
    }

    /// <summary>Mirror Game1.ResolveLifepodHull (Game1.Eject.cs): the lifepod
    /// hull id for a side, via the world's SIDE_ROSTER CivId →
    /// <c>StaticCore.Civilizations[civ].Lifepod</c>, falling back to the first
    /// <c>c_habmLifepod</c> hull in the catalogue.</summary>
    private static void InstallLifepodResolver(ConnectionSession s, ShopCatalog cat)
    {
        var world = s.World;
        world.LifepodHullResolver = sideId =>
        {
            if (cat.Core?.Civilizations is { } civs)
            {
                short? civId = null;
                if (world.SideRoster is { } sr)
                {
                    foreach (var side in sr.Sides)
                    {
                        if (side.SideId == sideId) { civId = side.CivId; break; }
                    }
                }
                if (civId is { } cid && civs.TryGetValue(cid, out var civ) && civ.Lifepod != -1)
                    return civ.Lifepod;
            }
            foreach (var h in cat.Hulls)
                if (h.HasCapability(DataHullTypeIGC.HabmLifepod)) return h.HullId;
            return null;
        };
    }
}
