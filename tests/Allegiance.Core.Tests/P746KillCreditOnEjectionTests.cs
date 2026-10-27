using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;
using PlayerScore = Allegiance.Server.PlayerScore;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
[Trait("Requires", "Artwork")]
public sealed class P746KillCreditOnEjectionTests
{
    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private LogonAck? _ack;
        private SetCluster? _set;
        private GameOverPlayers? _results;
        public GameOverPlayers? Results { get { lock (_gate) return _results; } }
        private readonly List<ushort> _combatOrder = new();
        public IReadOnlyList<ushort> CombatOrder { get { lock (_gate) return _combatOrder.ToArray(); } }
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
                        case LogonAck.Fmid:
                            _ack = LogonAck.Decode(m);
                            if (_ack.FValidated)
                                _t.Send(ConnectionId.Server, FedPacket.Pack(new PositionReq { SideId = -1 }.Encode()), guaranteed: true);
                            break;
                        case SetCluster.Fmid: _set = SetCluster.Decode(m); break;
                        case Eject.Fmid: _ejects.Add(Eject.Decode(m)); _combatOrder.Add(fmid); break;
                        case KillShip.Fmid: _kills.Add(KillShip.Decode(m)); _combatOrder.Add(fmid); break;
                        case ShipDelete.Fmid: _deletes.Add(ShipDelete.Decode(m)); break;
                        case EnterLifepod.Fmid: _pods.Add(EnterLifepod.Decode(m)); break;
                        case GameOver.Fmid: _combatOrder.Add(fmid); break;
                        case GameOverPlayers.Fmid: _results = GameOverPlayers.Decode(m); break;
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    private sealed class Harness : IDisposable
    {
        public readonly Srv Server = new();
        public readonly List<WireClient> Clients = new();
        private readonly List<Ship> _ships = new();
        private readonly List<PlayerScore> _scores = new();
        public Mission Mission { get; }
        public Ship Victim => ShipOf(0);
        public Ship Killer => ShipOf(1);
        public Ship ShipOf(int i) => _ships[i];
        public PlayerScore Score(int i) => _scores[i];
        public Side KillerSide => Mission.Sides.Single(s => s.Id == Killer.SideId);
        public Side VictimSide => Mission.Sides.Single(s => s.Id == Victim.SideId);

        public Harness(int peers = 2)
        {
            Server.SpawnInitialMinersAtBoot = false;
            Server.ShipSpawner = (core, mission, id) =>
            {
                var hull = core.HullTypes.Values.OrderBy(h => h.HullId)
                    .First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod));
                // Stay inside the sector boundary and separate peers: ids
                // start at 8096, so scaling the full id spawns outside space.
                var ship = new Ship(id, hull, new Vector3(0, 1000, (id % 16) * 300), Vector3.Zero, Orientation.Identity)
                    { SideId = 0 };
                var sector = mission.Sectors.First(s => s.Stations.Count == 0);
                mission.PlaceShip(ship, sector);
                return (ship, sector);
            };
            Server.Boot(new Allegiance.Server.ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
                SideSelectAutoSeatSeconds = 30,   // P763: these WireClient peers never send POSITIONREQ
                Games = [
                    new() { Name = "P746", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
                    new() { Name = "Other mission", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
                ],
            });
            Mission = Server.Missions[0];
            foreach (var m in Server.Missions) m.Stage = MissionStage.Started;
            for (int i = 0; i < peers; i++)
            {
                var client = new WireClient(); Clients.Add(client);
                client.Connect(Server.BoundPort, $"P746-{i}");
                client.Logon(new LogonRequest
                {
                    CharacterName = $"P746-{i}", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL",
                    MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1,
                    SteamAuthTicket = new byte[16], DrmHash = "P746",
                });
                Wait(() => client.ShipAssignment is not null);
                var live = Server.Ships.Single(s => s.Ship.Id == client.ShipAssignment!.Ship.ShipId);
                _ships.Add(live.Ship); _scores.Add(live.PlayerScore);
            }
            for (int i = 0; i < peers; i++)
            {
                var ship = ShipOf(i);
                ship.Sector!.Remove(ship);
                var m = i == 3 ? Server.Missions[1] : Mission;
                var sector = i == 2 ? m.Sectors.First(s => s != Victim.Sector) : m.Sectors.First(s => s.Stations.Count == 0);
                m.PlaceShip(ship, sector);
                ship.SideId = (short)(i == 1 ? 1 : 0);
            }
        }

