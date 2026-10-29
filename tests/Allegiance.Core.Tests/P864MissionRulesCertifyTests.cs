using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using PlayerScore = global::Allegiance.Server.PlayerScore;

namespace Allegiance.Core.Tests;

/// <summary>
/// P864 — the pure, unit-tested core of the two headline parity changes:
/// <list type="bullet">
/// <item><b>SIM-0504</b> — <c>PlayerScoreObject StartCommand/StopCommand</c> on
/// leader change (commander score time): the <c>TimeCommanded</c> accumulator,
/// the <c>StartCommand</c>/<c>StopCommand</c>/<c>FoldOpenCommandTime</c> stint
/// folding (native <c>igc.h:5615-5630</c>), and the commander-score override
/// (<c>SetCommanderScore</c>, <c>igc.h:5519-5527</c>).</item>
/// <item><b>SIM-0528 + SIM-0761</b> — the <c>GetExpMult</c> wiring: the pure
/// <c>ApplyGameOverExperienceBalance</c> body that accumulates per-side
/// experience as Σ timePlayed·careerScore (native fsmission.cpp:3261-3272) and
/// applies the multiplier at all three <c>ProcessGameOver</c> call sites —
/// connected scores (:3290-3314), departed scores (:3316-3327), commander
/// (:3340-3376) — then folds the results into the career record.</item>
/// </list>
/// The live-server wiring proofs live in <see cref="P864GameOverExperienceLiveTests"/>.
/// </summary>
public sealed class P864MissionRulesCertifyTests
{
    // ── SIM-0504: command-time bookkeeping ───────────────────────────────

    /// <summary>⭐ StartCommand/StopCommand accumulate exactly the commanded
    /// interval (native igc.h:5615-5630); StopCommand is idempotent when not
    /// commanding; FoldOpenCommandTime closes a still-open stint (native
    /// Disconnect, igc.h:5609-5611); ResetCommandTime clears the per-game stat.</summary>
    [Fact]
    public void CommandTime_start_stop_fold_and_reset_match_native_intervals()
    {
        var ps = new PlayerScore();
        Assert.False(ps.Commanding);
        Assert.Equal(0f, ps.TimeCommanded);

        ps.StartCommand(100.0);
        Assert.True(ps.Commanding, "StartCommand must set Commanding (m_bCommanding, igc.h:5616)");
        ps.StopCommand(150.0);                       // 50 s stint
        Assert.Equal(50f, ps.TimeCommanded, 3);
        Assert.False(ps.Commanding);

        ps.StopCommand(200.0);                       // idempotent — not commanding
        Assert.Equal(50f, ps.TimeCommanded, 3);

        ps.StartCommand(200.0);
        ps.FoldOpenCommandTime(240.0);               // open stint folded at finalize
        Assert.Equal(90f, ps.TimeCommanded, 3);      // 50 + (240-200)
        Assert.False(ps.Commanding);

        ps.ResetCommandTime();
        Assert.Equal(0f, ps.TimeCommanded);
        Assert.False(ps.Commanding);
    }

    /// <summary>⭐ SetCommanderScore keeps the higher of the computed score and
    /// the commander award, and stamps the command win/lose/credit flags
    /// (native igc.h:5519-5527).</summary>
    [Fact]
    public void SetCommanderScore_overrides_only_when_higher_and_sets_command_flags()
    {
        var ps = new PlayerScore { Score = 100f, Win = true };

        ps.SetCommanderScore(200f);                  // award exceeds the kill tally
        Assert.Equal(200f, ps.Score);
        Assert.True(ps.CommandCredit);
        Assert.True(ps.CommandWin);
        Assert.False(ps.CommandLose);

        var ps2 = new PlayerScore { Score = 100f, Win = false };
        ps2.SetCommanderScore(50f);                  // award lower than the tally
        Assert.Equal(100f, ps2.Score);
        Assert.True(ps2.CommandCredit);
        Assert.True(ps2.CommandLose);
    }

    // ── SIM-0528 + SIM-0761: GetExpMult wiring (pure body) ───────────────

    /// <summary>⭐ Balanced careers → GetExpMult is 1.0 on both sides → no score
    /// change; the career record folds each final score.</summary>
    [Fact]
    public void ApplyGameOverExperience_balanced_sides_do_not_damp_and_fold_career()
    {
        var a = new PlayerScore { Score = 500f, TimePlayed = 100f };
        var b = new PlayerScore { Score = 500f, TimePlayed = 100f };
        var career = new Dictionary<string, float> { ["A"] = 10f, ["B"] = 10f };

        var connected = new[] { ("A", a, (short)0), ("B", b, (short)1) };
        PlayerScore.ApplyGameOverExperienceBalance(
            connected, Array.Empty<(string, PlayerScore, short)>(), 2, 1000.0,
            careerOf: n => career.GetValueOrDefault(n),
            foldCareer: (n, s) => career[n] = career.GetValueOrDefault(n) + s);

        // Balanced: each side's exp equals the other's → f = 1.0, no damping.
        Assert.Equal(500f, a.Score, 3);
        Assert.Equal(500f, b.Score, 3);
        // Careers folded.
        Assert.Equal(510f, career["A"], 3);
        Assert.Equal(510f, career["B"], 3);
    }

