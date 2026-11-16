using System.Net;
using System.Net.Http.Json;
using Allegiance.Lobby.Career;
using Allegiance.Lobby.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P1028 — R1 §4 lead: game servers could post unbounded result values, and
/// the per-civ career totals were accumulated with unchecked <c>int</c>
/// arithmetic (both confirmed against the pre-fix tree, see
/// docs/reports/LOBBY_CAREER_BOUNDS_P1028.md). These tests pin the fix:
/// per-field admission limits (CareerContracts.Limits) rejected as a 4xx with
/// a reason, and overflow-safe totals that fail loudly instead of wrapping.
/// </summary>
public sealed class P1028CareerBoundsTests
{
    private static (IdentityStore store, CareerStore career, string dir) Stores()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        string dir = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p1028-" + Guid.NewGuid().ToString("N"));
        var settings = new IdentitySettings { ConnectionString = "Data Source=" + Path.Combine(dir, "accounts.db") };
        var store = new IdentityStore(settings, clock); store.Migrate(); store.Migrate();
        return (store, new CareerStore(settings, clock), dir);
    }

    private static CareerContracts.MatchResultEntry Entry(string accountId, int kills = 1, int minutes = 10, float score = 100) =>
        new(accountId, Civ: 1, Score: score, CombatRating: 50f, Kills: kills, Deaths: 0, BaseKills: 0, BaseCaptures: 0,
            Minutes: minutes, Win: false, Loss: false, CommanderWin: false, CommanderLoss: false, ScoresCount: true);

    // --- Part (a): unbounded values are rejected on entry ---

    [Fact]
    public void Absurd_values_are_rejected_with_a_named_field_reason()
    {
        var (store, career, _) = Stores();
        var a = store.CreateAccount("Absurd");
        var r = Entry(a.Id, kills: CareerContracts.Limits.MaxKills + 1, minutes: int.MaxValue, score: float.MaxValue);
        var ex = Assert.Throws<ReportRejectedException>(() =>
            career.ApplyMatchReport(new CareerContracts.MatchReport("m1", [r], "srv", "sec"), "srv", 1));
        Assert.Contains("score", ex.Message);   // float.MaxValue score trips first
        Assert.Null(career.GetCareer(a.Id)!.Civs.FirstOrDefault());   // nothing applied
    }

    [Fact]
    public void Negative_values_are_rejected()
    {
        var (store, career, _) = Stores();
        var a = store.CreateAccount("Negative");
        foreach (var (field, r) in new[]
        {
            ("kills -1", Entry(a.Id) with { Kills = -1 }),
            ("deaths -1", Entry(a.Id) with { Deaths = -1 }),
            ("score -1", Entry(a.Id) with { Score = -1f }),
            ("minutes must not be negative", Entry(a.Id) with { Minutes = -1 }),
            ("base captures -1", Entry(a.Id) with { BaseCaptures = -1f }),
            ("time played -1", Entry(a.Id) with { TimePlayedSeconds = -1f }),
        })
        {
            var ex = Assert.Throws<ReportRejectedException>(() =>
                career.ApplyMatchReport(new CareerContracts.MatchReport($"neg-{r.Kills}-{r.Deaths}", [r], "srv", "sec"), "srv", 1));
            Assert.Contains(field, ex.Message);
        }
        Assert.Null(career.GetCareer(a.Id)!.Civs.FirstOrDefault());
    }

    [Fact]
    public void Validate_names_the_offending_field_and_bound()
    {
        Assert.Contains("kills", Entry("x", kills: -1).Validate());
        Assert.Contains("minutes", Entry("x", minutes: CareerContracts.Limits.MaxMinutes + 1).Validate());
        Assert.Contains("score", Entry("x", score: float.MaxValue).Validate());
        Assert.Null(Entry("x").Validate());   // a sane result passes
    }

    // --- Part (b): totals are overflow-safe, not wrapping ---

    [Fact]
    public void Near_overflow_totals_fail_loudly_instead_of_wrapping()
    {
        var (store, career, _) = Stores();
        var a = store.CreateAccount("Overflow");
        // Legacy/corrupt row seeded directly (a real post can no longer get
        // minutes this high); one more valid match must be rejected, not wrap.
        store.Execute("""
            INSERT INTO career_by_civ(account_id,civ,score,rank,combat_rating,kills,deaths,base_kills,base_captures,
                                      minutes,wins,losses,games,commander_wins,commander_losses)
            VALUES(@p0,@p1,0,0,0,0,0,0,0,@p2,0,0,0,0,0)
            """, a.Id, 1, int.MaxValue - 5);
        var ex = Assert.Throws<ReportRejectedException>(() =>
            career.ApplyMatchReport(new CareerContracts.MatchReport("m1", [Entry(a.Id, minutes: 10)], "srv", "sec"), "srv", 1));
        Assert.Contains("would overflow", ex.Message);
        Assert.Equal(int.MaxValue - 5, career.GetCareer(a.Id)!.Civs.Single().MinutesPlayed);   // unchanged
    }

    // --- HTTP: a rejected result is a 4xx with a clear message, nothing applied ---

    [Fact]
    public async Task Endpoint_rejects_out_of_limits_reports_with_400_and_applies_nothing()
    {
        using var app = new LobbyTestApp(); using var http = app.CreateClient();
        var identities = app.Services.GetRequiredService<IdentityStore>();
        var serverId = "server-p1028";
        var operatorAcct = identities.CreateAccount("Operator");
        identities.Execute("INSERT INTO enrolled_servers(id,secret_hash,operator_id,name) VALUES(@p0,@p1,@p2,@p3)",
            serverId, IdentityStore.Hash("secret"), operatorAcct.Id, "t");
        var account = identities.CreateAccount("Pilot");

        CareerContracts.MatchReport Report(int kills, int minutes) =>
            new("rm", [Entry(account.Id, kills: kills, minutes: minutes)], serverId, "secret");

        // Valid small result → OK, and the career line appears.
        using var ok = await http.PostAsJsonAsync("/api/career/results", Report(2, 15));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(2, (await http.GetFromJsonAsync<CareerContracts.CareerInfo>($"/api/career/{account.Id}"))!.Civs.Single().Kills);

        // Absurd kills → 400 with the field named; nothing new applied.
        using var tooBig = await http.PostAsJsonAsync("/api/career/results", Report(CareerContracts.Limits.MaxKills + 1, 15));
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        Assert.Contains("kills", await tooBig.Content.ReadAsStringAsync());
        Assert.Equal(0, (await http.GetFromJsonAsync<CareerContracts.CareerInfo>($"/api/career/{account.Id}"))!.Civs.Single().Wins);

        // Negative deaths → 400 as well.
        using var neg = await http.PostAsJsonAsync("/api/career/results", Report(-1, 15));
        Assert.Equal(HttpStatusCode.BadRequest, neg.StatusCode);
    }
}
