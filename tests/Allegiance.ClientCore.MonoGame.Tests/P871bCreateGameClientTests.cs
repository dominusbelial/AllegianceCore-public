using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Games;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P871b — the client end of the new-game wizard: the request body carries the
/// native <c>MissionParams</c> struct as a base64 blob (never JSON fields), and an
/// ACK from the lobby sends the player to the server that is creating the mission
/// (native ends the create flow by connecting to that server,
/// <c>lobby/client.cpp:390-465</c>).
/// </summary>
public sealed class P871bCreateGameClientTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void Set(Game1 game, string name, object? value) =>
        (typeof(Game1).GetField(name, Hidden) ?? throw new InvalidOperationException(name)).SetValue(game, value);

    private static T Get<T>(Game1 game, string name) =>
        (T)(typeof(Game1).GetField(name, Hidden) ?? throw new InvalidOperationException(name)).GetValue(game)!;

    private static object? Call(Game1 game, string name, params object[] args) =>
        (typeof(Game1).GetMethod(name, Hidden) ?? throw new InvalidOperationException(name)).Invoke(game, args);

    /// <summary>Answers the catalog, then ACKs the create request.</summary>
    private sealed class Lobby : HttpMessageHandler
    {
        public string? CreateBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string json;
            if (request.Method == HttpMethod.Get)
                json = """{"servers":[{"id":"s1","name":"Host","address":"127.0.0.1","port":6073,"core":"PCore001.igc","missionName":"Configured World"}],"cores":["PCore001.igc"],"maps":["m2xspiderw.igc"]}""";
            else
            {
                CreateBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
                json = """{"id":"game1","gameName":"Client Game","state":"Queued"}""";
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static Game1 Headless(HttpClient http)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_http", http);
        Set(game, "_lobbyUrl", "http://127.0.0.1:5000");
        // GetUninitializedObject skips field initializers, so the wizard slice's
        // own state has to be created here.
        Set(game, "_createGame", new CreateGameWizard());
        Set(game, "_createGameLock", new object());
        Set(game, "_createGameError", "");
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "Intro"));
        return game;
    }

    [Fact]
    public async Task An_ack_sends_the_player_to_the_server_that_creates_the_mission()
    {
        var lobby = new Lobby();
        var game = Headless(new HttpClient(lobby));
        var wizard = Get<CreateGameWizard>(game, "_createGame");
        wizard.ApplyCatalog(new CreateGameWizard.Catalog(
            [new CreateGameWizard.ServerChoice("s1", "Host", "127.0.0.1", 6073, "PCore001.igc", "Configured World")],
            ["PCore001.igc"], ["m2xspiderw.igc"]));
        wizard.TypeName("Client Game");
        // Four DOWNs walk SERVER -> CORE -> MAP -> NAME -> CREATE (no wrap past it).
        for (int i = 0; i < 4; i++) wizard.Move(1);
        Assert.Equal(CreateGameWizard.Field.Create, wizard.Cursor);

        // ENTER on the CREATE row posts the request.
        Call(game, "UpdateCreateGame", new Microsoft.Xna.Framework.Input.KeyboardState(),
            false, false, true, false);
        for (int i = 0; i < 100 && Get<(string, int)?>(game, "_createGameJoinPending") is null; i++)
            await Task.Delay(20);

        // The body carries the native struct as base64 — no JSON fields.
        Assert.NotNull(lobby.CreateBody);
        using var body = JsonDocument.Parse(lobby.CreateBody!);
        string wire = body.RootElement.GetProperty("paramsWire").GetString()!;
        var parameters = Allegiance.Core.Wire.MissionParamsMsg.Decode(Convert.FromBase64String(wire));
        Assert.Equal("Client Game", parameters.StrGameName);
        Assert.DoesNotContain("\"bits\"", lobby.CreateBody);
        Assert.DoesNotContain("\"StrGameName\"", lobby.CreateBody);

        // ...and the ACK points the client at the chosen server (native's "you are
        // sent to the server that created your mission").
        Assert.Equal(("127.0.0.1", 6073), Get<(string, int)?>(game, "_createGameJoinPending"));
        Assert.Equal("Game requested (game1).", wizard.Status);
    }
}
