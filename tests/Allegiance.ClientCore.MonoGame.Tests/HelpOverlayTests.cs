using System.Collections.Generic;
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
public class HelpOverlayTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>
    /// P941 — these tests pin the KEY SET the port shipped before the native
    /// defaults arrived, so they read the table of an explicit "Port layout"
    /// map (never the process-wide live one, whose fresh-profile default is now
    /// the native defaultinputmap.mdl layout).
    /// </summary>
    private static readonly ActionMap PortMap = new(InputActionCatalog.Definitions, InputLayout.Port);

    private static IReadOnlyList<KeyBindingTable.Group> Groups => PortMap.ToHelpGroups();

    // ── KeyBindingTable (data-driven from the action map) ─────────────────

    private static KeyBindingTable.Entry Find(string key) =>
        Groups
            .SelectMany(g => g.Entries)
            .First(e => e.Key == key);

    [Fact]
    public void Table_HasAllLogicalGroups()
    {
        Assert.Equal(19, Groups.Count);   // + LOCAL SKIRMISH (SP-5), TEAM FUNDING (P728) and TRAINING (P785)
        Assert.Contains(Groups, g => g.Title == "LOCAL SKIRMISH");
        Assert.Contains(Groups, g => g.Title == "MOVEMENT");
        // P445 — the persistent-cruise-throttle keys' own group.
        Assert.Contains(Groups, g => g.Title == "CRUISE THROTTLE");
        Assert.Contains(Groups, g => g.Entries.Any(e => e.Key == "=" && e.Description.StartsWith("Cruise")));
        Assert.Contains(Groups, g => g.Entries.Any(e => e.Key == "Backspace" && e.Description.StartsWith("Throttle ZERO")));
        Assert.Contains(Groups, g => g.Entries.Any(e => e.Key == "\\" && e.Description.StartsWith("Throttle FULL")));
        Assert.Contains(Groups, g => g.Title == "WEAPONS");
        Assert.Contains(Groups, g => g.Title.StartsWith("TARGETING"));
        Assert.Contains(Groups, g => g.Title == "CAMERA / VIEW");
        Assert.Contains(Groups, g => g.Title == "MUSIC");
        Assert.Contains(Groups, g => g.Title == "CHAT");
        Assert.Contains(Groups, g => g.Title.StartsWith("LOBBY"));
        // P282 — the pre-flight READY-CHECK screen's own bindings.
        Assert.Contains(Groups, g => g.Title.StartsWith("READY CHECK"));
        // P424 — the accept/reject/clear queued-order keys' own group.
        Assert.Contains(Groups, g => g.Title.StartsWith("QUEUED ORDERS"));
        Assert.Contains(Groups, g => g.Entries.Any(e => e.Key == "Home" && e.Description.StartsWith("Accept")));
        Assert.Contains(Groups, g => g.Entries.Any(e => e.Key == "End" && e.Description.StartsWith("Reject")));
        Assert.Contains(Groups, g => g.Entries.Any(e => e.Key == "Delete" && e.Description.StartsWith("Clear")));
        // P425 — the commandee-select / issue-order keys' own group.
        Assert.Contains(Groups, g => g.Title.StartsWith("COMMANDED SHIP"));
        // P626 — CycleCommandee moved from PageDown (native ChatPageDown owns
        // PageDown) to Shift+PageDown.
        Assert.Contains(Groups, g => g.Entries.Any(e => e.Key == "Shift + PageDown" && e.Description.StartsWith("Cycle")));
        // P624 — IssueBuild moved from Insert (now TK_ThrustUp) to Alt+I.
        Assert.Contains(Groups, g => g.Entries.Any(e => e.Key == "Alt + I" && e.Description.StartsWith("Issue")));
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
        Assert.Contains(Groups, g => g.Entries.Any(
            e => e.Description.Contains("rebind keys", System.StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(Groups.SelectMany(g => g.Entries),
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
        Assert.Contains(Groups.SelectMany(g => g.Entries),
            e => e.Key == "F5" && e.Description.Contains("fly-by", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Groups.SelectMany(g => g.Entries),
            e => e.Key == "F11" && e.Description.Contains("station", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Groups.SelectMany(g => g.Entries),
            e => e.Key == "PrintScreen" && e.Description.Contains("screen shot", System.StringComparison.OrdinalIgnoreCase));
        // Music volume is on Ctrl + = / Ctrl + -, nowhere on the F-row.
        Assert.Contains(Groups.SelectMany(g => g.Entries),
            e => e.Key == "Ctrl + =" && e.Description.Contains("volume up", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Groups.SelectMany(g => g.Entries),
            e => e.Key == "Ctrl + -" && e.Description.Contains("volume down", System.StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Groups.SelectMany(g => g.Entries),
            e => e.Key is "F5" or "F6" or "F11"
                 && e.Description.Contains("volume", System.StringComparison.OrdinalIgnoreCase));
        // And screenshot is NOT on F11.
        Assert.DoesNotContain(Groups.SelectMany(g => g.Entries),
            e => e.Key == "F11" && e.Description.Contains("screen shot", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryCell_IsPureAscii_AndNonEmpty()
    {
        // The HUD spritefonts bake ASCII 32..126 only; any high/unicode char
        // throws "cannot be resolved by this SpriteFont" and kills the game.
        foreach (var group in Groups)
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
        foreach (var group in Groups)
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
        Assert.All(Groups, g => Assert.NotEmpty(g.Entries));
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
    public void ContentBand_ReservesTheTopicBarAndFooter()
    {
        // P757b — the manual's topic bar under the header and the footer note
        // are fixed rows: the columns must start below the bar and end above the
        // note, so "CAMERA / VIEW" can never collide with the topic bar.
        const float panelY = 36f, panelH = 648f;
        var withBar = HelpOverlayLayout.ContentBand(panelY, panelH, topicBar: true);
        Assert.True(withBar.Top >= panelY + HelpOverlayLayout.Padding + HelpOverlayLayout.TopicBarHeight);
        Assert.True(withBar.Top + withBar.Height
            <= panelY + panelH - HelpOverlayLayout.Padding - HelpOverlayLayout.FooterReserve);

        var withoutBar = HelpOverlayLayout.ContentBand(panelY, panelH, topicBar: false);
        Assert.Equal(withBar.Top - HelpOverlayLayout.TopicBarHeight, withoutBar.Top);
        Assert.Equal(withBar.Height + HelpOverlayLayout.TopicBarHeight, withoutBar.Height);
    }

    [Theory]
    [InlineData("hlpControls", 720)]
    [InlineData("hlpControls2", 720)]
    [InlineData("hlpControls", 1080)]
    [InlineData("hlpControls2", 1080)]
    [InlineData("hlpControls", 1440)]
    [InlineData("hlpControls2", 1440)]
    public void ControlsPage_fits_with_two_rows_of_headroom(string topic, int height)
    {
        foreach (var layout in new[] { InputLayout.Port, InputLayout.Native })
        {
            var groups = HelpManualModel.ControlsGroups(new ActionMap(InputActionCatalog.Definitions, layout), topic);
            var band = HelpOverlayLayout.ContentBand(height * 0.04f, height * 0.92f, topicBar: true);
            // Conservative 11px HUD monospace advance for a graphics-free guard. The
            // window proof also validates this plan with the actual spritefont.
            var table = HelpOverlayLayout.MeasureControls(groups, text => text.Length * 11f,
                height * 16f / 9f * 0.982f - 2f * HelpOverlayLayout.Padding, band.Height);
            output.WriteLine($"{topic} {layout} {height}p: {groups.Sum(g => g.Entries.Count)} entries, "
                + $"scale={table.Scale:F2}, columns={table.ColumnWidths.Length}, "
                + $"rows used={table.UsedHeight / (22f * table.Scale):F2}, "
                + $"capacity={table.CapacityHeight / (22f * table.Scale):F2}, spare={table.RemainingRows:F2}");
            Assert.True(table.Scale >= (layout == InputLayout.Port ? 0.51f : 0.45f),
                $"{topic} {layout}: text scale {table.Scale:F2} leaves too little reading room");
            Assert.True(table.RemainingRows >= 2f - 0.001f,
                $"{topic}: only {table.RemainingRows:F2} spare rows; rebalance complete groups before adding bindings");
            Assert.True(table.UsedWidth <= height * 16f / 9f * 0.982f - 2f * HelpOverlayLayout.Padding);
            foreach (var section in table.Sections)
                Assert.True((section.Y + section.Height) * table.Scale <= band.Height);
            foreach (var column in table.Sections.GroupBy(s => s.Column))
            {
                var ordered = column.OrderBy(s => s.Y).ToArray();
                for (int i = 1; i < ordered.Length; i++)
                    Assert.True(ordered[i - 1].Y + ordered[i - 1].Height
                        + HelpOverlayLayout.ControlsGroupGap <= ordered[i].Y);
            }
        }
    }

    [Theory]
    [InlineData(InputLayout.Port)]
    [InlineData(InputLayout.Native)]
    public void Controls_pages_cover_every_live_group_and_entry_exactly_once(InputLayout layout)
    {
        var map = new ActionMap(InputActionCatalog.Definitions, layout);
        var pages = new[] { "hlpControls", "hlpControls2" }
            .SelectMany(topic => HelpManualModel.ControlsGroups(map, topic)).ToArray();
        Assert.Equal(map.ToHelpGroups().Count, pages.Length);
        Assert.Equal(pages.Length, pages.Select(g => g.Title).Distinct().Count());
        foreach (var group in map.ToHelpGroups())
            Assert.Equal(group.Entries, pages.Single(g => g.Title == group.Title).Entries);
        foreach (var topic in new[] { "hlpControls", "hlpControls2" })
        {
            var groups = HelpManualModel.ControlsGroups(map, topic);
            var band = HelpOverlayLayout.ContentBand(720f * 0.04f, 720f * 0.92f, true);
            var sections = HelpOverlayLayout.MeasureControls(groups, text => text.Length * 11f,
                1280f * 0.982f - 2f * HelpOverlayLayout.Padding, band.Height).Sections;
            for (int g = 0; g < groups.Count; g++)
                Assert.Equal(Enumerable.Range(0, groups[g].Entries.Count), sections.Where(s => s.GroupIndex == g)
                    .SelectMany(s => Enumerable.Range(s.FirstEntry, s.EntryCount)));
        }
    }

    [Theory]
    [InlineData("Target enemy [TK_TargetEnemy]", "Target enemy")]
    [InlineData("Zoom camera (hold)", "Zoom camera (hold)")]
    [InlineData("Cycle view [P836]", "Cycle view [P836]")]
    public void Help_description_omits_only_the_native_source_tag(string source, string display)
        => Assert.Equal(display, HelpManualModel.ControlsDescription(source));

    [Fact]
    public void Controls_entry_point_next_previous_and_back_keep_stable_topic_ids()
    {
        var manual = new HelpManualModel();
        manual.GoTo("hlpControls"); // KeyBind and InFlightProof entry point
        Assert.Equal("CONTROLS: FLIGHT", manual.Current.Title);
        manual.MoveTopicSelection(1);
        Assert.Equal("hlpControls2", manual.CurrentTopic);
        Assert.Equal("CONTROLS: OTHER", manual.Current.Title);
        manual.MoveTopicSelection(-1);
        Assert.Equal("hlpControls", manual.CurrentTopic);
        manual.Back();
        Assert.Equal("hlpControls2", manual.CurrentTopic);
        manual.Back();
        Assert.Equal("hlpControls", manual.CurrentTopic);
        manual.GoTo("hlpControls2"); // topic bar selection
        Assert.True(HelpManualModel.IsControlsTopic(manual.CurrentTopic));
    }

    [Fact]
    public void Table_BalancesIntoTypicalPanelWithoutOverflow()
    {
        // The REAL table — now the full 17-category reference (P625b routed every
        // pane/menu, free-cam and system command into the map, so the help overlay
        // lists them all). At the client's standard 2560x1440 backbuffer (the
        // resolution every live proof captures) the balanced columns fit the
        // panel without overflow. Uses the production splittable layout
        // (LayoutSections, what HelpOverlayRenderer actually draws): since the
        // P757+P759 merge the CAMERA/VIEW group alone is taller than the 1440p
        // band, so the whole-group Layout can no longer place it — the renderer
        // splits such groups across columns, and this test pins that no section
        // of any group exceeds the panel.
        var groups = Groups;
        const float panelY = 22f;
        // P761 — the renderer's panel grew to 92% of the backbuffer (the
        // CAMERA / VIEW group's 56 rows no longer fit the 90% band).
        const float panelH = 1440f * 0.92f - 2f * HelpOverlayLayout.Padding;
        var sections = HelpOverlayLayout.LayoutSections(
            groups.Select(g => g.Entries.Count).ToArray(), panelY, panelH);

        Assert.InRange(sections.Max(s => s.Column) + 1, 2, HelpOverlayLayout.MaxSectionColumns);
        foreach (var section in sections)
        {
            Assert.True(section.Y >= panelY, $"section of group {section.GroupIndex} starts above the panel");
            Assert.True(section.Y + section.Height <= panelY + panelH,
                $"section of group {section.GroupIndex} overflows the typical panel "
                + $"(y={section.Y}, h={section.Height}, panel={panelY}+{panelH})");
        }
        for (int g = 0; g < groups.Count; g++)
        {
            var covered = sections.Where(s => s.GroupIndex == g).SelectMany(
                s => Enumerable.Range(s.FirstEntry, s.EntryCount)).ToArray();
            Assert.Equal(groups[g].Entries.Count, covered.Length);
            Assert.Equal(Enumerable.Range(0, groups[g].Entries.Count), covered);
        }
    }
}
