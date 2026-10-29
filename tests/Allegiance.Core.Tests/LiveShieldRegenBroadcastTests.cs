using Allegiance.Core;
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
/// P391 — live proof that a player's OWN ship's shield-fraction regeneration
/// refreshes their own HUD while the ship sits completely still.
///
/// The server-side regen math (P389) is correct — but the per-tick own-ship
/// SINGLE_SHIP_UPDATE broadcast (the message whose byte 41 carries
/// <c>ServerShipUpdate.ShieldFraction</c>) is gated on a dirty check with only
/// position / orientation / mining terms. A ship that isn't moving never
/// re-trips those terms, so its owner's HUD shield reading froze at whatever
/// value was last broadcast. P391 adds a shield-fraction term to that dirty
/// gate.
///
/// These tests run a REAL in-process server + real loopback TCP clients and
/// confirm the fix on the actual wire, in the same "LiveNetwork" sequential
/// collection as the other real-socket live suites.
/// </summary>
[Collection("LiveNetwork")]
public class LiveShieldRegenBroadcastTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // A hull that can fight (energy+ammo+fixed hardpoint) paired with the
    // cheapest dispersion-0 weapon that does real damage — the same selection
    // LiveCombatWireTests uses to actually land hits over the wire.
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

    // A real SHIELD with a big pool (so a hit drains it gradually, no risk of
    // one-volley overkill) and the fastest regeneration rate available, so the
    // HUD climb over the test window is unambiguous. Selected from the same
    // representative core the rest of the suite uses.
    private static readonly Lazy<DataPartTypeIGC> RegenShield = new(() =>
        (from sp in RepresentativeCore().PartTypes.Values
         where sp.EquipmentType == EquipmentType.Shield
         let st = (DataPartTypeIGC.Shield)sp.Type
         where st.MaxStrength >= 760f && st.RateRegen > 0f
         select sp).OrderByDescending(sp => ((DataPartTypeIGC.Shield)sp.Type).RateRegen).First());

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

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
        SteamID = 74000000000000000ul, DrmHash = "liveregen",
    };

    private const float TargetZ = -180f;

    /// <summary>Mutable holder so a lambdas-invoked-later spawner can hand the
    /// server-side target <see cref="Ship"/> back to the test (an <c>out</c>
    /// parameter cannot be captured in a lambda — CS1628).</summary>
    private sealed class ShipRef { public Ship? Ship; }

    /// <summary>Spawn the shooter at origin (mounted weapon + full energy so it
    /// can sustain fire) and the target down −z with a real mounted shield.
    /// Stores the server-side target <see cref="Ship"/> into
    /// <paramref name="target"/> so the test can read its live
    /// <c>ShieldFraction</c> to control the fire burst precisely.</summary>
    private static void InjectFightSpawn(Srv server, ShipRef target)
    {
        var placedShooter = false;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (hull, weapon) = Fight.Value;
            Vector3 pos = !placedShooter ? Vector3.Zero : new Vector3(0, 0, TargetZ);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, mission.Sectors[0]);
            if (!placedShooter)
            {
                ship.Energy = hull.MaxEnergy * 100f;   // sustain fire
                ship.MountWeapon(weapon);              // Gap 1 mount
            }
            else
            {
                // P87: a real mounted shield, armed to full. It absorbs the
                // incoming fire, then (P389) regenerates while the ship is idle.
                ship.MountPart(RegenShield.Value, amount: 1, mountId: 0, fractionMounted: 255);
                target.Ship = ship;
            }
            placedShooter = true;
            return (ship, mission.Sectors[0]);
        };
    }

    /// <summary>A minimal loopback client that decodes its OWN ship's
    /// SINGLE_SHIP_UPDATE stream (positions + shield fraction) and nothing
    /// else it doesn't need.</summary>
    private sealed class ShieldClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private LogonAck? _ack;
        private SetCluster? _set;
        private short _myShip;
        private readonly List<SingleShipUpdate> _states = new();

        public ShieldClient() => _t = new TcpTransport(this);

        public LogonAck? Ack { get { lock (_gate) return _ack; } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
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
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    /// <summary>
    /// ⭐ THE live proof. A shielded ship takes real combat damage (shield
    /// drained below full), then sits COMPLETELY still (neutral controls, no
    /// thrust, no turning) while its shield regenerates. Its OWN client's
    /// SINGLE_SHIP_UPDATE stream must show the ShieldFraction climbing on its
    /// own — matching the real server regen curve — with zero movement. The
    /// climb is attributed to the P391 shield term, not motion, because the
    /// target never moves (position held constant across every received state).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Stationary_ship_streams_its_regenerating_shield_over_the_wire()
    {
        Require.Artwork();
        using var server = new Srv();
        var targetShipRef = new ShipRef();
        InjectFightSpawn(server, targetShipRef);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass ready-up lobby
        ushort port = server.BoundPort;

        using var shooter = new ShieldClient();
        using var target = new ShieldClient();
        shooter.Connect(port, "SHD-SHOOTER"); target.Connect(port, "SHD-TARGET");
        shooter.Logon(SampleRequest("SHD-SHOOTER")); target.Logon(SampleRequest("SHD-TARGET"));
        PumpUntil(server, () => shooter.ShipAssignment is not null && target.ShipAssignment is not null, 4000);

        var shSet = shooter.ShipAssignment ?? throw new InvalidOperationException("no shooter ship");
        var tgSet = target.ShipAssignment ?? throw new InvalidOperationException("no target ship");

        // Roles by spawn position (first-spawned shooter at origin Z≈0; target
        // at Z=-180), not socket connection order — the two sockets race.
        var shAtOrigin = shSet.Ship.Position.Z > -1f;
        short targetId = (shAtOrigin ? tgSet : shSet).Ship.ShipId;
        var shooterClient = shAtOrigin ? shooter : target;
        var targetClient = shAtOrigin ? target : shooter;
        Assert.NotEqual(shAtOrigin ? shSet.Ship.ShipId : tgSet.Ship.ShipId, targetId);

        // The target is commanded NEUTRAL for the WHOLE test — it never thrusts
        // nor turns, so it never trips the position/orientation dirty terms.
        targetClient.CommandControls(ControlData.Neutral, 0);

        var targetShip = targetShipRef.Ship ?? throw new InvalidOperationException("target ship never spawned");
        Assert.True(targetShip.ShieldFraction >= 0.999f, "target shield must start fully charged");
        Assert.Contains(targetShip.Parts, p => p.EquipmentType == EquipmentType.Shield);

        // ── PHASE 1: drain the shield below full via real combat damage ──
        // Fire in 1-tick bursts, stopping as soon as the server-side shield has
        // clearly dropped (well above the 0.1 down-threshold so the hull never
        // leaks and the target can't die).
        shooterClient.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
        int drainTicks = 0;
        const int MaxDrain = 400;
        while (drainTicks < MaxDrain && targetShip.ShieldFraction >= 0.74f)
        {
            server.TickSim();
            drainTicks++;
            Thread.Sleep(2);
            Assert.True(!targetShip.Dead, $"target died during drain at tick {drainTicks} (shield {targetShip.ShieldFraction})");
        }
        shooterClient.CommandControls(ControlData.Neutral, 0);   // STOP firing
        Assert.True(drainTicks < MaxDrain, $"target shield never dropped below 0.74 (ended {targetShip.ShieldFraction})");
        float drainedShield = targetShip.ShieldFraction;
        Assert.True(drainedShield < 0.799f, $"shield not meaningfully drained: server={drainedShield}");
        Assert.True(targetShip.Fraction > 0.5f, "target hull must survive the drain intact");
        Console.WriteLine($"P391: target shield drained to {drainedShield:F3} over {drainTicks} combat ticks; hull={targetShip.Fraction:F3}");

        // ── PHASE 2: sit completely still and let the shield regenerate ──
        // Record the position so we can prove the target never moved.
        var anchorPos = targetClient.States.Count > 0
            ? targetClient.States[^1].Ship.Position
            : tgSet.Ship.Position;

        int regenTicks = 0;
        const int MaxRegen = 550;          // ~27 s of sim: shield climbs ~2.5%/s
        while (regenTicks < MaxRegen && targetShip.ShieldFraction < 0.988f)
        {
            server.TickSim();
            regenTicks++;
            Thread.Sleep(2);
        }

        var states = targetClient.States;
        Assert.NotEmpty(states);
        var last = states[^1].Ship;
        float lastSeen = last.ShieldFraction;
        // The lowest shield fraction the target's OWN client ever saw. Computed
        // over the FULL stream (sampling late dodges the async receive lag): with
        // the fix the depletion broadcasts that flowed during the stationary drain
        // give the true drained low; without the fix the stream holds only the
        // initial full value, so there is nothing to climb from.
        float minSeen = states.Select(s => s.Ship.ShieldFraction).Min();

        // (1) The target's own client must observe the shield CLIMBED, without
        //     the ship having moved at all.
        Assert.True(lastSeen - minSeen >= 0.040f,
            $"own shield must climb on the wire while stationary: minSeen={minSeen:F3} lastSeen={lastSeen:F3} (server {targetShip.ShieldFraction:F3})");
        Console.WriteLine($"P391: target's own wire shield climbed {minSeen:F3} -> {lastSeen:F3} while stationary " +
                          $"(server reached {targetShip.ShieldFraction:F3}) over {regenTicks} regen ticks");

        // (2) PROVE it was stationary: every received position is the anchor.
        foreach (var st in states)
        {
            var d = (st.Ship.Position - anchorPos).Length();
            Assert.True(d <= 0.012f,
                $"target moved during regen (Δ={d:F4}) — climb must be attributed to motion, not the shield term");
        }

        // (3) The climb spans real representable steps — confirm we actually
        //     saw intermediate values climb (not a single jump at the end),
        //     i.e. multiple distinct shield fractions on the wire.
        var distinct = states.Select(s => s.Ship.ShieldFraction).Distinct().OrderBy(v => v).ToList();
        Console.WriteLine($"P391: distinct shield fractions observed on own wire = {distinct.Count} [{string.Join(", ", distinct.Select(v => v.ToString("F3")).Take(12))}{(distinct.Count > 12 ? "..." : "")}]");
        Assert.True(distinct.Count >= 3, "stationary regen must produce a monotone stream of distinct shield values, not a single value");
    }

    /// <summary>
    /// Scope guard (P391 item 3): a ship with NO mounted shield has
    /// <c>ShieldFraction ≡ 0</c>, so the new shield dirty term is a strict
    /// no-op — it must not fabricate per-tick rebroadcasts for every unshielded
    /// ship. A stationary unshielded ship should receive exactly ONE own-ship
    /// SINGLE_SHIP_UPDATE (the P332 forced first send) over many idle ticks,
    /// not a per-tick flood. Passes identically with and without the fix.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Stationary_unshielded_ship_generates_no_spurious_shield_rebroadcasts()
    {
        Require.Artwork();
        using var server = new Srv();
        // No custom spawner: the default spawn uses the core's first hull (an
        // Adv Miner) with NO weapon and NO shield mounted — unshielded.
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        ushort port = server.BoundPort;

        using var client = new ShieldClient();
        client.Connect(port, "SHD-NOSH"); client.Logon(SampleRequest("SHD-NOSH"));
        PumpUntil(server, () => client.ShipAssignment is not null, 4000);
        var set = client.ShipAssignment ?? throw new InvalidOperationException("no ship");
        short myShip = set.Ship.ShipId;

        // Command it neutral (stationary) and let ~12 s of idle sim elapse.
        client.CommandControls(ControlData.Neutral, 0);
        for (int i = 0; i < 240; i++) { server.TickSim(); Thread.Sleep(2); }

        var mine = client.States.Where(s => s.Ship.ShipId == myShip).ToList();
        // Exactly one broadcast is expected: the P332 forced first send. Every
        // received value must be ShieldFraction 0 (unshielded), and a per-tick
        // shield-spam flood (~240) is the failure mode this guards against.
        Assert.All(mine, s => Assert.Equal(0f, s.Ship.ShieldFraction));
        Console.WriteLine($"P391: stationary UNshielded ship received {mine.Count} own-ship updates over 240 idle ticks (expect exactly 1)");
        Assert.True(mine.Count <= 2, $"unshielded stationary ship got {mine.Count} own-ship updates — spurious rebroadcast flood");
    }
}
