using Allegiance.Lobby.Identity;
using Allegiance.Lobby.Squad;
using Npgsql;

namespace Allegiance.Lobby.Tests;

/// <summary>P820a squads on PostgreSQL (the Docker lobby's store): the SQLite-backed P820aSquadTests
/// cannot catch dialect differences such as case-sensitive LIKE. Runs only with
/// P808A_POSTGRES_TEST_CONNECTION set (an isolated test database), like P808PostgresTests.</summary>
public sealed class P820aSquadPostgresTests
{
    [SkippableFact, Trait("Tier", "Live")]
    public async Task Postgres_squad_create_find_membership_and_ownership_log()
    {
        string? connection = Environment.GetEnvironmentVariable("P808A_POSTGRES_TEST_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connection), "Set P808A_POSTGRES_TEST_CONNECTION to an isolated PostgreSQL test database");
        string schema = "p820_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        await using (var cmd = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin)) await cmd.ExecuteNonQueryAsync();
        try
        {
            var settings = new IdentitySettings
            {
                Store = "Postgres",
                ConnectionString = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema }.ConnectionString,
            };
            var store = new IdentityStore(settings, TimeProvider.System); store.Migrate();
            var squads = new SquadStore(settings, TimeProvider.System);
            var owner = store.CreateAccount("Owner");
            var heir = store.CreateAccount("Heir");

            Assert.True(squads.CreateSquad(owner.Id, new("Iron Harbor", "IRN", "", "", -1), 100).Ok);
            Assert.False(squads.CreateSquad(heir.Id, new("iron harbor", "IRN", "", "", -1), 100).Ok);   // names unique ignoring case
            var id = Assert.Single(squads.Find("iron")).Id;                                             // find ignores case
            Assert.Single(squads.Find("HARBOR"));

            Assert.True(squads.Join(heir.Id, id, 200).Ok);
            Assert.True(squads.Accept(owner.Id, id, heir.Id, 300).Ok);
            Assert.True(squads.Promote(owner.Id, id, heir.Id, 400).Ok);
            Assert.True(squads.Transfer(owner.Id, id, heir.Id, 500).Ok);
            var log = squads.Log(id);
            Assert.Equal(2, log.Count);
            Assert.StartsWith("Heir          ", log[0].Line);
            Assert.StartsWith("Owner          ", log[1].Line);
            Assert.Equal(heir.Id, squads.Details(id)!.LeaderAccountId);

            // The leader quits: the previous owner (made ASL by the transfer) succeeds.
            Assert.True(squads.Quit(heir.Id, id, 800).Ok);
            Assert.Equal(owner.Id, squads.Details(id)!.LeaderAccountId);
            Assert.StartsWith("Owner          ", squads.Log(id)[0].Line);
            Assert.True(squads.Quit(owner.Id, id, 900).Ok);                                            // last member: the squad dissolves
            Assert.Null(squads.Details(id));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin); await drop.ExecuteNonQueryAsync();
        }
    }
}
