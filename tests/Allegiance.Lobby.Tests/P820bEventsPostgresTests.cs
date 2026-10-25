using Allegiance.Lobby.Events;
using Allegiance.Lobby.Identity;
using Npgsql;

namespace Allegiance.Lobby.Tests;

/// <summary>P820b zone events on PostgreSQL (the Docker lobby's store): the SQLite-backed
/// P820bEventsTests cannot catch dialect differences (e.g. the reader-must-be-closed rule
/// Npgsql enforces). Runs only with P808A_POSTGRES_TEST_CONNECTION set (an isolated test
/// database), like P820aSquadPostgresTests / P808PostgresTests.</summary>
public sealed class P820bEventsPostgresTests
{
    [SkippableFact, Trait("Tier", "Live")]
    public async Task Postgres_events_create_list_update_delete()
    {
        string? connection = Environment.GetEnvironmentVariable("P808A_POSTGRES_TEST_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connection), "Set P808A_POSTGRES_TEST_CONNECTION to an isolated PostgreSQL test database");
        string schema = "p820b_" + Guid.NewGuid().ToString("N");
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
            var events = new EventStore(settings);
            long now = 100;

            Assert.True(events.Create(new EventContracts.CreateEventRequest("2026-11-05", "19:00", "Cup", "Final", "https://x.example", ["rules.pdf", "notes.txt"]), now).Ok);
            Assert.True(events.Create(new EventContracts.CreateEventRequest("2026-11-02", "12:00", "Early", "", "", []), now + 1).Ok);

            var list = events.List();
            Assert.Equal(2, list.Count);
            Assert.Equal("2026-11-02", list[0].Date);               // sorted by date
            Assert.Equal(new[] { "rules.pdf", "notes.txt" }, list[1].Files);

            Assert.True(events.Update(list[1].Id, new EventContracts.UpdateEventRequest("2026-11-06", "20:00", "Cup (moved)", "", "", ["pack.zip"]), now + 2).Ok);
            Assert.Equal("Cup (moved)", events.Get(list[1].Id)!.Name);
            Assert.True(events.Delete(list[1].Id));
            Assert.Single(events.List());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin); await drop.ExecuteNonQueryAsync();
        }
    }
}