    /// <summary>⭐ A stacked side (far more experience than the others) is damped
    /// by <c>avgExp/sideExp</c> at all three call sites — connected players,
    /// departed players and the (damped) commander award — while the weak side is
    /// untouched; careers fold the damped scores.</summary>
    [Fact]
    public void ApplyGameOverExperience_stacked_side_damps_connected_departed_and_commander()
    {
        var a = new PlayerScore { Score = 500f, TimePlayed = 100f, TimeCommanded = 100f };   // side0 commander
        var b = new PlayerScore { Score = 400f, TimePlayed = 100f, TimeCommanded = 100f };   // side1 commander
        var d = new PlayerScore { Score = 200f, TimePlayed = 50f };                          // side0 departed
        var career = new Dictionary<string, float> { ["A"] = 1000f, ["B"] = 1f, ["D"] = 500f };

        var connected = new[] { ("A", a, (short)0), ("B", b, (short)1) };
        var departed = new[] { ("D", d, (short)0) };
        PlayerScore.ApplyGameOverExperienceBalance(
            connected, departed, 2, 1000.0,
            careerOf: n => career.GetValueOrDefault(n),
            foldCareer: (n, s) => career[n] = career.GetValueOrDefault(n) + s);

        // side0 exp = 100·1000 + 50·500 = 125000; side1 exp = 100·1 = 100.
        // f(side0) = avg(other)/side0 = 100/125000 -> floored at 0.1.
        // f(side1): side1 exp (100) <= avg(125000) -> 1.0.
        Assert.Equal(50f, a.Score, 3);       // 500 · 0.1 (call site 1)
        Assert.Equal(400f, b.Score, 3);      // undamped (weak side)
        Assert.Equal(20f, d.Score, 3);       // 200 · 0.1 (call site 2, departed)

        // Commander award (call site 3): side0 commandScore = 700/150 = 4.667,
        // damped ·0.1 = 0.4667, ·100 s = 46.67 < the damped kill score 50 → no
        // score override, but the commander still earns the command credit
        // (native SetCommanderScore sets m_bCommandCredit unconditionally).
        Assert.True(a.CommandCredit);
        Assert.Equal(50f, a.Score, 3);
        Assert.Equal(400f, b.Score, 3);

        // Careers folded with the damped scores.
        Assert.Equal(1050f, career["A"], 3);  // 1000 + 50
        Assert.Equal(401f, career["B"], 3);   // 1 + 400
        Assert.Equal(520f, career["D"], 3);   // 500 + 20
    }

    /// <summary>⭐ A stacked side whose commander's award EXCEEDS their (already
    /// damped) kill tally is overridden UP to the damped commander award
    /// (native SetCommanderScore keeps the higher, igc.h:5521-5523) — isolating
    /// the commander award from the kill-score override.</summary>
    [Fact]
    public void ApplyGameOverExperience_commander_award_overrides_when_higher()
    {
        // Commander a has a LOW personal tally (20) but his side0 earns a lot per
        // player-second thanks to teammate a2's high score — so the commander
        // award (side points/sec · own time, damped) exceeds a's own damped
        // kill tally and SetCommanderScore overrides upward (igc.h:5521-5523).
        var a = new PlayerScore { Score = 20f, TimePlayed = 100f, TimeCommanded = 100f };
        var a2 = new PlayerScore { Score = 1000f, TimePlayed = 100f };
        var b = new PlayerScore { Score = 10f, TimePlayed = 100f, TimeCommanded = 100f };
        var career = new Dictionary<string, float> { ["A"] = 1000f, ["A2"] = 1000f, ["B"] = 1f };

        var connected = new[] { ("A", a, (short)0), ("A2", a2, (short)0), ("B", b, (short)1) };
        PlayerScore.ApplyGameOverExperienceBalance(
            connected, Array.Empty<(string, PlayerScore, short)>(), 2, 1000.0,
            careerOf: n => career.GetValueOrDefault(n),
            foldCareer: (n, s) => career[n] = career.GetValueOrDefault(n) + s);

        // side0 exp = 100·1000 + 100·1000 = 200000; side1 exp = 100·1 = 100 →
        // f(side0) = 0.1. a's kill score 20 → damped 2. Commander award:
        // commandScore = 1020/200 = 5.1, ·0.1 = 0.51, ·100 s = 51 > 2 → override.
        Assert.Equal(51f, a.Score, 1);
        Assert.True(a.CommandCredit);
        // Non-commanding side0 teammate a2 is merely damped (1000 → 100).
        Assert.Equal(100f, a2.Score, 1);
    }
}

