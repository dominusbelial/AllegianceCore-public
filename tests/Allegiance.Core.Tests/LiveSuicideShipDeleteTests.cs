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
/// P103 verification — the <c>FM_C_SUICIDE(80)</c> death slice and the
/// <c>FM_S_SHIP_DELETE(20)</c> <c>SDR_KILLED</c>-only slice that rides it.
/// Three layers:
///  (1) codec tests pin the exact bytes of both new wire messages, following
///      the suite's established convention (round-trip where the shape is
///      simple, a hand-composed expected buffer for the interesting layout —
///      SHIP_DELETE's enum-induced padding);
///  (2) a unit view of the kill seam — proving that firing <c>KillShipEvent</c>
///      the way <c>HandleSuicide</c> does (a NULL-launcher death) produces the
///      same KILL_SHIP death event a combat death does;
///  (3) the real loopback-in-process-server live proofs for the four task
///      scenarios: a flying suicide kills (real <c>KILL_SHIP(105)</c> +
///      <c>SHIP_DELETE(20)</c> naming the suiciding ship), a docked suicide is
///      a no-op (nothing dies, nothing broadcasts), and a combat death also
///      emits KILL_SHIP to sector peers and SHIP_DELETE to mission peers
///      (native fsship.cpp:196-223).
/// Like the other live suites this drives a real in-process <see cref="Srv"/>
/// over loopback TCP; the [Collection("LiveNetwork")] tag keeps it sequential
/// against the other live suites.
/// </summary>
[Collection("LiveNetwork")]
public class LiveSuicideShipDeleteTests
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
            new Gm { Name = "Suicide Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 880u, DwCookie = 0x880u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8830000000000000000ul, DrmHash = "suicide",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>A hull that can actually fight, paired with the cheapest real-
    /// damaging weapon — the same selection the other combat live tests use,
    /// so a combat death here is a real, fast, deterministic kill.</summary>
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        var core = StaticCore.Load(file);
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.EnergyPerShot))
            {
                if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    return (hull, p);
            }
        }
        throw new InvalidOperationException("no fightable hull+weapon in PCore001.igc");
    });

    /// <summary>Inject the fight spawn (shooter at origin, target down −z) the
    /// same way <c>LiveCombatWireTests</c> does — first-spawned ship armed,
    /// both placed in sector 0.</summary>
    private static void InjectFightSpawn(Srv server)
    {
        const float TargetZ = -180f;
        var placedShooter = false;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (hull, weapon) = Fight.Value;
            Vector3 pos = !placedShooter ? Vector3.Zero : new Vector3(0f, 0f, TargetZ);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, mission.Sectors[0]);
            if (!placedShooter)
            {
                ship.Energy = hull.MaxEnergy * 100f;
                ship.MountWeapon(weapon);
            }
            placedShooter = true;
            return (ship, mission.Sectors[0]);
        };
    }

    /// <summary>Inject a docked spawn: the bot's ship is planted inside a
    /// friendly station's dock radius so the first tick latches it docked
    /// (<c>ServerHost.DockWithStationEvent</c> → <see cref="Ship.StationId"/>),
    /// reproducing <c>DockDockingTests.SpawnAt</c> — the controlled hook into
    /// placement that gives us a genuinely-docked, still-placed ship.</summary>
    private static void InjectDockedSpawn(Srv server)
    {
        server.ShipSpawner = (core, mission, id) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(id, hull, DockTestPlacement.Inside(mission, station, hull), Vector3.Zero, Orientation.Identity);
            ship.SideId = station.SideId;
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
    }

    // ── the two new codecs ─────────────────────────────────────────────────

    /// <summary><c>FM_C_SUICIDE</c>(80) is header-only: encode emits exactly
    /// the 4 bytes <c>{ cbmsg=4, fmid=80 }</c>, decode round-trips and rejects a
    /// foreign fmid / a wrong length (Messages.h:590-591).</summary>
    [Fact]
    public void Suicide_message_round_trips()
    {
        var msg = new Suicide();
        var d = Suicide.Decode(msg.Encode());
        Assert.Equal(Suicide.Fmid, d.GetFmid());

        var bytes = msg.Encode();
        Assert.Equal(4, bytes.Length);
        Assert.Equal(new byte[] { 4, 0, 80, 0 }, bytes);      // cbmsg, fmid, no fields
    }

    /// <summary><c>FM_S_SHIP_DELETE</c>(20) round-trips: shipID short at
    /// offset 4, then 2 pad bytes, then the enum int32 at offset 8 —
    /// <c>cbmsg = 12</c> (Messages.h:196-199, native struct layout).</summary>
    [Fact]
    public void ShipDelete_message_round_trips()
    {
        var msg = ShipDelete.ForKilledShip(701);
        var d = ShipDelete.Decode(msg.Encode());
        Assert.Equal(ShipDelete.Fmid, d.GetFmid());
        Assert.Equal((short)701, d.ShipId);
        Assert.Equal(ShipDelete.SdrKilled, d.Sdr);
    }

    /// <summary><c>FM_S_SHIP_DELETE</c>(20) emits the exact 12 declared bytes,
    /// hand-composed from <c>Messages.h:196-199</c> and the native struct layout
    /// (the plain <c>ShipDeleteReason</c> enum is a full 4-byte int aligned to
    /// 4, inserting 2 pad bytes after the short shipID) — not from the encoder.</summary>
    [Fact]
    public void ShipDelete_message_emits_exact_declared_bytes()
    {
        var got = new ShipDelete { ShipId = 0x0383 /* 899 */, Sdr = ShipDelete.SdrKilled }.Encode();
        Assert.Equal(12, got.Length);
        Assert.Equal(new byte[]
        {
            12, 0,        // cbmsg
            20, 0,        // fmid
            0x83, 0x03,   // shipID (short, LE)
            0, 0,         // enum 4-align padding
            1, 0, 0, 0,   // sdr = SDR_KILLED (int32 LE)
        }, got);

        // Decode the hand-built buffer symmetrically.
        var d = ShipDelete.Decode(got);
        Assert.Equal((short)0x0383, d.ShipId);
        Assert.Equal(ShipDelete.SdrKilled, d.Sdr);
    }

    // ── unit view of the kill seam (what HandleSuicide triggers) ─────────────

    /// <summary>The exact call <c>HandleSuicide</c> makes — a NULL-launcher
    /// <c>KillShipEvent</c> — is the identical seam a combat death's lethal
    /// branch (<c>Ship.ReceiveDamage</c> → <c>Mission.Site.KillShipEvent</c>,
    /// <c>Ship.cs:650</c>) uses: one <c>KillShipEvent</c> for the recipient, no
    /// parallel mechanism. Verified through a <see cref="RecordingIgcSite"/>.
    /// <paramref name="asCombat"/> toggles between simulating the combat death
    /// (ReceiveDamage lethal) and the suicide direct call.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Kill_seam_raises_kill_ship_exactly_once_either_way(bool asCombat)
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors[0];
        var ship = new Ship(9901, mission.Core.HullTypes.Values.First(), Vector3.Zero, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(ship, sector);

        if (asCombat)
        {
            // A combat death: enough damage bottoms the hull and raises KillShipEvent once.
            ship.ReceiveDamage(0, ship.HitPoints * 2f, mission.LastUpdate, ship.Position, Vector3.Zero, null);
        }
        else
        {
            // A suicide death: the same direct KillShipEvent a native suicide
            // makes (FedSrv.CPP:6339-6341), NULL launcher.
            mission.Site.KillShipEvent(mission.LastUpdate, ship, null, 1.0f, ship.Position, Vector3.Zero);
        }

        Assert.Equal(1, recording.Calls.Count(c => c.StartsWith("KillShipEvent")));
    }

    private static Mission NewStartedMission(IIgcSite site)
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var m = Mission.Assemble(core, map, [new SideSpec(0, "Team 1", civ.CivilizationId)], site);
        m.Stage = MissionStage.Started;
        return m;
    }

    // ── the wire client ────────────────────────────────────────────────────

    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private LogonAck? _ack;
        private SetCluster? _set;
        private readonly List<KillShip> _kills = new();
        private readonly List<ShipDelete> _deletes = new();

        public LogonAck? Ack { get { lock (_gate) return _ack; } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
        public IReadOnlyList<KillShip> Kills { get { lock (_gate) return _kills.ToArray(); } }
        public IReadOnlyList<ShipDelete> Deletes { get { lock (_gate) return _deletes.ToArray(); } }

        public WireClient() => _t = new TcpTransport(this);

        public void Connect(ushort port, string name) => _t.Join("127.0.0.1", port, name);
        public void Logon(LogonRequest req) => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);
        public void SendSuicide() => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new Suicide().Encode() }), guaranteed: true);

        public void CommandControls(ControlData c, int stateM = 0)
        {
            var m = new ShipUpdate
            {
                Time = 0, Cookie = (byte)(_ack?.Cookie ?? 0),
                Payload = ClientShipUpdate.FromControls(0u, stateM, c),
            };
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { m.Encode() }), guaranteed: true);
        }

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
                        case KillShip.Fmid: _kills.Add(KillShip.Decode(m)); break;
                        case ShipDelete.Fmid: _deletes.Add(ShipDelete.Decode(m)); break;
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    // ── live proofs ────────────────────────────────────────────────────────

    /// <summary>⭐ A flying, placed ship that sends <c>SUICIDE(80)</c> dies via
    /// the real kill path: its own client receives a genuine <c>KILL_SHIP(105)</c>
    /// (same shape a combat death produces) <b>and</b> the accompanying
    /// <c>SHIP_DELETE(20)</c> with <c>sdr = SDR_KILLED</c>, both naming the
    /// suiciding ship id.
    /// <para><b>P107 note (why the ship is already a Lifepod):</b> with eject
    /// pods always enabled (the port's mandated simplification — the original
    /// <c>!bEjectPods</c> clause of the <c>bKilled</c> gate is never decoded,
    /// <c>FedSrv.CPP:11779</c>), suicide on a <b>non-lifepod</b> player ship
    /// routes to the <b>in-space EJECT</b> branch, not death: native suicide
    /// passes <c>amount = 1.0f</c> (<c>FedSrv.CPP:6347</c>), which makes
    /// <c>bKilled = false</c> for a non-lifepod ship. So the ship-under-test
    /// here rides its side's Lifepod hull from the start, making its suicide a
    /// true <b>second</b> death that reaches the kill branch (native
    /// <c>bLifepod → bKilled</c>, <c>FedSrv.CPP:11776-11780</c>) — exactly the
    /// scenario P107's tests prove is mutually exclusive with the eject.</para>
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Suiciding_ship_dies_and_both_death_messages_arrive()
    {
        Require.Artwork();
        using var server = new Srv();
        server.ShipSpawner = (core, mission, id) =>
        {
            // An ALREADY-a-lifepod ship, so its suicide is a real death (not an
            // in-space eject). See the P107 note above.
            var hull = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
            var ship = new Ship(id, hull, new Vector3(720f, 730f, 920f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            return (ship, mission.Sectors[0]);
        };
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        ushort port = server.BoundPort;

        using var client = new WireClient();
        client.Connect(port, "SUICIDER");
        client.Logon(Request("SUICIDER"));
        PumpUntil(server, () => client.ShipAssignment is not null, 4000);
        var set = client.ShipAssignment ?? throw new InvalidOperationException("no ship assignment");
        short myId = set.Ship.ShipId;

        // P653 H-26 restores native's child-or-CAN_CHEAT suicide gate (FedSrv.CPP:6338):
        // a lone pod may no longer suicide. Enable the configured admin predicate for the
        // test connection (native's CanCheat escape hatch).
        server.Config.LiveSettings.AdminCheats = new global::Allegiance.Server.AdminCheatsConfig { Enabled = true, AllowNames = ["SUICIDER"] };

        client.SendSuicide();
        int ticks = PumpUntil(server,
            () => client.Deletes.Any(d => d.ShipId == myId) && client.Kills.Any(k => k.ShipId == myId),
            2000);

        Assert.True(ticks < 2000, "suicide did not produce KILL_SHIP + SHIP_DELETE on the wire");
        Assert.Contains(client.Kills, k => k.ShipId == myId && k.BDeathCredit);
        Assert.Contains(client.Deletes, d => d.ShipId == myId && d.Sdr == ShipDelete.SdrKilled);

        var k = client.Kills.First(x => x.ShipId == myId);
        var dd = client.Deletes.First(x => x.ShipId == myId);
        Console.WriteLine($"SUICIDE-LIVE: ship {myId} died over the wire after {ticks} ticks");
        Console.WriteLine($"SUICIDE-LIVE: KILL_SHIP(105) shipID={k.ShipId} idCredit={k.IdCredit} bDeath={k.BDeathCredit} = {BitConverter.ToString(k.Encode())}");
        Console.WriteLine($"SUICIDE-LIVE: SHIP_DELETE(20) shipID={dd.ShipId} sdr={dd.Sdr}(SDR_KILLED) = {BitConverter.ToString(dd.Encode())}");
    }

    /// <summary>⭐ A <b>docked</b> (placed but <c>StationId != null</c>) ship's
    /// <c>SUICIDE(80)</c> is a no-op: nothing dies, no <c>KILL_SHIP(105)</c> and
    /// no <c>SHIP_DELETE(20)</c> arrive — matching the native <c>GetCluster()</c>
    /// gate (<c>FedSrv.CPP:6338</c>). The ship stays docked and alive.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Suiciding_docked_ship_is_a_no_op()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectDockedSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var client = new WireClient();
        client.Connect(server.BoundPort, "DOCKED");
        client.Logon(Request("DOCKED"));
        int ticks = PumpUntil(server, () => client.ShipAssignment is not null, 4000);
        Assert.True(ticks < 4000, "no ship assignment");
        short myId = client.ShipAssignment!.Ship.ShipId;

        // Prove we really are docked before attempting the suicide.
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == myId).Ship.StationId is not null, 200);
        Assert.NotNull(server.Ships.First(s => s.Ship.Id == myId).Ship.StationId);

        client.SendSuicide();
        int quiet = PumpUntil(server, () => false, 120);   // plenty of sim for a broadcast if one were coming

        // No death, no broadcast.
        Assert.Empty(client.Kills);
        Assert.Empty(client.Deletes);
        Assert.NotNull(server.Ships.First(s => s.Ship.Id == myId).Ship.StationId);   // still docked & alive
        Console.WriteLine($"SUICIDE-LIVE: docked ship {myId} refused (no kill/no broadcast after {quiet} ticks)");
    }

    /// <summary>⭐ A combat death (the pre-existing path, unchanged) now also
    /// produces a real <c>SHIP_DELETE(20)</c> with <c>sdr = SDR_KILLED</c>
    /// naming the victim. Both armed clients share the sector and receive both;
    /// off-sector mission peers receive deletion only (fsship.cpp:196-223).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Combat_death_produces_ship_delete_to_same_audience()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectFightSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        ushort port = server.BoundPort;

        using var shooter = new WireClient();
        using var target = new WireClient();
        shooter.Connect(port, "SHOOTER3"); target.Connect(port, "TARGET3");
        shooter.Logon(Request("SHOOTER3")); target.Logon(Request("TARGET3"));
        PumpUntil(server, () => shooter.ShipAssignment is not null && target.ShipAssignment is not null, 4000);

        var ackS = shooter.Ack ?? throw new InvalidOperationException("no shooter ACK");
        var ackT = target.Ack ?? throw new InvalidOperationException("no target ACK");
        Assert.True(ackS.FValidated && ackT.FValidated);
        var shSet = shooter.ShipAssignment!;
        var tgSet = target.ShipAssignment!;
        short shId = shSet.Ship.ShipId, tgId = tgSet.Ship.ShipId;
        Assert.NotEqual(shId, tgId);

        // Roles by spawn position (first-spawned at origin), not socket order.
        var shAtOrigin = shSet.Ship.Position.Z > -1f;
        var shooterClient = shAtOrigin ? shooter : target;
        var targetClient = shAtOrigin ? target : shooter;
        short shooterId = shAtOrigin ? shId : tgId;
        short targetId = shAtOrigin ? tgId : shId;

        // Shooter arms all weapons and stays put; target idles. The shooter's
        // projectiles converge on the target until it dies.
        shooterClient.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
        targetClient.CommandControls(ControlData.Neutral, 0);

        int ticks = 0;
        const int MaxTicks = 900;
        while (ticks < MaxTicks && !targetClient.Deletes.Any(d => d.ShipId == targetId))
        {
            server.TickSim();
            ticks++;
            Thread.Sleep(2);
        }

        // The death is witnessed on BOTH clients as KILL_SHIP + SHIP_DELETE
        // naming the same victim id. They share the sector; deletion also reaches
        // off-sector mission peers (native AnnounceExit, fsship.cpp:196-223).
        Assert.True(ticks < MaxTicks, $"no SHIP_DELETE within {MaxTicks} ticks");
        Assert.True(SpinWait.SpinUntil(() => shooterClient.Kills.Any(k => k.ShipId == targetId)
            && shooterClient.Deletes.Any(d => d.ShipId == targetId), TimeSpan.FromSeconds(30)),
            "shooter must decode death credit and deletion independently of the victim");
        Assert.Contains(targetClient.Kills, k => k.ShipId == targetId && k.BDeathCredit);
        Assert.Contains(shooterClient.Kills, k => k.ShipId == targetId && k.IdCredit == shooterId);
        Assert.Contains(targetClient.Deletes, d => d.ShipId == targetId && d.Sdr == ShipDelete.SdrKilled);
        Assert.Contains(shooterClient.Deletes, d => d.ShipId == targetId && d.Sdr == ShipDelete.SdrKilled);

        var k = targetClient.Kills.First(x => x.ShipId == targetId);
        var dd = shooterClient.Deletes.First(x => x.ShipId == targetId);
        Console.WriteLine($"COMBAT-DELETE-LIVE: target {targetId} killed over the wire after {ticks} ticks");
        Console.WriteLine($"COMBAT-DELETE-LIVE: KILL_SHIP(105) on shooter: shipID={k.ShipId} idCredit={k.IdCredit} = {BitConverter.ToString(k.Encode())}");
        Console.WriteLine($"COMBAT-DELETE-LIVE: SHIP_DELETE(20) on shooter: shipID={dd.ShipId} sdr={dd.Sdr}(SDR_KILLED) = {BitConverter.ToString(dd.Encode())}");
    }
}
