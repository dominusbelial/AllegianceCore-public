using System.Net.Http.Json;
using Allegiance.Core.Identity;
using Allegiance.Lobby.Identity;
using Npgsql;

namespace Allegiance.Lobby.Tests;

public sealed class P808PostgresTests
{
    [SkippableFact, Trait("Tier", "Live")]
    public async Task Postgres_migrates_and_preserves_passkey_account_device_grant_and_signing_identity()
    {
        string? connection = Environment.GetEnvironmentVariable("P808A_POSTGRES_TEST_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connection), "Set P808A_POSTGRES_TEST_CONNECTION to an isolated PostgreSQL test database");
        string schema = "p808_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        await using (var cmd = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin)) await cmd.ExecuteNonQueryAsync();
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema };
            using var app = new LobbyTestApp();
            app.IdentityConfiguration = new IdentitySettings
            {
                Store = "Postgres", ConnectionString = cs.ConnectionString,
                SigningKeyFile = Path.Combine(app.IdentityDirectory, "private.pem"), PublicKeyFile = Path.Combine(app.IdentityDirectory, "public.pem"),
            };
            using var http = app.CreateClient(); http.DefaultRequestHeaders.Add("Origin", "http://localhost:5000");
            using var authenticator = new VirtualPasskey();
            using var registered = await authenticator.Register(http, "PostgresPilot"); registered.EnsureSuccessStatusCode();
            var account = (await registered.Content.ReadFromJsonAsync<IdentityAccount>())!;
            var store = new IdentityStore(app.IdentityConfiguration, TimeProvider.System); store.Migrate();
            Assert.Equal(account, store.Account(account.Id));
            var device = store.StartDevice(new DeviceStartRequest("player", "Postgres test"));
            Assert.True(store.Approve(device.UserCode, account.Id));
            var result = store.Poll(device.DeviceCode); Assert.Equal("approved", result.Status);
            Assert.Equal(account, store.Authenticate(result.Session)); Assert.Equal("expired", store.Poll(device.DeviceCode).Status);
            using var login = await authenticator.Login(http); login.EnsureSuccessStatusCode();
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin); await drop.ExecuteNonQueryAsync();
        }
    }
}
