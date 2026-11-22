using System.Text.RegularExpressions;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.Browser.Tests;

/// <summary>P1073 — guards for the KNI WebGL point-primitive trap that stopped Game1
/// on the first live generated-map flight frame.</summary>
public sealed class P1073BrowserRenderGuards
{
    [Fact]
    public void Star_dots_expand_each_point_into_one_short_line()
    {
        var points = new[]
        {
            new VertexPositionColor(new Vector3(7000f, 0f, 0f), Color.Red),
            new VertexPositionColor(new Vector3(0f, 0f, -7000f), Color.Green),
            new VertexPositionColor(Vector3.Zero, Color.Blue),
        };
        VertexPositionColor[] buffer = [];
        var expanded = BrowserStarDots.Expand(points, ref buffer);

        Assert.Same(buffer, expanded);
        Assert.Equal(points.Length * 2, expanded.Length);
        for (int i = 0; i < points.Length - 1; i++)
        {
            var head = expanded[i * 2];
            var tail = expanded[i * 2 + 1];
            Assert.Equal(points[i].Position, head.Position);
            Assert.Equal(points[i].Color, head.Color);
            Assert.Equal(points[i].Color, tail.Color);
            // A non-degenerate but tiny segment (about 1.5 px at a 1080 px, 90° view).
            float length = (tail.Position - head.Position).Length();
            Assert.InRange(length, 1e-4f, points[i].Position.Length() * 0.01f);
        }
        // The origin has no direction to offset along; it stays a safe degenerate dot.
        Assert.Equal(Vector3.Zero, expanded[5].Position);
    }

    [Fact]
    public void Expanding_reuses_the_buffer_and_handles_empty_input()
    {
        var points = new[] { new VertexPositionColor(new Vector3(1f, 0f, 0f), Color.White) };
        VertexPositionColor[] buffer = [];
        BrowserStarDots.Expand(points, ref buffer);
        var first = buffer;
        BrowserStarDots.Expand(points, ref buffer);
        Assert.Same(first, buffer);
        Assert.Empty(BrowserStarDots.Expand([], ref buffer));
        Assert.Empty(buffer);
    }

    [Fact]
    public void Hud_power_prefers_the_local_native_simulation_when_predicting()
    {
        // Native's owning client reads its own CafterburnerIGC::GetPower; a remote
        // ship applies the update byte (Igc/shipIGC.cpp:3080). Both are the same
        // quantity, so Classic and Modern read the same source.
        Assert.Equal(0.25f, HudFormat.AfterburnerPower(ownShipPredicting: true, simulated: 0.25f, wire: 1f));
        Assert.Equal(1f, HudFormat.AfterburnerPower(ownShipPredicting: false, simulated: 0.25f, wire: 1f));
        Assert.Equal("0", HudFormat.FormatPercent(HudFormat.AfterburnerPower(false, 0.25f, 0f)));
    }

    [Fact]
    public void No_browser_reachable_renderer_draws_a_point_list()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AllegianceCore.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        string renderers = Path.Combine(root.FullName, "src/Allegiance.ClientCore.Game/Rendering");
        var offenders = new List<string>();
        int browserAlternatives = 0;
        foreach (string file in Directory.GetFiles(renderers, "*.cs"))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || !line.Contains("PrimitiveType.PointList", StringComparison.Ordinal)) continue;
                // The shared renderers keep native points for desktop inside the
                // nearest `#if !BROWSER`, with a LineList/BROWSER alternative; a
                // point draw under a BROWSER-active (or no) guard is the trap.
                int guardIndex = -1;
                for (int j = i - 1; j >= 0; j--)
                    if (lines[j].TrimStart().StartsWith("#if", StringComparison.Ordinal)) { guardIndex = j; break; }
                string? guard = guardIndex < 0 ? null : lines[guardIndex].Trim();
                bool desktopOnly = guard is not null && Regex.IsMatch(guard, @"^#if\s+!BROWSER\b");
                if (!desktopOnly && guard is not null && Regex.IsMatch(guard, @"^#if\s+BROWSER\b"))
                    // The point draw is the #else arm of `#if BROWSER` (desktop-only).
                    desktopOnly = lines.Skip(guardIndex + 1).Take(i - guardIndex - 1)
                        .Any(l => l.TrimStart().StartsWith("#else", StringComparison.Ordinal));
                bool hasLineAlternative = lines.Any(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    && l.Contains("PrimitiveType.LineList", StringComparison.Ordinal));
                if (!desktopOnly) offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line}");
                else if (!hasLineAlternative) offenders.Add($"{Path.GetFileName(file)}:{i + 1}: no browser line alternative");
                else browserAlternatives++;
            }
        }
        Assert.Empty(offenders);
        Assert.True(browserAlternatives >= 3, $"expected WarpRenderer/NativeSkyRenderer/Starfield/SpaceStarRenderer point paths guarded, saw {browserAlternatives}");
    }
}
