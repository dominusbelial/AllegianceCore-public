using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-12b merge — source guards for the wiring the behavioural tests can't
/// reach headless: both renderers draw the cloak readout through
/// <c>HudStringCache.CloakStatusText</c> (so CLOAK DRAINED shows before the first
/// strength update), the command summary builds no strings per frame, and
/// self-target / TK_TargetCommand pass <c>UseDefault: false</c> (native sends
/// c_cidNone there, WinTrek.cpp:10410, 10442).
/// </summary>
public class Hud12bSourceGuardTests
{
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
    public void Both_renderers_draw_the_cloak_readout_through_the_cached_status_text()
    {
        const string call = "HudStringCache.CloakStatusText(status.CloakDrained, status.CloakStrengthPercent)";
        Assert.Contains(call, Src(Path.Combine("Rendering", "HudRenderer.cs")));
        Assert.Contains(call, Src(Path.Combine("Rendering", "ModernHudRenderer.cs")));
    }

    [Fact]
    public void Command_summary_and_gauges_build_no_strings_per_frame()
    {
        var hud = Src(Path.Combine("Rendering", "HudRenderer.cs"));
        foreach (var signature in new[] { "void DrawCommandShipSummary(", "void DrawCommandGauge(" })
        {
            var body = Method(hud, signature);
            Assert.DoesNotMatch(new Regex("\\$\"|\"\\s*\\+|\\+\\s*\""), body);
        }
    }

    [Fact]
    public void Command_summary_gauge_bar_keeps_the_modern_spread_base()
    {
        // FONT-1c fix 7 — Modern inherits DrawCommandShipSummary, so its gauge
        // inset/bar size must stay on the own-status Spread base; classic keeps
        // FONT-1b's native 1:1 TextScale.
        var body = Method(Src(Path.Combine("Rendering", "HudRenderer.cs")), "void DrawCommandGauge(");
        Assert.Contains("float paneScale = UsesModernGauges ? OwnStatusLayout.Spread(_viewportHeight) : TextScale;", body);
    }

    [Fact]
    public void Self_target_and_command_selection_do_not_resolve_a_default_order()
    {
        Assert.Contains("new AutopilotTarget(TargetKind.Ship, myId, name, UseDefault: false)", Src("Game1.Targeting.cs"));
        Assert.Contains("c.CommandId, UseDefault: false)", Src("Game1.Orders.cs"));
    }
}
