using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Career;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Lobby;
using Allegiance.Lobby.Events;
using Allegiance.Lobby.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xna.Framework;

// P820b — REAL-WINDOW captures of the online hub (career screen) showing the
// Events entry and the configured Zone web link, and the Events screen with
// two seeded zone events. Driven through the production Game1 and the real
// lobby's zone-events HTTP API. A real Allegiance.Lobby app boots in-process on
// an ephemeral loopback port, is seeded with two events and a ZoneWebUrl
// through its own store/settings types, and the production client fetches them
// over HTTP via EventApiClient (nothing is seeded into the renderer). Each
// screen is captured in BOTH HUD styles (Classic + Modern), per the HUD-parity
// rule.
//
// Run from the repository root:
//   dotnet run --project tools/p820b-ui-proof -- docs/reports/P820B_assets
string output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/reports/P820B_assets");
Directory.CreateDirectory(output);
string release = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
string content = Directory.Exists(release) ? release
    : Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

// ── Real lobby app on an ephemeral loopback port ───────────────────────────
string scratch = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p820b-ui-" + Guid.NewGuid().ToString("N"));
var identity = new IdentitySettings
{
    ConnectionString = "Data Source=" + Path.Combine(scratch, "accounts.db"),
    SigningKeyFile = Path.Combine(scratch, "signing.pem"),
    PublicKeyFile = Path.Combine(scratch, "public.pem"),
};
var settings = new LobbySettings { ZoneWebUrl = "https://zone.allegiancelobby.example" };

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton<IdentityStore>();
builder.Services.AddSingleton<Allegiance.Lobby.Events.EventStore>();
builder.Services.AddSingleton(settings);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.RespectNullableAnnotations = true;
});
builder.Services.AddLobbyRateLimiting();
var app = builder.Build();
app.Services.GetRequiredService<IdentityStore>().Migrate();   // v4 creates the `events` table
app.UseRateLimiter();
app.MapEvents();
app.MapZoneWeb();
await app.StartAsync();
var lobbyUrl = new Uri(app.Urls.First(u => u.StartsWith("http://")));

// ── Seed two zone events through the lobby's own store type ────────────────
var events = app.Services.GetRequiredService<Allegiance.Lobby.Events.EventStore>();
long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
void Seed(string date, string time, string name, string description, string signUp, string[] files)
{
    var r = events.Create(new EventContracts.CreateEventRequest(date, time, name, description, signUp, files), now);
    if (!r.Ok) throw new InvalidOperationException("Event seed failed: " + r.Error);
}
Seed("2026-11-07", "20:00", "Squad Night Fleet Op",
    "Coordinated multi-squad fleet operation - carriers, bombers and escort wings working the evening objective.",
    "https://zone.allegiancelobby.example/signup/squad-night", ["operation-brief.pdf", "loadout-guide.txt"]);
Seed("2026-11-14", "18:30", "New Player Orientation",
    "Walkthrough of the docking bay, mining basics and the squad roster for rookies looking to join a team.",
    "https://zone.allegiancelobby.example/signup/orientation", ["rookie-checklist.txt"]);

try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        Intro = true, PlayerName = "ProofPilot", LobbyUrl = lobbyUrl.AbsoluteUri,
        WindowWidth = 1280, WindowHeight = 720,
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new EventsProof(game, output, lobbyUrl.AbsoluteUri));
    game.Run();
}
finally
{
    await app.StopAsync();
    await app.DisposeAsync();
    try { Directory.Delete(scratch, recursive: true); } catch (Exception) { /* leave for inspection */ }
}

Console.WriteLine("P820B_UI_PROOF done");
return;

// ═══════════════════════════════════════════════════════════════════════════
// Drives the production Game1 through the intro → career → Events path and
// captures the online hub (Events entry + Zone web link) and the two-seeded-
// events screen in both HUD styles.
// ═══════════════════════════════════════════════════════════════════════════
sealed class EventsProof(Game1 game, string output, string lobbyUrl) : GameComponent(game)
{
    private const BindingFlags H = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private bool _finishAfterFlush;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, H)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, H)!.SetValue(game, value);
    private object? Call(string name, params object?[] args) => typeof(Game1).GetMethod(name, H)!.Invoke(game, args);

    private void Shot(string name)
    {
        Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true);
        Console.WriteLine("P820B_SCREEN " + name);
    }
    private void SetStyle(HudStyle style) => Get<HudSettings>("_hudSettings").Style = style;
    private object CareerMode(int value) =>
        System.Enum.ToObject(typeof(Game1).GetNestedType("CareerMode", BindingFlags.NonPublic)!, value);
    private EventsScreenModel Events => Get<EventsScreenModel>("_events");
    private bool Loaded => Events.Events.Count >= 2 && Events.ZoneWebUrl.Length > 0;

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("P820b events proof did not finish");
        if (_finishAfterFlush)
        {
            if (!(bool)typeof(Game1).GetField("_autoShotScheduled", H)!.GetValue(game)!)
                game.Exit();
            return;
        }
        if (Get<object>("_state").ToString() != "Intro") return;
        if (_watch.Elapsed.TotalSeconds < _next) return;

        switch (_step++)
        {
            case 0:
                // Pin the lobby URL and open the career (online hub) screen.
                Set("_lobbyUrl", lobbyUrl);
                Call("EnterCareerScreen");
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 1:
                // Events mode: fetches the seeded events + the configured Zone
                // web link from the real lobby over HTTP.
                Set("_careerMode", CareerMode(3));
                Call("ReloadCareer");
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                break;
            case 2:
                if (!Loaded) { _step--; return; }
                SetStyle(HudStyle.Classic);
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 3:
                // The online hub: Events tab (entry) active + Zone web link footer.
                Shot("hub-events-entry-classic-720p");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 4:
                // Move the cursor onto the second seeded event so its details
                // (description / sign-up URL / files) are the visible selection.
                Events.MoveCursor(+1);
                _next = _watch.Elapsed.TotalSeconds + 0.4;
                break;
            case 5:
                Shot("events-two-seeded-classic-720p");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 6:
                SetStyle(HudStyle.Modern);
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 7:
                Shot("hub-events-entry-modern-720p");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 8:
                Events.MoveCursor(-1);
                _next = _watch.Elapsed.TotalSeconds + 0.4;
                break;
            case 9:
                Shot("events-two-seeded-modern-720p");
                _finishAfterFlush = true;
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            default:
                game.Exit();
                return;
        }
    }
}
