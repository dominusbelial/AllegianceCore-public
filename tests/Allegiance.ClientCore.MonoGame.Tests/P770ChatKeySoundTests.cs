using System.IO;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// SIM-0317/0318/0319 — chat-key sound gates. Each row shares the same
/// <c>chatKeySound</c> wave, but the native compose-state gate at each call
/// site differs. The decision functions are pure static logic so every state
/// is headless-testable, and source guards pin the production wiring.
/// </summary>
public class P770ChatKeySoundTests
{
    // ── Decision-function coverage ─────────────────────────────────────────

    [Theory]
    [InlineData(NativeComposeState.NotComposing, true)]
    [InlineData(NativeComposeState.MouseCommand, true)]
    [InlineData(NativeComposeState.Chat, true)]
    [InlineData(NativeComposeState.Command, true)]
    [InlineData(NativeComposeState.Shell, false)]
    [InlineData(NativeComposeState.Quick, true)]
    public void OnVerb_obeys_native_shell_gate(NativeComposeState state, bool expected)
    {
        Assert.Equal(expected, ChatKeySound.OnVerb(state));
    }

    [Theory]
    [InlineData(NativeComposeState.NotComposing, false)]
    [InlineData(NativeComposeState.MouseCommand, false)]
    [InlineData(NativeComposeState.Chat, true)]
    [InlineData(NativeComposeState.Command, true)]
    [InlineData(NativeComposeState.Shell, true)]
    [InlineData(NativeComposeState.Quick, true)]
    public void OnChar_obeys_native_mouse_command_gate(NativeComposeState state, bool expected)
    {
        Assert.Equal(expected, ChatKeySound.OnChar(state));
    }

    [Fact]
    public void OnStartChat_always_allows_the_sound()
    {
        Assert.True(ChatKeySound.OnStartChat());
    }

    // ── Source guards ──────────────────────────────────────────────────────

    private static string Src(string relative)
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        return File.ReadAllText(Path.Combine(root, "src", "Allegiance.ClientCore.Game", relative));
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing " + signature);
        int open = source.IndexOf('{', start), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced " + signature);
    }

    [Fact]
    public void Verb_site_routes_through_ChatKeySound_OnVerb_before_playing()
    {
        var body = Method(Src("Game1.Commanding.cs"), "bool TryCommandBarPick(MouseState mouse)");
        int gate = body.IndexOf("ChatKeySound.OnVerb(LiveComposeState)", StringComparison.Ordinal);
        int play = body.IndexOf("_sound?.PlayChatKey()", StringComparison.Ordinal);
        Assert.True(gate >= 0, "missing ChatKeySound.OnVerb call");
        Assert.True(play > gate, "sound must play after the gate is evaluated");
    }

    [Fact]
    public void Char_site_routes_through_ChatKeySound_OnChar_before_playing()
    {
        var body = Method(Src("Game1.Session.cs"), "void OnTextInput(object? sender, TextInputEventArgs e)");
        int gate = body.IndexOf("ChatKeySound.OnChar(LiveComposeState)", StringComparison.Ordinal);
        int play = body.IndexOf("_sound?.PlayChatKey()", StringComparison.Ordinal);
        Assert.True(gate >= 0, "missing ChatKeySound.OnChar call");
        Assert.True(play > gate, "sound must play after the gate is evaluated");
    }

    [Fact]
    public void StartChat_site_routes_through_ChatKeySound_OnStartChat_before_playing()
    {
        var body = Method(Src("Game1.FlightInput.cs"), "void HandleChatCompose(KeyboardState ks)");
        int gate = body.IndexOf("ChatKeySound.OnStartChat(", StringComparison.Ordinal);
        int play = body.IndexOf("_sound?.PlayChatKey()", StringComparison.Ordinal);
        Assert.True(gate >= 0, "missing ChatKeySound.OnStartChat call");
        Assert.True(play > gate, "sound must play after the gate is evaluated");
    }
}
