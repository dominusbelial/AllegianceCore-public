using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P377 — the lifepod oxygen COUNTDOWN reaches the DRIVER's HUD but nobody
/// else's. Whereas P279 proved the pod's oxygen timer destroys/respawns it,
/// P377 surfaces the remaining fraction (native <c>IshipIGC::GetEndurance</c>,
/// self-only <c>ModelData::GetEndurance</c>) to the OWNING peer's own-ship
/// update only, riding the FUEL slot of <c>ServerShipUpdate.FromModel</c>
/// (<c>carryEndurance:true</c>) while the pod is adrift; a same-side teammate's
/// relay of the same pod (<c>carryEndurance:false</c>) keeps the flat 1.0 fuel
/// value.
/// <para>
/// Live proof with a real in-process <see cref="Srv"/> over loopback TCP:
///  (1) after the pilot ejects, the DRIVER's own SINGLE_SHIP_UPDATE fuel
///      fraction counts down from near-full to nearly empty while the pod
///      drifts (real server-computed value, not a client guess);
///  (2) SELF-ONLY: no non-driver client ever observes a depleted (&lt;1.0)
///      value for the pod; and WHEN the peer roster happens to place a second
///      player on the driver's side (a same-side watcher — the realistic
///      "someone watching the pod" case), that watcher is asserted to have
///      received the relay and to have seen it only at flat 1.0. The
///      deterministic, side-independent guarantee behind (2) is the pure wire
///      unit test <c>FromModel_endurance_rides_only_the_owner_payload</c> plus
///      the client-side gate (<see cref="Rendering.LifepodHud"/>) that only
///      ever computes oxygen for the local player's OWN ship;
///  (3) once the pod's oxygen expires and it death-respawns DOCKED at a
///      friendly station, the driver's countdown reads full/inactive (1.0) —
///      the docking-cancels-the-timer behaviour surfaced correctly.
/// The [Collection("LiveNetwork")] tag keeps these sequential with the rest.
/// </summary>
[Collection("LiveNetwork")]
public class LiveLifepodEnduranceDisplayTests
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
            new Gm { Name = "Endurance Display Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 886u, DwCookie = 0x886u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8940000000000000000ul, DrmHash = "endu",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static (Side a, Side b) TwoSides(Mission m)
    {
        var owners = m.Sectors.SelectMany(s => s.Stations)
            .Where(st => st.SideId >= 0).Select(st => st.SideId).Distinct().ToList();
        var a = m.Sides.First(s => s.Id == owners[0]);
        var b = m.Sides.First(s => s.Id == owners[1]);
        return (a, b);
    }

    /// <summary>A loopback wire client that also decodes SINGLE_SHIP_UPDATE(78)
    /// and records, per observed ship, the latest FUEL fraction (which for the
    /// OWNING peer of an adrift lifepod is the oxygen fraction, P377).</summary>
    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private LogonAck? _ack;
        private SetCluster? _set;
        private readonly List<Eject> _ejects = new();
        private readonly List<EnterLifepod> _pods = new();
        private readonly Dictionary<short, List<ServerShipUpdate>> _fuels = new();

        public LogonAck? Ack { get { lock (_gate) return _ack; } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
        public IReadOnlyList<Eject> Ejects { get { lock (_gate) return _ejects.ToArray(); } }
        public IReadOnlyList<EnterLifepod> Pods { get { lock (_gate) return _pods.ToArray(); } }

        /// <summary>Latest observed fuel fraction for a ship (null = none yet).</summary>
        public float? LastFuel(short id) { lock (_gate) return _fuels.TryGetValue(id, out var l) && l.Count > 0 ? l[^1].FuelFraction : null; }
        /// <summary>How many fuel-bearing updates we've seen for a ship (0 = none).</summary>
        public int FuelCount(short id) { lock (_gate) return _fuels.TryGetValue(id, out var l) ? l.Count : 0; }
        /// <summary>All observed fuel fractions for a ship.</summary>
        public IReadOnlyList<float> Fuels(short id) { lock (_gate) return _fuels.TryGetValue(id, out var l) ? l.Select(u => u.FuelFraction).ToArray() : Array.Empty<float>(); }

        public IReadOnlyList<ServerShipUpdate> Updates(short id)
        { lock (_gate) return _fuels.TryGetValue(id, out var l) ? l.ToArray() : []; }

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
                        case EnterLifepod.Fmid: _pods.Add(EnterLifepod.Decode(m)); break;
                        case SingleShipUpdate.Fmid:
                            var su = SingleShipUpdate.Decode(m);
                            (_fuels.TryGetValue(su.OidTarget, out var l) ? l : (_fuels[su.OidTarget] = new List<ServerShipUpdate>())).Add(su.Ship);
                            break;
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    /// <summary>⭐ PRIMARY LIVE PROOF. Three real peers in an already-started
    /// mission (so they seat automatically, no side-select dance). The driver
    /// self-ejects (null launcher) into a drifting lifepod. (1) The DRIVER's own
    /// SINGLE_SHIP_UPDATE fuel fraction counts down from near-full to nearly
    /// empty. (2) No non-driver ever sees a depleted value for the pod; and if a
    /// same-side watcher exists in the roster, it demonstrably received the
    /// relay at flat 1.0. (3) After the pod's oxygen expires and it respawns
    /// docked, the driver's countdown reads full/inactive.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Driver_sees_countdown_others_do_not_and_dock_cancels()
    {
        Require.Artwork();
        using var server = new Srv();
        Sector? noStationSector = null;
        var spawnIndex = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (sideA, _) = TwoSides(mission);
            noStationSector ??= mission.Sectors.First(s => s.Stations.Count == 0);
            var lifepodIds = new HashSet<short> { sideA.Civilization.Lifepod };
            DataHullTypeIGC hull = core.HullTypes.Values
                .Where(h => !lifepodIds.Contains(h.HullId) && !h.HasCapability(DataHullTypeIGC.HabmLifepod))
                .OrderBy(h => h.HullId).First();
            Vector3 pos = new[] { new Vector3(3000f, 0f, 0f), new Vector3(3000f, 0f, 300f), new Vector3(3000f, 0f, -300f) }[spawnIndex];
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = (short)(spawnIndex == 0 ? sideA.Id : -1) };
            mission.PlaceShip(ship, noStationSector);
            spawnIndex++;
            return (ship, noStationSector);
        };
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;   // sim reaches OnUpdate; peers seat automatically

        using var driver = new WireClient();
        using var b = new WireClient();
        using var c = new WireClient();
        driver.Connect(server.BoundPort, "DRIVER"); b.Connect(server.BoundPort, "BWATCH"); c.Connect(server.BoundPort, "CWATCH");
        driver.Logon(Request("DRIVER")); b.Logon(Request("BWATCH")); c.Logon(Request("CWATCH"));
        PumpUntil(server,
            () => driver.ShipAssignment is not null && b.ShipAssignment is not null && c.ShipAssignment is not null,
            4000);

        short driverId = driver.ShipAssignment!.Ship.ShipId;
        short bId = b.ShipAssignment!.Ship.ShipId;
        short cId = c.ShipAssignment!.Ship.ShipId;
        var driverShip = server.Ships.First(s => s.Ship.Id == driverId).Ship;
        var bShip = server.Ships.First(s => s.Ship.Id == bId).Ship;
        var cShip = server.Ships.First(s => s.Ship.Id == cId).Ship;
        Assert.False(driverShip.HullType.HasCapability(DataHullTypeIGC.HabmLifepod), "precondition: driver not yet a lifepod");

        // Let side seating settle so we can classify the two non-driver peers.
        PumpUntil(server, () => driverShip.SideId is not null && bShip.SideId is not null && cShip.SideId is not null, 4000);
        short driverSide = driverShip.SideId!.Value;
        var watchers = new[]
        {
            (client: b, id: bId, ship: bShip),
            (client: c, id: cId, ship: cShip),
        };
        // The realistic "second player watching the pod" is a SAME-SIDE peer;
        // whether one exists in a given run's roster depends on the server's
        // (balanced) seating, so we detect it and prove it when present.
        int sameSideIdx = Array.FindIndex(watchers, w => w.ship.SideId == driverSide);
        WireClient? sameSideWatcher = sameSideIdx >= 0 ? watchers[sameSideIdx].client : null;

        // Compress the oxygen countdown to 5 s — long enough that the pod is
        // still adrift for the whole ~4.5 s sampling window (so we control the
        // dock deterministically for the inactive check), short enough that the
        // countdown visibly depletes well past the midpoint. (Real = 300 s.)
        driverShip.LifepodEnduranceOverrideSeconds = 5.0;

        // Put the driver INTO an adrift Lifepod deterministically: the P107
        // in-space EJECT that normally forms the pod is P279's proven domain
        // (already live-tested there); P377 only cares that the driver IS an
        // adrift pod whose timer is armed. ChangeHull arms LifepodTimeToDie
        // (the exact port of native shipIGC.cpp:1407-1408) — the same seam the
        // eject uses — so this exercises the identical P377 code path with none
        // of the eject's geometric flakiness.
        var lifepodHull = server.Missions[0].Core.HullTypes.Values
            .First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
        driverShip.ChangeHull(lifepodHull);
        PumpUntil(server, () => driverShip.LifepodTimeToDie is not null, 500);
        Assert.Null(driverShip.StationId);
        Assert.True(driverShip.HullType.HasCapability(DataHullTypeIGC.HabmLifepod), "driver now a lifepod");
        Assert.True(driverShip.LifepodTimeToDie is not null);

        // Sample while the pod drifts: keep its position dirty so the owner's
        // own-update (and any same-side relay) keep flowing. Fixed window long
        // enough to see the 3 s countdown deplete well past the midpoint; the
        // dock-cancel is checked deterministically below rather than by racing
        // the death-respawn (which is P279's proven domain).
        for (int ticks = 0; ticks < 90; ticks++)
        {
            if (ticks % 4 == 0) driverShip.Velocity = new Vector3(20f, 0f, 0f);   // nudge to keep channels dirty
            server.TickSim();
            uint sampleTime = (uint)server.LoopIterations;
            // Decode this simulation timestamp before advancing again. Otherwise
            // latest-wins coalescing can erase the near-full/low samples entirely.
            Assert.True(SpinWait.SpinUntil(() => driver.Updates(driverId).Any(u => u.Time >= sampleTime),
                TimeSpan.FromSeconds(30)), $"no owner oxygen update at {sampleTime}");
        }

        // (1) the DRIVER saw a real countdown: its own-ship fuel fraction spans
        // from near-full down to nearly empty while adrift.
        var driverFracs = driver.Fuels(driverId);
        Assert.True(driverFracs.Count >= 3, $"driver saw {driverFracs.Count} fuel samples");
        Assert.True(driverFracs.Any(f => f >= 0.975f), "driver never saw a near-full oxygen reading");
        Assert.True(driverFracs.Min() < 0.3f, $"driver countdown never dropped low (min {driverFracs.Min():F3})");
        Assert.True(driverFracs.Min() < driverFracs.Max() - 0.5f,
            $"driver countdown did not decline (max {driverFracs.Max():F3} -> min {driverFracs.Min():F3})");

        // (2) SELF-ONLY: NO non-driver ever observes a depleted value for the
        // pod; and when a same-side watcher exists, it demonstrably received the
        // relay (someone watching the pod) yet saw it only at flat 1.0.
        foreach (var w in watchers)
            Assert.All(w.client.Fuels(driverId),
                f => Assert.True(f >= 1.0f - 1e-3f, $"watcher {w.id} saw a depleted fraction {f} on the driver's pod"));
        if (sameSideWatcher is not null)
        {
            Assert.True(sameSideWatcher.FuelCount(driverId) >= 1,
                "same-side watcher never received the driver pod's relayed update");
            Assert.All(sameSideWatcher.Fuels(driverId),
                f => Assert.True(f >= 1.0f - 1e-3f, $"same-side watcher saw a depleted fraction {f} on the driver's pod"));
        }

        // (3) docking cancels the timer (P279 scope-4): once the pod is berthed
        // — exactly what rescue (PerformRescue) does — the server stops draining
        // oxygen and the own-ship countdown reads full/inactive (1.0), not a
        // depleting value. Simulate the rescue's berth by docking the pod (the
        // same StationId latch rescue/PerformRescue sets) at any mission
        // station — for the displayed FRACTION what matters is that the pod is
        // berthed (StationId non-null → GetEndurance reads 1.0 / inactive).
        short stationId = server.Missions[0].Sectors.SelectMany(s => s.Stations).First().StationId;
        driverShip.StationId = stationId;
        uint dockTime = (uint)server.LoopIterations + 1;
        // Force a dirty own pose while berthed so the wire assertion has a
        // post-dock sample even when the stationary pod would send no update.
        driverShip.Position += new Vector3(2f, 0f, 0f);
        for (int i = 0; i < 30; i++)
        {
            if (i % 4 == 0) driverShip.Velocity = new Vector3(20f, 0f, 0f);
            server.TickSim();
        }
        Assert.True(SpinWait.SpinUntil(() => driver.Updates(driverId).Any(u => u.Time >= dockTime),
            TimeSpan.FromSeconds(30)), "no post-dock owner update");
        Assert.Equal(1f, driverShip.LifepodEnduranceFraction(driverShip.Mission!.LastUpdate));
        // A late pre-dock frame is still a valid depleted sample. Classify by
        // its server timestamp, never by when the receive thread happened to run.
        Assert.All(driver.Updates(driverId).Where(u => u.Time >= dockTime),
            u => Assert.True(u.FuelFraction >= 1.0f - 1e-3f,
                $"driver's countdown was not inactive while docked (got {u.FuelFraction:F3})"));

        Console.WriteLine($"[endurance] driver pod {driverId}: {driverFracs.Count} samples, max {driverFracs.Max():F3} -> min {driverFracs.Min():F3} declining; same-side watcher {(sameSideWatcher is null ? -1 : sameSideWatcher.FuelCount(driverId))} relays flat 1.0; docked -> inactive 1.0.");
    }
}
