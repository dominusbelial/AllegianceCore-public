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
/// P140 — the <c>FM_C_PROMOTE(190)</c> / <c>FM_S_PROMOTE(189)</c> server
/// slice: the real role-swap by which a boarded turret gunner is promoted into
/// its carrier's command seat in place — the carrier is demoted into the exact
/// turret mount the gunner vacated (<c>CshipIGC::Promote</c>,
/// <c>shipIGC.cpp:3509-3591</c>), <b>nothing is destroyed</b>.
///
/// Layers, mirroring <see cref="LiveBoardShipTests"/>:
///  (1) codec round-trips live in <see cref="Wire.PromoteWireTests"/>;
///  (2) a pure-unit view of the role-swap proves the full state transfer
///      (hull, loadout, ammo/energy, position/orientation, hull/shield
///      fractions, sibling-gunner transfer, parent demotion to the same turret
///      mount) deterministically on unplaced ships;
///  (3) real loopback-in-process-server live proofs for the three verification
///      scenarios: promote-over-the-wire with the broadcast, the silent no-op
///      for a non-matching mount, and post-promote tick stability.
///
/// The [Collection("LiveNetwork")] tag keeps this suite sequential against the
/// other live suites.
/// </summary>
[Collection("LiveNetwork")]
public class LivePromoteTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // Concrete hulls from PCore001.igc (measured, not guessed — same ids the
    // P108 live suite uses):
    //   792 = a turret-capable gunship/carrier, slots [2,6), price 4000
    //   504 = a 500-credit normal hull (the gunner), slots [2,4)
    private const short CarrierHullId = 792;
    private const short GunnerHullId = 504;

    // ── layer 2: pure-unit role-swap on unplaced ships (deterministic) ──────

    /// <summary>
    /// ⭐ The full role-swap, driven directly on <see cref="Ship.Promote"/>
    /// against <b>unplaced</b> ships — a mission-less setup makes
    /// <see cref="Ship.ReceiveDamage"/>'s armor factor deterministic (the
    /// <c>DamageConstant</c> fallback is exactly 1.0 with no core), so the
    /// hull/shield-fraction copy can be asserted to real sub-1.0 values rather
    /// than trivially-equal defaults. Proves, with precise magnitudes:
    /// the gunner takes the carrier's hull, parts (3 parts, copy preserved),
    /// ammo/energy, position/orientation/velocity, hull fraction, and shield
    /// fraction; every sibling gunner transfers; and the old parent is demoted
    /// into the <b>exact</b> turret slot the promoted gunner vacated.
    /// </summary>
    [Fact]
    public void Promote_transfers_full_role_to_gunner_and_demotes_parent()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var carrierHull = core.HullTypes[CarrierHullId];
        var gunnerHull = core.HullTypes[GunnerHullId];

        var cpos = new Vector3(10f, 20f, 30f);
        var cvel = new Vector3(1f, 2f, 3f);
        var corient = Orientation.Identity; corient.SetForward(new Vector3(0f, 1f, 0f));
        var carrier = new Ship(700, carrierHull, cpos, cvel, corient);
        var gunner1 = new Ship(701, gunnerHull, new Vector3(100f, 100f, 100f), Vector3.Zero, Orientation.Identity);
        var gunner2 = new Ship(702, gunnerHull, new Vector3(200f, 200f, 200f), Vector3.Zero, Orientation.Identity);

        // Equip the carrier: two weapons on turret slots 2 and 3 (the P108
        // slot walk's armed case). The shield is mounted AFTER the hull dent
        // below so the dent hit reaches the hull unabsorbed.
        var weapon = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
        var shield = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Shield);
        carrier.MountPart(weapon, 1, (byte)carrierHull.MaxFixedWeapons, 255);        // slot 2
        carrier.MountPart(weapon, 1, (byte)(carrierHull.MaxFixedWeapons + 1), 249);  // slot 3

        // Give the carrier distinctive ammo/energy, and dent its hull to a
        // known sub-1.0 value (deterministic dtm=1.0 while unplaced; no shield
        // mounted yet so the hit reaches the hull).
        carrier.Ammo = 987;
        carrier.Energy = 456.75f;
        carrier.ReceiveDamage(0, 50f, 0.0, cpos, Vector3.Zero, null);   // dents hull
        float hullFrac = carrier.Fraction;                              // < 1.0
        Assert.True(hullFrac < 1.0f, "carrier hull should be dented");

        // Now mount the shield (charge 1.0) and drain it to a known sub-1.0
        // value — the shield absorbs a small hit entirely (leak 0, hull
        // unchanged) but its charge fraction drops deterministically.
        carrier.MountPart(shield, 1, 4, 255);                           // shield slot 4
        carrier.ReceiveDamage(0, 1f, 0.0, cpos, Vector3.Zero, null);    // absorbed by shield
        float shieldFrac = carrier.ShieldFraction;                      // < 1.0, hull unchanged
        Assert.True(shieldFrac < 1.0f, "carrier shield should be drained");

        // Board two gunners at slots 2 and 3 (mirror P108 PerformBoard occupancy).
        gunner1.ParentShip = carrier; gunner1.TurretId = 2; carrier.AddChildShip(gunner1);
        gunner2.ParentShip = carrier; gunner2.TurretId = 3; carrier.AddChildShip(gunner2);

        int carrierPartCount = carrier.Parts.Count;   // 3
        var oldParentForward = carrier.Orientation.GetForward();

        // ── promote gunner1 (the one at slot 2) ──
        gunner1.Promote();

        // Promoted gunner took the parent's role wholesale.
        Assert.Null(gunner1.ParentShip);
        Assert.Null(gunner1.TurretId);
        Assert.Equal(CarrierHullId, gunner1.HullType.HullId);          // hull swap
        Assert.Equal(carrierPartCount, gunner1.Parts.Count);           // loadout copied (3)
        Assert.Equal((short)987, gunner1.Ammo);
        Assert.Equal(456.75f, gunner1.Energy, 3);
        Assert.Equal(cpos, gunner1.Position);                          // kinematics
        Assert.Equal(oldParentForward, gunner1.Orientation.GetForward());
        Assert.Equal(cvel, gunner1.Velocity);
        Assert.Equal(hullFrac, gunner1.Fraction, 4);                   // hull fraction copied
        Assert.Equal(shieldFrac, gunner1.ShieldFraction, 4);           // shield fraction copied

        // Old parent demoted into the EXACT vacated slot (2).
        Assert.Same(gunner1, carrier.ParentShip);
        Assert.Equal((short)2, carrier.TurretId);
        Assert.Contains(gunner1.ChildShips, s => ReferenceEquals(s, carrier));
        Assert.Empty(carrier.Parts);   // "no parts allowed on a turret"

        // The surviving sibling transferred to the newly-promoted ship.
        Assert.Same(gunner1, gunner2.ParentShip);
        Assert.Equal((short)3, gunner2.TurretId);
        Assert.Contains(gunner1.ChildShips, s => ReferenceEquals(s, gunner2));

        // carrier no longer lists the promoted ship as its child (role inverted).
        Assert.DoesNotContain(carrier.ChildShips, s => ReferenceEquals(s, gunner1));
    }

    /// <summary>Promoting an observer (no turret mount) is invalid — the
    /// handler only ever matches genuine turret gunners, and
    /// <see cref="Ship.Promote"/> guards against a misdirected observer call.
    /// </summary>
    [Fact]
    public void Promote_on_observer_throws()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var carrier = new Ship(710, core.HullTypes[CarrierHullId], Vector3.Zero, Vector3.Zero, Orientation.Identity);
        var observer = new Ship(711, core.HullTypes[GunnerHullId], Vector3.Zero, Vector3.Zero, Orientation.Identity);
        observer.ParentShip = carrier; carrier.AddChildShip(observer);   // TurretId null = observer
        Assert.Throws<InvalidOperationException>(() => observer.Promote());
    }

    // ── layer 3: live loopback-in-process-server proofs ─────────────────────

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Promote Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 992u, DwCookie = 0x992u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8890000000000000000ul, DrmHash = "promote",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    private const int PumpMax = 1200;

    private sealed class PromoteSpawn
    {
        public Ship Carrier = null!, Gunner = null!;
    }

    /// <summary>A two-ship, one-side, both-docked spawn injection: the first
    /// spawned ship is the 792 turret-capable carrier equipped with a real
    /// part in its first turret slot 2; the second is a plain 504 gunner. Both
    /// are placed beside the same friendly station on the same side so the
    /// first tick latches both docked (the P108 live-setup recipe).</summary>
    private static PromoteSpawn InjectSpawn(Srv server)
    {
        var sp = new PromoteSpawn();
        int n = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var hull = core.HullTypes[n == 0 ? CarrierHullId : GunnerHullId];
            var ship = new Ship(id, hull, StationPos(station) + new Vector3(25f + n * 10f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            ship.SideId = station.SideId;
            mission.PlaceShip(ship, sector);

            if (n == 0)
            {
                // Equip the carrier's FIRST turret slot (MaxFixedWeapons=2) so a
                // boarder becomes a real turret gunner at a known mount.
                var weapon = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
                ship.MountPart(weapon, 1, (byte)hull.MaxFixedWeapons, 255);
            }

            if (n == 0) sp.Carrier = ship; else sp.Gunner = ship;
            n++;
            return (ship, sector);
        };
        return sp;
    }

    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private SetCluster? _set;
        private readonly List<Promote> _promotes = new();
        private readonly List<ushort> _allFmids = new();

        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
        public IReadOnlyList<Promote> Promotes { get { lock (_gate) return _promotes.ToArray(); } }
        public IReadOnlyList<ushort> AllFmids { get { lock (_gate) return _allFmids.ToArray(); } }

        public WireClient() => _t = new TcpTransport(this);

        public void Connect(ushort port, string name) => _t.Join("127.0.0.1", port, name);
        public void Logon(LogonRequest req) => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);
        public void SendBoard(short sidParent) =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new BoardShip { SidParent = sidParent }.Encode() }), guaranteed: true);
        public void SendPromote(short mount) =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new PromoteRequest { MountidPromoted = mount }.Encode() }), guaranteed: true);

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
                    _allFmids.Add(fmid);
                    if (fmid == SetCluster.Fmid) _set = SetCluster.Decode(m);
                    else if (fmid == Promote.Fmid) _promotes.Add(Promote.Decode(m));
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    private static (Srv server, WireClient carrier, WireClient gunner, PromoteSpawn sp) Setup(out short carrierId, out short gunnerId)
    {
        var server = new Srv();
        var sp = InjectSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        ushort port = server.BoundPort;

        var a = new WireClient();
        var b = new WireClient();
        a.Connect(port, "CARRIER"); b.Connect(port, "GUNNER");
        a.Logon(Request("CARRIER")); b.Logon(Request("GUNNER"));
        PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);

        WireClient carrier, gunner;
        if (a.ShipAssignment!.Ship.ShipId == sp.Carrier.Id) { carrier = a; gunner = b; }
        else { carrier = b; gunner = a; }
        carrierId = carrier.ShipAssignment!.Ship.ShipId;
        gunnerId = gunner.ShipAssignment!.Ship.ShipId;
        return (server, carrier, gunner, sp);
    }

    private static void WaitDocked(Srv server, params (WireClient _, Ship ship)[] ships)
    {
        int n = PumpUntil(server, () => ships.All(s => s.ship.StationId is not null), 400);
        Assert.True(n < 400, "ships did not dock");
    }

    /// <summary>Board the gunner onto the carrier at its equipped turret slot,
    /// returning the resulting turret mount id (2) once occupancy is live.</summary>
    private static short BoardGunner(Srv server, WireClient carrier, WireClient gunner, short carrierId, Ship carrierShip, Ship gunnerShip)
    {
        gunner.SendBoard(carrierId);
        int n = PumpUntil(server, () => gunnerShip.ParentShip is not null, PumpMax);
        Assert.True(n < PumpMax, "board did not set ParentShip");
        Assert.Same(carrierShip, gunnerShip.ParentShip);
        Assert.NotNull(gunnerShip.TurretId);
        return gunnerShip.TurretId!.Value;
    }

    /// <summary>⭐ Scenario 1: a real commanding ship with a real boarded
    /// gunner at a known turret mount — the carrier promotes that gunner over
    /// the wire. Asserts the gunner's ship now has the former parent's hull,
    /// the former parent is now a child of the promoted ship at the exact same
    /// turret mount, and <c>PROMOTE(189)</c> broadcasts with the correct
    /// <c>shipidPromoted</c> to the sector-wide audience (both peers).</summary>
    [Fact]
    public void Promote_via_wire_broadcasts_and_swaps_roles()
    {
        var (server, carrier, gunner, sp) = Setup(out short cId, out short gId);
        WaitDocked(server, (carrier, sp.Carrier), (gunner, sp.Gunner));

        short slot = BoardGunner(server, carrier, gunner, cId, sp.Carrier, sp.Gunner);

        // The carrier client requests the promote of its own boarded gunner at
        // the known mount.
        carrier.SendPromote(slot);

        // Role swap lands: the gunner is now free (commander) and the old
        // carrier is demoted into the vacated mount.
        int n = PumpUntil(server, () => sp.Gunner.ParentShip is null && sp.Carrier.ParentShip is not null, PumpMax);
        Assert.True(n < PumpMax, "promote did not swap parent/child roles");

        Assert.Null(sp.Gunner.ParentShip);
        Assert.Same(sp.Gunner, sp.Carrier.ParentShip);
        Assert.Equal(slot, sp.Carrier.TurretId!.Value);
        Assert.Contains(sp.Gunner.ChildShips, s => ReferenceEquals(s, sp.Carrier));

        // Hull swap: the promoted gunner now flies the carrier's hull.
        Assert.Equal(CarrierHullId, sp.Gunner.HullType.HullId);

        // PROMOTE(189) broadcast with the correct shipidPromoted, to the
        // sector-wide audience (both peers).
        int seen = PumpUntil(server, () => carrier.Promotes.Any(p => p.ShipidPromoted == gId) && gunner.Promotes.Any(p => p.ShipidPromoted == gId), PumpMax);
        Assert.True(seen < PumpMax, "PROMOTE(189) not seen on both peers");
        Assert.Contains(carrier.Promotes, p => p.ShipidPromoted == gId);
        Assert.Contains(gunner.Promotes, p => p.ShipidPromoted == gId);

        Console.WriteLine($"PROMOTE-LIVE: carrier {cId} promoted gunner {gId} at mount {slot} -> PROMOTE(189) shipidPromoted={gId} on both peers; carrier demoted to slot {slot}");
    }

    /// <summary>⭐ Scenario 2: a promote request for a turret mount that
    /// doesn't match any real boarded child is a silent no-op — no state
    /// change, no crash, no broadcast.</summary>
    [Fact]
    public void Promote_nonmatching_mount_is_silent_noop()
    {
        var (server, carrier, gunner, sp) = Setup(out short cId, out short gId);
        WaitDocked(server, (carrier, sp.Carrier), (gunner, sp.Gunner));
        short slot = BoardGunner(server, carrier, gunner, cId, sp.Carrier, sp.Gunner);

        // A mount that matches no real child (77) — and also the observer case
        // is unreachable here since the gunner is a real turret occupant.
        carrier.SendPromote(77);
        int n = PumpUntil(server, () => false, 60);   // let any (wrong) broadcast surface

        Assert.Null(sp.Carrier.ParentShip);                    // carrier still commander
        Assert.Same(sp.Carrier, sp.Gunner.ParentShip);         // gunner still boarded
        Assert.Equal(slot, sp.Gunner.TurretId!.Value);         // mount unchanged
        Assert.Empty(carrier.Promotes);                        // no broadcast to the requester
        Assert.Empty(gunner.Promotes);                         // no broadcast to the sector
        Assert.DoesNotContain(carrier.AllFmids, f => f == Promote.Fmid);
        Assert.DoesNotContain(gunner.AllFmids, f => f == Promote.Fmid);

        Console.WriteLine($"PROMOTE-NOOP: mount 77 matched no child on {cId} -> silent no-op (no state change, no PROMOTE(189))");
    }

    /// <summary>⭐ Scenario 3: after a successful promote, pumping further
    /// ticks leaves the new structure stable — no runaway state, no crash, the
    /// role inversion and demotion persist.</summary>
    [Fact]
    public void Promote_structure_stable_across_ticks()
    {
        var (server, carrier, gunner, sp) = Setup(out short cId, out short gId);
        WaitDocked(server, (carrier, sp.Carrier), (gunner, sp.Gunner));
        short slot = BoardGunner(server, carrier, gunner, cId, sp.Carrier, sp.Gunner);

        carrier.SendPromote(slot);
        int n = PumpUntil(server, () => sp.Gunner.ParentShip is null && sp.Carrier.ParentShip is not null, PumpMax);
        Assert.True(n < PumpMax, "promote did not swap roles");

        // Pump well past the transition — structure must persist untouched.
        PumpUntil(server, () => false, 150);

        Assert.Null(sp.Gunner.ParentShip);
        Assert.Same(sp.Gunner, sp.Carrier.ParentShip);
        Assert.Equal(slot, sp.Carrier.TurretId!.Value);
        Assert.Contains(sp.Gunner.ChildShips, s => ReferenceEquals(s, sp.Carrier));
        Assert.DoesNotContain(sp.Carrier.ChildShips, s => ReferenceEquals(s, sp.Gunner));

        Console.WriteLine($"PROMOTE-STABLE: after {n}+150 ticks, {gId} remains commander, {cId} demoted at mount {slot}");
    }
}
