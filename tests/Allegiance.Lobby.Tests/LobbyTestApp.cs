using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// Shared in-process test host. Drives the app with a controllable <see cref="TimeProvider"/>
/// and <see cref="LobbySettings"/> by overriding the relevant DI registrations directly.
///
/// Program.cs builds its <see cref="ServerRegistry"/> and hosted <see cref="ExpirySweepService"/>
/// out of the registered <see cref="TimeProvider"/>, so swapping the clock + re-pointing the
/// registry/sweep here means a test can (a) shrink the sweep cadence / expiry window and
/// (b) drive time forward to observe expiry deterministically — all without touching the real
/// wall clock or the real 30s sweep.
///
/// (Overriding the services rather than injecting config is deliberate: with
/// WebApplicationFactory the config-section binding in Program is hard to reach reliably,
/// whereas RemoveAll+Add here is guaranteed to win for singleton resolution.)
/// </summary>
internal sealed class LobbyTestApp : WebApplicationFactory<Program>
{
    internal readonly string IdentityDirectory = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "lobby-" + Guid.NewGuid().ToString("N"));
    internal Allegiance.Lobby.Identity.IdentitySettings? IdentityConfiguration { get; set; }
    private readonly TimeProvider _time;
    private readonly Action<LobbySettings>? _configure;
    private readonly System.Net.IPAddress _source;

    /// <param name="sourceIp">P894 — the socket source every request appears to
    /// come from (loopback by default, native's always-allowed address). An
    /// allow/block-list test supplies a public address.</param>
    public LobbyTestApp(TimeProvider? time = null, Action<LobbySettings>? configure = null, string? sourceIp = null)
    {
        _time = time ?? TimeProvider.System;
        _configure = configure;
        _source = System.Net.IPAddress.Parse(sourceIp ?? "127.0.0.1");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new LobbySettings();
        _configure?.Invoke(settings);

        builder.ConfigureServices(services =>
        {
            var identity = IdentityConfiguration ?? new Allegiance.Lobby.Identity.IdentitySettings
            {
                ConnectionString = "Data Source=" + Path.Combine(IdentityDirectory, "accounts.db"),
                SigningKeyFile = Path.Combine(IdentityDirectory, "key.pem"),
                PublicKeyFile = Path.Combine(IdentityDirectory, "public.pem"),
            };
            services.RemoveAll<Allegiance.Lobby.Identity.IdentitySettings>();
            services.AddSingleton(identity);
            services.AddSingleton<IStartupFilter>(new LoopbackSource(_source));
            // Inject the fake (or system) clock.
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_time);

            // Re-point the registry at the configured window/cap, backed by that clock.
            services.RemoveAll<ServerRegistry>();
            services.AddSingleton<ServerRegistry>(
                new ServerRegistry(_time, settings.MaxEntries, TimeSpan.FromSeconds(settings.MaxAgeSeconds),
                    settings.MaxEntriesPerAddress, settings.GameFactor, settings.MaxLoad));

            // P810 — the version advertisement (/api/update/info) reads the
            // settings from DI so a test can advertise versions/feeds.
            services.RemoveAll<LobbySettings>();
            services.AddSingleton(settings);

            // Drop Program's hosted sweep and re-add one honoring the test cadence + our registry.
            services.RemoveAll<IHostedService>();
            services.AddHostedService(sp => new ExpirySweepService(
                sp.GetRequiredService<ServerRegistry>(),
                sp.GetRequiredService<TimeProvider>(),
                TimeSpan.FromSeconds(settings.SweepIntervalSeconds),
                TimeSpan.FromSeconds(settings.MaxAgeSeconds)));
        });
    }
    private sealed class LoopbackSource(System.Net.IPAddress source) : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next)
            => app =>
            {
                Microsoft.AspNetCore.Builder.UseExtensions.Use(app, async (context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = source;
                    await continuation();
                });
                next(app);
            };
    }
}
