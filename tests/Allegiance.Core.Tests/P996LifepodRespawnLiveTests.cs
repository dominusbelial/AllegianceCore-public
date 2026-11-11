using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P996 — the Lifepod respawn loop Domingo hit on the rig (ship 8110): podded,
/// the pod was killed, the player respawned DOCKED as a Lifepod — and every
/// launch press then died instantly of oxygen ("[oxygen] lifepod ship 8110
/// oxygen exhausted at sim-t 1030.7 (deadline 926.25)" 6 ms after "[undock]").
///
/// <para><b>Native rules.</b> The pod's clock (<c>CshipIGC::m_timeToDie</c>,
/// <c>shipIGC.h:2607</c>) is armed ONLY on a hull swap to a lifepod
/// (<c>shipIGC.cpp:1408</c>) and is read ONLY by <c>Update</c>
/// (<c>shipIGC.cpp:361-363</c>) and <c>GetEndurance</c>
/// (<c>shipIGC.h:2473-2491</c>). A berthed ship is not in a cluster at all —
/// <c>SetStation</c>'s <c>s-&gt;AddShip(this)</c> "also sets the ship's cluster to
/// NULL" (<c>shipIGC.h:457</c>, <c>assert (GetCluster() == NULL)</c> at
/// <c>:459</c>) — so a docked pod has no clock and a deadline left over from the
/// previous life can never fire. Relaunching as a pod is allowed natively
/// (<c>PurchaseShipLoadout</c> gates a changed hull only on
/// <c>m_station-&gt;CanBuy</c>, <c>shipIGC.h:996-1000</c>, and a hull swap
/// re-arms the clock, <c>:1019-1020</c> → <c>shipIGC.cpp:1408</c>); the native
/// client simply never asks, because its hangar restores the last real loadout
/// while docked in a pod (<c>appmsg.cpp:2022-2023</c> →
/// <c>RestoreLoadout</c>, <c>clintlib.h:1876-1886</c>).</para>
///
/// <para>This live loopback test reproduces the rig's exact message sequence
/// over TCP: kill the pod → docked Lifepod → send <c>BUY_LOADOUT(lifepod hull,
/// fLaunch)</c> — and pins that (a) the berthed pod carries NO deadline, (b) the
/// pod launch is accepted and gets a FRESH deadline instead of dying instantly,
/// (c) a pod relaunched after a pod death still dies when its fresh oxygen clock
/// runs out, and (d) a ship relaunched into a real hull after a pod death has no
/// deadline at all.</para>
/// </summary>
[Collection("LiveNetwork")]
public class P996LifepodRespawnLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: WireClient peers never send POSITIONREQ
        Games =
        [
            new Gm { Name = "P996 Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x05],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 996u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 6390000000000000996ul, DrmHash = "p996",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Two DISTINCT sides that both own a real station (so the respawn
    /// leg is reachable for whichever peer becomes the victim).</summary>
    private static (Side a, Side b) TwoStationOwningSides(Mission m)
    {
        var owners = m.Sectors.SelectMany(s => s.Stations)
            .Where(st => st.SideId >= 0).Select(st => st.SideId).Distinct().ToList();
        if (owners.Count < 2) throw new InvalidOperationException("map has fewer than two station-owning sides");
        return (m.Sides.First(s => s.Id == owners[0]), m.Sides.First(s => s.Id == owners[1]));
    }

    /// <summary>The loopback peer: records the messages this scenario needs
    /// (EJECT, KILL_SHIP, SHIP_DELETE, ENTER_LIFEPOD) and can send the real
    /// <c>FM_C_BUY_LOADOUT(4)</c> the hangar's Launch button sends.</summary>
    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private SetCluster? _set;
        private readonly List<Eject> _ejects = new();
        private readonly List<KillShip> _kills = new();
        private readonly List<ShipDelete> _deletes = new();
        private readonly List<EnterLifepod> _pods = new();
        private readonly List<BuyLoadoutAck> _acks = new();

        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
        public IReadOnlyList<Eject> Ejects { get { lock (_gate) return _ejects.ToArray(); } }
        public IReadOnlyList<KillShip> Kills { get { lock (_gate) return _kills.ToArray(); } }
        public IReadOnlyList<ShipDelete> Deletes { get { lock (_gate) return _deletes.ToArray(); } }
        public IReadOnlyList<EnterLifepod> Pods { get { lock (_gate) return _pods.ToArray(); } }
        public IReadOnlyList<BuyLoadoutAck> Acks { get { lock (_gate) return _acks.ToArray(); } }

        public WireClient() => _t = new TcpTransport(this);

        public void Connect(ushort port, string name) => _t.Join("127.0.0.1", port, name);
        public void Logon(LogonRequest req) => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);

        /// <summary>The Hangar "Launch"/"Buy" button: <c>BuyLoadout(ship, fLaunch)</c>
        /// (hangarscreen.cpp:145-160) → <c>FM_C_BUY_LOADOUT</c>.</summary>
        public void SendBuyLoadout(short hullId, bool launch, IReadOnlyList<ExpandedPartData>? parts = null) =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[]
            {
                new BuyLoadoutRequest { HullId = hullId, FLaunch = launch, Parts = parts ?? [] }.Encode(),
            }), guaranteed: true);

        public void ResetAcks() { lock (_gate) _acks.Clear(); }

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
                        case SetCluster.Fmid: _set = SetCluster.Decode(m); break;
                        case Eject.Fmid: _ejects.Add(Eject.Decode(m)); break;
                        case KillShip.Fmid: _kills.Add(KillShip.Decode(m)); break;
                        case ShipDelete.Fmid: _deletes.Add(ShipDelete.Decode(m)); break;
                        case EnterLifepod.Fmid: _pods.Add(EnterLifepod.Decode(m)); break;
                        case BuyLoadoutAck.Fmid: _acks.Add(BuyLoadoutAck.Decode(m)); break;
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    /// <summary>Boot a mission whose victim spawns as a real (non-pod) hull in a
    /// station-less sector so the first lethal hit ejects in place.</summary>
    private static (Srv server, WireClient victim, WireClient observer, Ship victimShip, Mission mission) Boot()
    {
        var server = new Srv();
        Sector? noStationSector = null;
        var spawnCount = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (sideA, sideB) = TwoStationOwningSides(mission);
            noStationSector ??= mission.Sectors.First(s => s.Stations.Count == 0);
            var lifepodIds = new HashSet<short> { sideA.Civilization.Lifepod, sideB.Civilization.Lifepod };
            var hull = core.HullTypes.Values
                .Where(h => !lifepodIds.Contains(h.HullId) && !h.HasCapability(DataHullTypeIGC.HabmLifepod))
                .OrderBy(h => h.HullId).First();
            Vector3 pos = spawnCount == 0 ? new Vector3(3000f, 0f, 0f) : new Vector3(3000f, 0f, -300f);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = (short)(spawnCount == 0 ? sideA.Id : sideB.Id);
            mission.PlaceShip(ship, noStationSector);
            spawnCount++;
            return (ship, noStationSector);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        var victim = new WireClient();
        var observer = new WireClient();
        victim.Connect(server.BoundPort, "VICTIM"); observer.Connect(server.BoundPort, "OBSERVER");
        victim.Logon(Request("VICTIM")); observer.Logon(Request("OBSERVER"));
        Assert.True(PumpUntil(server, () => victim.ShipAssignment is not null && observer.ShipAssignment is not null, 4000) < 4000,
            "peers were never seated");
        short victimId = victim.ShipAssignment!.Ship.ShipId;
        var mission = server.Missions[0];
        var victimShip = server.Ships.First(s => s.Ship.Id == victimId).Ship;
        return (server, victim, observer, victimShip, mission);
    }

    /// <summary>⭐ P996 PRIMARY PROOF — the rig's exact loop. A pod death docks a
    /// Lifepod (native FedSrv.CPP:12030-12065); the berthed pod must carry NO
    /// oxygen deadline; the client's <c>BUY_LOADOUT(lifepod, fLaunch)</c> must be
    /// accepted per native (<c>PurchaseShipLoadout</c> tech gate + re-armed clock)
    /// and launch with a FRESH deadline; the fresh clock still kills on time; and a
    /// relaunch into a real hull carries no deadline at all.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Pod_launch_after_pod_death_gets_a_fresh_deadline_instead_of_instant_death()
    {
        Require.Artwork();
        var (server, victim, observer, victimShip, mission) = Boot();
        using var _s = server; using var _v = victim; using var _o = observer;
        var observerShip = server.Ships.First(s => s.Ship.Id == observer.ShipAssignment!.Ship.ShipId).Ship;
        short podHull = mission.Sides.First(s => s.Id == victimShip.SideId!.Value).Civilization.Lifepod;

        // Compress the pod's real 300 s endurance (the P279 seam) so a full
        // oxygen death can be observed inside the test.
        victimShip.LifepodEnduranceOverrideSeconds = 1.0;

        // ── (A) a FRESH eject arms a fresh deadline (P279 behaviour kept) ──────
        // The eject runs synchronously inside ReceiveDamage (KillShipEvent →
        // EjectSurvivor → ChangeHull(lifepod)), so the armed clock is readable
        // before any further tick advances LastUpdate.
        double armedAt = mission.LastUpdate;
        victimShip.ReceiveDamage(0, victimShip.HitPoints * 2f, mission.LastUpdate, victimShip.Position, Vector3.Zero, observerShip);
        Assert.True(PumpUntil(server, () => victim.Ejects.Any(e => e.ShipId == victimShip.Id), 2000) < 2000,
            "no EJECT: the in-space eject path did not run");
        Assert.Null(victimShip.StationId);
        Assert.Equal(podHull, victimShip.HullType.HullId);
        double firstDeadline = Assert.IsType<double>(victimShip.LifepodTimeToDie);
        Assert.InRange(firstDeadline - armedAt, 0.5, 1.5);   // fresh, ~1 s (compressed)

        // ── (B) the pod dies for real → docked Lifepod (FedSrv.CPP:12030-12065)
        victimShip.ReceiveDamage(0, victimShip.HitPoints * 2f, mission.LastUpdate, victimShip.Position, Vector3.Zero, observerShip);
        Assert.True(PumpUntil(server, () => victim.Pods.Any(p => p.ShipId == victimShip.Id), 4000) < 4000,
            "the pod death did not respawn the player as a docked Lifepod");
        Assert.NotNull(victimShip.StationId);
        Assert.Equal(podHull, victimShip.HullType.HullId);
        // ⭐ THE FIX: a berthed pod has no clock (native SetStation takes it out of
        // the cluster, shipIGC.h:457-459 → no Update can reach it).
        Assert.Null(victimShip.LifepodTimeToDie);
        int deathsSoFar = victim.Kills.Count(k => k.ShipId == victimShip.Id && k.BDeathCredit);

        // Pump far past the previous life's (compressed) deadline: the docked pod
        // must stay put, with no second death and no re-respawn.
        Assert.Equal(60, PumpUntil(server, () => false, 60));   // 3 s of sim at 20 Hz
        Assert.Equal(deathsSoFar, victim.Kills.Count(k => k.ShipId == victimShip.Id && k.BDeathCredit));
        Assert.Single(victim.Pods);
        Assert.NotNull(victimShip.StationId);
        Assert.Null(victimShip.LifepodTimeToDie);

        // ── (C) the client asks to launch the LIFEPOD hull (the rig's message) ──
        victim.ResetAcks();
        double sentAt = mission.LastUpdate;
        victim.SendBuyLoadout(podHull, launch: true);
        Assert.True(PumpUntil(server, () => victim.Acks.Any(a => a.HullId == podHull), 2000) < 2000,
            "BUY_LOADOUT(lifepod, fLaunch) got no ACK");
        var podAck = victim.Acks.Last(a => a.HullId == podHull);
        Assert.True(podAck.FLaunch, "native PurchaseShipLoadout allows a lifepod hull: the request must be granted as a LAUNCH, not refused");
        Assert.True(PumpUntil(server, () => victimShip.StationId is null, 2000) < 2000, "the pod never undocked");
        Assert.False(victimShip.Dead);
        Assert.Equal(podHull, victimShip.HullType.HullId);
        // ⭐ A launched pod starts drifting, so it gets a FRESH deadline — and the
        // launch must NOT be an instant oxygen death.
        double freshDeadline = Assert.IsType<double>(victimShip.LifepodTimeToDie);
        Assert.True(freshDeadline > mission.LastUpdate,
            $"the relaunched pod must carry a FUTURE deadline, got {freshDeadline:R} at sim-t {mission.LastUpdate:R}");
        Assert.InRange(freshDeadline - sentAt, 0.0, 2.0);
        Assert.Equal(deathsSoFar, victim.Kills.Count(k => k.ShipId == victimShip.Id && k.BDeathCredit));   // no instant death

        // Pump a further 0.5 s (still inside the fresh 1 s clock): still alive.
        Assert.Equal(10, PumpUntil(server, () => false, 10));
        Assert.Equal(deathsSoFar, victim.Kills.Count(k => k.ShipId == victimShip.Id && k.BDeathCredit));
        Assert.Null(victimShip.StationId);

        // ── (D) the fresh clock still kills the pod on time (P279 parity) ──────
        Assert.True(PumpUntil(server, () => victim.Kills.Any(k => k.ShipId == victimShip.Id && k.BDeathCredit && k.IdCredit == -1), 4000) < 4000,
            "the relaunched pod's fresh oxygen deadline never fired — a pod in space must still die on time");
        Assert.True(PumpUntil(server, () => victim.Pods.Count >= 2, 4000) < 4000, "the oxygen death did not respawn the player");
        Assert.NotNull(victimShip.StationId);
        Assert.Null(victimShip.LifepodTimeToDie);   // berthed again → still no clock

        // ── (E) relaunch into a REAL hull after a pod death: no deadline at all ─
        var realHull = EconomyLoadoutTestSetup.Sell(mission, EconomyLoadoutTestSetup.WeaponHull(mission.Core));
        victim.ResetAcks();
        victim.SendBuyLoadout(realHull.HullId, launch: true);
        Assert.True(PumpUntil(server, () => victim.Acks.Any(a => a.HullId == realHull.HullId), 4000) < 4000,
            "the real hull after a pod death was never granted");
        var realAck = victim.Acks.Last(a => a.HullId == realHull.HullId);
        Assert.True(realAck.FLaunch);
        Assert.True(PumpUntil(server, () => victimShip.StationId is null, 2000) < 2000, "the real hull never launched");
        Assert.Equal(realHull.HullId, victimShip.HullType.HullId);
        Assert.False(victimShip.HullType.HasCapability(DataHullTypeIGC.HabmLifepod));
        Assert.Null(victimShip.LifepodTimeToDie);   // ⭐ no pod clock survives a pod death
        int deathsBefore = victim.Kills.Count(k => k.ShipId == victimShip.Id && k.BDeathCredit);
        Assert.Equal(40, PumpUntil(server, () => false, 40));   // 2 s well past any compressed pod clock
        Assert.Equal(deathsBefore, victim.Kills.Count(k => k.ShipId == victimShip.Id && k.BDeathCredit));

        Console.WriteLine($"[p996] pod death -> docked pod (no deadline) -> BUY_LOADOUT(lifepod, fLaunch) launched with fresh "
            + $"deadline {freshDeadline:R} (sent at {sentAt:R}), died on time, respawned docked (no deadline), then hull "
            + $"{realHull.HullId} launched with LifepodTimeToDie=null after {deathsBefore} deaths");
    }
}
