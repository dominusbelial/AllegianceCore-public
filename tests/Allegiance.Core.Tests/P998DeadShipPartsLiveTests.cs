using System.Reflection;
using Allegiance.ClientCore.State;
using Allegiance.Core;
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
/// P998 — the LOOPBACK WIRE proof that a dying ship's parts die with it, on
/// every death path, exactly as native's <c>KillShipEvent</c> does
/// (<c>FedSrv/FedSrv.CPP:11686-11696</c> one treasure roll per part then
/// <c>p-&gt;Terminate()</c>; ammo/fuel zeroed at <c>:11698-11728</c>; only then the
/// eject/restart hull swap at <c>:11942</c>/<c>:12052</c>, which is why
/// <c>Igc/shipIGC.cpp:1377</c> can assert the part list is empty).
///
/// <para>Real TCP peers (a victim + a same-side teammate) against a real
/// in-process server; the loadout is bought through the real
/// <c>FM_C_BUY_LOADOUT(4)</c> path so both the owner's and the teammate's
/// <see cref="ClientWorld"/> really hold the dead ship's parts before the death —
/// which is what makes the post-death "no parts" assertions meaningful. The
/// teammates learn it from the native <c>LOADOUT_CHANGE(96)</c> broadcast that
/// follows the whole-loadout mutation (this port's stand-in for native's
/// cluster-entry <c>QueueLoadoutChange</c>, <c>FedSrv/fsship.cpp:511-542</c> +
/// the clients' own EJECT/ENTER_LIFEPOD part termination,
/// <c>clintlib/appmsg.cpp:193-201,1973-1981</c>).</para>
///
/// <para>Second half: the P411 loot rolls still see the intact loadout, ammo and
/// fuel (a seeded run whose part/ammo/fuel treasure counts would collapse to 0
/// if the strip ever ran before the rolls), and a root ship that dies for real
/// (no owning peer → <c>FinalizeDeadShip</c>) is stripped too, exactly like
/// native's unconditional root-ship terminate block.</para>
/// </summary>
[Collection("LiveNetwork")]
public class P998DeadShipPartsLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int? seed = null) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: WireClient peers never send POSITIONREQ themselves
        BenchmarkSeed = seed,
        // Test-only fixture (same shape the P699/P996 pod proofs use): all tech +
        // a funded wallet, so the hangar purchase runs the real pricing/debit path.
        LiveSettings = new() { TestUnlock = new() { Enabled = true, Money = 1_000_000 } },
        Games =
        [
            new Gm { Name = "P998 Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], MaxTeamImbalance = 0x7fff },
        ],
    };

    private static LogonRequest Request(string name, uint time = 998u) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x07],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = time, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 6390000000000000998ul, DrmHash = "p998",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A real TCP peer that feeds every decoded message into a real
    /// <see cref="ClientWorld"/> — the same view the MonoGame client has.</summary>
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
        private readonly List<Allegiance.Core.Wire.LoadoutChange> _loadouts = new();

        public readonly ClientWorld World = new();

        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
        public IReadOnlyList<Eject> Ejects { get { lock (_gate) return _ejects.ToArray(); } }
        public IReadOnlyList<KillShip> Kills { get { lock (_gate) return _kills.ToArray(); } }
        public IReadOnlyList<ShipDelete> Deletes { get { lock (_gate) return _deletes.ToArray(); } }
        public IReadOnlyList<EnterLifepod> Pods { get { lock (_gate) return _pods.ToArray(); } }
        public IReadOnlyList<BuyLoadoutAck> Acks { get { lock (_gate) return _acks.ToArray(); } }
        public IReadOnlyList<Allegiance.Core.Wire.LoadoutChange> LoadoutChanges { get { lock (_gate) return _loadouts.ToArray(); } }

        public WireClient() => _t = new TcpTransport(this);

        public void Connect(ushort port, string name, short wantSide)
        {
            _t.Join("127.0.0.1", port, name);
            _wantSide = wantSide;
        }

        private short _wantSide = -1;

        public void Logon(LogonRequest req) =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);

        /// <summary>The hangar Buy/Launch button: <c>FM_C_BUY_LOADOUT(4)</c>
        /// (<c>BuyLoadout(ship, fLaunch)</c>, hangarscreen.cpp:145-160).</summary>
        public void SendBuyLoadout(short hullId, bool launch, IReadOnlyList<ExpandedPartData> parts) =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[]
            {
                new BuyLoadoutRequest { HullId = hullId, FLaunch = launch, Parts = parts }.Encode(),
            }), guaranteed: true);

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
                    // A codec/apply failure must only drop THAT message (the
                    // ConnectionSession convention) — an exception escaping here
                    // is an "invalid frame" session loss in the TCP read loop.
                    try { ApplyMessage(m); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[p998] client decode/apply failed: {ex.GetType().Name} {ex.Message}");
                    }
                }
            }
        }

        private void ApplyMessage(byte[] m)
        {
            ushort fmid;
            try { fmid = FedMessageHeader.Read(m).Fmid; }
            catch { return; }
            switch (fmid)
            {
                case LogonAck.Fmid:
                    var ack = LogonAck.Decode(m);
                    if (ack.FValidated)
                        _t.Send(ConnectionId.Server,
                            FedPacket.Pack(new PositionReq { SideId = _wantSide }.Encode()), guaranteed: true);
                    break;
                case SetCluster.Fmid:
                    _set = SetCluster.Decode(m);
                    World.Apply(_set);
                    break;
                case ShipStatusMessage.Fmid:
                    World.Apply(ShipStatusMessage.Decode(m));
                    break;
                case ExportShip.Fmid when m.Length >= 12 && BitConverter.ToInt32(m.AsSpan(8, 4)) == ExportShip.OtShip:
                    // FM_S_EXPORT(26) is the generic object-export fmid (clusters,
                    // stations, warps, asteroids ride it too) — branch on objecttype
                    // like ConnectionSession/Bot do.
                    World.Apply(ExportShip.Decode(m));
                    break;
                case BuyLoadoutAck.Fmid:
                    var bought = BuyLoadoutAck.Decode(m);
                    _acks.Add(bought);
                    World.Apply(bought);
                    break;
                case Allegiance.Core.Wire.LoadoutChange.Fmid:
                    var lc = Allegiance.Core.Wire.LoadoutChange.Decode(m);
                    _loadouts.Add(lc);
                    World.Apply(lc);
                    break;
                case Eject.Fmid:
                    var eject = Eject.Decode(m);
                    _ejects.Add(eject);
                    World.Apply(eject);
                    break;
                case KillShip.Fmid:
                    var kill = KillShip.Decode(m);
                    _kills.Add(kill);
                    World.Apply(kill);
                    break;
                case ShipDelete.Fmid:
                    var del = ShipDelete.Decode(m);
                    _deletes.Add(del);
                    World.Apply(del);
                    break;
                case EnterLifepod.Fmid:
                    var pod = EnterLifepod.Decode(m);
                    _pods.Add(pod);
                    World.Apply(pod);
                    break;
                case DockedNotify.Fmid:
                    World.Apply(DockedNotify.Decode(m));
                    break;
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    /// <summary>The bought loadout: a realized gun, a shield, an afterburner and an
    /// ammo pack in the cargo hold (native cargo parts are parts too,
    /// <c>Igc/shipIGC.cpp:1516-1549</c>) — sized/selected from the loaded core.</summary>
    private static (DataHullTypeIGC hull, short hullId, List<ExpandedPartData> parts) BuyableLoadout(
        Mission mission, StaticCore core)
    {
        var hull = EconomyLoadoutTestSetup.Sell(mission, EconomyLoadoutTestSetup.WeaponHull(core));

        var gun = core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon && hull.CanMountPart(p, 0))
            .OrderBy(p => p.PartId).First();
        var parts = new List<ExpandedPartData>
        {
            new(gun.PartId, 40, 0, ExpandedPartData.FullyMounted),
        };
        var shield = core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Shield && hull.CanMountPart(p, 0))
            .OrderBy(p => p.PartId).Cast<DataPartTypeIGC?>().FirstOrDefault();
        if (shield is { } sh) parts.Add(new(sh.PartId, 1, 0, ExpandedPartData.FullyMounted));
        var booster = core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Afterburner && hull.CanMountPart(p, 0))
            .OrderBy(p => p.PartId).Cast<DataPartTypeIGC?>().FirstOrDefault();
        if (booster is { } bo) parts.Add(new(bo.PartId, 1, 0, ExpandedPartData.FullyMounted));
        var pack = core.PartTypes.Values
            .First(p => p.Type is DataPartTypeIGC.Pack pk && pk.PackType == 0);
        parts.Add(new(pack.PartId, 3, 0xFF, ExpandedPartData.FullyMounted));   // cargo mount -1
        return (EconomyLoadoutTestSetup.Sell(mission, hull), hull.HullId, parts);
    }

    /// <summary>An in-process server with two same-side peers spawned DOCKED at
    /// the same friendly garrison — the victim and a teammate.</summary>
    private static (Srv server, WireClient victim, WireClient mate, Ship victimShip, Ship mateShip,
        Mission mission, StaticCore core, short sideId) Boot()
    {
        var server = new Srv { SpawnInitialMinersAtBoot = false };
        DataHullTypeIGC? spawnHull = null;
        server.ShipSpawner = (core, mission, id) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            spawnHull ??= EconomyLoadoutTestSetup.Sell(mission, EconomyLoadoutTestSetup.WeaponHull(core));
            var pos = new Vector3(station.Position[0] + 30f, station.Position[1], station.Position[2]);
            var ship = new Ship(id, spawnHull.Value, pos, Vector3.Zero, Orientation.Identity)
            {
                SideId = station.SideId,
                StationId = station.StationId,
            };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var firstMission = server.Missions[0];
        var core0 = firstMission.Core;
        // Whichever side owns the first real station — where the spawner parks both
        // peers, and the side both peers explicitly request (so they are TEAMMATES).
        short spawnSide = firstMission.Sectors.SelectMany(s => s.Stations)
            .First(st => st.SideId >= 0).SideId;

        var victim = new WireClient();
        var mate = new WireClient();
        victim.Connect(server.BoundPort, "VICTIM", spawnSide);
        mate.Connect(server.BoundPort, "MATE", spawnSide);
        victim.Logon(Request("VICTIM"));
        mate.Logon(Request("MATE"));
        Assert.True(PumpUntil(server, () => victim.ShipAssignment is not null && mate.ShipAssignment is not null, 8000) < 8000,
            "peers were never seated");
        short victimId = victim.ShipAssignment!.Ship.ShipId;
        short mateId = mate.ShipAssignment!.Ship.ShipId;
        var victimShip = server.Ships.First(s => s.Ship.Id == victimId).Ship;
        var mateShip = server.Ships.First(s => s.Ship.Id == mateId).Ship;
        Assert.Equal(victimShip.SideId, mateShip.SideId);   // a real teammate

        // The client wires this resolver on connect (Game1.Lifecycle); do the same
        // so the world can classify the pod hull like the native client does.
        Func<short, short?> resolver = side => firstMission.Sides.FirstOrDefault(s => s.Id == side)?.Civilization.Lifepod;
        victim.World.LifepodHullResolver = resolver;
        mate.World.LifepodHullResolver = resolver;

        return (server, victim, mate, victimShip, mateShip, firstMission, core0, spawnSide);
    }

    /// <summary>⭐ P998 PRIMARY PROOF — kill a fully loaded fighter, eject, and check
    /// the pod is BARE on the server and in BOTH ClientWorlds; then kill the pod and
    /// check the docked Lifepod is bare too, with the P996 saved loadout intact so
    /// the hangar can still launch the real ship.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_killed_fighter_leaves_a_bare_pod_on_the_server_and_in_every_client_world()
    {
        Require.Artwork();
        var (server, victim, mate, victimShip, mateShip, mission, core, sideId) = Boot();
        using var _s = server; using var _v = victim; using var _m = mate;
        short victimId = victimShip.Id;
        short podHull = mission.Sides.First(s => s.Id == sideId).Civilization.Lifepod;

        // ── the victim really flies a FULL loadout (bought over the wire) ─────
        var (hull, hullId, parts) = BuyableLoadout(mission, core);
        Assert.True(parts.Count >= 3, "the fixture must buy gun + shield + booster + cargo pack");
        victim.SendBuyLoadout(hullId, launch: true, parts);
        Assert.True(PumpUntil(server, () => victimShip.StationId is null
            && victim.Acks.Any(a => a.FLaunch && a.HullId == hullId), 8000) < 8000,
            "the loaded fighter never launched");
        Assert.True(victimShip.Parts.Count >= 3,
            $"the server ship must carry the bought loadout, has {victimShip.Parts.Count}");
        Assert.NotNull(victimShip.CargoHold[0]);

        // Both worlds hold the parts BEFORE the death — otherwise "the pod has no
        // parts" would be trivially true. The owner learns them from its
        // BUY_LOADOUT_ACK, the teammate from the LOADOUT_CHANGE broadcast.
        Assert.True(PumpUntil(server, () => OwnLoadout(victim, victimId) >= 3 && OwnLoadout(mate, victimId) >= 3, 4000) < 4000,
            $"both clients must know the victim's loadout before the death "
            + $"(owner {OwnLoadout(victim, victimId)}, teammate {OwnLoadout(mate, victimId)})");
        int boughtParts = victimShip.Parts.Count;
        short ammoBefore = victimShip.Ammo;
        float fuelBefore = victimShip.Fuel;
        Assert.True(ammoBefore > 0 && fuelBefore > 0f, "the fixture ship must have ammo and fuel to lose");

        // ── the fighter is destroyed → EJECT → drifting Lifepod ──────────────
        victimShip.ReceiveDamage(0, victimShip.HitPoints * 2f, mission.LastUpdate,
            victimShip.Position, Vector3.Zero, null);
        Assert.True(PumpUntil(server, () => victim.Ejects.Any(e => e.ShipId == victimId), 4000) < 4000,
            "no EJECT: the in-space eject path did not run");
        Assert.Null(victimShip.StationId);
        Assert.Equal(podHull, victimShip.HullType.HullId);

        // SERVER: native terminated every part before the hull swap.
        Assert.Empty(victimShip.Parts);
        Assert.Empty(victimShip.Weapons);
        Assert.All(victimShip.CargoHold, p => Assert.Null(p));
        Assert.Equal((short)0, victimShip.Ammo);        // FedSrv.CPP:11711
        Assert.Equal(0f, victimShip.Fuel);              // FedSrv.CPP:11727
        Assert.Equal(0f, victimShip.ShieldFraction);

        // OWNER + TEAMMATE: the pod is bare on the wire too (the LOADOUT_CHANGE
        // that follows the strip carries the pod hull + 0 parts; EJECT/ENTER_LIFEPOD
        // clear the parts client-side like native appmsg.cpp:193-201).
        PumpUntil(server, () => OwnLoadout(victim, victimId) == 0 && OwnLoadout(mate, victimId) == 0, 2000);
        Assert.Equal(0, OwnLoadout(victim, victimId));
        Assert.Equal(0, OwnLoadout(mate, victimId));
        Assert.Equal(podHull, victim.World.Ships[victimId].HullId);
        Assert.Equal(podHull, mate.World.Ships[victimId].HullId);
        Assert.True(OwnCargo(victim, victimId) == 0 && OwnCargo(mate, victimId) == 0,
            "the dead ship's cargo parts must not survive on the pod either");

        // The P996 saved loadout (native m_loadouts head) survived the strip: it is
        // what the hangar replays while docked in the pod (clintlib.h:1876-1886).
        Assert.NotNull(victim.World.OwnSavedLoadout);
        Assert.Equal(hullId, victim.World.OwnSavedLoadout!.HullId);
        // The saved snapshot is the MOUNTED loadout (P996/P626: the shop carries no
        // cargo — the documented existing gap), so compare against the mounted half.
        Assert.Equal(parts.Count(p => p.MountId < 0x80), victim.World.OwnSavedLoadout.Parts.Count);

        // ── the pod is destroyed → the player respawns DOCKED as a Lifepod ───
        victimShip.ReceiveDamage(0, victimShip.HitPoints * 2f, mission.LastUpdate,
            victimShip.Position, Vector3.Zero, null);
        Assert.True(PumpUntil(server, () => victim.Pods.Any(p => p.ShipId == victimId)
            && victimShip.StationId is not null, 4000) < 4000,
            "the pod death did not respawn the player as a docked Lifepod");
        Assert.Equal(podHull, victimShip.HullType.HullId);
        Assert.Empty(victimShip.Parts);
        Assert.All(victimShip.CargoHold, p => Assert.Null(p));
        PumpUntil(server, () => victim.World.OwnShipDockedInLifepod, 2000);
        Assert.True(victim.World.OwnShipDockedInLifepod, "the berthed pod must be recognised (native pod hull && GetStation())");
        Assert.Equal(0, OwnLoadout(victim, victimId));   // CURRENT SHIP pane: bare pod

        // ── Launch restores the last REAL loadout (P996 behaviour kept) ──────
        var saved = victim.World.OwnSavedLoadout!;
        int acksBefore = victim.Acks.Count;
        victim.SendBuyLoadout(saved.HullId, launch: true, saved.Parts);
        Assert.True(PumpUntil(server, () => victim.Acks.Count > acksBefore && victim.Acks.Any(a => a.FLaunch && a.HullId == saved.HullId), 8000) < 8000,
            "the restored hull was never granted — the P996 hangar restore broke");
        Assert.True(PumpUntil(server, () => victimShip.StationId is null, 4000) < 4000, "the restored ship never launched");
        Assert.Equal(saved.HullId, victimShip.HullType.HullId);
        Assert.True(victimShip.Parts.Count >= 3, "the restored ship must relaunch with its parts");
        Assert.Null(victimShip.LifepodTimeToDie);   // P996: no pod clock survives the pod death

        Console.WriteLine($"[p998] loaded fighter ({boughtParts} parts, ammo {ammoBefore}, fuel {fuelBefore:0.#}) destroyed -> bare pod "
            + $"(server {victimShip.Parts.Count} parts, owner {OwnLoadout(victim, victimId)}, teammate {OwnLoadout(mate, victimId)}); "
            + $"pod killed -> docked bare Lifepod; relaunched hull {saved.HullId} with {victimShip.Parts.Count} part(s)");
    }

    /// <summary>
    /// ⭐ A pod cannot fire a weapon nor raise the dead ship's shield, proved over
    /// the wire: after the eject the server's firing pass has no realized weapon
    /// for the pod, and a damage event lands wholly on the pod's hull (no inherited
    /// shield part to absorb it).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_pod_cannot_fire_the_dead_ships_guns_nor_use_its_shield()
    {
        Require.Artwork();
        var (server, victim, mate, victimShip, _, mission, core, sideId) = Boot();
        using var _s = server; using var _v = victim; using var _m = mate;
        var (_, hullId, parts) = BuyableLoadout(mission, core);
        victim.SendBuyLoadout(hullId, launch: true, parts);
        Assert.True(PumpUntil(server, () => victimShip.StationId is null && victimShip.Parts.Count >= 3, 8000) < 8000,
            "the loaded fighter never launched");

        victimShip.ReceiveDamage(0, victimShip.HitPoints * 2f, mission.LastUpdate,
            victimShip.Position, Vector3.Zero, null);
        Assert.True(PumpUntil(server, () => victim.Ejects.Any(e => e.ShipId == victimShip.Id), 4000) < 4000, "no eject");

        // FIRE: hold every trigger for a second of sim; no round leaves the pod.
        var sector = victimShip.Sector ?? throw new InvalidOperationException("pod left its sector");
        foreach (var round in sector.Models.OfType<Projectile>().ToArray()) sector.Remove(round);
        victimShip.StateM |= ShipControlState.AllWeapons | ShipControlState.MissileFire | ShipControlState.MineFire;
        for (int i = 0; i < 20; i++) server.TickSim();
        Assert.Empty(victimShip.Weapons);
        Assert.DoesNotContain(sector.Models.OfType<Projectile>(), p => ReferenceEquals(p.Launcher, victimShip));

        // SHIELD: the same amount fully absorbed by the loaded fighter's shield now
        // reaches the pod's hull (no shield part survives).
        Assert.Equal(0f, victimShip.ShieldFraction);
        float hullBefore = victimShip.Fraction;
        victimShip.ReceiveDamage(0, 1.0f, mission.LastUpdate, victimShip.Position, Vector3.Zero, null);
        Assert.True(victimShip.Fraction < hullBefore, "a bare pod has no shield: the hit reaches its hull");
        Console.WriteLine($"[p998] pod hull {victimShip.HullType.HullId}: 0 warnings, 0 weapons, 0 projectiles, shield 0 "
            + $"(hull {hullBefore:0.####} -> {victimShip.Fraction:0.####})");
    }

    /// <summary>
    /// ⭐ P411 loot rolls UNCHANGED (seeded): the strip runs AFTER the rolls, so a
    /// dead ship still drops its part/ammo/fuel treasures at the 1-in-5 odds. With
    /// <c>BenchmarkSeed</c> the treasure counts are exact — a reordering that
    /// terminated the parts first would take every count to 0.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Seeded_loot_rolls_still_see_the_dying_ships_parts_ammo_and_fuel()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(seed: 998));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var mission = server.Missions[0];
        var core = mission.Core;
        // A sector with no station, so an ejected pod really drifts (the death
        // path under test here is the non-eject / root-ship loot one anyway).
        var sector = mission.Sectors.First(s => s.Stations.Count == 0);
        short sideId = mission.Sides[0].Id;

        var fightHull = EconomyLoadoutTestSetup.WeaponHull(core);
        var gun = core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon && fightHull.CanMountPart(p, 0))
            .OrderBy(p => p.PartId).First();
        short ammoPack = core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Pack pk && pk.PackType == 0).PartId;
        short fuelPack = core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Pack pk && pk.PackType == 1).PartId;

        const int N = 60;
        const short Ammo = 480;
        var victims = new List<Ship>(N);
        for (int i = 0; i < N; i++)
        {
            var v = new Ship((short)(5000 + i), fightHull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = sideId };
            mission.PlaceShip(v, sector);
            Assert.NotNull(v.MountPart(gun, 1, 0, ExpandedPartData.FullyMounted));
            v.Ammo = Ammo;
            v.SetFuel(fightHull.MaxFuel);
            victims.Add(v);
            mission.Site.KillShipEvent(mission.LastUpdate, v, null, 1.0f, Vector3.Zero, Vector3.Zero);
        }

        var treasures = sector.Models.OfType<Treasure>().ToList();
        int partDrops = treasures.Count(t => t.Data.TreasureCode == TreasureCode.Part && t.Data.TreasureId == gun.PartId);
        int ammoDrops = treasures.Count(t => t.Data.TreasureCode == TreasureCode.Part && t.Data.TreasureId == ammoPack);
        int fuelDrops = treasures.Count(t => t.Data.TreasureCode == TreasureCode.Part && t.Data.TreasureId == fuelPack);

        // Every victim was a ROOT ship, so every one was stripped (native's
        // unconditional root-ship terminate block) even on this non-eject death path.
        Assert.All(victims, v => Assert.Empty(v.Parts));

        // Seeded counts: exact (BenchmarkSeed 998). > 0 proves the rolls saw the
        // parts/ammo/fuel BEFORE the strip; the 1-in-5 expectation is 12 each.
        Console.WriteLine($"[p998] seeded loot: {N} loaded victims -> {partDrops} part, {ammoDrops} ammo, {fuelDrops} fuel treasure(s)");
        Assert.True(partDrops > 0, "part treasures vanished — the strip ran before the loot rolls");
        Assert.True(ammoDrops > 0, "ammo treasures vanished");
        Assert.True(fuelDrops > 0, "fuel treasures vanished");
        // Pinned seeded outcome (BenchmarkSeed 998, N=60): deterministic today and
        // would collapse toward 0 if the strip ever ran before the rolls.
        Assert.Equal(12, partDrops);
        Assert.Equal(17, ammoDrops);
        Assert.Equal(14, fuelDrops);
    }

    /// <summary>
    /// Native's terminate block is inside `if (pshipParent == NULL)`
    /// (<c>FedSrv.CPP:11644</c>), so a CHILD/turret ship's death neither strips nor
    /// loots — it has no parts to begin with (boarding terminates them,
    /// <c>Igc/shipIGC.cpp:3619-3633</c>). Pinned here on the real kill seam so a
    /// future "strip every victim" refactor cannot silently diverge.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_child_ship_death_neither_loots_nor_strips_like_native()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(seed: 998));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var mission = server.Missions[0];
        var core = mission.Core;
        var sector = mission.Sectors.First(s => s.Stations.Count == 0);
        short sideId = mission.Sides[0].Id;
        var hull = EconomyLoadoutTestSetup.WeaponHull(core);
        var gun = core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon && hull.CanMountPart(p, 0))
            .OrderBy(p => p.PartId).First();

        var parent = new Ship(5100, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = sideId };
        mission.PlaceShip(parent, sector);
        var child = new Ship(5101, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = sideId };
        mission.PlaceShip(child, sector);
        Assert.NotNull(child.MountPart(gun, 1, 0, ExpandedPartData.FullyMounted));
        child.Ammo = 200;
        child.ParentShip = parent;   // a boarded child/turret ship

        // The map may author treasures of its own, so compare counts across the kill.
        int treasuresBefore = sector.Models.OfType<Treasure>().Count();
        mission.Site.KillShipEvent(mission.LastUpdate, child, null, 1.0f, Vector3.Zero, Vector3.Zero);

        Assert.Single(child.Parts);   // native's root-ship gate: no terminate block for a child
        Assert.Equal(treasuresBefore, sector.Models.OfType<Treasure>().Count());   // …and no loot roll
    }

    // ── ClientWorld helpers ─────────────────────────────────────────────────

    private static int OwnLoadout(WireClient c, short shipId) =>
        c.World.Ships.TryGetValue(shipId, out var e) ? e.Loadout?.Count ?? 0 : 0;

    private static int OwnCargo(WireClient c, short shipId) =>
        c.World.Ships.TryGetValue(shipId, out var e) ? e.Cargo?.Count ?? 0 : 0;
}