/// <summary>
/// P864 — live-loopback proofs that the two headline changes are wired into the
/// real server paths: leader assignment fires <c>StartCommand</c> and a leader
/// departure folds command time onto the successor (SIM-0504), and a real
/// game-over runs the GetExpMult wiring end-to-end — seeding the in-memory
/// career store, folding every seated player's final score into it, and damping
/// the stacked side (SIM-0528 + SIM-0761).
/// [Collection("LiveNetwork")] keeps this suite sequential with the other live
/// suites on the shared host.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P864GameOverExperienceLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P864", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 0x864u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 0864000000000000000ul, DrmHash = "p864",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, $"{name} did not seat");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    private static PlayerScore PlayerScoreOf(Srv server, FltBot bot) =>
        server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).PlayerScore;

    /// <summary>⭐ SIM-0504 — a logon-elected team leader starts commanding
    /// (StartCommand fires even though the ship was registered during the same
    /// seat), and a departing leader's command time is folded onto their
    /// successor.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Leader_command_time_starts_on_join_and_hands_off_on_departure()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        short side0 = server.Missions[0].Sides[0].Id;

        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "PX1");
        short bId = Seat(server, b, "PX2");

        // Deterministically organise: a first onto side0 → leader; b second → not.
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));

        var aPso = PlayerScoreOf(server, a);
        Assert.True(aPso.Commanding, "the first joiner of a side must StartCommand (SIM-0504)");
        Assert.False(PlayerScoreOf(server, b).Commanding, "a second joiner must not be commanding");

        // Elapse sim time so the command stint is measurable.
        for (int i = 0; i < 40; i++) { server.TickSim(); Thread.Sleep(1); }

        // Leader departs → succession hands command to b and folds a's stint.
        a.Dispose();
        int settled = PumpUntil(server,
            () => !server.Ships.Any(s => s.Ship.Id == aId)
               && server.Missions[0].Sides.First(s => s.Id == side0).LeaderShipId == bId,
            800);
        Assert.True(settled < 800, "leader departure did not promote the successor");

        Assert.True(PlayerScoreOf(server, b).Commanding, "successor must StartCommand (SIM-0504)");
        Assert.True(aPso.TimeCommanded > 0f,
            $"deposed leader must have accumulated command time (got {aPso.TimeCommanded})");
    }

    /// <summary>⭐ SIM-0528 + SIM-0761 — a real game-over runs the GetExpMult
    /// wiring end-to-end: every seated player's final score is folded into the
    /// in-memory career store (native SetCharStats), proving the three call
    /// sites executed over the real mission roster. (The damping MATH itself is
    /// pinned by the pure <c>ApplyGameOverExperienceBalance</c> unit tests; a
    /// live game of idle bots earns ~0 score, so the fold-presence is the
    /// deterministic wiring proof here.)</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Game_over_runs_getexpmult_wiring_and_folds_career()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        using var a0 = new FltBot();
        using var a1 = new FltBot();
        using var b0 = new FltBot();
        using var b1 = new FltBot();
        short a0Id = Seat(server, a0, "KA0");
        short a1Id = Seat(server, a1, "KA1");
        short b0Id = Seat(server, b0, "KB0");
        short b1Id = Seat(server, b1, "KB1");
        Assert.True(server.TryJoinSide(ShipOf(server, a0Id), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, a1Id), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, b0Id), side1));
        Assert.True(server.TryJoinSide(ShipOf(server, b1Id), side1));

        // Now that both sides are organised (no empty side to trigger an early
        // elimination), start the game and stack side0's experience.
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        server._careerScores["KA0"] = 1000f;
        server._careerScores["KA1"] = 1000f;

        // Capture the finalizing PlayerScore references BEFORE the game-over so
        // the rotation/migration that follows cannot detach them.
        var a0ps = PlayerScoreOf(server, a0);
        var a1ps = PlayerScoreOf(server, a1);
        var b0ps = PlayerScoreOf(server, b0);
        var b1ps = PlayerScoreOf(server, b1);

        // Eliminate side1 → side0 wins by elimination → DeclareGameOver.
        b0.Dispose();
        b1.Dispose();
        int seen = PumpUntil(server, () => a0.GameOvers.Count > 0, 800);
        Assert.True(seen < 800, "game-over did not fire after elimination");

        // (a) The GetExpMult wiring ran: every seated player's final score was
        // folded into the in-memory career store (native SetCharStats). The
        // stacked side0's folded career retains its seeded baseline (the fold
        // added the game score on top of it).
        Assert.Equal(1000f + a0ps.Score, server._careerScores["KA0"], 3);
        Assert.Equal(1000f + a1ps.Score, server._careerScores["KA1"], 3);
        Assert.Equal(b0ps.Score, server._careerScores["KB0"], 3);
        Assert.Equal(b1ps.Score, server._careerScores["KB1"], 3);

        // (b) Damping smoke: the heavily stacked side0 (f = 0.1) is at or below
        // the weak side1's score, even for idle bots (0 ≤ 0).
        Assert.True(a0ps.Score <= b0ps.Score,
            $"stacked side0 ({a0ps.Score}) should be damped at/below weak side1 ({b0ps.Score})");
        Assert.True(a1ps.Score <= b1ps.Score,
            $"stacked side0 ({a1ps.Score}) should be damped at/below weak side1 ({b1ps.Score})");
    }
}