        public void Wait(Func<bool> done)
        {
            for (int n = 0; n < 5000 && !done(); n++) { Server.TickSim(); Thread.Sleep(1); }
            Assert.True(done(), "loopback receipt timed out");
        }
        public void Fire(IModelIgc? credit, float amount = 1) => Mission.Site.KillShipEvent(
            Mission.LastUpdate, Victim, credit, amount, Victim.Position, Vector3.Zero);
        public void Dispose()
        {
            foreach (var c in Clients) c.Dispose();
            Server.Dispose();
        }
    }

    [SkippableFact]
    public void Ejection_credits_player_side_rating_class_and_bonus_once_and_reaches_deadline_ranking()
    {
        Require.Artwork(); using var h = new Harness(4);
        h.Score(0).CombatRating = h.Score(1).CombatRating = 0.5f;
        h.Victim.AddKillBonus();
        // Drive lethal damage through the actual sim seam (not the helper).
        h.Victim.ReceiveDamage(0, h.Victim.HitPoints * 100, h.Mission.LastUpdate,
            h.Victim.Position, Vector3.Zero, h.Killer);
        h.Wait(() => h.Clients.Take(3).All(c => c.Kills.Count == 1)
            && h.Clients[0].Ejects.Count == 1 && h.Clients[1].Ejects.Count == 1);
        Assert.Equal(new ushort[] { KillShip.Fmid, Eject.Fmid }, h.Clients[0].CombatOrder);
        Assert.Equal(new ushort[] { KillShip.Fmid, Eject.Fmid }, h.Clients[1].CombatOrder);
        Assert.Equal(1, h.Score(1).Kills);
        Assert.Equal((short)1, h.KillerSide.Kills);
        Assert.Equal(0.50625f, h.Score(1).CombatRating, 5);
        Assert.Equal(0.49375f, h.Score(0).CombatRating, 5);
        Assert.Equal(1f, h.Score(1).PlayerKills + h.Score(1).MinerKills + h.Score(1).BuilderKills + h.Score(1).CarrierKills);
        Assert.Equal(1f, h.Killer.KillBonus);
        Assert.Equal(1f, h.Victim.KillBonus); // ejection preserves victim streak
        Assert.Equal(1, h.Score(0).Ejections);
        Assert.Equal(0, h.Score(0).Deaths);
        Assert.Equal((short)0, h.VictimSide.Deaths);
        Assert.True(h.Victim.HullType.HasCapability(DataHullTypeIGC.HabmLifepod));
        // The ranking consumes actual earned kills; earlier objectives disabled.
        h.Mission.Goals.ConquestPercentage = 0; h.Mission.Goals.TerritoryPercentage = 100; h.Mission.Goals.TeamMoney = 0;
        h.Mission.Goals.Artifacts = 0; h.Mission.Goals.Flags = 0;
        var result = h.Mission.EvaluateCountdown(h.Mission.LastUpdate, deadlineReached: true);
        Assert.Same(h.KillerSide, result.Winner); Assert.Equal("kills", result.Criterion);
        Assert.Contains($"{h.KillerSide.Id}:kills=1", h.Mission.CountdownValues("kills"));

        for (int i = 0; i < 3; i++)
        {
            var k = Assert.Single(h.Clients[i].Kills);
            Assert.True(k.BKillCredit); Assert.False(k.BDeathCredit);
            Assert.Equal(h.Killer.Id, k.IdCredit); Assert.Equal(h.Killer.SideId, k.SideIdKiller);
            Assert.Equal((short)IgcObjectType.Ship, k.TypeCredit);
            Assert.Empty(h.Clients[i].Deletes); Assert.Empty(h.Clients[i].Pods);
        }
        // Off-sector peers get mission KILL_SHIP, but no sector EJECT.
        Assert.NotSame(h.Victim.Sector, h.ShipOf(2).Sector);
        Assert.Empty(h.Clients[2].Ejects);
        // Give the other mission's receive queue time to drain, without a full sim second.
        for (int i = 0; i < 50; i++) { h.Server.TickSim(); Thread.Sleep(1); }
        Assert.Empty(h.Clients[3].Kills); Assert.Empty(h.Clients[3].Ejects);
        Assert.Equal(1, h.Score(1).Kills); Assert.Equal((short)1, h.KillerSide.Kills);
        Assert.Equal(0.50625f, h.Score(1).CombatRating, 5);

        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = h.Victim.Id, CharacterName = "Victim" });
        world.Apply(Assert.Single(h.Clients[2].Kills));
        Assert.Equal("Victim ejected", Assert.Single(world.FeedbackLines).Text);
        Assert.False(world.Ships[h.Victim.Id].IsDead); Assert.False(world.Ships[h.Victim.Id].IsGone);
    }

    [SkippableTheory]
    [InlineData("lifepod")]
    [InlineData("team")]
    [InlineData("sideless")]
    [InlineData("zero")]
    public void Ineligible_events_award_no_player_side_rating_class_or_bonus(string kind)
    {
        Require.Artwork(); using var h = new Harness();
        h.Score(0).CombatRating = h.Score(1).CombatRating = 0.5f;
        if (kind == "lifepod") h.Victim.ChangeHull(h.Mission.Core.HullTypes[h.VictimSide.Civilization.Lifepod]);
        if (kind == "team") h.Killer.SideId = h.Victim.SideId;
        if (kind == "sideless") h.Killer.SideId = null;
        h.Fire(h.Killer, kind == "zero" ? 0 : 1);
        if (kind != "zero") h.Wait(() => h.Clients[0].Kills.Count == 1);
        Assert.Equal(0, h.Score(1).Kills);
        Assert.All(h.Mission.Sides, s => Assert.Equal((short)0, s.Kills));
        Assert.Equal(0.5f, h.Score(0).CombatRating); Assert.Equal(0.5f, h.Score(1).CombatRating);
        Assert.Equal(0f, h.Score(1).PlayerKills + h.Score(1).MinerKills + h.Score(1).BuilderKills + h.Score(1).CarrierKills);
        Assert.Equal(0f, h.Killer.KillBonus);
        if (kind == "zero")
        {
            h.Wait(() => h.Clients[0].Ejects.Count == 1);
            Assert.Empty(h.Clients[0].Kills);
        }
        else
        {
            var k = Assert.Single(h.Clients[0].Kills);
            Assert.False(k.BKillCredit); Assert.Equal(kind == "lifepod", k.BDeathCredit);
            Assert.Equal(kind == "lifepod" ? 1 : 0, h.Score(0).Deaths);
            Assert.Equal(kind == "lifepod" ? 0 : 1, h.Score(0).Ejections);
            if (kind == "lifepod")
            {
                var world = new ClientWorld();
                world.Apply(new ExportShip { ShipId = h.Victim.Id, CharacterName = "Victim" }); world.Apply(k);
                Assert.Equal("Victim was killed", Assert.Single(world.FeedbackLines).Text);
                Assert.True(world.Ships[h.Victim.Id].IsDead);
            }
        }
    }

    [SkippableFact]
    public void Missing_eject_data_sends_death_credit_and_awards_killer_once()
    {
        Require.Artwork(); using var h = new Harness();
        h.Score(0).CombatRating = h.Score(1).CombatRating = 0.5f;
        // Exercise the existing cannot-eject fallback without removing the
        // victim's peer, mission or hull class from the scoring event.
        h.Victim.SideId = 774;
        h.Victim.AddKillBonus();
        h.Fire(h.Killer);
        h.Wait(() => h.Clients[0].Kills.Count == 1 && h.Clients[0].Deletes.Count == 1);
        var k = Assert.Single(h.Clients[0].Kills);
        Assert.True(k.BKillCredit); Assert.True(k.BDeathCredit);
        Assert.Empty(h.Clients[0].Ejects);
        Assert.Equal(0, h.Score(0).Ejections); Assert.Equal(1, h.Score(0).Deaths);
        Assert.Equal(0f, h.Victim.KillBonus);
        Assert.Equal(1, h.Score(1).Kills); Assert.Equal((short)1, h.KillerSide.Kills);
        Assert.Equal(0.50625f, h.Score(1).CombatRating, 5);
        Assert.Equal(0.49375f, h.Score(0).CombatRating, 5);
        Assert.Equal(1f, h.Killer.KillBonus);
    }

    [SkippableTheory]
    [InlineData(1f, false)]
    [InlineData(-1f, true)]
    public void Winning_kill_finishes_wire_and_victim_score_before_mission_rotation(float amount, bool death)
    {
        Require.Artwork(); using var h = new Harness();
        h.Mission.Goals.TeamKills = 1;
        h.Fire(h.Killer, amount);
        h.Wait(() => h.Clients[0].Results is not null);
        Assert.Equal(MissionStage.Over, h.Mission.Stage);
        Assert.Equal(death ? new ushort[] { KillShip.Fmid, GameOver.Fmid }
            : new ushort[] { KillShip.Fmid, Eject.Fmid, GameOver.Fmid }, h.Clients[0].CombatOrder);
        Assert.Equal((short)1, h.KillerSide.Kills);
        var victimResult = h.Clients[0].Results!.Entries.Single(e => e.CharacterName == "P746-0");
        var killerResult = h.Clients[0].Results!.Entries.Single(e => e.CharacterName == "P746-1");
        Assert.Equal((short)1, killerResult.Kills);
        Assert.Equal(death ? 0 : 1, h.Score(0).Ejections);
        Assert.Equal(death ? 1 : 0, h.Score(0).Deaths);
        Assert.Equal((short)(death ? 0 : 1), victimResult.Ejections);
        Assert.Equal((short)(death ? 1 : 0), victimResult.Deaths);
    }

    [SkippableTheory]
    [InlineData("station", IgcObjectType.Station)]
    [InlineData("probe", IgcObjectType.Probe)]
    [InlineData("mine", IgcObjectType.Mine)]
    [InlineData("turret", IgcObjectType.Ship)]
    public void Non_ship_and_turret_credit_preserves_identity_and_awards_side_once(string kind, IgcObjectType type)
    {
        Require.Artwork(); using var h = new Harness();
        const short id = 16000;
        IModelIgc credit = kind switch
        {
            "station" => new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
                { StationId = id, SideId = h.KillerSide.Id, Position = [0, 0, 0] },
                h.Mission.Core.StationTypes.Values.First()),
            "probe" => new Probe(id, h.Victim.Sector!, h.Mission.Core.ProbeTypes.Values.First(), Vector3.Zero,
                h.Mission.LastUpdate, h.KillerSide.Id),
            "mine" => new Mine(id, h.Victim.Sector!, h.Mission.Core.MineTypes.Values.First(), Vector3.Zero,
                h.Mission.LastUpdate, h.Killer),
            _ => h.Killer,
        };
        if (kind == "turret")
        {
            var parent = new Ship(id, h.Killer.HullType, h.Killer.Position, Vector3.Zero, Orientation.Identity)
                { SideId = h.Killer.SideId };
            h.Mission.PlaceShip(parent, h.Killer.Sector!);
            h.Killer.ParentShip = parent; h.Killer.TurretId = 0; parent.AddChildShip(h.Killer);
        }
        h.Mission.Goals.TeamKills = 1;
        h.Fire(credit);
        h.Wait(() => h.Clients[0].Kills.Count == 1);
        var k = Assert.Single(h.Clients[0].Kills);
        Assert.True(k.BKillCredit); Assert.False(k.BDeathCredit);
        Assert.Equal((short)type, k.TypeCredit);
        Assert.Equal(kind == "turret" ? h.Killer.Id : id, k.IdCredit);
        Assert.Equal(h.KillerSide.Id, k.SideIdKiller);
        Assert.Equal((short)1, h.KillerSide.Kills);
        Assert.Equal(kind == "turret" ? 1 : 0, h.Score(1).Kills);
        Assert.Equal(kind == "turret" ? 1f : 0f, h.Killer.KillBonus);
        Assert.Equal(MissionStage.Over, h.Mission.Stage);
        Assert.Equal(1, h.Score(0).Ejections); Assert.Equal(0, h.Score(0).Deaths);
    }
}
