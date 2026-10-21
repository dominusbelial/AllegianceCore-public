using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P756/SIM-0276 — pins the autopilot-disengage sound decision so the production
/// handler cannot accidentally double-play or fire on silent transitions.
/// Native plays <c>salAutopilotDisengageSound</c> only for two transitions:
/// manual flight input (WinTrek/WinTrek.cpp:8315) and the autopilot toggle key
/// (WinTrek/WinTrek.cpp:10365). Every other <c>SetAutoPilot(false)</c> transition
/// is silent (arrived, docked, rescued, target lost, spectating, target nothing,
/// and the commented-out accepted-order branch at :8877-8898).
/// </summary>
public class AutopilotDisengageSoundTests
{
    public static IEnumerable<object[]> ReasonAndWasEngaged()
    {
        foreach (Game1.AutopilotDisengageReason reason in Enum.GetValues<Game1.AutopilotDisengageReason>())
        {
            yield return new object[] { reason, true };
            yield return new object[] { reason, false };
        }
    }

    [Theory]
    [MemberData(nameof(ReasonAndWasEngaged))]
    internal void ShouldPlayAutopilotDisengageCue_matches_native_two_transition_rule(
        Game1.AutopilotDisengageReason reason, bool wasEngaged)
    {
        bool expected = wasEngaged && reason is Game1.AutopilotDisengageReason.ToggleOff
                                          or Game1.AutopilotDisengageReason.ManualInput;
        Assert.Equal(expected, Game1.ShouldPlayAutopilotDisengageCue(wasEngaged, reason));
    }

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
    public void DisengageAutopilot_routes_through_rule_and_is_the_only_caller_of_PlayAutopilotDisengage()
    {
        var flightInput = Src("Game1.FlightInput.cs");
        var body = Method(flightInput, "private void DisengageAutopilot(AutopilotDisengageReason reason)");
        Assert.Contains("ShouldPlayAutopilotDisengageCue(wasEngaged, reason)", body);

        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        var gameDir = Path.Combine(root, "src", "Allegiance.ClientCore.Game");
        int callSites = 0;
        foreach (var path in Directory.EnumerateFiles(gameDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            foreach (Match m in Regex.Matches(text, @"PlayAutopilotDisengage\s*\("))
            {
                // Exclude the method definition in SoundManager.cs.
                var line = text[..m.Index].Split('\n').Length;
                var lineText = text.Split('\n')[line - 1];
                if (!lineText.Contains("void PlayAutopilotDisengage"))
                    callSites++;
            }
        }
        Assert.Equal(1, callSites);
    }

    [Fact]
    public void Manual_input_passes_AutopilotDisengageReason_ManualInput()
    {
        var flightInput = Src("Game1.FlightInput.cs");
        var body = Method(flightInput, "private void UpdateFlying(");
        Assert.Contains("DisengageAutopilot(AutopilotDisengageReason.ManualInput)", body);
    }

    [Fact]
    public void Toggle_key_passes_AutopilotDisengageReason_ToggleOff()
    {
        var flightInput = Src("Game1.FlightInput.cs");
        var body = Method(flightInput, "private void ToggleAutopilotForTarget(");
        Assert.Contains("DisengageAutopilot(AutopilotDisengageReason.ToggleOff)", body);
    }

    [Fact]
    public void No_string_reason_DisengageAutopilot_calls_remain()
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        var gameDir = Path.Combine(root, "src", "Allegiance.ClientCore.Game");
        foreach (var path in Directory.EnumerateFiles(gameDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotMatch(
                new Regex(@"DisengageAutopilot\s*\(\s*"""),
                text);
        }
    }
}
