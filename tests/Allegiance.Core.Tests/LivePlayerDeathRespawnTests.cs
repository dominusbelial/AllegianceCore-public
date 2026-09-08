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
/// P105 verification — the player-death auto-respawn + <c>FM_S_ENTER_LIFEPOD(166)</c>
/// slice, hooked at the exact seam P103 wired (<c>ShipKilled</c> →
/// <c>BroadcastKillShip</c>, <c>Server.cs:266</c>). Four layers:
///  (1) codec tests pin the bytes of <c>ENTER_LIFEPOD(166)</c> (header 4 +
///      shipID short = 6, no padding);
///  (2) the live main proof — a real player ship dies (suicide) with a real
///      friendly station present: it is relocated to the station's cluster,
///      its hull becomes the side's Lifepod hull, it is docked at the station,
///      and <b>only</b> the owning peer receives a real <c>ENTER_LIFEPOD</c>;
///      the same death still emits <c>SHIP_DELETE(20, SDR_KILLED)</c> +
///      <c>KILL_SHIP(105)</c> exactly as P103 proved (regression);
///  (3) a player death with <b>no</b> friendly station anywhere — no
///      ENTER_LIFEPOD, no respawn, matching native's <c>if (pstationDest)</c>
///      guard (FedSrv.CPP:12055);
///  (4) a non-player (bot) ship's death — <c>SHIP_DELETE</c>/<c>KILL_SHIP</c>
///      only, no respawn (native's <c>IsPlayer()</c> gate, FedSrv.CPP:12026).
/// Like the other live suites this drives a real in-process <see cref="Srv"/>
/// over loopback TCP; the [Collection("LiveNetwork")] tag keeps it sequential.
/// </summary>
[Collection("LiveNetwork")]
public class LivePlayerDeathRespawnTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Respawn Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 880u, DwCookie = 0x880u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8390000000000000000ul, DrmHash = "resp",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>Pick the first station in the mission (deterministic sector/
    /// station order) whose <see cref="DataStationIGC.SideId"/> is non-negative
    /// (a real side owns it) — the friendly station the respawn test targets.
    /// This mirrors the deliberate simplification in
    /// <see cref="Srv.FindFriendlyStation"/>: any one friendly match is fine.</summary>
    private static (DataStationIGC station, Sector sector) FirstFriendlyStation(Mission mission)
    {
        foreach (var sector in mission.Sectors)
            foreach (var station in sector.Stations)
                if (station.SideId >= 0)
                    return (station, sector);
        throw new InvalidOperationException("no friendly (SideId>=0) station in mission");
    }

    /// <summary>A short side id absent from every station's <see cref="DataStationIGC.SideId"/>
    /// in the mission — used for the no-friendly-station case.</summary>
    private static short AbsentSideId(Mission mission)
    {
        var used = mission.Sectors.SelectMany(s => s.Stations).Select(st => st.SideId).ToHashSet();
        short candidate = 551;
        while (used.Contains(candidate)) candidate++;
        return candidate;
    }

    // ── the new codec ────────────────────────────────────────────────────

    /// <summary><c>FM_S_ENTER_LIFEPOD</c>(166) round-trips the shipID short at
    /// offset 4: encode emits exactly <c>{ cbmsg=6, fmid=166, shipID }</c> and
    /// decode recovers it (Messages.h:886-888 — the struct carries a
    /// <c>ShipID shipID</c> field; the native sender leaves it unset,
    /// FedSrv.CPP:12061, but we populate it with the dying ship's id).</summary>
    [Fact]
    public void EnterLifepod_message_round_trips()
    {
        var msg = EnterLifepod.ForPlayer(0x0383 /* 899 */);
        var d = EnterLifepod.Decode(msg.Encode());
        Assert.Equal(EnterLifepod.Fmid, d.GetFmid());
        Assert.Equal((short)0x0383, d.ShipId);
    }

    /// <summary>Hand-composed 6-byte buffer (header 4 + shipID short) matches
    /// the encoder exactly, composed from <c>Messages.h:886-888</c> and the
    /// native struct layout (the <c>ShipID</c> is a <c>short</c> ObjectID,
    /// <c>igc.h:477,480</c>) — not from the encoder.</summary>
    [Fact]
    public void EnterLifepod_message_emits_exact_declared_bytes()
    {
        var got = new EnterLifepod { ShipId = 0x0103 /* 259 */ }.Encode();
        Assert.Equal(6, got.Length);
        Assert.Equal(new byte[]
        {
            6, 0,          // cbmsg
            166, 0,        // fmid
            0x03, 0x01,    // shipID (short, LE)
        }, got);

        var d = EnterLifepod.Decode(got);
        Assert.Equal((short)0x0103, d.ShipId);
    }

    // ── the wire client (records ENTER_LIFEPOD too) ───────────────────────

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
        public void SendSuicide() => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new Suicide().Encode() }), guaranteed: true);

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

    // ── live proofs ────────────────────────────────────────────────────────

    /// <summary>⭐ A real player ship dies (suicide) with a real friendly
    /// station present in the mission. Assert the native death-respawn
    /// (<c>FedSrv.CPP:12026-12074</c>): the ship leaves its old sector (lands
    /// in the station's cluster), its hull becomes the side's Lifepod hull, it
    /// ends up docked at the friendly station, and the owning peer — <b>and
    /// only that peer</b> — receives a real <c>ENTER_LIFEPOD(166)</c>. The same
    /// death still produces <c>SHIP_DELETE(20,SDR_KILLED)</c> + <c>KILL_SHIP(105)</c>
    /// to both peers exactly as P103 proved (no regression).</summary>
    [Fact]
    public void Player_death_relocates_to_docked_lifepod_and_only_owner_gets_enter_lifepod()
    {
        using var server = new Srv();
        Sector? noStationSector = null;
        var placedObserver = false;
        server.ShipSpawner = (core, mission, id) =>
        {
            // Spawn both peers in a sector that has NO stations — the friendly
            // station lives in another cluster (exercising native's
            // c_ttAnyCluster scan). This makes the respawn's relocation
            // provable: the victim starts HERE and must end up in the
            // station's cluster. Clear of DockRadius (200) so they fly.
            noStationSector ??= mission.Sectors.First(s => s.Stations.Count == 0);
            var (station, _) = FirstFriendlyStation(mission);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            Vector3 pos = (placedObserver
                ? new Vector3(3000f, 0f, -300f)
                : new Vector3(3000f, 0f, 0f));
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = station.SideId;              // a side that owns a station
            mission.PlaceShip(ship, noStationSector);  // both peers in the SAME station-less sector
            placedObserver = true;
            return (ship, noStationSector);
        };
        server.Boot(Config());

        using var victim = new WireClient();
        using var observer = new WireClient();
        victim.Connect(server.BoundPort, "VICTIM"); observer.Connect(server.BoundPort, "OBSERVER");
        victim.Logon(Request("VICTIM")); observer.Logon(Request("OBSERVER"));
        PumpUntil(server, () => victim.ShipAssignment is not null && observer.ShipAssignment is not null, 4000);

        short victimId = victim.ShipAssignment!.Ship.ShipId;
        var mission = server.Missions[0];
        var (station, _) = FirstFriendlyStation(mission);
        var side = mission.Sides.First(s => s.Id == station.SideId);
        short lifepodId = side.Civilization.Lifepod;
        Assert.True(mission.Core.HullTypes.ContainsKey(lifepodId), "civ Lifepod hull id must resolve in the core");
        var origSectorId = noStationSector!.Id;
        var preHull = server.Ships.First(s => s.Ship.Id == victimId).Ship.HullType;
        Assert.Equal(origSectorId, server.Ships.First(s => s.Ship.Id == victimId).Ship.Sector!.Id);

        victim.SendSuicide();
        int ticks = PumpUntil(server,
            () => victim.Pods.Any(p => p.ShipId == victimId),
            2000);
        Assert.True(ticks < 2000, "no ENTER_LIFEPOD within ticks");

        var victimShip = server.Ships.First(s => s.Ship.Id == victimId).Ship;
        Assert.Equal(lifepodId, victimShip.HullType.HullId);                    // hull swapped to Lifepod
        Assert.NotEqual(preHull.HullId, victimShip.HullType.HullId);            // actually changed
        Assert.NotEqual(origSectorId, victimShip.Sector!.Id);                    // LEFT the old (station-less) sector
        Assert.Equal(station.ClusterId, victimShip.Sector!.Id);                 // and landed in the station's cluster
        Assert.Equal(station.StationId, victimShip.StationId);                   // docked at the friendly station

        // The owning peer alone got ENTER_LIFEPOD(166), naming its own ship.
        Assert.Contains(victim.Pods, p => p.ShipId == victimId);
        Assert.Empty(observer.Pods);

        // No regression: the same death still emitted SHIP_DELETE(20,SDR_KILLED)
        // + KILL_SHIP(105) to BOTH peers in the victim's former sector.
        Assert.Contains(victim.Kills, k => k.ShipId == victimId && k.BDeathCredit);
        Assert.Contains(observer.Kills, k => k.ShipId == victimId);
        Assert.Contains(victim.Deletes, d => d.ShipId == victimId && d.Sdr == ShipDelete.SdrKilled);
        Assert.Contains(observer.Deletes, d => d.ShipId == victimId && d.Sdr == ShipDelete.SdrKilled);

        var pod = victim.Pods.First();
        var dl = victim.Deletes.First(d => d.ShipId == victimId);
        Console.WriteLine($"[respawn] player {victimId} died after {ticks} ticks; hull {preHull.HullId} -> Lifepod {victimShip.HullType.HullId}; " +
                          $"docked at station {victimShip.StationId} in sector {victimShip.Sector.Id} (old sector {origSectorId})");
        Console.WriteLine($"[respawn] ENTER_LIFEPOD(166) shipID={pod.ShipId} = {BitConverter.ToString(pod.Encode())}");
        Console.WriteLine($"[respawn] SHIP_DELETE(20) shipID={dl.ShipId} sdr={dl.Sdr}(SDR_KILLED) = {BitConverter.ToString(dl.Encode())}; ENTER_LIFEPOD to observer={(observer.Pods.Count > 0 ? "YES (BAD)" : "no")}");
    }

    /// <summary>⭐ A player death with <b>no</b> friendly station anywhere in
    /// the mission — assert no <c>ENTER_LIFEPOD</c> and no respawn, matching
    /// native's own <c>if (pstationDest)</c> guard (<c>FedSrv.CPP:12055</c>):
    /// the ship stays gone (whatever <c>SHIP_DELETE</c> already said).</summary>
    [Fact]
    public void Player_death_with_no_friendly_station_does_not_respawn()
    {
        using var server = new Srv();
        short absentSide = 0; int spawnCount = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (station, sector) = FirstFriendlyStation(mission);
            if (spawnCount == 0) absentSide = AbsentSideId(mission);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            Vector3 pos = StationPos(station) + (spawnCount == 0
                ? new Vector3(3000f, 0f, 0f)
                : new Vector3(3000f, 0f, -300f));
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = absentSide;                      // a side owning NO station anywhere
            mission.PlaceShip(ship, sector);
            spawnCount++;
            return (ship, sector);
        };
        server.Boot(Config());

        using var victim = new WireClient();
        using var observer = new WireClient();
        victim.Connect(server.BoundPort, "VOID"); observer.Connect(server.BoundPort, "BASE");
        victim.Logon(Request("VOID")); observer.Logon(Request("BASE"));
        PumpUntil(server, () => victim.ShipAssignment is not null && observer.ShipAssignment is not null, 4000);
        short victimId = victim.ShipAssignment!.Ship.ShipId;
        var preHull = server.Ships.First(s => s.Ship.Id == victimId).Ship.HullType;

        victim.SendSuicide();
        int ticks = PumpUntil(server, () => victim.Kills.Any(k => k.ShipId == victimId), 4000);
        PumpUntil(server, () => false, 30);   // extra sim — a broadcast would land by now

        var victimShip = server.Ships.First(s => s.Ship.Id == victimId).Ship;
        Assert.True(ticks < 4000, "suicide did not kill the ship");
        Assert.Empty(victim.Pods);                            // no ENTER_LIFEPOD
        Assert.Empty(observer.Pods);
        Assert.Null(victimShip.StationId);                    // NOT docked — no respawn
        Assert.Equal(preHull.HullId, victimShip.HullType.HullId); // hull unchanged (no lifepod swap)

        // No regression: SHIP_DELETE(20,SDR_KILLED)+KILL_SHIP(105) still flow.
        Assert.Contains(victim.Kills, k => k.ShipId == victimId && k.BDeathCredit);
        Assert.Contains(victim.Deletes, d => d.ShipId == victimId && d.Sdr == ShipDelete.SdrKilled);
        Console.WriteLine($"[respawn] player {victimId} (side {absentSide}, no station) died after {ticks} ticks — no ENTER_LIFEPOD, no respawn: SHIP_DELETE only.");
    }

    /// <summary>⭐ A <b>non-player</b> (bot) ship's death does not trigger any
    /// respawn — <c>SHIP_DELETE</c>/<c>KILL_SHIP</c> only, matching P103. The
    /// bot has a friendly station present AND a valid side (so only the
    /// <c>IsPlayer()</c> gate, not a missing station, blocks the respawn),
    /// but no peer owns it — proving the discriminator is peer-ownership.</summary>
    [Fact]
    public void Bot_ship_death_never_triggers_respawn()
    {
        using var server = new Srv();
        var injectedBot = false;
        short botId = 9701;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (station, sector) = FirstFriendlyStation(mission);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            // Peer placed clear of DockRadius (200) so it stays flying, in the
            // same sector as the bot and the friendly station.
            var ship = new Ship(id, hull, StationPos(station) + new Vector3(3000f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            ship.SideId = station.SideId;
            mission.PlaceShip(ship, sector);
            if (!injectedBot)
            {
                // A bot ship: owned by NO peer, same sector + side as the peer.
                var botHull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
                var bot = new Ship(botId, botHull, StationPos(station) + new Vector3(3000f, 0f, -300f), Vector3.Zero, Orientation.Identity);
                bot.SideId = station.SideId;
                mission.PlaceShip(bot, sector);
                injectedBot = true;
            }
            return (ship, sector);
        };
        server.Boot(Config());

        using var peer = new WireClient();
        peer.Connect(server.BoundPort, "PEER");
        peer.Logon(Request("PEER"));
        PumpUntil(server, () => peer.ShipAssignment is not null, 4000);
        short peerId = peer.ShipAssignment!.Ship.ShipId;

        // Kill the bot (peer-owned observer same sector → receives the broadcast).
        var bot = server.Missions[0].Sectors
            .SelectMany(s => s.Models)
            .OfType<Ship>()
            .First(s => s.Id == botId);
        server.TickSim();                                     // ensure placed/ticking context is settled
        bot.ReceiveDamage(0, bot.HitPoints * 2f, server.Missions[0].LastUpdate, bot.Position, Vector3.Zero, null);

        // The peer saw the bot die via KILL_SHIP + SHIP_DELETE, but NO ENTER_LIFEPOD.
        int quiet = PumpUntil(server, () => false, 30);
        Assert.Empty(peer.Pods);
        Assert.Contains(peer.Kills, k => k.ShipId == botId && k.BDeathCredit);
        Assert.Contains(peer.Deletes, d => d.ShipId == botId && d.Sdr == ShipDelete.SdrKilled);

        var botShip = server.Missions[0].Sectors.SelectMany(s => s.Models).OfType<Ship>().FirstOrDefault(s => s.Id == botId);
        Assert.True(botShip is null || botShip.StationId is null, "bot must not end up docked/respawned");
        Console.WriteLine($"[respawn] bot {botId} died (side {bot.SideId}, friendly station present); peer {peerId} saw SHIP_DELETE/KILL_SHIP only; {quiet} quiet ticks — no respawn.");
    }
}
