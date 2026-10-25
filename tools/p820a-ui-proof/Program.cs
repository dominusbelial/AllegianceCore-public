using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Lobby.Identity;
using Allegiance.Lobby.Squad;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xna.Framework;

// P820a — REAL-WINDOW screenshots of the Squads screen driven through the
// production Game1 and the real lobby's squad HTTP API. A real Allegiance.Lobby
// app boots in-process on an ephemeral loopback port, seeds player accounts and
// squads (with members in native roles + an ownership log) through its own store
// types, and the production client fetches them over HTTP via SquadApiClient
// (nothing is seeded into the renderer). The screen is captured in BOTH HUD
// styles (Classic + Modern), per the HUD-parity rule.
//
// Run from the repository root:
//   dotnet run --project tools/p820a-ui-proof -- docs/reports/P820A_assets
string output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/reports/P820A_assets");
Directory.CreateDirectory(output);
string release = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
string content = Directory.Exists(release) ? release
    : Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

// ── Real lobby app on an ephemeral loopback port ───────────────────────────
string scratch = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p820a-ui-" + Guid.NewGuid().ToString("N"));
var identity = new IdentitySettings
{
    ConnectionString = "Data Source=" + Path.Combine(scratch, "accounts.db"),
    SigningKeyFile = Path.Combine(scratch, "signing.pem"),
    PublicKeyFile = Path.Combine(scratch, "public.pem"),
};

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton<IdentityStore>();
builder.Services.AddSingleton<SquadStore>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.RespectNullableAnnotations = true;
});
var app = builder.Build();
app.Services.GetRequiredService<IdentityStore>().Migrate();
app.MapSquad();           // the squad HTTP surface the client screen consumes
await app.StartAsync();
var lobbyUrl = new Uri(app.Urls.First(u => u.StartsWith("http://")));

// ── Seed accounts + squads through the lobby's own store types ─────────────
var store = app.Services.GetRequiredService<IdentityStore>();
var squads = app.Services.GetRequiredService<SquadStore>();
long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
string leader = store.CreateAccount("Rynn").Id;
string asl = store.CreateAccount("Dax").Id;
string member = store.CreateAccount("Kal").Id;
string pending = store.CreateAccount("Zee").Id;
string rival = store.CreateAccount("Meridian").Id;

// The flagship squad: leader + ASL + member + a pending join request + log.
squads.CreateSquad(leader, new SquadContracts.CreateSquadRequest("Iron Harbor", "IRN", "Docking bay, refit, and chill - recruiting carriers and miners.", "https://ironharbor.example", 2), now);
var flagshipId = squads.All(SquadContracts.SquadSortColumn.Name, null, 100).First(s => s.Name == "Iron Harbor").Id;
squads.Join(asl, flagshipId, now); squads.Accept(leader, flagshipId, asl, now); squads.Promote(leader, flagshipId, asl, now);
squads.Join(member, flagshipId, now); squads.Accept(leader, flagshipId, member, now);
squads.Join(pending, flagshipId, now);                       // outstanding join request
squads.Transfer(leader, flagshipId, asl, now);               // ownership log entry

// Two more squads so the All list has more than one row.
squads.CreateSquad(rival, new SquadContracts.CreateSquadRequest("Red Dawn Patrol", "RDP", "Interceptors only. Apply in person.", "", 1), now);
squads.CreateSquad(store.CreateAccount("Helix").Id, new SquadContracts.CreateSquadRequest("Helix Collective", "HLX", "Research division - scientists welcome.", "https://helix.example", 3), now);

try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        Intro = true, PlayerName = "ProofPilot", LobbyUrl = lobbyUrl.AbsoluteUri,
        WindowWidth = 1280, WindowHeight = 720,
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new SquadProof(game, output, lobbyUrl.AbsoluteUri));
    game.Run();
}
finally
{
    await app.StopAsync();
    await app.DisposeAsync();
    try { Directory.Delete(scratch, recursive: true); } catch (Exception) { /* leave for inspection */ }
}

Console.WriteLine("P820A_UI_PROOF done");
return;

// ═══════════════════════════════════════════════════════════════════════════
// Drives the production Game1 through the intro → career → squads path and
// captures the All list and a squad's details in both HUD styles.
// ═══════════════════════════════════════════════════════════════════════════
sealed class SquadProof(Game1 game, string output, string lobbyUrl) : GameComponent(game)
{
    private const BindingFlags H = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private bool _finishAfterFlush;
    private string _flagshipId = "";

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, H)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, H)!.SetValue(game, value);
    private object? Call(string name, params object?[] args) => typeof(Game1).GetMethod(name, H)!.Invoke(game, args);
    private void Shot(string name)
    {
        Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true);
        Console.WriteLine("P820A_SCREEN " + name);
    }
    private void SetStyle(HudStyle style) => Get<HudSettings>("_hudSettings").Style = style;
    private object CareerMode(int value) =>
        System.Enum.ToObject(typeof(Game1).GetNestedType("CareerMode", BindingFlags.NonPublic)!, value);

    private Allegiance.ClientCore.MonoGame.Career.SquadScreenModel Squads => Get<Allegiance.ClientCore.MonoGame.Career.SquadScreenModel>("_squads");

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("P820a squads proof did not finish");
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
                Set("_lobbyUrl", lobbyUrl);
                Call("EnterCareerScreen");
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 1:
                // Squads mode: the All tab loads the seeded squad list.
                Set("_careerMode", CareerMode(2));
                Call("ReloadCareer");
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                break;
            case 2:
                if (Squads.AllRows.Count == 0) { _step--; return; }
                _flagshipId = Squads.AllRows.First(s => s.Name == "Iron Harbor").Id;
                SetStyle(HudStyle.Classic);
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 3:
                Shot("squads-all-classic-720p");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 4:
                SetStyle(HudStyle.Modern);
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 5:
                Shot("squads-all-modern-720p");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 6:
                // Move the cursor onto the flagship squad, then open its details (members + ownership log),
                // as a player would: the details pane always describes the highlighted row.
                Squads.MoveCursor(Squads.AllRows.ToList().FindIndex(s => s.Id == _flagshipId) - Squads.Cursor);
                Call("LoadDetailsAsync", _flagshipId);
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                break;
            case 7:
                if (Squads.Details is null) { _step--; return; }
                Call("LoadLogAsync", _flagshipId);
                SetStyle(HudStyle.Classic);
                _next = _watch.Elapsed.TotalSeconds + 0.7;
                break;
            case 8:
                Shot("squads-details-classic-720p");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 9:
                SetStyle(HudStyle.Modern);
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 10:
                Shot("squads-details-modern-720p");
                _finishAfterFlush = true;
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            default:
                game.Exit();
                return;
        }
    }
}
