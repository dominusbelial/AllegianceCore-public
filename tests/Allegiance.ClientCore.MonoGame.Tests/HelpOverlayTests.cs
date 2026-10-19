using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P257 H keybinding table + layout (updated for P625,
/// where the table became LIVE/data-driven — generated from the action map so a
/// rebind is reflected immediately). No graphics device (same discipline as
/// <see cref="StarmapLayoutTests"/>). They pin the contract that earns a test:
/// the table is complete enough to be a useful reference, every cell is
/// renderable by the ASCII-only spritefonts, and the balanced column layout
/// never lets a group overflow the panel or leaves the columns wildly
/// unbalanced.
/// </summary>
public class HelpOverlayTests
{
    // ── KeyBindingTable (data-driven from the live action map) ────────────

    private static KeyBindingTable.Entry Find(string key) =>
        KeyBindingTable.All
            .SelectMany(g => g.Entries)
            .First(e => e.Key == key);

    [Fact]
    public void Table_HasAllLogicalGroups()
    {
        Assert.Equal(18, KeyBindingTable.GroupCount);   // + LOCAL SKIRMISH (SP-5) and TEAM FUNDING (P728)
        Assert.Contains(KeyBindingTable.All, g => g.Title == "LOCAL SKIRMISH");
        Assert.Contains(KeyBindingTable.All, g => g.Title == "MOVEMENT");
        // P445 — the persistent-cruise-throttle keys' own group.
        Assert.Contains(KeyBindingTable.All, g => g.Title == "CRUISE THROTTLE");
        Assert.Contains(KeyBindingTable.All, g => g.Entries.Any(e => e.Key == "=" && e.Description.StartsWith("Cruise")));
        Assert.Contains(KeyBindingTable.All, g => g.Entries.Any(e => e.Key == "Backspace" && e.Description.StartsWith("Throttle ZERO")));
        Assert.Contains(KeyBindingTable.All, g => g.Entries.Any(e => e.Key == "\\" && e.Description.StartsWith("Throttle FULL")));
        Assert.Contains(KeyBindingTable.All, g => g.Title == "WEAPONS");
        Assert.Contains(KeyBindingTable.All, g => g.Title.StartsWith("TARGETING"));
        Assert.Contains(KeyBindingTable.All, g => g.Title == "CAMERA / VIEW");
        Assert.Contains(KeyBindingTable.All, g => g.Title == "MUSIC");
        Assert.Contains(KeyBindingTable.All, g => g.Title == "CHAT");
        Assert.Contains(KeyBindingTable.All, g => g.Title.StartsWith("LOBBY"));
        // P282 — the pre-flight READY-CHECK screen's own bindings.
        Assert.Contains(KeyBindingTable.All, g => g.Title.StartsWith("READY CHECK"));
        // P424 — the accept/reject/clear queued-order keys' own group.
        Assert.Contains(KeyBindingTable.All, g => g.Title.StartsWith("QUEUED ORDERS"));
        Assert.Contains(KeyBindingTable.All, g => g.Entries.Any(e => e.Key == "Home" && e.Description.StartsWith("Accept")));
        Assert.Contains(KeyBindingTable.All, g => g.Entries.Any(e => e.Key == "End" && e.Description.StartsWith("Reject")));
        Assert.Contains(KeyBindingTable.All, g => g.Entries.Any(e => e.Key == "Delete" && e.Description.StartsWith("Clear")));
        // P425 — the commandee-select / issue-order keys' own group.
        Assert.Contains(KeyBindingTable.All, g => g.Title.StartsWith("COMMANDED SHIP"));
        // P626 — CycleCommandee moved from PageDown (native ChatPageDown owns
        // PageDown) to Shift+PageDown.
        Assert.Contains(KeyBindingTable.All, g => g.Entries.Any(e => e.Key == "Shift + PageDown" && e.Description.StartsWith("Cycle")));
        // P624 — IssueBuild moved from Insert (now TK_ThrustUp) to Alt+I.
        Assert.Contains(KeyBindingTable.All, g => g.Entries.Any(e => e.Key == "Alt + I" && e.Description.StartsWith("Issue")));
    }

    [Fact]
    public void Table_HasTheHelpSelfEntry()
    {
        // P623 — native wins: native's Help key is F1 (defaultinputmap.mdl,
        // (112,8,204) = TK_Help), so the port's Help moved from H to F1 and the
        // help entry is found under F1.
        Assert.Equal("Toggle this help (you are here)", Find("F1").Description);
    }

    [Fact]
    public void Table_SaysHowToOpenTheRebindScreen()
    {
        // P625 — the Controls (rebind) popup is announced in the table and its
        // own entry exists (the help screen tells you how to rebind).
        Assert.Contains(KeyBindingTable.All, g => g.Entries.Any(
            e => e.Description.Contains("rebind keys", System.StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(KeyBindingTable.All.SelectMany(g => g.Entries),
            e => e.Key == "F6" && e.Description.Contains("Controls", System.StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    // Representative one per group — proves the table actually captures the
    // real bindings implemented in Game1 (cross-checked against the code).
    [InlineData("W", "Throttle")]
    [InlineData("Shift", "Afterburner")]
    [InlineData("Space", "Fire weapons")]
    [InlineData("Ctrl", "Fire missile")]
    [InlineData("`", "Group-fire")]
    [InlineData("C", "Drop chaff")]
    [InlineData("N", "Drop mine")]
    [InlineData("T", "target")]
    [InlineData("G", "autopilot")]
    [InlineData("V", "cockpit")]
    [InlineData("M", "mouse flight")]
    [InlineData("Tab", "starmap")]
    [InlineData("R", "quick-chat")]
    public void Table_ContainsCoreBindings(string key, string expectedFragment)
    {
        Assert.Contains(expectedFragment, Find(key).Description, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Table_FKeyFix_CamerasShowOnF5AndF11()
    {
        // P625 — the F-key fix, visible in the table: F5 = fly-by camera and
        // F11 = station camera; screenshot moved to PrintScreen; music volume on
        // the Ctrl chord (not F5/F6/F11).
        Assert.Contains(KeyBindingTable.All.SelectMany(g => g.Entries),
            e => e.Key == "F5" && e.Description.Contains("fly-by", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(KeyBindingTable.All.SelectMany(g => g.Entries),
            e => e.Key == "F11" && e.Description.Contains("station", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(KeyBindingTable.All.SelectMany(g => g.Entries),
            e => e.Key == "PrintScreen" && e.Description.Contains("screen shot", System.StringComparison.OrdinalIgnoreCase));
        // Music volume is on Ctrl + = / Ctrl + -, nowhere on the F-row.
        Assert.Contains(KeyBindingTable.All.SelectMany(g => g.Entries),
            e => e.Key == "Ctrl + =" && e.Description.Contains("volume up", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(KeyBindingTable.All.SelectMany(g => g.Entries),
            e => e.Key == "Ctrl + -" && e.Description.Contains("volume down", System.StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(KeyBindingTable.All.SelectMany(g => g.Entries),
            e => e.Key is "F5" or "F6" or "F11"
                 && e.Description.Contains("volume", System.StringComparison.OrdinalIgnoreCase));
        // And screenshot is NOT on F11.
        Assert.DoesNotContain(KeyBindingTable.All.SelectMany(g => g.Entries),
            e => e.Key == "F11" && e.Description.Contains("screen shot", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryCell_IsPureAscii_AndNonEmpty()
    {
        // The HUD spritefonts bake ASCII 32..126 only; any high/unicode char
        // throws "cannot be resolved by this SpriteFont" and kills the game.
        foreach (var group in KeyBindingTable.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(group.Title));
            foreach (var e in group.Entries)
            {
                Assert.All(new[] { group.Title, e.Key, e.Description },
                    s => Assert.All(s, ch =>
                    {
                        Assert.True(ch >= 32 && ch <= 126,
                            $"non-ASCII char U+{(int)ch:X4} in \"{s}\"");
                    }));
                Assert.False(string.IsNullOrWhiteSpace(e.Key));
                Assert.False(string.IsNullOrWhiteSpace(e.Description));
            }
        }
    }

    [Fact]
    public void NoDuplicateKeys_WithinAGroup()
    {
        foreach (var group in KeyBindingTable.All)
        {
            var dup = group.Entries
                .Select(e => e.Key)
                .GroupBy(k => k)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            Assert.Empty(dup);
        }
    }

    [Fact]
    public void EveryGroup_HasAtLeastOneEntry()
    {
        Assert.All(KeyBindingTable.All, g => Assert.NotEmpty(g.Entries));
    }

    // ── HelpOverlayLayout ────────────────────────────────────────────────

    [Fact]
    public void Layout_EmptyInput_ProducesEmptySlots()
    {
        var slots = HelpOverlayLayout.Layout([], 100f, 500f);
        Assert.Empty(slots);
    }

    [Fact]
    public void Layout_TallPanel_UsesOneColumn_AndStacksFromTop()
    {
        // A huge panel keeps a single column; groups stack top-down from panelY
        // with GroupGap between them.
        var slots = HelpOverlayLayout.Layout([40f, 60f, 30f], 100f, 2000f);
        Assert.All(slots, s => Assert.Equal(0, s.Column));
        Assert.Equal(100f, slots[0].Y);
        Assert.Equal(100f + 40f + HelpOverlayLayout.GroupGap, slots[1].Y);
        Assert.Equal(slots[1].Y + 60f + HelpOverlayLayout.GroupGap, slots[2].Y);
    }

    [Fact]
    public void Layout_EqualHeights_SmallPanel_BalanceIntoRoundRobin()
    {
        // Equal 100-px groups in a 400-px panel -> 3 columns, packed shortest-
        // first -> 0,1,2,0,1,2.
        var slots = HelpOverlayLayout.Layout([100f, 100f, 100f, 100f, 100f, 100f], 0f, 400f);
        Assert.Equal([0, 1, 2, 0, 1, 2], slots.Select(s => s.Column));
    }

    [Fact]
    public void Layout_EveryGroupStaysInsideThePanelBounds()
    {
        // Representative group footprints for the real six-group table.
        var heights = new[] { 164f, 164f, 76f, 120f, 120f, 120f };
        float panelY = 50f, panelH = 500f;
        var slots = HelpOverlayLayout.Layout(heights, panelY, panelH);
        for (int i = 0; i < heights.Length; i++)
        {
            Assert.True(slots[i].Y >= panelY);
            Assert.True(slots[i].Y + heights[i] <= panelY + panelH,
                $"group {i} overflows panel bottom");
        }
    }

    [Fact]
    public void Layout_Balancing_KeepsColumnsNearlyEqual()
    {
        // Six equal groups over a 400-px panel -> 3 columns each ending at the
        // same cumulative height (difference must stay within one group + gap).
        var slots = HelpOverlayLayout.Layout([100f, 100f, 100f, 100f, 100f, 100f], 0f, 400f);
        var colMax = new float[3];
        for (int i = 0; i < slots.Length; i++)
            colMax[slots[i].Column] = System.Math.Max(
                colMax[slots[i].Column], slots[i].Y + 100f);
        float hi = colMax.Max(), lo = colMax.Min();
        Assert.True(hi - lo <= 100f + HelpOverlayLayout.GroupGap,
            $"columns imbalanced: {string.Join(",", colMax)}");
    }

    [Fact]
    public void Table_BalancesIntoTypicalPanelWithoutOverflow()
    {
        // The REAL table — now the full 14-category reference (P625b routed every
        // pane/menu, free-cam and system command into the map, so the help overlay
        // lists them all). At the client's standard 2560x1440 backbuffer (the
        // resolution every live proof captures) the balanced columns fit the
        // panel without overflow: a handful of columns, every group within bounds.
        var heights = KeyBindingTable.All.Select(g => g.Height).ToArray();
        const float panelY = 22f;
        const float panelH = 1440f * 0.90f - 2f * HelpOverlayLayout.Padding;
        var slots = HelpOverlayLayout.Layout(heights, panelY, panelH);

        Assert.InRange(slots.Max(s => s.Column) + 1, 2, 6);
        for (int i = 0; i < heights.Length; i++)
        {
            Assert.True(slots[i].Y + heights[i] <= panelY + panelH,
                $"group {i} overflows the typical panel");
        }
    }
}
