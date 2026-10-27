using Allegiance.Lobby.Career;
using Allegiance.Lobby.Identity;
using Allegiance.Lobby.Squad;
using Microsoft.Extensions.Time.Testing;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P855 / SIM-0765 — squad game results change squad standings in the lobby
/// store. Native reports a finished squad game at game over
/// (<c>FedSrv/fsmission.cpp:3421-3427</c> gate → <c>RecordSquadGame</c>,
/// <c>FedSrv/FedSrv.CPP:1198-1300</c>, which posts <c>squadIDWon</c> plus the
/// losing squads' ids through the club DB <c>SquadReportGame</c> query,
/// <c>FedSrv/srvqueries.h:173-194</c>). The port rides the same report path
/// career results already use: the game server attaches a squad-game section to
/// its <see cref="CareerContracts.MatchReport"/> and
/// <see cref="CareerStore.ApplyMatchReport"/> credits the winner squad one win
/// and each losing squad one loss.
/// </summary>
public sealed class P855SquadResultTests
{
    private static (IdentityStore store, CareerStore career, SquadStore squads, string dir) Stores(
        TimeProvider? clock = null)
    {
        clock ??= new FakeTimeProvider(DateTimeOffset.UtcNow);
        string dir = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p855-" + Guid.NewGuid().ToString("N"));
        var settings = new IdentitySettings { ConnectionString = "Data Source=" + Path.Combine(dir, "accounts.db") };
        var store = new IdentityStore(settings, clock); store.Migrate(); store.Migrate();
        return (store, new CareerStore(settings, clock), new SquadStore(settings, clock), dir);
    }

    private static (string winnerSquadId, string loserSquadId) TwoSquads(
        IdentityStore store, SquadStore squads, long now)
    {
        var leaderW = store.CreateAccount("P855WinLeader");
        var leaderL = store.CreateAccount("P855LossLeader");
        Assert.True(squads.CreateSquad(leaderW.Id, new SquadContracts.CreateSquadRequest("Alpha Squad", "ALP", "", "", 1), now).Ok);
        Assert.True(squads.CreateSquad(leaderL.Id, new SquadContracts.CreateSquadRequest("Bravo Squad", "BRV", "", "", 3), now).Ok);
        var w = Assert.Single(squads.Find("Alpha Squad"));
        var l = Assert.Single(squads.Find("Bravo Squad"));
        return (w.Id, l.Id);
    }

    private static CareerContracts.MatchReport SquadReport(string matchId, string winner, string loser) => new(
        matchId, Players: [],
        SquadGame: new CareerContracts.SquadGameResult(WinnerSquadId: winner, LoserSquadIds: [loser]));

    private static (int Wins, int Losses) Standings(SquadStore squads, string squadId)
    {
        var row = squads.All(SquadContracts.SquadSortColumn.Name, null, 50).FirstOrDefault(s => s.Id == squadId)
                  ?? throw new Exception("squad vanished");
        return (row.Wins, row.Losses);
    }

    [Fact]
    public void Qualifying_squad_game_updates_both_squads_standings()
    {
        var (store, career, squads, _) = Stores();
        long now = 1_700_000_000;
        var (winner, loser) = TwoSquads(store, squads, now);

        var outcome = career.ApplyMatchReport(SquadReport("m1", winner, loser), "srv-p855", now, squads);

        Assert.Equal(0, outcome.Applied);   // no player rows; the standings ride the squad section
        Assert.Equal((1, 0), Standings(squads, winner));
        Assert.Equal((0, 1), Standings(squads, loser));
    }

    [Fact]
    public void Report_without_squad_game_section_leaves_standings_unchanged()
    {
        var (store, career, squads, _) = Stores();
        long now = 1_700_000_000;
        var (winner, loser) = TwoSquads(store, squads, now);

        // A non-qualifying game (native never calls RecordSquadGame) reports no
        // squad section — even with verified players attached.
        var a = store.CreateAccount("P855NoSquad");
        var report = new CareerContracts.MatchReport("m2", [
            new CareerContracts.MatchResultEntry(a.Id, 1, 100, 0, 1, 0, 0, 0, 10, true, false, false, false, true),
        ]);
        career.ApplyMatchReport(report, "srv-p855", now, squads);

        Assert.Equal((0, 0), Standings(squads, winner));
        Assert.Equal((0, 0), Standings(squads, loser));
    }

    [Fact]
    public void Replayed_squad_report_is_idempotent()
    {
        var (store, career, squads, _) = Stores();
        long now = 1_700_000_000;
        var (winner, loser) = TwoSquads(store, squads, now);

        career.ApplyMatchReport(SquadReport("m3", winner, loser), "srv-p855", now, squads);
        career.ApplyMatchReport(SquadReport("m3", winner, loser), "srv-p855", now + 5, squads);

        Assert.Equal((1, 0), Standings(squads, winner));
        Assert.Equal((0, 1), Standings(squads, loser));
    }

    [Fact]
    public void Squad_report_is_scoped_per_server()
    {
        var (store, career, squads, _) = Stores();
        long now = 1_700_000_000;
        var (winner, loser) = TwoSquads(store, squads, now);

        // Two servers reusing a match id never double-apply the same game.
        career.ApplyMatchReport(SquadReport("shared", winner, loser), "srv-a", now, squads);
        career.ApplyMatchReport(SquadReport("shared", winner, loser), "srv-b", now, squads);

        Assert.Equal((2, 0), Standings(squads, winner));
        Assert.Equal((0, 2), Standings(squads, loser));
    }

    [Fact]
    public void Squad_only_report_needs_no_player_rows()
    {
        // Native posts the squad query regardless of SetCharStats output
        // (FedSrv.CPP:1198-1300 is independent of the per-player path).
        var (store, career, squads, _) = Stores();
        long now = 1_700_000_000;
        var (winner, loser) = TwoSquads(store, squads, now);

        var outcome = career.ApplyMatchReport(
            new CareerContracts.MatchReport("m4", [], SquadGame: new CareerContracts.SquadGameResult(winner, [loser])),
            "srv-p855", now, squads);

        Assert.Equal((1, 0), Standings(squads, winner));
        Assert.Equal((0, 1), Standings(squads, loser));
        Assert.Equal(0, outcome.Applied);
    }
}
