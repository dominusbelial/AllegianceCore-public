using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Server;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P739PeriodicTests
{
    private static StaticCore Core() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
    private static Mission World(double initialTime = 0)
    {
        var core = Core();
        return Mission.Assemble(core,
            MissionMap.FromLists([DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 }], [], [], []),
            [new SideSpec(0, "A", core.Civilizations.Keys.First()), new SideSpec(1, "B", core.Civilizations.Keys.First()),
             new SideSpec(2, "C", core.Civilizations.Keys.First())], new NullIgcSite(), initialTime);
    }
    private static object? Invoke(Srv server, string name, params object[] args) =>
        typeof(Srv).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, args);
    private static T Field<T>(Srv server, string name) => (T)typeof(Srv)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
    private static ServerConfig Config(bool empty = true) => new()
    {
        ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        LiveSettings = new() { AutoNextMatch = new() { Enabled = false } },
        Games = [new() { Name = "P739", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
            SideNames = ["A", "B"], AllowEmptyTeams = empty }]
    };

    // FedSrv/fsmission.cpp:5817-5834: equality does not expire a pending vote;
    // all-votes-in still resolves early and cancellation suppresses resolution.
    [Fact]
    public void Ballot_strict_expiry_all_votes_and_cancellation()
    {
        var ballot = new PendingBallot { Expiration = 30 };
        ballot.AddEligibleVoter(1, 0);
        Assert.False(ballot.ShouldResolve(29.999));
        Assert.False(ballot.ShouldResolve(30));
        Assert.True(ballot.ShouldResolve(30.001));
        Assert.True(ballot.TryCastVote(1, 0, true));
        Assert.True(ballot.ShouldResolve(1)); Assert.True(ballot.HasPassed());
        ballot.Canceled = true;
        Assert.False(ballot.ShouldResolve(100));
    }

    // FedSrv/fsmission.cpp:2339-2342,2468-2504: strict boundary, kills then
    // fewer deaths; money cannot break an otherwise complete tie.
    [SkippableFact]
    public void Countdown_strict_expiry_stage_and_kills_deaths_draw()
    {
        Require.Artwork(); var m = World(); m.CountdownDuration = 20; m.CountdownStart = 10;
        Assert.False(m.EvaluateCountdown(100).Expired);
        m.Stage = MissionStage.Starting; Assert.False(m.EvaluateCountdown(100).Expired);
        m.Stage = MissionStage.Started;
        Assert.False(m.EvaluateCountdown(30).Expired);
        Assert.True(m.EvaluateCountdown(30.001).Expired);
        m.Sides[0].Kills = 4; m.Sides[1].Kills = 4; m.Sides[2].Kills = 3;
        m.Sides[0].Deaths = 2; m.Sides[1].Deaths = 1;
        Assert.Same(m.Sides[1], m.EvaluateCountdown(31).Winner);
        m.Sides[0].Deaths = 1; m.Sides[0].Money = 999999;
        Assert.Null(m.EvaluateCountdown(31).Winner);
        m.Stage = MissionStage.Over; Assert.False(m.EvaluateCountdown(100).Expired);
    }

    [SkippableFact]
    public void Expired_match_suppresses_due_payday_but_exact_timeout_boundary_does_not()
    {
        Require.Artwork(); var expired = World(); expired.Stage = MissionStage.Started;
        expired.CountdownDuration = 10; expired.NextPaydayTime = 10;
        expired.Update(10.001);
        Assert.All(expired.Sides, side => Assert.Equal(0, side.Money));
        Assert.Equal(10, expired.NextPaydayTime);
        var boundary = World(); boundary.Stage = MissionStage.Started;
        boundary.CountdownDuration = 10; boundary.NextPaydayTime = 10;
        boundary.Update(10);
        Assert.All(boundary.Sides, side => Assert.True(side.Money > 0));
        Assert.Equal(70, boundary.NextPaydayTime);
    }

    // FedSrv/fsmission.cpp:2348-2465: priorities operate on all sides. Test
    // authored competing leaders, and ties falling through every criterion.
    [SkippableTheory]
    [InlineData("conquest", 0)] [InlineData("territory", 1)]
    [InlineData("prosperity", 2)] [InlineData("artifacts", 0)]
    [InlineData("flags", 1)] [InlineData("kills", 2)]
    public void Countdown_obeys_native_victory_precedence(string criterion, int expected)
    {
        Require.Artwork(); var m = World(); m.Stage = MissionStage.Started; m.CountdownDuration = 1;
        string[] order = ["conquest", "territory", "prosperity", "artifacts", "flags", "kills"];
        int start = Array.IndexOf(order, criterion);
        m.ConquestEnabled = start <= 0; m.TerritoryEnabled = start <= 1;
        m.ProsperityEnabled = start <= 2; m.ArtifactEnabled = start <= 3; m.FlagsEnabled = start <= 4;
        var type = DataStationTypeIGC.Decode(new byte[DataStationTypeIGC.Size]) with
        { StationTypeId = 30000, SabmCapabilities = DataStationTypeIGC.SabmFlag, MaxArmorHitPoints = 100 };
        m.PlaceStation(new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        { StationId = 30000, SideId = 0, ClusterId = 0, Position = [0, 0, 0] }, type), m.Sectors[0]);
        m.Sides[1].TerritoryCount = 3;
        foreach (var side in m.Sides)
        {
            var bucket = side.Buckets.First(b => b.Development?.DevelopmentId == Mission.TeamMoneyDevelopmentId);
            bucket.SetTimeAndMoney(side.Id == 2 ? 20000 : 10000, 1000);
        }
        m.Sides[0].Artifacts = 4; m.Sides[1].Flags = 5; m.Sides[2].Kills = 9;
        var result = m.EvaluateCountdown(2);
        Assert.Equal(criterion, result.Criterion); Assert.Same(m.Sides[expected], result.Winner);
        // Tie the selected metric, then the next metric wins across all sides.
        if (criterion == "territory")
        {
            m.Sides[0].TerritoryCount = 3;
            Assert.Same(m.Sides[2], m.EvaluateCountdown(2).Winner);
        }
    }

    // Igc/igc.h:5081-5095: one retirement per outer update, including equality.
    [SkippableFact]
    public void Damage_slots_retire_one_boundary_per_call_and_sort_only_on_damage()
    {
        Require.Artwork(); var hull = Core().HullTypes.Values.First();
        var a = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        var b = new Ship(2, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        var track = new DamageTrack(0); track.ApplyDamage(0, a, 100);
        track.Update(40); Assert.Same(a, track.TopDealer);
        for (int i = 0; i < 6; i++) track.Update(40);
        Assert.Same(a, track.TopDealer);
        track.Update(40); Assert.Null(track.TopDealer);
        var ranked = new DamageTrack(0); ranked.ApplyDamage(0, a, 100);
        ranked.Update(5); ranked.ApplyDamage(5, b, 50); ranked.ApplyDamage(5, a, 1);
        for (int i = 2; i <= 8; i++) ranked.Update(i * 5);
        Assert.Same(a, ranked.TopDealer); // native deliberately keeps the old rank
        ranked.ApplyDamage(40, b, 0); Assert.Same(b, ranked.TopDealer);
    }

    [SkippableFact]
    public void Mission_damage_phase_is_shared_once_before_chunks_and_seeded_at_rotation_time()
    {
        Require.Artwork(); var m = World(100);
        var clock = typeof(Mission).GetProperty("DamageClock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(m)!;
        long Generation() => (long)clock.GetType().GetProperty("Generation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
        m.Stage = MissionStage.Started;
        m.Update(104.99); Assert.Equal(0, Generation());
        m.Update(105); Assert.Equal(1, Generation());
        m.Update(145); Assert.Equal(2, Generation()); // many sector chunks, one slot
        m.Update(145); Assert.Equal(2, Generation());
        m.Stage = MissionStage.NotStarted; m.Update(150); Assert.Equal(2, Generation());
    }

    [SkippableFact]
    public void Ships_damaged_at_different_times_share_mission_slot_phase()
    {
        Require.Artwork(); var m = World(); m.Stage = MissionStage.Started;
        var hull = m.Core.HullTypes.Values.First(h => h.HitPoints > 100);
        var launcher = new Ship(1, hull, new Vector3(90000, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 2 };
        var a = new Ship(2, hull, new Vector3(80000, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        var b = new Ship(3, hull, new Vector3(70000, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 1 };
        foreach (var ship in new[] { launcher, a, b }) m.PlaceShip(ship, m.Sectors[0]);
        byte damageType = (byte)Enumerable.Range(0, 20).First(i => m.Core.Constants.DamageConstants[i][hull.DefenseType] > 0);
        DamageTrack Track(Ship ship) => (DamageTrack)typeof(Ship)
            .GetField("_damageTrack", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ship)!;
        m.Update(6); a.ReceiveDamage(damageType, 1, 6, a.Position, launcher.Position, launcher);
        m.Update(11); b.ReceiveDamage(damageType, 1, 11, b.Position, launcher.Position, launcher);
        m.Update(41); Assert.Same(launcher, Track(a).TopDealer); Assert.Same(launcher, Track(b).TopDealer);
        for (int i = 42; i <= 47; i++) m.Update(i);
        Assert.Null(Track(a).TopDealer); Assert.Same(launcher, Track(b).TopDealer);
        m.Update(50); Assert.Null(Track(b).TopDealer);
    }

    // Igc/mineigc.cpp:150-156: the mine still exists at exact expiry.
    [SkippableFact]
    public void Mine_survives_exact_expiry_and_is_removed_after_it()
    {
        Require.Artwork(); var m = World(); var sector = m.Sectors[0];
        var type = m.Core.MineTypes.Values.First() with { Lifespan = 10 };
        var mine = new Mine(30000, sector, type, Vector3.Zero, 1); sector.Add(mine);
        mine.Update(10.999); Assert.Contains(mine, sector.Models);
        mine.Update(11); Assert.Contains(mine, sector.Models);
        mine.Update(11.001); Assert.DoesNotContain(mine, sector.Models);
    }

    // FedSrv.CPP:8237-8243: strict expiry and players-or-arena permission.
    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void Start_countdown_requires_strict_expiry_and_players_or_arena_policy(bool empty)
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false; server.Boot(Config(empty));
        var m = server.Missions[0]; m.Stage = MissionStage.NotStarted;
        Invoke(server, "StartCountdown", m, 15f);
        double deadline = Field<Dictionary<Mission, double>>(server, "_startCountdownDeadline")[m];
        Invoke(server, "AdvanceStartCountdowns", deadline); Assert.Equal(MissionStage.Starting, m.Stage);
        Invoke(server, "AdvanceStartCountdowns", deadline + .001);
        Assert.Equal(empty ? MissionStage.Started : MissionStage.Starting, m.Stage);
        if (empty) Assert.Equal(deadline, m.CountdownStart);
    }

    // FedSrv.CPP:8182-8232: briefing uses remaining time, cancellation waits
    // for zero, warning is emitted once, minimum occupancy is not force-readied.
    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void Restart_briefing_preserves_absolute_deadline_or_cancels_empty_match(bool empty)
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false; server.Boot(Config(empty));
        var m = server.Missions[0]; m.Stage = MissionStage.NotStarted;
        Field<Dictionary<Mission, double>>(server, "_startCountdownDeadline").Clear();
        Field<Dictionary<Mission, double>>(server, "_autoRestartDeadline")[m] = 60;
        Invoke(server, "AdvanceAutoRestarts", 29d); Assert.Equal(MissionStage.NotStarted, m.Stage);
        Invoke(server, "AdvanceAutoRestarts", 30d); Invoke(server, "AdvanceAutoRestarts", 31d);
        typeof(Srv).GetField("_simNow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, 50d);
        Invoke(server, "AdvanceAutoRestarts", 50d);
        Assert.Equal(empty ? MissionStage.Starting : MissionStage.NotStarted, m.Stage);
        if (empty)
        {
            Assert.Equal(60, Field<Dictionary<Mission, double>>(server, "_startCountdownDeadline")[m]);
        }
        else
        {
            Assert.All(m.Sides, side => Assert.True(side.Ready));
            Invoke(server, "AdvanceAutoRestarts", 60d); Assert.Equal(MissionStage.Over, m.Stage);
            Assert.DoesNotContain(m, Field<Dictionary<Mission, double>>(server, "_autoRestartDeadline").Keys);
        }
    }

    [SkippableFact]
    public void Match_timeout_uses_real_game_over_rotation_once()
    {
        Require.Artwork(); var cfg = Config(); cfg.Games[0].GameLengthSeconds = .1;
        using var server = new Srv(); server.SpawnInitialMinersAtBoot = false; server.Boot(cfg);
        var old = server.Missions[0]; old.Stage = MissionStage.Started; old.Sides[1].Kills = 2;
        server.TickSim(); Assert.Same(old, server.Missions[0]);
        server.TickSim(); Assert.Same(old, server.Missions[0]);
        server.TickSim(); Assert.NotSame(old, server.Missions[0]);
        Assert.Equal(MissionStage.Over, old.Stage);
        var fresh = server.Missions[0]; server.TickSim(); Assert.Same(fresh, server.Missions[0]);
    }
}
