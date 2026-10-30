using Allegiance.Lobby;
using Allegiance.Lobby.Career;
using Allegiance.Lobby.Chat;
using Allegiance.Lobby.Games;
using Allegiance.Lobby.Events;
using Allegiance.Lobby.Identity;
using Allegiance.Lobby.Squad;
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
// Registered as a singleton so endpoints (and tests, via LobbyTestApp) can
// resolve/override the same instance.
var settings = new LobbySettings();
builder.Configuration.GetSection("Lobby").Bind(settings);
builder.Services.AddSingleton(settings);

// TimeProvider is registered as a singleton so tests can swap in a fake clock; the
// ServerRegistry depends on it rather than sampling DateTime.UtcNow itself.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = settings.KnownProxies.Count > 0 ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto : ForwardedHeaders.None;
    options.ForwardLimit = 1;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    foreach (var proxy in settings.KnownProxies) options.KnownProxies.Add(IPAddress.Parse(proxy));
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new ServerRegistry(
    sp.GetRequiredService<TimeProvider>(),
    settings.MaxEntries,
    TimeSpan.FromSeconds(settings.MaxAgeSeconds), settings.MaxEntriesPerAddress,
    settings.GameFactor, settings.MaxLoad));

var identity = new IdentitySettings();
builder.Configuration.GetSection("Lobby:Identity").Bind(identity);
identity.Validate();
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton<IdentityStore>();
builder.Services.AddSingleton<CareerStore>();
builder.Services.AddSingleton<SquadStore>();
builder.Services.AddSingleton<Allegiance.Lobby.Events.EventStore>();
builder.Services.AddSingleton<DirectoryChat>();
builder.Services.AddSingleton<GameCreationStore>();
builder.Services.AddSingleton<IdentitySigningKey>();
builder.Services.AddSingleton<PasskeyService>();
builder.Services.AddIdentityProviders(identity);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.RespectNullableAnnotations = true;
    o.SerializerOptions.RespectRequiredConstructorParameters = true;
    // P871b — the mission parameters no longer ride JSON at all: the games
    // endpoints carry native's packed `MissionParams` struct as a base64 blob
    // (`paramsWire`, GameEndpoints), so the lobby's serializer stays on the
    // default contract. P871 set `IncludeFields = true` here, which added every
    // public FIELD of every response type to EVERY endpoint's JSON (identity,
    // career, squad, events) — see P871b's JSON snapshot tests.
});
builder.Services.AddLobbyRateLimiting();
builder.Services.AddHostedService(sp => new ExpirySweepService(
    sp.GetRequiredService<ServerRegistry>(),
    sp.GetRequiredService<TimeProvider>(),
    TimeSpan.FromSeconds(settings.SweepIntervalSeconds),
    TimeSpan.FromSeconds(settings.MaxAgeSeconds)));

var app = builder.Build();

app.Services.GetRequiredService<IdentityStore>().Migrate();
_ = app.Services.GetRequiredService<IdentitySigningKey>();
app.UseForwardedHeaders();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
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
app.MapLobbyEndpoints(settings);
app.MapIdentity(app.Services.GetRequiredService<IdentitySettings>());
app.MapCareer();
app.MapSquad();
app.MapEvents();
app.MapChat();
app.MapGames();
app.MapZoneWeb();

app.Run();

// Public so tests (Microsoft.AspNetCore.Mvc.Testing's WebApplicationFactory<Program>)
// can boot this app in-process.
public partial class Program;
