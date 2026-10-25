using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Lobby;
using Allegiance.Lobby.Career;
using Allegiance.Lobby.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xna.Framework;

// P808c — REAL-WINDOW screenshots of the career screens (character info +
// leaderboard) driven through the production Game1 and the real lobby's career
// HTTP API. A real Allegiance.Lobby app boots in-process on an ephemeral
// loopback port, is seeded with player accounts + career lines through its own
// store types, and the production client fetches them over HTTP via
// CareerApiClient (nothing is seeded into the renderer). Each screen is
// captured in BOTH HUD styles (Classic + Modern), per the HUD-parity rule.
//
// Run from the repository root:
//   dotnet run --project tools/p808c-ui-proof -- docs/reports/P808C_assets
string output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/reports/P808C_assets");
Directory.CreateDirectory(output);
string release = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
string content = Directory.Exists(release) ? release
    : Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

// ── Real lobby app on an ephemeral loopback port ───────────────────────────
string scratch = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p808c-ui-" + Guid.NewGuid().ToString("N"));
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
builder.Services.AddSingleton<CareerStore>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.RespectNullableAnnotations = true;
});
var app = builder.Build();
app.Services.GetRequiredService<IdentityStore>().Migrate();
app.MapCareer();          // the career HTTP surface the client screens consume
await app.StartAsync();
var lobbyUrl = new Uri(app.Urls.First(u => u.StartsWith("http://")));

// ── Seed accounts + career lines through the lobby's own store types ───────
var store = app.Services.GetRequiredService<IdentityStore>();
var career = app.Services.GetRequiredService<CareerStore>();
long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
string serverId = "srv-ui-proof";
store.Execute("INSERT INTO enrolled_servers(id,secret_hash,operator_id,name) VALUES(@p0,@p1,@p2,@p3)",
    serverId, IdentityStore.Hash("ui-proof-secret"), store.CreateAccount("Operator").Id, "UI proof server");

// Six players with varied, sortable career histories across two civs each.
(string callsign, (short civ, float score, int kills, int mins, int rating) c1, (short civ, float score, int kills, int mins, int rating) c2)[] players =
{
    ("ArchonAlpha",  (2, 4850, 120, 920, 910),  (3, 2100,  40, 380, 0)),
    ("BelterBeta",   (2, 9700, 260, 1500, 985),  (1, 4820, 150, 870, 925)),
    ("CosmoGamma",   (3, 2230,  55, 310, 887),   (2, 915,   18, 140, 872)),
    ("DeltaDawn",    (1, 48600, 964, 9240, 997), (3, 8840, 210, 921, 932)),
    ("EchoEpsilon",  (2, 896,   22, 95, 874),    (2, 883,   19, 88, 877)),
    ("FoxtrotFelon", (1, 9175, 487, 886, 989),   (3, 4790, 191, 962, 914)),
};
var accountIds = new Dictionary<string, string>();
foreach (var p in players)
    accountIds[p.callsign] = store.CreateAccount(p.callsign).Id;

foreach (var p in players)
{
    var entries = new List<CareerContracts.MatchResultEntry>();
    foreach (var (civ, score, kills, mins, rating) in new[] { p.c1, p.c2 })
        entries.Add(new CareerContracts.MatchResultEntry(accountIds[p.callsign], civ, score, rating, kills, 5, 3, 1, mins, true, false, false, false, true));
    career.ApplyMatchReport(new CareerContracts.MatchReport("seed-" + p.callsign, entries), serverId, now);
}
// One player's public profile description (shown by the character-info screen).
career.SetDescription(accountIds["ArchonAlpha"], "Archon of the alpha wing - mining for glory.", now);

try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        Intro = true, PlayerName = "ProofPilot", LobbyUrl = lobbyUrl.AbsoluteUri,
        WindowWidth = 1280, WindowHeight = 720,
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new CareerProof(game, output, accountIds["ArchonAlpha"], lobbyUrl.AbsoluteUri));
    game.Run();
}
finally
{
    await app.StopAsync();
    await app.DisposeAsync();
    try { Directory.Delete(scratch, recursive: true); } catch (Exception) { /* leave for inspection */ }
}

Console.WriteLine("P808C_UI_PROOF done");
return;

// ═══════════════════════════════════════════════════════════════════════════
// Drives the production Game1 through the intro → career path and captures each
// screen in both HUD styles.
// ═══════════════════════════════════════════════════════════════════════════
sealed class CareerProof(Game1 game, string output, string accountId, string lobbyUrl) : GameComponent(game)
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
        Console.WriteLine("P808C_SCREEN " + name);
    }

    private void SetStyle(HudStyle style) => Get<HudSettings>("_hudSettings").Style = style;
    private bool Loaded => Get<Task>("_careerLoad") is { IsCompleted: true };
    private bool InfoLoaded => Get<Allegiance.ClientCore.MonoGame.Career.CharacterInfoModel>("_characterInfo").Callsign.Length > 0;
    private bool BoardLoaded => Get<Allegiance.ClientCore.MonoGame.Career.LeaderboardModel>("_leaderboard").Entries.Count > 0;
    private object CareerMode(int value) =>
        System.Enum.ToObject(typeof(Game1).GetNestedType("CareerMode", BindingFlags.NonPublic)!, value);

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("P808c career proof did not finish");
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
                // Pin the lobby URL (the intro may have restored a saved one) and
                // enter the career screen through the production path.
                Set("_lobbyUrl", lobbyUrl);
                Call("EnterCareerScreen");
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 1:
                // Character-info for a seeded player (production fetch + ingest).
                Set("_careerViewAccount", accountId);
                Set("_careerMode", CareerMode(0));          // CharacterInfo
                Call("ReloadCareer");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                break;
            case 2:
                if (!InfoLoaded) { _step--; return; }
                SetStyle(HudStyle.Classic);
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 3:
                Shot("career-character-info-classic-720p");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 4:
                SetStyle(HudStyle.Modern);
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 5:
                Shot("career-character-info-modern-720p");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 6:
                // Leaderboard (all civs, default score sort).
                Set("_careerMode", CareerMode(1));
                Call("ReloadCareer");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                break;
            case 7:
                if (!BoardLoaded) { _step--; return; }
                SetStyle(HudStyle.Classic);
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 8:
                Shot("career-leaderboard-classic-720p");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 9:
                SetStyle(HudStyle.Modern);
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 10:
                Shot("career-leaderboard-modern-720p");
                _finishAfterFlush = true;
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            default:
                game.Exit();
                return;
        }
    }
}
