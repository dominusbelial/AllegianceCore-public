using System.Text.Json;
using Allegiance.Core.Identity;
using Allegiance.Lobby;
using Allegiance.Lobby.Identity;
using Allegiance.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Allegiance.Core.Tests;

public sealed class P808ServerEnrollmentTests
{
    [Fact]
    public async Task Heartbeat_enrolls_once_saves_private_credential_and_reuses_it_after_restart()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "enrollment-" + Guid.NewGuid().ToString("N"));
        var settings = new IdentitySettings
        {
            ConnectionString = "Data Source=" + Path.Combine(directory, "accounts.db"),
            SigningKeyFile = Path.Combine(directory, "signing.pem"), PublicKeyFile = Path.Combine(directory, "public.pem"),
        };
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var original = new LobbyHarness();
        using var app = original.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IdentitySettings>(); services.AddSingleton(settings);
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
        }));
        using var http = app.CreateClient();
        var store = app.Services.GetRequiredService<IdentityStore>();
        var account = store.CreateAccount("OperatorPilot"); settings.OperatorAccountIds = [account.Id];
        var config = new LobbyConfig { Url = "http://localhost", DisplayName = "Enrolled test server", EnrollmentFile = Path.Combine(directory, "server.json") };
        string serverId;
        using (var heartbeat = new LobbyHeartbeat(http, config, () => 0, "Test", "Core", "version", 6073, clock))
        {
            await heartbeat.PumpAsync();
            Assert.False(Assert.Single(app.Services.GetRequiredService<ServerRegistry>().List()).Verified);
            string code = store.Row("SELECT user_code FROM devices WHERE purpose='server'")![0];
            Assert.True(store.Approve(code, account.Id)); clock.Advance(TimeSpan.FromSeconds(5));
            await heartbeat.PumpAsync();
            var listing = Assert.Single(app.Services.GetRequiredService<ServerRegistry>().List()); Assert.True(listing.Verified);
            serverId = listing.IdentityServerId!;
            var saved = JsonSerializer.Deserialize(File.ReadAllText(config.EnrollmentFile), IdentityJsonContext.Default.ServerEnrollment)!;
            Assert.Equal(serverId, saved.ServerId); Assert.Equal(serverId, store.VerifyServer(saved.ServerId, saved.ServerSecret));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(config.EnrollmentFile));
        }
        Assert.Empty(app.Services.GetRequiredService<ServerRegistry>().List());
        using var restarted = new LobbyHeartbeat(http, config, () => 0, "Test", "Core", "version", 6073, clock);
        await restarted.PumpAsync();
        Assert.Equal(serverId, Assert.Single(app.Services.GetRequiredService<ServerRegistry>().List()).IdentityServerId);
        Assert.Null(store.Row("SELECT user_code FROM devices WHERE purpose='server'"));
    }
}
