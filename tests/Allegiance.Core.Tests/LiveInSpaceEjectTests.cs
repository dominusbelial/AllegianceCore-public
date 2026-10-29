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
/// P107 verification — the in-space EJECT (drifting lifepod hull), the
/// <c>!bKilled</c> branch of <c>KillShipEvent</c> (<c>FedSrv.CPP:11770-12022</c>),
/// wired at the same seam P103/P105 hooked (<c>ShipKilled</c> →
/// <c>BroadcastKillShip</c>, <c>Server.cs:266</c>). Layers:
///  (1) codec tests pin the bytes of <c>FM_S_EJECT(77)</c> (48 B: header +
///      shipID short + pad + 3×12-B Vectors + 1 cookie byte + 3 trailing pad);
///  (2) the live main proof — a real, non-lifepod, peer-owned player ship with
///      a friendly station present (elsewhere) takes lethal damage from a
///      <b>cross-side</b> launcher: it survives <b>in the same sector</b> (not
///      removed), its hull becomes the side's Lifepod type, and a real
///      <c>EJECT(77)</c> broadcasts to <b>both</b> peers (same-side victim peer
///      <b>and</b> cross-side observer peer in the same sector) — no
///      <c>SHIP_DELETE</c>, no <c>KILL_SHIP</c>, no <c>ENTER_LIFEPOD</c>
///      (proving the sector-wide, cross-side audience and that this branch is
///      mutually exclusive with P103/P105's);
///  (3) an <b>already-a-lifepod</b> ship taking lethal damage again is truly
///      killed (no second EJECT; real SHIP_DELETE + KILL_SHIP + the P105
///      respawn), proving the two branches are disjoint.
/// Like the other live suites this drives a real in-process <see cref="Srv"/>
/// over loopback TCP; the [Collection("LiveNetwork")] tag keeps it sequential.
/// </summary>
[Collection("LiveNetwork")]
public class LiveInSpaceEjectTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: WireClient peers never send POSITIONREQ
        Games =
        [
            new Gm { Name = "Eject Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 880u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8390000000000000000ul, DrmHash = "ejct",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>Pick two DISTINCT sides, both of which own a real station, plus
    /// the first station belonging to <c>a</c>. Guarantees that whichever of the
    /// two spawned peers ends up as the test victim, its side owns a friendly
    /// station (so a wrong route to P105's respawn would be detected). Throws if
    /// the map lacks two station-owning sides. Deterministic.</summary>
    private static (Side a, Side b, DataStationIGC stationA, Sector stationSectorA) TwoStationOwningSides(Mission m)
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

    // ── the new codec ────────────────────────────────────────────────────

    /// <summary><c>FM_S_EJECT</c>(77) round-trips its five fields: shipID short
    /// at offset 4, position/velocity/forward Vectors at offsets 8/20/32, and a
    /// 1-byte Cookie at offset 44 (Messages.h:572-578; Cookie = BYTE,
    /// igc.h:516). Fixed 48 bytes.</summary>
    [Fact]
    public void Eject_message_round_trips()
    {
        var msg = Eject.ForSurvivor(0x0383 /* 899 */,
            new Vector3(1.0f, -2.0f, 3.25f),
            new Vector3(0.5f, 0f, -40f),
            new Vector3(0f, 0f, -1f));
        var d = Eject.Decode(msg.Encode());
        Assert.Equal(Eject.Fmid, d.GetFmid());
        Assert.Equal((short)0x0383, d.ShipId);
        Assert.Equal(1.0f, d.Position.X, 4);
        Assert.Equal(-2.0f, d.Position.Y, 4);
        Assert.Equal(3.25f, d.Position.Z, 4);
        Assert.Equal(-40f, d.Velocity.Z, 4);
        Assert.Equal(0f, d.Forward.X, 4);
        Assert.Equal(-1f, d.Forward.Z, 4);
        Assert.Equal(0, d.Cookie);
    }

    /// <summary>Hand-composed 48-byte buffer matches the encoder exactly,
    /// composed from <c>Messages.h:572-578</c> and the native struct layout
    /// (Vectors 4-aligned → pad after the 2-byte shipID; trailing pad rounds to
    /// the largest-member alignment 4 → 48), not from the encoder.</summary>
    [Fact]
    public void Eject_message_emits_exact_declared_bytes()
    {
        var got = new Eject
        {
            ShipId = 0x0103,                       // 259
            Position = new Vector3(1f, 2f, 3f),
            Velocity = new Vector3(4f, 5f, 6f),
            Forward = new Vector3(0f, 0f, -1f),
            Cookie = 0x42,
        }.Encode();

        Assert.Equal(48, got.Length);
        Assert.Equal(new byte[]
        {
            0x30, 0x00,                      // cbmsg = 48
            0x4d, 0x00,                      // fmid  = 77
            0x03, 0x01,                      // shipID (short, LE) = 259
            0x00, 0x00,                      // pad (align Vector)
            0x00, 0x00, 0x80, 0x3f,          // position.x = 1.0f
            0x00, 0x00, 0x00, 0x40,          // position.y = 2.0f
            0x00, 0x00, 0x40, 0x40,          // position.z = 3.0f
            0x00, 0x00, 0x80, 0x40,          // velocity.x = 4.0f
            0x00, 0x00, 0xa0, 0x40,          // velocity.y = 5.0f
            0x00, 0x00, 0xc0, 0x40,          // velocity.z = 6.0f
            0x00, 0x00, 0x00, 0x00,          // forward.x = 0.0f
            0x00, 0x00, 0x00, 0x00,          // forward.y = 0.0f
            0x00, 0x00, 0x80, 0xbf,          // forward.z = -1.0f
            0x42,                            // cookie
            0x00, 0x00, 0x00,                // trailing pad to 48
        }, got);

        var d = Eject.Decode(got);
        Assert.Equal((short)0x0103, d.ShipId);
        Assert.Equal(1f, d.Position.X, 4);
        Assert.Equal(6f, d.Velocity.Z, 4);
        Assert.Equal(-1f, d.Forward.Z, 4);
        Assert.Equal(0x42, d.Cookie);
    }

    // ── the wire client (records EJECT too) ──────────────────────────────

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

    // ── live proofs ────────────────────────────────────────────────────────

    /// <summary>⭐ A real, <b>non-lifepod</b>, peer-owned player ship with a
    /// friendly station present (in another sector) takes lethal damage from a
    /// <b>cross-side</b> launcher. Assert the in-space eject: the ship survives
    /// <b>in the same sector</b> (not removed, not relocated to any station), its
    /// hull becomes the side's Lifepod type, and a real <c>EJECT(77)</c> goes to
    /// <b>both</b> the same-side victim peer and the cross-side observer peer in
    /// the sector — with <b>no</b> <c>SHIP_DELETE</c>/<c>KILL_SHIP</c>/<c>ENTER_LIFEPOD</c>.
    /// Because a friendly station exists for the victim's side, a mis-route to
    /// P105's respawn would have relocated+docked it — asserting it stays put
    /// proves the branches are genuinely mutually exclusive.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Player_ship_lethal_damage_ejects_in_sector_cross_side_broadcast()
    {
        Require.Artwork();
        using var server = new Srv();
        Sector? noStationSector = null;
        var spawnCount = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (sideA, sideB, _, _) = TwoStationOwningSides(mission);
            noStationSector ??= mission.Sectors.First(s => s.Stations.Count == 0);
            // Pick a NON-lifepod hull: the lowest-HullId hull that is not the
            // lifepod of either side and lacks the lifepod capability bit.
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

        using var victim = new WireClient();
        using var observer = new WireClient();
        victim.Connect(server.BoundPort, "VICTIM"); observer.Connect(server.BoundPort, "OBSERVER");
        victim.Logon(Request("VICTIM")); observer.Logon(Request("OBSERVER"));
        PumpUntil(server, () => victim.ShipAssignment is not null && observer.ShipAssignment is not null, 4000);

        short victimId = victim.ShipAssignment!.Ship.ShipId;
        short observerId = observer.ShipAssignment!.Ship.ShipId;
        Assert.NotEqual(victimId, observerId);

        var mission = server.Missions[0];
        var (_, _, _, _) = TwoStationOwningSides(mission);   // exercised (throws if the map can't do cross-side station-owners)
        var victimShip = server.Ships.First(s => s.Ship.Id == victimId).Ship;
        var observerShip = server.Ships.First(s => s.Ship.Id == observerId).Ship;
        var preHull = victimShip.HullType;
        Assert.False(preHull.HasCapability(DataHullTypeIGC.HabmLifepod), "test precondition: victim is NOT already a lifepod");
        Assert.NotNull(victimShip.SideId);
        Assert.NotNull(observerShip.SideId);
        Assert.NotEqual(victimShip.SideId, observerShip.SideId);   // genuinely cross-side
        var victimSide = mission.Sides.First(s => s.Id == victimShip.SideId!.Value);
        short lifepodId = victimSide.Civilization.Lifepod;
        var lifepodHull = mission.Core.HullTypes[lifepodId];
        // The victim's side owns a real station SOMEWHERE (guaranteed by
        // TwoStationOwningSides), so a mis-route to P105 would have respawned it —
        // proving eject suppresses it.
        Assert.True(mission.Sectors.SelectMany(s => s.Stations).Any(st => st.SideId == victimSide.Id), "victim's side owns a station");
        Assert.True(lifepodHull.HasCapability(DataHullTypeIGC.HabmLifepod), "core lifepod hull must carry c_habmLifepod (0x04) — real-data validation of the bit value");
        var origSectorId = victimShip.Sector!.Id;
        Assert.Equal(origSectorId, observerShip.Sector!.Id);   // both peers in the SAME sector

        // Lethal damage from the CROSS-SIDE observer ship (same sector launcher).
        Vector3 p1 = victimShip.Position;
        victimShip.ReceiveDamage(0, victimShip.HitPoints * 2f, mission.LastUpdate, p1, Vector3.Zero, observerShip);

        // Eject is synchronous inside the kill seam — wait for both peers to get EJECT(77).
        int ticks = PumpUntil(server,
            () => victim.Ejects.Any(e => e.ShipId == victimId) && observer.Ejects.Any(e => e.ShipId == victimId),
            2000);
        Assert.True(ticks < 2000, "no EJECT within ticks");

        // Survivor invariants: still in the SAME sector, hull swapped to Lifepod.
        Assert.Equal(origSectorId, victimShip.Sector!.Id);       // not removed from the sector
        Assert.Equal(lifepodId, victimShip.HullType.HullId);     // hull is the side's Lifepod
        Assert.NotEqual(preHull.HullId, victimShip.HullType.HullId); // actually changed
        Assert.Null(victimShip.StationId);                        // NOT docked/relocated to any station

        // EJECT reached BOTH the same-side victim peer and the cross-side observer.
        var ev = victim.Ejects.First(e => e.ShipId == victimId);
        var eo = observer.Ejects.First(e => e.ShipId == victimId);
        Assert.Equal(p1.X, ev.Position.X, 4);  // position = impact point p1 (FedSrv.CPP:12000)
        Assert.Equal(p1.Y, ev.Position.Y, 4);
        Assert.Equal(p1.Z, ev.Position.Z, 4);
        Assert.Equal(ev.Position, eo.Position);
        Assert.Equal(ev.Velocity, eo.Velocity);
        Assert.Equal(ev.Forward, eo.Forward);
        // Outward burst: final velocity shorter than the (zero) pre-velocity by 100·f.
        Assert.True(ev.Velocity.Length() <= 100.0f + 0.001f, "outward velocity magnitude ≈ 100·|f|");

        // P578 — native sends KILL_SHIP(105) on an EJECT too, with
        // bDeathCredit=false and the credited killer's identity (FedSrv.CPP:
        // 11794-11795, 11806-11808), so observers + the victim learn who forced
        // the eject. The eject branch is still mutually exclusive with the
        // TRUE-DEATH artefacts: no SHIP_DELETE, no respawn, no ENTER_LIFEPOD.
        var ek = victim.Kills.FirstOrDefault(k => k.ShipId == victimId);
        Assert.NotNull(ek);   // the eject KILL_SHIP arrived at the victim
        Assert.False(ek!.BDeathCredit, "an eject's KILL_SHIP must carry bDeathCredit=false (not a death)");
        Assert.Equal(observerId, ek.IdCredit);   // the credited killer is the cross-side observer
        Assert.DoesNotContain(victim.Kills, k => k.ShipId == victimId && k.BDeathCredit);
        Assert.DoesNotContain(observer.Deletes, d => d.ShipId == victimId);
        Assert.DoesNotContain(victim.Deletes, d => d.ShipId == victimId);
        Assert.Empty(victim.Pods);
        Assert.Empty(observer.Pods);

        Console.WriteLine($"[eject] player {victimId} (side {victimSide.Id}) died by {observerId} (side {observerShip.SideId}) — survived as Lifepod {lifepodId} in sector {origSectorId}; EJECT(77) to victim peer + cross-side observer; hull stayed, no SHIP_DELETE/KILL_SHIP/ENTER_LIFEPOD.");
        Console.WriteLine($"[eject] EJECT bytes = {BitConverter.ToString(ev.Encode())}");
    }

    /// <summary>⭐ A ship whose base hull is <b>already</b> a Lifepod taking
    /// lethal damage again is truly killed (not re-ejected): <b>no</b> EJECT,
    /// and a real <c>SHIP_DELETE(20)</c>+<c>KILL_SHIP(105)</c> plus the P105
    /// respawn (<c>ENTER_LIFEPOD(166)</c> to the owner). This is the exact
    /// <c>bLifepod → bKilled</c> route (FedSrv.CPP:11776-11780) — proving the
    /// two branches are disjoint, not overlapping.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Already_a_lifepod_is_truly_killed_not_re_ejected()
    {
        Require.Artwork();
        using var server = new Srv();
        Sector? noStationSector = null;
        var spawnCount = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (sideA, sideB, _, _) = TwoStationOwningSides(mission);
            noStationSector ??= mission.Sectors.First(s => s.Stations.Count == 0);
            // BOTH spawned peers ride their side's LIFEPOD hull — so whichever
            // connection the victim lands on (assignment order races the spawn
            // order, observed live), it is already-a-lifepod and its second death
            // reaches the death/respawn branch, not a re-eject.
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

        using var victim = new WireClient();
        using var observer = new WireClient();
        victim.Connect(server.BoundPort, "POD"); observer.Connect(server.BoundPort, "SEE");
        victim.Logon(Request("POD")); observer.Logon(Request("SEE"));
        PumpUntil(server, () => victim.ShipAssignment is not null && observer.ShipAssignment is not null, 4000);

        short victimId = victim.ShipAssignment!.Ship.ShipId;
        var mission = server.Missions[0];
        var (_, _, _, _) = TwoStationOwningSides(mission);   // exercised; guarantees both sides own a station (respawn possible)
        var victimShip = server.Ships.First(s => s.Ship.Id == victimId).Ship;
        var victimSide = mission.Sides.First(s => s.Id == victimShip.SideId!.Value);
        Assert.True(victimShip.HullType.HasCapability(DataHullTypeIGC.HabmLifepod), "test precondition: victim IS already a lifepod");
        Assert.True(mission.Sectors.SelectMany(s => s.Stations).Any(st => st.SideId == victimSide.Id), "victim's side owns a station (respawn possible)");

        victimShip.ReceiveDamage(0, victimShip.HitPoints * 2f, mission.LastUpdate, victimShip.Position, Vector3.Zero, null);

        int ticks = PumpUntil(server,
            () => victim.Kills.Any(k => k.ShipId == victimId) && observer.Kills.Any(k => k.ShipId == victimId),
            20000);
        Assert.True(ticks < 20000, "second lethal hit did not truly kill the lifepod");
        Assert.True(SpinWait.SpinUntil(() => victim.Deletes.Any(d => d.ShipId == victimId)
            && observer.Deletes.Any(d => d.ShipId == victimId)
            && victim.Pods.Any(p => p.ShipId == victimId), TimeSpan.FromSeconds(30)),
            "both death recipients must decode deletion and the owner must decode respawn");

        // No second EJECT anywhere.
        Assert.DoesNotContain(victim.Ejects, e => e.ShipId == victimId);
        Assert.DoesNotContain(observer.Ejects, e => e.ShipId == victimId);

        // Real death: KILL_SHIP + SHIP_DELETE to both same-sector peers…
        Assert.Contains(victim.Kills, k => k.ShipId == victimId && k.BDeathCredit);
        Assert.Contains(observer.Kills, k => k.ShipId == victimId);
        Assert.Contains(victim.Deletes, d => d.ShipId == victimId && d.Sdr == ShipDelete.SdrKilled);
        Assert.Contains(observer.Deletes, d => d.ShipId == victimId && d.Sdr == ShipDelete.SdrKilled);

        // …and the P105 respawn (docked Lifepod at the friendly station).
        Assert.Contains(victim.Pods, p => p.ShipId == victimId);
        Assert.NotEqual(victimShip.Sector!.Id, noStationSector!.Id);   // relocated out of the old sector
        Assert.NotNull(victimShip.StationId);                           // docked

        Console.WriteLine($"[eject] already-lifepod {victimId} died AGAIN: no re-EJECT, SHIP_DELETE+KILL_SHIP flowed, ENTER_LIFEPOD respawned it — eject and death branches are mutually exclusive.");
    }
}
