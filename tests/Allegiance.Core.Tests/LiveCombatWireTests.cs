using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// Serializes the real-socket, tick-paced live classes against each other.
/// xUnit runs test classes in parallel by default; LiveCombatWireTests (and
/// potentially others) boot a real in-process server and drain loopback TCP on
/// their own async read threads, paced by wall-clock <see cref="Thread.Sleep"/>
/// budgets. Under CPU contention from unrelated parallel classes that read
/// thread can starve long enough that the fixed tick budget elapses before the
/// client observes the packet it's waiting for — a race, not a functional bug.
/// Tagging these classes with this collection makes them run sequentially
/// relative to each other while everything outside the collection still runs in
/// parallel — the minimal fix (no blanket parallelization disable for the
/// whole suite).
/// </summary>
[CollectionDefinition("LiveNetwork", DisableParallelization = true)]
public class LiveNetworkCollection
{
}

/// <summary>
/// The live proof for combat wiring (P19): a real in-process <see cref="Srv"/>
/// serving real <see cref="TcpTransport"/> clients over loopback TCP, with the
/// combat wiring from the two gaps exercised end to end:
///   Gap 1 — a spawned ship gets a real weapon mounted (the server spawn path
///          calls <see cref="Ship.MountWeapon"/>);
///   Gap 2 — when that weapon's projectiles kill the target, the mission's
///          <see cref="IIgcSite"/> raises <c>KillShipEvent</c> and the server
///          turns it into a real <c>FM_S_KILL_SHIP(105)</c> wire broadcast.
/// Like <see cref="BotFlightTests"/> this drives a real <see cref="Srv"/> and
/// real socket clients (not mocked sim internals); unlike the flight tests it
/// only needs the one deterministic assertion: a <c>KILL_SHIP(105)</c> packet
/// arrives on the wire. Because the stock <c>server.json</c> spawn uses the
/// core's first hull (Adv Miner, 0 energy / 0 ammo / no hardpoints — a weapon
/// can never fire), this injects a fightable hull + spawn via the server's
/// own <see cref="Srv.ShipSpawner"/> seam (the exact seam <c>BotFlightTests</c>
/// uses to seed a known spawn). The two bots are real: one commands
/// <c>AllWeapons</c> + forward, the other sits idle; the server ticks real
/// chunks, fires real projectiles, resolves real collisions, and (the point)
/// broadcasts <c>KILL_SHIP</c> to peers in the victim's sector.
/// </summary>
[Collection("LiveNetwork")]
public class LiveCombatWireTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // A hull that can actually fight (energy+ammo and a fixed hardpoint at
    // mount 0) paired with the cheapest dispersion-0 weapon that does real
    // damage (projectile Power > 0) the core offers — the same requirement the
    // CombatDeathTests selection enforces (a Power-0 weapon like 'PW Mini-Dis'
    // fires but never hurts, which is not what this live test wants).
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
    {
        var core = RepresentativeCore();
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
        throw new InvalidOperationException("no fightable hull+weapon in the representative core");
    });

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    /// <summary>Drive in-process ticks until a condition holds (logon ACKs /
    /// ship assignments arrive), giving the async bots a beat to connect and be
    /// drained. Like BotFlightTests' PumpUntil. Returns ticks pumped.</summary>
    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: some test clients never send POSITIONREQ
        Games =
        [
            new Gm
            {
                Name = "Test Mission", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
            },
        ],
    };

    private static LogonRequest SampleRequest(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 909u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 74000000000000000ul, DrmHash = "livecombat",
    };

    // The shooter nose-to-nose with the target at origin: shooter at (0,0,0)
    // facing −z (identity nose), target directly in front along −z so a
    // dispersion-0 projectile with identity heading flies straight into it.
    // Gap chosen so the projectile's travel (speed × lifespan) still reaches.
    private const float TargetZ = -180f;

    private static void InjectFightSpawn(Srv server, Vector3 shooterPos, Vector3 targetPos)
    {
        var placedShooter = false;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (hull, weapon) = Fight.Value;
            Vector3 pos = !placedShooter ? shooterPos : targetPos;
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, mission.Sectors[0]);   // sets ship.Mission/Sector (MountWeapon needs it)
            if (!placedShooter)
            {
                ship.Energy = hull.MaxEnergy * 100f;       // sustain fire: nothing recharges energy yet (slice)
                ship.MountWeapon(weapon);                  // Gap 1 — the mount the server's default spawn now performs
            }
            placedShooter = true;
            return (ship, mission.Sectors[0]);
        };
    }

    /// <summary>A minimal loopback TCP client that logs on and decodes the
    /// messages the combat wiring produces: SET_CLUSTER ship assignment,
    /// SINGLE_SHIP_UPDATE position feed, and — the key — KILL_SHIP(105).</summary>
    private sealed class CombatClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private LogonAck? _ack;
        private SetCluster? _set;
        private short _myShip;
        private readonly List<KillShip> _kills = new();
        private readonly List<SingleShipUpdate> _states = new();

        public CombatClient() => _t = new TcpTransport(this);

        public LogonAck? Ack { get { lock (_gate) return _ack; } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
        public IReadOnlyList<KillShip> Kills { get { lock (_gate) return _kills.ToArray(); } }
        public IReadOnlyList<SingleShipUpdate> States { get { lock (_gate) return _states.ToArray(); } }

        public void Connect(ushort port, string name) => _t.Join("127.0.0.1", port, name);
        public void Logon(LogonRequest req) => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);
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
                        case SetCluster.Fmid: _set = SetCluster.Decode(m); _myShip = _set.Ship.ShipId; break;
                        case SingleShipUpdate.Fmid:
                            var s = SingleShipUpdate.Decode(m);
                            if (s.OidTarget == _myShip) _states.Add(s);
                            break;
                        case KillShip.Fmid: _kills.Add(KillShip.Decode(m)); break;
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_two_party_combat_produces_a_kill_ship_over_the_wire()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectFightSpawn(server, new Vector3(0, 0, 0), new Vector3(0, 0, TargetZ));
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        ushort port = server.BoundPort;

        using var shooter = new CombatClient();
        using var target = new CombatClient();
        shooter.Connect(port, "SHOOTER"); target.Connect(port, "TARGET");

        // The shooter is the first client → gets the shooter ship (origin);
        // the target is the second → gets the target ship (down −z). Logons are
        // only answered when the server ticks (drain runs in SimPass), so pump.
        shooter.Logon(SampleRequest("SHOOTER"));
        target.Logon(SampleRequest("TARGET"));
        PumpUntil(server, () => shooter.ShipAssignment is not null && target.ShipAssignment is not null, 4000);

        var shAck = shooter.Ack ?? throw new InvalidOperationException("no shooter ACK");
        var tgAck = target.Ack ?? throw new InvalidOperationException("no target ACK");
        Assert.True(shAck.FValidated && tgAck.FValidated);
        var shSet = shooter.ShipAssignment ?? throw new InvalidOperationException("no shooter ship");
        var tgSet = target.ShipAssignment ?? throw new InvalidOperationException("no target ship");

        // The ShipSpawner puts the first-spawned ship at origin and the second
        // down −z, but the two sockets race on which logon the server drains
        // first. Resolve roles by spawn position, not connection order.
        var shAtOrigin = shSet.Ship.Position.Z > -1f;
        short shooterId = (shAtOrigin ? shSet : tgSet).Ship.ShipId;
        short targetId = (shAtOrigin ? tgSet : shSet).Ship.ShipId;
        Assert.NotEqual(shooterId, targetId);

        // The shooter commands AllWeapons (selection bits only) and stays at
        // rest; the target idles. Only the weapon-select bits cross the wire as
        // FM_C_SHIP_UPDATE(24) — no thrust, so the shooter stays nose-to-nose
        // with the target (the C++ weapon fires along the ship's heading).
        var shooterClient = shAtOrigin ? shooter : target;
        var targetClient = shAtOrigin ? target : shooter;
        shooterClient.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
        targetClient.CommandControls(ControlData.Neutral, 0);

        // Drive the real server for up to ~8 s of sim, letting the projectiles
        // fly, collide and accumulate hull damage until the target dies and the
        // server broadcasts KILL_SHIP(105) to the sector's peers (both bots).
        int ticks = 0;
        const int MaxTicks = 700;
        while (ticks < MaxTicks && !targetClient.Kills.Any(k => k.ShipId == targetId))
        {
            server.TickSim();
            ticks++;
            Thread.Sleep(2);
        }

        Assert.True(SpinWait.SpinUntil(() => shooterClient.Kills.Any(k => k.ShipId == targetId && k.IdCredit == shooterId),
            TimeSpan.FromSeconds(30)), "shooter must decode its kill credit independently of the victim");
        var targetKills = targetClient.Kills;
        var shooterKills = shooterClient.Kills;
        Assert.True(ticks < MaxTicks, $"no KILL_SHIP within {MaxTicks} ticks");
        Assert.True(targetKills.Count >= 1, "target client must receive KILL_SHIP(105)");
        Assert.Contains(targetKills, k => k.ShipId == targetId && k.BDeathCredit);

        // The shooter's own client saw the same kill (same sector), credited to
        // the shooter ship.
        Assert.Contains(shooterKills, k => k.ShipId == targetId && k.IdCredit == shooterId);

        // Live-proof evidence (visible with detailed logger verbosity):
        var tk = targetKills[0];
        Console.WriteLine($"LIVE-COMBAT: target {targetId} killed over the wire after {ticks} ticks");
        Console.WriteLine($"LIVE-COMBAT: KILL_SHIP(105) on target client: shipID={tk.ShipId} typeCredit={tk.TypeCredit} " +
                          $"idCredit={tk.IdCredit} sideKiller={tk.SideIdKiller} bKill={tk.BKillCredit} bDeath={tk.BDeathCredit}");
        Console.WriteLine($"LIVE-COMBAT: KILL_SHIP(105) bytes = {BitConverter.ToString(tk.Encode())}");
        var lowHull = targetClient.States.Select(s => s.Ship.HullFraction).DefaultIfEmpty(1f).Min();
        Console.WriteLine($"LIVE-COMBAT: target wire hull fraction fell to {lowHull:R} before death");
    }

    /// <summary>
    /// Separately records the broadcast-policy finding: SINGLE_SHIP_UPDATE is
    /// only sent for ships whose position changed, so an idle victim's falling
    /// hull is NOT streamed tick-by-tick — the hull slot exists in the payload
    /// (ServerShipUpdate.FromModel fills Ship.Fraction) but an immobile ship is
    /// never marked dirty. The kill itself (KILL_SHIP) is broadcast regardless
    /// of motion, so the death still crosses the wire. This test forces motion
    /// on the target (tiny forward thrust) and asserts the falling hull is then
    /// observable — the gap is broadcast gating, not payload.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Moving_target_streams_its_falling_hull_over_the_wire()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectFightSpawn(server, new Vector3(0, 0, 0), new Vector3(0, 0, TargetZ));
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        ushort port = server.BoundPort;

        using var shooter = new CombatClient();
        using var target = new CombatClient();
        shooter.Connect(port, "SHOOTER2"); target.Connect(port, "TARGET2");
        shooter.Logon(SampleRequest("SHOOTER2")); target.Logon(SampleRequest("TARGET2"));
        Assert.True(SpinWait.SpinUntil(() =>
        {
            if (shooter.ShipAssignment is not null && target.ShipAssignment is not null) return true;
            server.TickSim();
            return false;
        }, TimeSpan.FromSeconds(30)), "both combat clients must decode their ship assignments before controls");
        var shSet = shooter.ShipAssignment ?? throw new InvalidOperationException("no shooter ship");
        var tgSet = target.ShipAssignment ?? throw new InvalidOperationException("no target ship");

        // Resolve roles by spawn position (first-spawned at origin), not socket order.
        var shAtOrigin = shSet.Ship.Position.Z > -1f;
        short targetId = (shAtOrigin ? tgSet : shSet).Ship.ShipId;
        var shooterClient = shAtOrigin ? shooter : target;
        var targetClient = shAtOrigin ? target : shooter;

        shooterClient.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
        targetClient.CommandControls(new ControlData(0f, 0f, 0f, 0.005f), 0);   // a little drift so it broadcasts

        var lowestObserved = 1f;
        int ticks = 0;
        const int MaxTicks = 600;
        while (ticks < MaxTicks)
        {
            server.TickSim();
            ticks++;
            Thread.Sleep(2);
            foreach (var s in targetClient.States)
                if (s.Ship.HullFraction < lowestObserved) lowestObserved = s.Ship.HullFraction;
            if (targetClient.Kills.Any(k => k.ShipId == targetId)) break;
            if (lowestObserved < 0.995f && ticks > 30) break;   // we've SEEN hull drop — no need to wait for the kill
        }

        Assert.True(ticks < MaxTicks, "neither hull-drop nor kill observed");
        Assert.True(lowestObserved < 1f,
            $"moving target's hull must be observed falling on the wire; lowest={lowestObserved:R}");
    }

    /// <summary>
    /// P22: the real, shipped <see cref="FltBot"/> class (not the bespoke
    /// <see cref="CombatClient"/>) observes a kill through its new
    /// <see cref="FltBot.Kills"/> surface. Drives two real <c>Bot</c> instances
    /// exactly as the <c>allegiance.bot</c> console app drives one (connect →
    /// logon → wait for SET_CLUSTER → command controls), one firing AllWeapons
    /// at the other, and asserts the KILL_SHIP(105) this bot's OnReceive switch
    /// now records.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_Bot_class_observes_its_own_kill_over_the_wire()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectFightSpawn(server, new Vector3(0, 0, 0), new Vector3(0, 0, TargetZ));
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        ushort port = server.BoundPort;

        using var attacker = new FltBot();
        using var victim = new FltBot();
        attacker.Connect(port, "ATK-BOT");
        victim.Connect(port, "VIC-BOT");
        attacker.SendLogonRequest(SampleRequest("ATK-BOT"));
        victim.SendLogonRequest(SampleRequest("VIC-BOT"));
        PumpUntil(server, () => attacker.ShipAssignment is not null && victim.ShipAssignment is not null, 4000);

        var aAck = attacker.Ack ?? throw new InvalidOperationException("no attacker ACK");
        var vAck = victim.Ack ?? throw new InvalidOperationException("no victim ACK");
        Assert.True(aAck.FValidated && vAck.FValidated);
        var aSet = attacker.ShipAssignment ?? throw new InvalidOperationException("no attacker ship");
        var vSet = victim.ShipAssignment ?? throw new InvalidOperationException("no victim ship");

        // Roles by spawn position, not connection order (the two sockets race).
        var aAtOrigin = aSet.Ship.Position.Z > -1f;
        var shooter = aAtOrigin ? attacker : victim;
        var targetBot = aAtOrigin ? victim : attacker;
        short shooterId = (aAtOrigin ? aSet : vSet).Ship.ShipId;
        short targetId = (aAtOrigin ? vSet : aSet).Ship.ShipId;
        Assert.NotEqual(shooterId, targetId);

        shooter.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
        targetBot.CommandControls(ControlData.Neutral, 0);

        int ticks = 0;
        const int MaxTicks = 900;
        while (ticks < MaxTicks && !targetBot.Kills.Any(k => k.ShipId == targetId))
        {
            server.TickSim();
            ticks++;
            Thread.Sleep(1);
        }

        Assert.True(SpinWait.SpinUntil(() => shooter.Kills.Any(k => k.ShipId == targetId && k.IdCredit == shooterId),
            TimeSpan.FromSeconds(30)), "shooter must decode its kill credit independently of the victim");
        var targetKills = targetBot.Kills;
        var shooterKills = shooter.Kills;
        Assert.True(ticks < MaxTicks, $"no KILL_SHIP observed by real Bot within {MaxTicks} ticks");
        Assert.Contains(targetKills, k => k.ShipId == targetId && k.BDeathCredit);
        Assert.Contains(shooterKills, k => k.ShipId == targetId && k.IdCredit == shooterId);

        var k = targetKills.First(x => x.ShipId == targetId);
        Console.WriteLine($"BOT-KILL: target bot {targetId} observed its death over the wire after {ticks} ticks");
        Console.WriteLine($"BOT-KILL: KILL_SHIP(105) on the victim Bot: shipID={k.ShipId} typeCredit={k.TypeCredit} " +
                          $"idCredit={k.IdCredit} sideKiller={k.SideIdKiller} bKill={k.BKillCredit} bDeath={k.BDeathCredit}");
        Console.WriteLine($"BOT-KILL: bytes = {BitConverter.ToString(k.Encode())}");
    }
}
