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
/// P323 M6 — the oxygen-death "ghost ship" finalization live proof. Native's
/// KillShipEvent removes a dying player ship from its cluster unconditionally
/// (<c>pship-&gt;SetCluster(NULL)</c>, FedSrv.CPP:12045) BEFORE deciding whether
/// a station exists to respawn at; the port's combat path zeroes the hull
/// fraction (<c>Ship.ReceiveDamage</c>) and <c>Sector.ResolveCombatCollisions</c>
/// removes the dead ship. But the oxygen-death path (<c>Ship.OnUpdate</c> →
/// KillShipEvent) performed NEITHER, and <c>Server.BroadcastKillShip</c>'s
/// "no friendly station — no respawn, stays dead" branch used to log and return
/// WITHOUT removing anything — leaving the lifepod alive in its sector's
/// <c>_models</c>, still flying / broadcast every tick: a permanent
/// server/client desync. P323 wires <c>FinalizeDeadShip</c> (mark Dead + remove
/// from the sector) into every "stays dead" branch.
///
/// This test reproduces the exact scenario the review named — a drifting
/// lifepod's oxygen timer expiring when no friendly station exists to respawn
/// at — and proves the ship is genuinely finalized server-side: Dead, out of
/// every sector's <c>_models</c>, absent from every sector's real per-tick
/// ship-update candidate list (not ticking/broadcasting any more). It reaches
/// the no-friendly-station branch by putting the drifting pod on a side that
/// FindFriendlyStation cannot match — the port's "team's last station destroyed"
/// stand-in (a ship whose <c>SideId</c> is null, or whose side owns no station,
/// both make <c>FindFriendlyStation</c> return null; the branch is identical).
/// The [Collection("LiveNetwork")] tag keeps it sequential with the rest.
/// </summary>
[Collection("LiveNetwork")]
public class LiveShipDeathFinalizeTests
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
            new Gm { Name = "Ship Death Finalize", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 882u, DwCookie = 0x882u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7510000000000000000ul, DrmHash = "finl",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private LogonAck? _ack;
        private SetCluster? _set;
        private readonly List<KillShip> _kills = new();
        private readonly List<ShipDelete> _deletes = new();
        private readonly List<EnterLifepod> _pods = new();

        public LogonAck? Ack { get { lock (_gate) return _ack; } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
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
                        case KillShip.Fmid: _kills.Add(KillShip.Decode(m)); break;
                        case ShipDelete.Fmid: _deletes.Add(ShipDelete.Decode(m)); break;
                        case EnterLifepod.Fmid: _pods.Add(EnterLifepod.Decode(m)); break;
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    /// <summary>⭐ P323 M6 PRIMARY LIVE PROOF. A peer-owned, already-Lifepod ship
    /// drifts in a station-less sector with its oxygen timer test-compressed to
    /// 1 s. When that timer expires there is NO friendly station to respawn at
    /// (<c>FindFriendlyStation</c> returns null — see the class doc on the
    /// "last station destroyed" stand-in), so BroadcastKillShip's "no friendly
    /// station, stays dead" branch runs. The fix must finalize the death
    /// server-side: the ship becomes <see cref="Ship.Dead"/>, is removed from
    /// every sector's <c>_models</c>, and no longer appears in any sector's real
    /// per-tick candidate list — it is genuinely gone (no longer flying /
    /// mining / shooting / broadcast), not the pre-P323 ghost that stayed in
    /// <c>_models</c> and kept broadcasting every tick. The wire still carries
    /// the death (KILL_SHIP with launcher NULL + SHIP_DELETE), and NO
    /// ENTER_LIFEPOD arrives (no respawn).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Oxygen_dead_lifepod_with_no_friendly_station_is_finalized_server_side()
    {
        Require.Artwork();
        using var server = new Srv();
        Sector? noStationSector = null;
        var spawnCount = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            noStationSector ??= mission.Sectors.First(s => s.Stations.Count == 0);
            // The peer's ship is ALREADY a Lifepod, on a real mission side (so the
            // whole boot/assignment path is a normal side-ful peer).
            var hull = core.HullTypes[mission.Sides[0].Civilization.Lifepod];
            Vector3 pos = new(3000f, 0f, 0f);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity)
            {
                SideId = mission.Sides[0].Id,
            };
            mission.PlaceShip(ship, noStationSector);
            spawnCount++;
            return (ship, noStationSector);
        };
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;   // sim tick reaches OnUpdate (oxygen check)

        using var victim = new WireClient();
        victim.Connect(server.BoundPort, "FIN_VICTIM");
        victim.Logon(Request("FIN_VICTIM"));
        int conn = PumpUntil(server, () => victim.ShipAssignment is not null, 4000);
        Assert.True(conn < 4000, "no ship assignment");
        short victimId = victim.ShipAssignment!.Ship.ShipId;

        var mission = server.Missions[0];
        var victimShip = server.Ships.First(s => s.Ship.Id == victimId).Ship;
        Assert.True(victimShip.HullType.HasCapability(DataHullTypeIGC.HabmLifepod), "precondition: victim is a lifepod");
        Assert.Null(victimShip.StationId);                                        // drifting, not docked
        Assert.Equal(noStationSector!.Id, victimShip.Sector!.Id);                  // in the station-less sector

        // Arm the oxygen countdown at 1 s (real 300 s) — the P279 test seam — then
        // null the pod's side so FindFriendlyStation returns null: the port's
        // stand-in for "the team's last station is gone, nowhere to respawn".
        victimShip.LifepodEnduranceOverrideSeconds = 1.0;
        victimShip.ChangeHull(victimShip.HullType);      // re-arm the timer with the short override
        Assert.True(victimShip.LifepodTimeToDie is not null, "oxygen timer armed on the drifting pod");
        victimShip.SideId = null;                        // no friendly station to respawn at

        // Pump until the pod's oxygen expires and the death is finalized.
        int died = PumpUntil(server, () => victimShip.Dead, 4000);
        Assert.True(died < 4000, "oxygen expiry did not finalize the ship");

        // The wire death actually fired — KILL_SHIP with launcher NULL (died of
        // oxygen, no killer) + SHIP_DELETE — and no respawn (no ENTER_LIFEPOD).
        // Server finalization precedes the independent TCP reader. Await both
        // generated events without advancing simulation or assuming receipt.
        Assert.True(SpinWait.SpinUntil(() => victim.Kills.Any(k => k.ShipId == victimId)
            && victim.Deletes.Any(d => d.ShipId == victimId), TimeSpan.FromSeconds(30)),
            "victim must decode both death and deletion after oxygen expiry");
        var kill = victim.Kills.FirstOrDefault(k => k.ShipId == victimId);
        Assert.NotNull(kill);
        Assert.Equal((short)-1, kill!.IdCredit);
        Assert.Contains(victim.Deletes, d => d.ShipId == victimId && d.Sdr == ShipDelete.SdrKilled);
        Assert.Empty(victim.Pods);

        // ⭐ THE P323 FINALIZATION: the ship is Dead, has left its sector, and is
        // in NO mission sector's _models and NO sector's real per-tick candidate
        // list — the opposite of the pre-P323 ghost (which stayed in _models,
        // still flying and broadcast every tick).
        Assert.True(victimShip.Dead, "ship must be Dead after the no-respawn oxygen death");
        Assert.Null(victimShip.Sector);
        foreach (var sec in mission.Sectors)
        {
            Assert.False(sec.Models.Any(m => ReferenceEquals(m, victimShip)),
                $"ship {victimId} must not remain in sector {sec.Id} _models after finalization");
            Assert.False(sec.GetShipUpdateCandidates().Any(c => ReferenceEquals(c, victimShip)),
                $"ship {victimId} must not still tick (ship-update candidate) in sector {sec.Id}");
        }

        // Pump well past the death — the fire-once guard holds and the ship does
        // NOT resurrect into any sector (no re-add, no re-broadcast as alive).
        PumpUntil(server, () => false, 120);
        Assert.True(victimShip.Dead);
        Assert.Null(victimShip.Sector);
        foreach (var sec in mission.Sectors)
            Assert.False(sec.Models.Any(m => ReferenceEquals(m, victimShip)),
                $"ship {victimId} must stay finalized (no resurrection) in sector {sec.Id}");

        Console.WriteLine($"P323-M6: drifting lifepod {victimId} oxygen-expired with no friendly station after {died} ticks; " +
                          $"finalized server-side: Dead={victimShip.Dead}, Sector=null, in 0 of {mission.Sectors.Count} sector _models lists, " +
                          $"0 ship-update candidates, KILL_SHIP IdCredit={(short)-1}, SHIP_DELETE sent, no ENTER_LIFEPOD — ghost eliminated");
    }
}
