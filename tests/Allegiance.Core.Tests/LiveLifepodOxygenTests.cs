using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P279 — the drifting Lifepod's 5-minute oxygen countdown: the pod's own
/// lifetime, the one piece the (already-working) eject mechanism lacked.
/// Native: <c>CshipIGC::m_timeToDie</c> (shipIGC.h:2607) is armed on hull-swap
/// to a lifepod (<c>shipIGC.cpp:1407-1408</c>:
/// <c>m_timeToDie = lastUpdate + c_fcidLifepodEndurance[31]</c>, whose default
/// is 300.0f = 5 min, <c>missionigc.cpp:2724/2775</c>) and checked every Update
/// (<c>shipIGC.cpp:361-363</c>: expiry ⇒ <c>KillShipEvent(now,this,NULL,1.0,…)</c>).
/// The port reuses its OWN established death path for the expiry: that
/// KillShipEvent re-enters BroadcastKillShip → <c>bLifepod ⇒ bKilled</c> ⇒
/// KILL_SHIP(105) + SHIP_DELETE(20) + the P105 respawn (docked Lifepod at a
/// friendly station + ENTER_LIFEPOD(166) to the owner). Nothing about the
/// destroy/respawn is new — oxygen expiry just triggers it.
/// <para>
/// Two live proofs (a real in-process <see cref="Srv"/> over loopback TCP,
/// mission forced Started so the sim tick reaches each ship's
/// <see cref="Ship.OnUpdate"/>):
///  (1) a player ejects into a drifting pod; the pod's (test-compressed)
///      countdown expires ⇒ the pod is destroyed with real wire
///      KILL_SHIP/SHIP_DELETE to the same-sector observer AND the player
///      respawns (ENTER_LIFEPOD + docked at a friendly station) — no re-eject;
///  (2) a pod that has been brought safe to a base (docked — exactly what
///      rescue does) does NOT self-destruct even well past its expired oxygen
///      deadline — the scope-4 "rescue cancels the timer" via the docked gate.
/// The [Collection("LiveNetwork")] tag keeps these sequential with the rest.
/// </summary>
[Collection("LiveNetwork")]
public class LiveLifepodOxygenTests
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
            new Gm { Name = "Oxygen Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 881u, DwCookie = 0x881u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 6390000000000000000ul, DrmHash = "oxyg",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Two DISTINCT sides that both own a real station, plus the first
    /// station of <c>a</c> — guarantees a friendly station exists somewhere for
    /// whichever peer becomes the victim, so the respawn leg is reachable. See
    /// <c>LiveInSpaceEjectTests.TwoStationOwningSides</c>.</summary>
    private static (Side a, Side b, DataStationIGC stationA, Sector sectorA) TwoStationOwningSides(Mission m)
    {
        var owners = m.Sectors.SelectMany(s => s.Stations)
            .Where(st => st.SideId >= 0)
            .Select(st => st.SideId)
            .Distinct()
            .ToList();
        if (owners.Count < 2)
            throw new InvalidOperationException("map has fewer than two station-owning sides");
        var a = m.Sides.First(s => s.Id == owners[0]);
        var b = m.Sides.First(s => s.Id == owners[1]);
        var stationA = m.Sectors.SelectMany(s => s.Stations).First(st => st.SideId == a.Id);
        var sectorA = m.Sectors.First(s => s.Stations.Contains(stationA));
        return (a, b, stationA, sectorA);
    }

    // The same-loopback wire client the P107 suite uses, recording the messages
    // this slice's expiry reuses: EJECT (pod formed), KILL_SHIP / SHIP_DELETE
    // (pod destroyed) and ENTER_LIFEPOD (player respawned at a friendly station).
    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private LogonAck? _ack;
        private SetCluster? _set;
        private readonly List<Eject> _ejects = new();
        private readonly List<KillShip> _kills = new();
        private readonly List<ShipDelete> _deletes = new();
        private readonly List<EnterLifepod> _pods = new();

        public LogonAck? Ack { get { lock (_gate) return _ack; } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
        public IReadOnlyList<Eject> Ejects { get { lock (_gate) return _ejects.ToArray(); } }
        public IReadOnlyList<KillShip> Kills { get { lock (_gate) return _kills.ToArray(); } }
        public IReadOnlyList<ShipDelete> Deletes { get { lock (_gate) return _deletes.ToArray(); } }
        public IReadOnlyList<EnterLifepod> Pods { get { lock (_gate) return _pods.ToArray(); } }

        public WireClient() => _t = new TcpTransport(this);

        public void Connect(ushort port, string name) => _t.Join("127.0.0.1", port, name);
        public void Logon(LogonRequest req) => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);

        void ITransportSite.OnPeerJoined(ConnectionId id, string name) { }
        void ITransportSite.OnPeerLeft(ConnectionId id) { }
        void ITransportSite.OnSessionLost() { }
        void ITransportSite.OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
        {
            byte[][] msgs;
            try { msgs = FedPacket.Unpack(data.ToArray()); }
            catch (FedCrcException) { return; }
            lock (_gate)
            {
                foreach (var m in msgs)
                {
                    ushort fmid;
                    try { fmid = FedMessageHeader.Read(m).Fmid; } catch { continue; }
                    switch (fmid)
                    {
                        case LogonAck.Fmid: _ack = LogonAck.Decode(m); break;
                        case SetCluster.Fmid: _set = SetCluster.Decode(m); break;
                        case Eject.Fmid: _ejects.Add(Eject.Decode(m)); break;
                        case KillShip.Fmid: _kills.Add(KillShip.Decode(m)); break;
                        case ShipDelete.Fmid: _deletes.Add(ShipDelete.Decode(m)); break;
                        case EnterLifepod.Fmid: _pods.Add(EnterLifepod.Decode(m)); break;
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    /// <summary>⭐ PRIMARY LIVE PROOF. A real, non-lifepod, peer-owned player
    /// ship with a friendly station present (elsewhere) takes lethal damage
    /// from a cross-side launcher → the P107 in-space eject leaves it drifting
    /// as a Lifepod hull in the same sector (with <see cref="Ship.LifepodTimeToDie"/>
    /// armed, the port of native <c>m_timeToDie</c>). Then, with the oxygen
    /// countdown test-compressed to 1 s (instead of the real 300 s), the
    /// <b>pod's own timer</b> expires and the pod is destroyed — real wire
    /// KILL_SHIP(105)+SHIP_DELETE(20) to the same-sector observer — while the
    /// player respawns via the existing P105 path (ENTER_LIFEPOD(166) to the
    /// owner + docked at a friendly station), with <b>no re-eject</b>. This is
    /// the full AFS flow: eject → 5-minute oxygen → pod explodes → respawn.</summary>
    [Fact]
    public void Drifting_lifepod_oxygen_expiry_destroys_pod_and_respawns_player()
    {
        using var server = new Srv();
        Sector? noStationSector = null;
        var spawnCount = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (sideA, sideB, _, _) = TwoStationOwningSides(mission);
            noStationSector ??= mission.Sectors.First(s => s.Stations.Count == 0);
            var lifepodIds = new HashSet<short> { sideA.Civilization.Lifepod, sideB.Civilization.Lifepod };
            var hull = core.HullTypes.Values
                .Where(h => !lifepodIds.Contains(h.HullId) && !h.HasCapability(DataHullTypeIGC.HabmLifepod))
                .OrderBy(h => h.HullId).First();
            Vector3 pos = spawnCount == 0
                ? new Vector3(3000f, 0f, 0f)       // victim (side A)
                : new Vector3(3000f, 0f, -300f);   // observer (side B)
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = (short)(spawnCount == 0 ? sideA.Id : sideB.Id);
            mission.PlaceShip(ship, noStationSector);
            spawnCount++;
            return (ship, noStationSector);
        };
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;   // sim tick reaches OnUpdate (oxygen check)

        using var victim = new WireClient();
        using var observer = new WireClient();
        victim.Connect(server.BoundPort, "VICTIM"); observer.Connect(server.BoundPort, "OBSERVER");
        victim.Logon(Request("VICTIM")); observer.Logon(Request("OBSERVER"));
        PumpUntil(server, () => victim.ShipAssignment is not null && observer.ShipAssignment is not null, 4000);

        short victimId = victim.ShipAssignment!.Ship.ShipId;
        short observerId = observer.ShipAssignment!.Ship.ShipId;
        Assert.NotEqual(victimId, observerId);

        var mission = server.Missions[0];
        var (_, _, _, _) = TwoStationOwningSides(mission);
        var victimShip = server.Ships.First(s => s.Ship.Id == victimId).Ship;
        var observerShip = server.Ships.First(s => s.Ship.Id == observerId).Ship;
        Assert.False(victimShip.HullType.HasCapability(DataHullTypeIGC.HabmLifepod), "precondition: victim not yet a lifepod");
        var victimSide = mission.Sides.First(s => s.Id == victimShip.SideId!.Value);
        Assert.True(mission.Sectors.SelectMany(s => s.Stations).Any(st => st.SideId == victimSide.Id), "victim's side owns a station (respawn reachable)");

        // Compress the oxygen countdown to 1 s — the P279 test seam. (Real game
        // duration = the loaded core's FloatConstants[31] = 300 s / 5 min.)
        victimShip.LifepodEnduranceOverrideSeconds = 1.0;

        // Lethal cross-side hit → in-space EJECT to a drifting Lifepod.
        Vector3 p1 = victimShip.Position;
        victimShip.ReceiveDamage(0, victimShip.HitPoints * 2f, mission.LastUpdate, p1, Vector3.Zero, observerShip);

        // Phase A: the pod forms and drifts in the SAME sector — timer armed.
        int a = PumpUntil(server, () => victim.Ejects.Any(e => e.ShipId == victimId), 2000);
        Assert.True(a < 2000, "no EJECT within ticks");
        Assert.Null(victimShip.StationId);                                  // drifting, not docked
        Assert.Equal(victimShip.Sector!.Id, noStationSector!.Id);           // stayed in the eject sector
        Assert.Equal(victimSide.Civilization.Lifepod, victimShip.HullType.HullId); // hull swapped to Lifepod
        Assert.True(victimShip.LifepodTimeToDie is not null, "oxygen timer armed on ejection (native m_timeToDie)");
        Assert.NotNull(victimShip.LifepodTimeToDie);
        Assert.Empty(victim.Pods);                                          // not yet respawned — still drifting

        // Phase B: the pod's own oxygen runs out — no incoming damage, just the
        // timer → KillShipEvent → the established death path (destroy + respawn).
        int b = PumpUntil(server, () => victim.Pods.Any(p => p.ShipId == victimId), 4000);
        Assert.True(b < 4000, "oxygen expiry did not respawn the player");

        // The pod was destroyed — KILL_SHIP + SHIP_DELETE reached the same-sector observer…
        var kill = observer.Kills.FirstOrDefault(k => k.ShipId == victimId);
        Assert.NotNull(kill);
        Assert.Equal((short)-1, kill!.IdCredit);                              // launcher NULL — died of oxygen, no killer
        Assert.Contains(observer.Deletes, d => d.ShipId == victimId && d.Sdr == ShipDelete.SdrKilled);
        Assert.Contains(victim.Kills, k => k.ShipId == victimId);             // victim's own connection saw the death too
        // …and the player respawned at a friendly station (existing P105 path).
        Assert.Contains(victim.Pods, p => p.ShipId == victimId);              // ENTER_LIFEPOD(166) → own peer only
        Assert.NotNull(victimShip.StationId);                                 // docked at the friendly station
        Assert.NotEqual(victimShip.Sector!.Id, noStationSector!.Id);          // relocated off the drifting spot
        // No re-eject: an oxygen death on a lifepod is a real death (bLifepod⇒bKilled), not another EJECT.
        Assert.Single(victim.Ejects, e => e.ShipId == victimId);

        Console.WriteLine($"[oxygen] ship {victimId} (side {victimSide.Id}) ejected at sim-t, drifted as Lifepod {victimSide.Civilization.Lifepod}, oxygen expired after 1 s (real 300 s override) -> pod destroyed by (none), respawned docked at station; KILL_SHIP bytes = {BitConverter.ToString(kill!.Encode())}");
    }

    /// <summary>⭐ SCOPE-ITEM-4 PROOF — "being rescued cancels the timer".
    /// In this port rescue <i>docks</i> the pod at a friendly station
    /// (<c>PerformRescue</c> latches <see cref="Ship.StationId"/>), it does not
    /// delete the pod object; the timer is therefore cancelled by the
    /// <c>StationId is null</c> gate in <see cref="Ship.OnUpdate"/>. Exercise
    /// the same gate via the death-respawn path: an already-lifepod ship dies
    /// and becomes a <b>docked</b> Lifepod at a friendly station (timer re-armed
    /// with the short override by the respawn's hull-swap), then we pump well
    /// past that deadline and assert the docked pod does <b>not</b> self-
    /// destruct — no second KILL_SHIP/ENTER_LIFEPOD, still docked. A rescued
    /// pod is in exactly this berthed state, so the cancellation holds for it
    /// too (this is the mechanism; the transport is identical).</summary>
    [Fact]
    public void Docked_lifepod_does_not_self_destruct_after_oxygen_deadline_passes()
    {
        using var server = new Srv();
        Sector? noStationSector = null;
        var spawnCount = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (sideA, sideB, _, _) = TwoStationOwningSides(mission);
            noStationSector ??= mission.Sectors.First(s => s.Stations.Count == 0);
            DataHullTypeIGC hull = core.HullTypes[sideA.Civilization.Lifepod];
            Vector3 pos = spawnCount == 0
                ? new Vector3(3000f, 0f, 0f)
                : new Vector3(3000f, 0f, -300f);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = (short)(spawnCount == 0 ? sideA.Id : sideB.Id);
            mission.PlaceShip(ship, noStationSector);
            spawnCount++;
            return (ship, noStationSector);
        };
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var pod = new WireClient();
        using var see = new WireClient();
        pod.Connect(server.BoundPort, "PODSAFE"); see.Connect(server.BoundPort, "SEE");
        pod.Logon(Request("PODSAFE")); see.Logon(Request("SEE"));
        PumpUntil(server, () => pod.ShipAssignment is not null && see.ShipAssignment is not null, 4000);

        short podId = pod.ShipAssignment!.Ship.ShipId;
        var mission = server.Missions[0];
        var (_, _, _, _) = TwoStationOwningSides(mission);
        var podShip = server.Ships.First(s => s.Ship.Id == podId).Ship;
        Assert.True(podShip.HullType.HasCapability(DataHullTypeIGC.HabmLifepod), "precondition: victim already a lifepod");

        // Compress the oxygen countdown so the re-armed docked deadline passes quickly.
        podShip.LifepodEnduranceOverrideSeconds = 0.5;

        // Already-a-lifepod lethal hit → real death → the P105 respawn turns it
        // into a DOCKED Lifepod at a friendly station (timer re-armed at 0.5 s).
        podShip.ReceiveDamage(0, podShip.HitPoints * 2f, mission.LastUpdate, podShip.Position, Vector3.Zero, null);
        int a = PumpUntil(server, () => pod.Pods.Any(p => p.ShipId == podId), 4000);
        Assert.True(a < 4000, "initial lifepod death did not respawn");
        Assert.NotNull(podShip.StationId);                                   // docked at the base
        Assert.True(podShip.LifepodTimeToDie is not null, "docked respawn still armed a (short) oxygen deadline");
        int initialKills = pod.Kills.Count(k => k.ShipId == podId);
        Assert.Equal(1, initialKills);                                       // the one real death so far

        // Pump far past the 0.5 s deadline (sim ticks at 20 Hz → 100 ticks = 5 s).
        int b = PumpUntil(server, () => false, 100);
        Assert.Equal(100, b);                                                // ran the full long window

        // The docked pod is SAFE: no self-destruct, no second death/respawn.
        Assert.Equal(initialKills, pod.Kills.Count(k => k.ShipId == podId)); // no 2nd KILL_SHIP
        Assert.Single(pod.Pods);                                             // no 2nd ENTER_LIFEPOD
        Assert.NotNull(podShip.StationId);                                   // still berthed
        Assert.True(podShip.HullType.HasCapability(DataHullTypeIGC.HabmLifepod), "still a (safe, docked) lifepod");

        Console.WriteLine($"[oxygen] docked lifepod {podId} stayed berthed through {b} ticks (> oxygen deadline) — timer cancelled by docking; rescue's berth cancels it identically.");
    }
}
