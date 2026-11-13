// Real Game1 browser captures from local HTTP-handler fixtures. No public host,
// network listener, game server or artwork is needed. Both HUD styles use DrawLobby.
// SDL_VIDEODRIVER=offscreen dotnet run --project tools/p1004-lobby-proof --no-build -- --out <directory>
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;

int outArg = Array.IndexOf(args, "--out");
string output = Path.GetFullPath(outArg >= 0 ? args[outArg + 1] : "docs/reports/P1004_assets");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
using var http = new HttpClient(new BrowserFixture()) { BaseAddress = new Uri("http://127.0.0.1:5000") };
using var game = new Game1(new Game1.LaunchOptions
{
    Intro = true, PlayerName = "P1004Pilot", WindowWidth = 1280, WindowHeight = 720,
    LobbyUrl = "http://127.0.0.1:5000", LobbyUrlExplicit = true,
});
game.Content.RootDirectory = content;
game.Components.Add(new BrowserProof(game, http, output));
game.Run();

sealed class BrowserProof(Game1 game, HttpClient http, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] Scenarios = ["password-free", "full", "no-team-slots", "lobby-down", "http-error", "filtered", "newer-client"];
    private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
    private int _step;
    private double _next;
    private T Get<T>(string field) => (T)typeof(Game1).GetField(field, Hidden)!.GetValue(game)!;
    private void Set(string field, object? value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
    private object? Call(string method, params object[] args) => typeof(Game1).GetMethod(method, Hidden)!.Invoke(game, args);

    public override void Update(GameTime gameTime)
    {
        game.GraphicsDevice.Textures[0] = null;
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("P1004 proof timeout");
        if (Get<bool>("_autoShotScheduled") || _watch.Elapsed.TotalSeconds < _next) return;
        if (_step == 0)
        {
            if (Get<object>("_state").ToString() != "Intro") return;
            Call("StopIntroScan");
            Get<HttpClient>("_http").Dispose();
            Set("_http", http);
            Set("_state", Enum.Parse(typeof(Game1).GetField("_state", Hidden)!.FieldType, "Lobby"));
            Set("_lastLobbyFetch", double.PositiveInfinity);
        }
        int frame = _step / 2;
        if (frame >= Scenarios.Length * 2) { game.Exit(); return; }
        string scenario = Scenarios[frame % Scenarios.Length];
        var style = frame < Scenarios.Length ? HudStyle.Classic : HudStyle.Modern;
        if (_step % 2 == 0)
        {
            Get<HudSettings>("_hudSettings").Style = style;
            BrowserFixture.Scenario = scenario;
            Get<LobbyGameFilters>("_gameFilters").Reset();
            if (scenario == "filtered") Get<LobbyGameFilters>("_gameFilters").Developments = LobbyGameSort.BooleanFilter.No;
            Get<List<ServerListing>>("_servers").Clear();
            Call("RebuildDisplay", false);
            Set("_lanJoinPassword", new StringBuilder());
            Set("_editingLanPassword", false);
            Set("_lobbyError", null);
            ((Task)Call("FetchServersAsync")!).GetAwaiter().GetResult();
        }
        else
        {
            if (Get<bool>("_lobbyResultPending")) return;
            if (scenario == "password-free") Call("TryJoinSelected");
            Set("_autoshotDir", output);
            Set("_autoShotName", $"p1004_{scenario}_{style.ToString().ToLowerInvariant()}");
            Set("_autoShotScheduled", true);
            Console.WriteLine($"[p1004-proof] {style}/{scenario} visible={Get<List<int>>("_visibleRows").Count} error={Get<string?>("_lobbyError")}");
        }
        _step++;
        _next = _watch.Elapsed.TotalSeconds + 0.8;
    }
}

sealed class BrowserFixture : HttpMessageHandler
{
    public static string Scenario { get; set; } = "password-free";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/cores", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        if (Scenario == "lobby-down") throw new HttpRequestException("Connection refused (local fixture)");
        if (Scenario == "http-error") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var guarded = Listing("Guarded game", true, true);
        ServerListing[] rows = Scenario switch
        {
            "full" => [Listing("Full game", false, false, 200)],
            "no-team-slots" => [Listing("Locked game", false, false), Listing("No active base", false, false)],
            "newer-client" => [guarded with { MinClientVersion = "999.0.0" }],
            _ => [guarded],
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(rows), Encoding.UTF8, "application/json"),
        });
    }
    private static ServerListing Listing(string name, bool slots, bool password, int players = 0) => new(
        name, name, "127.0.0.1", 6073, players, 200, name, "PCore001.igc", "0.1.6",
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        Game: new(true, 0x7fff, true, true, true, true, false, false, false, false, false, false, -1, 1000,
            MaxPlayersPerGame: 200, AnySlotsAvailable: slots, GuaranteedSlotsAvailable: slots), PasswordRequired: password);
}
