using System.Net;
using System.Net.Http.Json;
using Allegiance.Lobby.Career;
using Allegiance.Lobby.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Allegiance.Lobby.Tests;

public sealed class P808CareerTests
{
    private static (IdentityStore store, CareerStore career, string dir) Stores(TimeProvider? clock = null)
    {
        clock ??= new FakeTimeProvider(DateTimeOffset.UtcNow);
        string dir = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "career-" + Guid.NewGuid().ToString("N"));
        var settings = new IdentitySettings { ConnectionString = "Data Source=" + Path.Combine(dir, "accounts.db") };
        var store = new IdentityStore(settings, clock); store.Migrate(); store.Migrate();
        return (store, new CareerStore(settings, clock), dir);
    }

    // --- Native rank rules (FedSrv/FedSrv.CPP:1062-1103) ---

    [Theory]
    [InlineData(0, 0)] [InlineData(50, 0)] [InlineData(99, 0)]
    [InlineData(101, 1)] [InlineData(401, 2)] [InlineData(1201, 3)]
    [InlineData(9501, 6)] [InlineData(1_000_000, 21)]
    public void Rank_is_highest_rung_below_score(int score, short expected)
        => Assert.Equal(expected, CareerRankTable.RankFor(score));

    [Fact]
    public void Promotion_at_is_next_rung_threshold_and_null_at_top()
    {
        Assert.Equal(100, CareerRankTable.PromotionAt(0));
        Assert.Equal(1200, CareerRankTable.PromotionAt(2));
        Assert.Null(CareerRankTable.PromotionAt(22));
        Assert.Equal("Master 9", CareerRankTable.Name(99)); // clamps to top
    }

    [Fact]
    public void Cumulative_scores_promote_across_matches_per_civ()
    {
        var (store, career, dir) = Stores();
        var a = store.CreateAccount("Alpha");
        long now = 1_700_000_000;
        var r1 = new CareerContracts.MatchResultEntry(a.Id, Civ: 2, Score: 500, CombatRating: 120.5f,
            Kills: 3, Deaths: 1, BaseKills: 0, BaseCaptures: 0, Minutes: 30, Win: true, Loss: false,
            CommanderWin: false, CommanderLoss: false, ScoresCount: true);
        var out1 = career.ApplyMatchReport(new CareerContracts.MatchReport("m1", [r1], ServerId: "srv", ServerSecret: "sec"), "srv", now);
        Assert.Equal((1, 0, 0), (out1.Applied, out1.Duplicates, out1.Skipped));

        var info = career.GetCareer(a.Id)!;
        Assert.Equal("Alpha", info.Callsign);
        var civ2 = Assert.Single(info.Civs);
        Assert.Equal(2, civ2.Civ);
        Assert.Equal(500f, civ2.Score);
        Assert.Equal(2, civ2.Rank);          // RankFor(500)=2 (Ensign)
        Assert.Equal(1200, civ2.PromotionAt); // next rung
        Assert.Equal(3, civ2.Kills);
        Assert.Equal(1, civ2.Deaths);
        Assert.Equal(120, civ2.Rating);
        Assert.Equal(1, civ2.Wins);

        // Second match: +800 → 1300 → rank 3 (Lieutenant Jr.)
        var r2 = r1 with { Score = 800, Kills = 1, Minutes = 20, Win = false, Loss = true };
        career.ApplyMatchReport(new CareerContracts.MatchReport("m2", [r2], ServerId: "srv", ServerSecret: "sec"), "srv", now + 1000);
        var after = career.GetCareer(a.Id)!.Civs[0];
        Assert.Equal(1300f, after.Score);
        Assert.Equal(3, after.Rank);
        Assert.Equal(4, after.Kills);   // 3 + 1
        Assert.Equal(1, after.Wins);
        Assert.Equal(1, after.Losses);
        Assert.Equal(50, after.MinutesPlayed); // 30 + 20 min cumulative (minutes, not seconds)
    }

    [Fact]
    public void One_result_per_player_per_match_dup_is_idempotently_skipped()
    {
        var (store, career, _) = Stores();
        var a = store.CreateAccount("Dupe");
        var r = new CareerContracts.MatchResultEntry(a.Id, Civ: 1, Score: 100, CombatRating: 0, Kills: 1,
            Deaths: 0, BaseKills: 0, BaseCaptures: 0, Minutes: 10, Win: false, Loss: false,
            CommanderWin: false, CommanderLoss: false, ScoresCount: true);
        var rep = new CareerContracts.MatchReport("match-X", [r], "srv", "sec");
        var first = career.ApplyMatchReport(rep, "srv", 100);
        var second = career.ApplyMatchReport(rep, "srv", 200);   // replay
        Assert.Equal((1, 0, 0), (first.Applied, first.Duplicates, first.Skipped));
        Assert.Equal((0, 1, 0), (second.Applied, second.Duplicates, second.Skipped));
        Assert.Equal(100f, career.GetCareer(a.Id)!.Civs[0].Score);  // not double-applied
    }

    [Fact]
    public void Non_scoring_or_zero_time_results_are_skipped()
    {
        var (store, career, _) = Stores();
        var a = store.CreateAccount("Skips");
        var noCount = new CareerContracts.MatchResultEntry(a.Id, Civ: 1, Score: 999, CombatRating: 0, Kills: 5,
            Deaths: 0, BaseKills: 0, BaseCaptures: 0, Minutes: 10, Win: false, Loss: false,
            CommanderWin: false, CommanderLoss: false, ScoresCount: false);
        var noTime = noCount with { ScoresCount = true, Minutes = 0 };
        var out1 = career.ApplyMatchReport(new CareerContracts.MatchReport("mx", [noCount, noTime], "s", "x"), "s", 1);
        Assert.Equal((0, 0, 2), (out1.Applied, out1.Duplicates, out1.Skipped));
        Assert.Null(career.GetCareer(a.Id)?.Civs.FirstOrDefault());
    }

    // --- Leaderboard (native LEADER_BOARD_QUERY, ClubMessages.h:320-360) ---

    [Fact]
    public void Leaderboard_sorts_positions_and_finds_basis_by_name_and_prefix()
    {
        var (store, career, _) = Stores();
        long now = 5;
        void Seed(string name, short civ, float score)
        {
            var acc = store.CreateAccount(name);
            career.ApplyMatchReport(new CareerContracts.MatchReport($"m-{name}-{civ}", [new CareerContracts.MatchResultEntry(
                acc.Id, civ, score, 100, 1, 0, 0, 0, 10, false, false, false, false, true)], "s", "x"), "s", now);
        }
        Seed("Adam", 1, 2000);
        Seed("Bob", 1, 500);
        Seed("Carol", 1, 9000);

        // Sort by score desc → Carol(#1,9000), Adam(#2,2000), Bob(#3,500).
        var lb = career.Leaderboard(new CareerContracts.LeaderboardQuery(1, CareerContracts.LeaderBoardSort.Score, null, null));
        Assert.Null(lb.FailReason);
        Assert.Equal("Carol", lb.EntryNumber1!.Callsign);
        Assert.Equal(1, lb.EntryNumber1.Position);
        Assert.Equal(["Carol", "Adam", "Bob"], lb.Entries.Select(e => e.Callsign).ToArray());

        // FindMe by exact name.
        var found = career.Leaderboard(new CareerContracts.LeaderboardQuery(1, CareerContracts.LeaderBoardSort.Score, "Bob", null));
        Assert.Equal(3, found.BasisPosition);
        Assert.Equal("Bob", found.Entries.First(e => e.Position == 3).Callsign);

        // Prefix search (native OffsetOfCharacterPrefix).
        var prefixed = career.Leaderboard(new CareerContracts.LeaderboardQuery(1, CareerContracts.LeaderBoardSort.Score, "Ca", null));
        Assert.Equal(1, prefixed.BasisPosition);
        Assert.Equal("Carol", prefixed.EntryNumber1!.Callsign);

        // Missing basis → FailReason.
        var missing = career.Leaderboard(new CareerContracts.LeaderboardQuery(1, CareerContracts.LeaderBoardSort.Score, "Nobody", null));
        Assert.Equal("Nobody", missing.FailReason);
    }

    [Fact]
    public void Overall_leaderboard_sums_all_civs_and_empty_civ_fails()
    {
        var (store, career, _) = Stores();
        var a = store.CreateAccount("Multi");
        career.ApplyMatchReport(new CareerContracts.MatchReport("m1", [new CareerContracts.MatchResultEntry(a.Id, 1, 1000, 50, 2, 0, 0, 0, 10, false, false, false, false, true)], "s", "x"), "s", 1);
        career.ApplyMatchReport(new CareerContracts.MatchReport("m2", [new CareerContracts.MatchResultEntry(a.Id, 2, 3000, 70, 1, 0, 0, 0, 5, true, false, false, false, true)], "s", "x"), "s", 2);

        var all = career.Leaderboard(new CareerContracts.LeaderboardQuery(CareerContracts.AllCivs, CareerContracts.LeaderBoardSort.Score, null, null));
        Assert.Null(all.FailReason);
        var row = all.Entries.Single();
        Assert.Equal(4000f, row.TotalScore);   // sum of both civs
        Assert.Equal(3, row.Kills);            // 2 + 1
        Assert.Equal(15, row.MinutesPlayed);

        var empty = career.Leaderboard(new CareerContracts.LeaderboardQuery(9, CareerContracts.LeaderBoardSort.Score, null, null));
        Assert.Equal("no players", empty.FailReason);
    }

    // --- HTTP endpoint auth (verified-server-only ingestion; owner-only edit) ---

    [Fact]
    public async Task Results_require_a_verified_server_and_description_edit_requires_owner()
    {
        using var app = new LobbyTestApp(); using var http = app.CreateClient();
        var identities = app.Services.GetRequiredService<IdentityStore>();
        var serverId = "server-1";
        var operatorAcct = identities.CreateAccount("Operator");
        // Enroll a server directly through the store (matches the device-approval path).
        identities.Execute("INSERT INTO enrolled_servers(id,secret_hash,operator_id,name) VALUES(@p0,@p1,@p2,@p3)", serverId, IdentityStore.Hash("secret"), operatorAcct.Id, "t");
        var account = identities.CreateAccount("OwnerPilot");

        var rep = new CareerContracts.MatchReport("rm", [new CareerContracts.MatchResultEntry(account.Id, Civ: 1, Score: 100, CombatRating: 0, 0, 0, 0, 0, 5, false, false, false, false, true)], serverId, "secret");

        // Unverified (bad secret) → 401.
        using var bad = await http.PostAsJsonAsync("/api/career/results", rep with { ServerSecret = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        // Verified → OK, and the career line appears.
        using var ok = await http.PostAsJsonAsync("/api/career/results", rep);
        ok.EnsureSuccessStatusCode();
        using var infoResp = await http.GetAsync($"/api/career/{account.Id}");
        infoResp.EnsureSuccessStatusCode();
        var info = (await infoResp.Content.ReadFromJsonAsync<CareerContracts.CareerInfo>())!;
        Assert.Equal("OwnerPilot", info.Callsign);
        Assert.Single(info.Civs);

        // Description edit without a session → Unauthorized.
        using var unauthEdit = await http.PutAsJsonAsync($"/api/career/{account.Id}/description", new { description = "hi" });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthEdit.StatusCode);
    }
}
