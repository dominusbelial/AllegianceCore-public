using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P414 — LIVE proof that the ripcord request key is wired end-to-end through
/// the REAL client control path. A real <see cref="ConnectionSession"/> (the
/// pilot) sends <c>FM_C_RIPCORD_REQUEST(184)</c> via the exact method the L-key
/// handler calls (<see cref="ConnectionSession.SendRipcordRequest"/>) to a real
/// in-process server, and we assert on BOTH halves of the loop:
/// <list type="number">
/// <item><b>Outbound</b> — the server's <c>RequestRipcordCore</c> arms a real
/// countdown on the pilot's server-side <see cref="Ship"/> (RipcordModel set,
/// time-left positive) for the named sector; the countdown completes and the
/// ship teleports into the target sector.</item>
/// <item><b>Inbound</b> — the pilot's <see cref="ClientWorld"/> decodes the
/// <c>RIPCORD_ACTIVATE(185)</c> broadcast (<see cref="ClientWorld.RipcordActive"/>
/// true, target sector = the sector the key sent), the <c>RIPCORD_DENIED(186)</c>
/// (a bare "Ripcord denied" system line appears), and the <c>RIPCORD_ABORTED(187)</c>
/// (state cleared + the single "ripcord aborted" sound event queued).</item>
/// </list>
///
/// Anchors + a no-anchor deny sector replicate the Core <c>LiveRipcordTests</c>
/// fixture (XXV_simple: fly 1052, anchored 1054/1055, deny 1056).
/// </summary>
[Collection("LiveNetwork")]
public sealed class RipcordRequestLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private const short FlySector = 1052;        // Borasisi — no station, stays flying
    private const short TargetA = 1054;          // Deucalion — injected side-0 anchor
    private const short TargetB = 1055;          // Eris      — injected side-0 anchor
    private const short NoAnchorSector = 1056;   // Farout — no anchor anywhere reachable

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static readonly FieldInfo StationsField =
        typeof(Sector).GetField("_stations", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Core.Sim.Sector._stations field not found");

    private static readonly FieldInfo RipcordCountdownField =
        typeof(Ship).GetField("_dtRipcordCountdown", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Core.Sim.Ship._dtRipcordCountdown field not found");

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        Games =
        [
            new Gm { Name = "Ripcord Request Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 414u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 94130000000000002ul, DrmHash = "p414",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static ConnectionSession Connect(Srv server, string name)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name));
        return s;
    }

    private static Ship ServerShip(Srv server, short id) =>
        server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models.OfType<Ship>())
            .First(s => s.Id == id);

    private static void AddStationToSector(Sector sector, DataStationIGC station)
    {
        var list = (List<DataStationIGC>)StationsField.GetValue(sector)!;
        list.Add(station);
    }

    private sealed class Fixture
    {
        public Srv Server = null!;
        public Ship Pilot = null!;
    }

    private static Fixture Boot(out short pilotId)
    {
        var fx = new Fixture();
        fx.Server = new Srv();
        bool first = true;
        ShipSpawnerField.SetValue(fx.Server, (StaticCore core, Mission mission, short shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var sectorA = mission.Sectors.First(s => s.Id == TargetA);
            var sectorB = mission.Sectors.First(s => s.Id == TargetB);
            if (first)
            {
                // Inject a real ripcord-capable station anchor into both targets.
                var anchorType = core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRipcord)).StationTypeId;
                AddStationToSector(sectorA, new DataStationIGC(
                    Position: [1000f, 0f, 0f], Up: [0f, 0f, 1f], Forward: [1f, 0f, 0f], Rotation: [1f, 0f, 0f, 0f],
                    ClusterId: TargetA, SideId: 0, StationId: 8290, StationTypeId: anchorType, BpHull: 1, BpShield: 1, Name: "AnchA"));
                AddStationToSector(sectorB, new DataStationIGC(
                    Position: [1000f, 0f, 0f], Up: [0f, 0f, 1f], Forward: [1f, 0f, 0f], Rotation: [1f, 0f, 0f, 0f],
                    ClusterId: TargetB, SideId: 0, StationId: 8291, StationTypeId: anchorType, BpHull: 1, BpShield: 1, Name: "AnchB"));
                first = false;
            }
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            var ship = new Ship(shipId, hull, new Vector3(300f, 620f, 880f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(ship, fly);
            fx.Pilot = ship;
            return (ship, fly);
        });

        fx.Server.Boot(Config());
        foreach (var m in fx.Server.Missions) m.Stage = MissionStage.Started;
        pilotId = 0;
        return fx;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RipcordKey_Initiate_Countdown_Teleport_Deny_Cancel_EndToEnd()
    {
        Require.Artwork();
        var fx = Boot(out _);
        using var server = fx.Server;
        ConnectionSession? pilot = null;
        try
        {
            pilot = Connect(server, "rc_pilot");
            int seated = PumpUntil(server, () => pilot.World.MyShipId is { } && ServerShip(server, pilot.World.MyShipId!.Value) == fx.Pilot, 4000);
            Assert.True(seated < 4000, "pilot seated");

            short pilotId = pilot.World.MyShipId!.Value;
            var ship = fx.Pilot;
            Assert.Equal(FlySector, ship.Sector!.Id);      // flying, not docked
            Assert.Null(ship.StationId);

            // ── (a) INITIATE: the L-key's exact outbound call arms a real countdown ──
            pilot.SendRipcordRequest(TargetA);
            int act = PumpUntil(server, () => ship.IsRipcordActive && pilot.World.RipcordActive, 4000);
            Assert.True(act < 4000, "RIPCORD_REQUEST must arm the server countdown AND the client must decode RIPCORD_ACTIVATE");
            Assert.True(ship.RipcordTimeLeft > 0f, "countdown seeded and ticking");
            Assert.Equal((short)TargetA, pilot.World.RipcordTargetSectorId);

            // ── (b) TELEPORT: nudge the countdown to ~0 and pump one tick; the
            // client-initiated ripcord completes, relocating the ship into the
            // target sector. (The server's teleport is silent on the wire — the
            // client HUD tag self-expires in Game1's render loop from the hull's
            // RipcordSpeed; the headless test asserts the real server-side
            // outcome here.) ──
            RipcordCountdownField.SetValue(ship, 0.011f);
            server.TickSim();
            Assert.False(ship.IsRipcordActive);
            Assert.Equal(TargetA, ship.Sector!.Id);        // teleported to the station's cluster
            // In the running client, Game1's RipcordStatusLabel() self-expires
            // the HUD/key ripcord state once the RipcordSpeed window elapses
            // (the server's teleport is silent on the wire). This headless test
            // has no Game1 render loop, so simulate that expiry — the same
            // public ClearRipcord() the client calls — to keep the world state
            // representative for the following deny/cancel scenarios.
            lock (pilot.Gate) pilot.World.ClearRipcord();
            Assert.False(pilot.World.RipcordActive);
            Console.WriteLine($"RIPCORD-P414-LIVE: pilot {pilotId} key -> sector {TargetA}: server countdown armed, teleport landed");

            // ── (c) DENY: request a sector with no anchor → RIPCORD_DENIED, surfaced ──
            pilot.SendRipcordRequest(NoAnchorSector);
            bool denied = PumpUntil(server, () =>
            {
                lock (pilot.Gate)
                    return pilot.World.Events.Chats.Any(c => c.Message.Contains("Ripcord denied"));
            }, 4000) < 4000;
            Assert.True(denied, "RIPCORD_DENIED must be decoded and surfaced as a system line, not silently dropped");
            Assert.False(pilot.World.RipcordActive, "a denied request must not arm a countdown");
            Console.WriteLine("RIPCORD-P414-LIVE: request -> sector 1056 (no anchor) surfaced 'Ripcord denied'");

            // ── (d) CANCEL: start a fresh countdown, then send NA → RIPCORD_ABORTED,
            // clearing state + queueing the single "ripcord aborted" sound event. ──
            pilot.SendRipcordRequest(TargetB);
            PumpUntil(server, () => ship.IsRipcordActive && pilot.World.RipcordActive, 4000);
            Assert.True(pilot.World.RipcordActive, "re-target to TargetB arms a new countdown");

            pilot.SendRipcordRequest(RipcordRequest.NA);
            PumpUntil(server, () => !ship.IsRipcordActive, 4000);
            Assert.False(ship.IsRipcordActive, "NA cancels the server countdown");
            // Server cancellation precedes decoding its guaranteed broadcast
            // (FedSrv.CPP:13329–13338). Freeze simulation while the reader catches up.
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (pilot.Gate) return !pilot.World.RipcordActive;
            }, TimeSpan.FromSeconds(30)), "pilot must decode RIPCORD_ABORTED");
            Assert.False(pilot.World.RipcordActive, "client ripcord state clears on RIPCORD_ABORTED");

            List<ClientSoundEvent> events;
            lock (pilot.Gate)
                events = pilot.World.TakeSoundEvents();
            Assert.Contains(events, e => e.Kind == ClientSoundEventKind.RipcordAborted);
            Console.WriteLine($"RIPCORD-P414-LIVE: pilot {pilotId} cancel (NA) -> RIPCORD_ABORTED cleared state + queued ripcord-aborted sound ({events.Count} sound events drained)");
        }
        finally
        {
            pilot?.Dispose();
        }
    }
}
