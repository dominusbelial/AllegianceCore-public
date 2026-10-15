using Allegiance.Lobby;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

// Program.cs — written by hand (no `dotnet new web` scaffold). The repo's root
// Directory.Build.props forces TreatWarningsAsErrors=true for every project, and the
// stock web template commonly emits nullable/analyzer noise that would hard-fail the
// build; this file is deliberately minimal and manual, in the same spirit as
// Allegiance.Server's / Allegiance.Bot's trivial top-level `Program.cs` files.
//
// This is the FIRST project in the tree that requires the ASP.NET Core runtime on the
// host (not just the base .NET runtime) — see ALLEGIANCE_LOBBY_STAGE1_P174.md.
var builder = WebApplication.CreateBuilder(args);

// Settings bound from the "Lobby" config section (defaults in LobbySettings).
var settings = new LobbySettings();
builder.Configuration.GetSection("Lobby").Bind(settings);

// TimeProvider is registered as a singleton so tests can swap in a fake clock; the
// ServerRegistry depends on it rather than sampling DateTime.UtcNow itself.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = settings.KnownProxies.Count > 0 ? ForwardedHeaders.XForwardedFor : ForwardedHeaders.None;
    options.ForwardLimit = 1;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    foreach (var proxy in settings.KnownProxies) options.KnownProxies.Add(IPAddress.Parse(proxy));
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new ServerRegistry(
    sp.GetRequiredService<TimeProvider>(),
    settings.MaxEntries,
    TimeSpan.FromSeconds(settings.MaxAgeSeconds), settings.MaxEntriesPerAddress));

builder.Services.AddLobbyRateLimiting();
builder.Services.AddHostedService(sp => new ExpirySweepService(
    sp.GetRequiredService<ServerRegistry>(),
    sp.GetRequiredService<TimeProvider>(),
    TimeSpan.FromSeconds(settings.SweepIntervalSeconds),
    TimeSpan.FromSeconds(settings.MaxAgeSeconds)));

var app = builder.Build();

app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    if (context.Request.ContentLength > 16 * 1024)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }
    await next(context);
});
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapLobbyEndpoints();

app.Run();

// Public so tests (Microsoft.AspNetCore.Mvc.Testing's WebApplicationFactory<Program>)
// can boot this app in-process.
public partial class Program;
