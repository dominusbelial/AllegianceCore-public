using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P805 (PAR-10) — HUD leftovers. Per-row headless pins:
/// <list type="bullet">
/// <item><b>Target model preview</b> (row 409) — the <c>SetTargetGeo</c> gate is
/// NOT kind-specific: native shows any target with a hit test + ThingSite
/// (WinTrek.cpp:8783-8818), so warp/probe/treasure/mine kinds are eligible
/// (aleph surface warpIGC.cpp:33; probe mesh probetypeigc.cpp:28-33; mine sprite
/// mineigc.cpp:103; treasure decal/model treasureIGC.cpp:118-171).</item>
/// <item><b>Radar treasure and tech contacts</b> (row 428) — the LIVE dev-tech
/// split: a development treasure obsolete under the side's development techs is
/// <b>not useful</b> (radarimage.cpp:452-463; developmentigc.h:110-113).</item>
/// <item><b>Radar construction highlight</b> (row 437) — native c_maskHighlight
/// is the FLAG-treasure highlight and has NO bracket surface: it only keeps the
/// full brightness (radarimage.cpp:637-640,1094-1096), so a Highlight-only
/// contact draws no overlay bracket and no invented ring.</item>
/// <item><b>Selected target bracket</b> (row 418) — the port's 3-D-view
/// selection box is a superset (native marks the target only on the radar,
/// radarimage.cpp:307-319,352), so <c>HudSettings.SelectionBracket</c> defaults
/// OFF = native.</item>
/// <item><b>Tip text lines</b> (row 456) — the side-colour fallback is native's
/// <c>Color(1,1,1)</c> white (consoledata.cpp:99-107), not an accent.</item>
/// <item><b>Money credits</b> (row 459) — native has no flight credit counter;
/// the creditbox money pane is F5-only (dialog.ml:1885-1908,5199-5203,5368) —
/// pinned by the existing Hud10PanelsTests money layout.</item>
/// <item><b>HUD style and software layouts / console visibility</b> (row 470) —
/// native's independent ShowCenterHUD / ShowTargetHUD persisted toggles
/// (WinTrek.cpp:2898-2899,3300-3303) and the menu-only ShowChatHistoryHUD
/// (:5074-5078) gate the shared model.</item>
/// <item><b>Console pane slide</b> (row 471) — the target pane now rides the
/// native 0.5 s slide (dialog.mdl:5404, 208 px travel).</item>
/// <item><b>Command view ship summary</b> (row 492) — the native target group's
/// range/speed rows (dialog.mdl:4606-4656) replace the removed money/AP/RADAR
/// echo line (a port invention; console.cpp:791-793 feeds separate slots).</item>
/// </list>
/// </summary>
public class P805HudLeftoversTests
{
    // ── Row 409 — target model preview kinds ──────────────────────────────

    [Fact]
    public void Preview_gate_accepts_warp_probe_treasure_and_mine_targets()
    {
        foreach (var kind in new[] { TargetPreviewKind.Warp, TargetPreviewKind.Probe,
                     TargetPreviewKind.Treasure, TargetPreviewKind.Mine })
        {
            Assert.True(TargetModelPreview.Eligible(kind, targetSourceShipId: -1, ownSourceShipId: 10,
                sameCluster: true, visible: true, hasMesh: true), $"{kind} should be eligible");
            Assert.False(TargetModelPreview.Eligible(kind, -1, 10, sameCluster: false, visible: true, hasMesh: true),
                $"{kind} outside the cluster must stay empty (WinTrek.cpp:8799-8800)");
            Assert.False(TargetModelPreview.Eligible(kind, -1, 10, sameCluster: true, visible: true, hasMesh: false),
                $"{kind} with no art must stay empty");
        }
    }

    [Fact]
    public void Preview_kind_enum_has_the_four_new_kinds()
    {
        Assert.True(Enum.IsDefined(TargetPreviewKind.Warp));
        Assert.True(Enum.IsDefined(TargetPreviewKind.Probe));
        Assert.True(Enum.IsDefined(TargetPreviewKind.Treasure));
        Assert.True(Enum.IsDefined(TargetPreviewKind.Mine));
    }

    // ── Row 428 — live dev-tech split ─────────────────────────────────────

    [Fact]
    public void Development_treasure_is_not_useful_once_obsolete()
    {
        // radarimage.cpp:452-463 — pd->IsObsolete(GetDevelopmentTechs()) →
        // c_iNotUseful (the contact loses its icon in every LOD but All).
        Assert.Equal(RadarTreasureUse.NotUseful, RadarTreasure.Usefulness(
            TreasureCode.Development, developmentObsolete: true));
        Assert.Equal(RadarTreasureUse.UsefulPart, RadarTreasure.Usefulness(
            TreasureCode.Development, developmentObsolete: false));
        // Default stays non-obsolete (unknown mask = still useful, native reads
        // the live side mask which the client always has after CREATE_BUCKETS).
        Assert.Equal(RadarTreasureUse.UsefulPart, RadarTreasure.Usefulness(TreasureCode.Development));
    }

    [Fact]
    public void Part_treasure_effect_tech_gate_is_still_the_live_input()
    {
        // radarimage.cpp:506-511 — effect techs NOT yet researched → the
        // icontechbmp override (c_iUsefulTech); researched → launcher-mountable
        // or not-useful.
        Assert.Equal(RadarTreasureUse.UsefulTech, RadarTreasure.Usefulness(
            TreasureCode.Part, EquipmentType.Shield, partEffectsSatisfied: false));
        Assert.Equal(RadarTreasureUse.NotUseful, RadarTreasure.Usefulness(
            TreasureCode.Part, EquipmentType.Shield, partEffectsSatisfied: true, launcherMountable: false));
    }

    [Fact]
    public void Flag_treasure_is_a_useful_part()
    {
        // radarimage.cpp:443-450 — c_tcFlag returns c_iUsefulPart; the FLAG
        // highlight (c_maskHighlight) rides the contact, not the usefulness.
        Assert.Equal(RadarTreasureUse.UsefulPart, RadarTreasure.Usefulness(TreasureCode.Flag));
    }

    // ── Row 437 — the highlight mask has no bracket surface ──────────────

    [Fact]
    public void Highlight_only_contact_draws_no_overlay_bracket()
    {
        // Native DrawBlip's bracket pass handles enemy/me/subject/flag/artifact/
        // rip/targeted/last-fired only (radarimage.cpp:336-357); c_maskHighlight
        // has no surface. A contact carrying ONLY the highlight (a flag treasure
        // nobody targets, radarimage.cpp:637-640) draws no bracket.
        var highlightOnly = new RadarBracketRenderer.RadarContact(
            RadarBracketRenderer.RadarContactType.Treasure, new System.Numerics.Vector2(100, 100), true, 1f, null, false, false,
            Highlight: true);
        Assert.False(RadarBracketRenderer.HasOverlayBracket(in highlightOnly));
        // A carried-flag ship DOES draw one (c_maskFlag, radarimage.cpp:343-345).
        var flagCarrier = highlightOnly with { Highlight = false, CarriedFlag = true };
        Assert.True(RadarBracketRenderer.HasOverlayBracket(in flagCarrier));
    }

    // ── Row 418 — the 3-D selection box is an opt-in superset ────────────

    [Fact]
    public void Selection_bracket_defaults_off_in_classic_and_on_in_modern()
    {
        // Native has no 3-D-view selection box (radarimage.cpp:307-319,352 draw the
        // targetedbmp bracket on the RADAR contact only): Classic defaults to native,
        // Modern keeps the port's readable box; an explicit choice wins in either style.
        Assert.False(new HudSettings { Style = HudStyle.Classic }.SelectionBracketOn);
        Assert.True(new HudSettings { Style = HudStyle.Modern }.SelectionBracketOn);
        var settings = new HudSettings { Style = HudStyle.Modern, SelectionBracket = false };
        settings.Normalize();
        Assert.False(settings.SelectionBracketOn);
        settings = new HudSettings { Style = HudStyle.Classic, SelectionBracket = true };
        settings.Normalize();
        Assert.True(settings.SelectionBracketOn);
    }

    // ── Row 456 — tip side-colour fallback is native white ───────────────

    [Fact]
    public void Tip_side_colour_fallback_is_white()
    {
        // dialog.mdl:26 mySideColor = GetSideColor(Me) whose ModelData fallback
        // is Color(1,1,1) (consoledata.cpp:99-107) — never the teal accent.
        Assert.Equal(1f, HudConsoleFormat.FallbackSideR);
        Assert.Equal(1f, HudConsoleFormat.FallbackSideG);
        Assert.Equal(1f, HudConsoleFormat.FallbackSideB);
    }

    // ── Row 470 — independent centre/target/chat visibility ──────────────

    [Fact]
    public void Center_and_target_hud_settings_default_on_like_native()
    {
        var settings = new HudSettings();
        Assert.True(settings.ShowCenterHud);   // WinTrek.cpp:2898, ModifiableBoolean(true)
        Assert.True(settings.ShowTargetHud);   // WinTrek.cpp:2899
        settings.ShowCenterHud = false;
        settings.ShowTargetHud = false;
        settings.Normalize();
        Assert.False(settings.ShowCenterHud);  // persisted preferences
        Assert.False(settings.ShowTargetHud);
    }

    [Fact]
    public void Target_pane_gates_on_the_native_target_hud_switch()
    {
        var pane = new HudTargetPane(true, "bandit", "Scout", null,
            new HudColor(1, 0, 0, 1), 0.5f, 0.5f, 900f, 40f, null, null, true, true);
        var on = HudModelAdapter.Create(null, CameraMode.Cockpit, targetPane: pane);
        Assert.True(on.TargetPaneVisible);
        var off = HudModelAdapter.Create(null, CameraMode.Cockpit, targetPane: pane, targetHudVisible: false);
        Assert.False(off.TargetPaneVisible);
    }

    [Fact]
    public void Centre_hud_gates_on_the_native_center_hud_switch()
    {
        var on = HudModelAdapter.Create(null, CameraMode.Cockpit);
        Assert.True(on.CentreHudVisible);
        var off = HudModelAdapter.Create(null, CameraMode.Cockpit, centreHudVisible: false);
        Assert.False(off.CentreHudVisible);
    }

    [Fact]
    public void Chat_history_gate_hides_history_but_defaults_on()
    {
        var on = HudModelAdapter.Create(null, CameraMode.Cockpit);
        Assert.True(on.ChatHistoryVisible);
        var off = HudModelAdapter.Create(null, CameraMode.Cockpit, chatHistoryVisible: false);
        Assert.False(off.ChatHistoryVisible);
    }

    // ── Row 471 — the target pane rides the native slide ─────────────────

    [Fact]
    public void Target_pane_slide_tracks_the_cockpit_chase_mask()
    {
        // dialog.mdl:5404 — TargetDisplayImage mask ofInCockpit | ofInChase,
        // .5 s like every other pane slide.
        Assert.Equal(ConsoleOverlayMask.InCockpit | ConsoleOverlayMask.InChase,
            PaneSlideTracker.Bit(SlidingPane.TargetPane));

        var tracker = new PaneSlideTracker();
        Assert.Equal(0f, tracker.Progress(SlidingPane.TargetPane, 10.0));
        tracker.Update(10.0, ConsoleOverlayMask.InCockpit);
        Assert.Equal(0f, tracker.Progress(SlidingPane.TargetPane, 10.0));   // slide just started
        float mid = tracker.Progress(SlidingPane.TargetPane, 10.25);
        Assert.InRange(mid, 0.01f, 0.99f);
        Assert.Equal(1f, tracker.Progress(SlidingPane.TargetPane, 10.5));   // 0.5 s travel
        Assert.Equal(1f, tracker.Progress(SlidingPane.TargetPane, 11.0));
    }

    [Fact]
    public void Target_pane_slide_offset_matches_the_native_travel()
    {
        // SideBottom(-320,-183) → (-320,25): 208 native px of travel
        // (dialog.mdl:5404); 0 progress = fully off-screen below, 1 = anchored.
        Assert.Equal(208f, TargetPaneLayout.SlideOffset(0f, 1f), 3f);
        Assert.Equal(0f, TargetPaneLayout.SlideOffset(1f, 1f), 3f);
        Assert.Equal(104f, TargetPaneLayout.SlideOffset(0.5f, 1f), 3f);
        Assert.Equal(416f, TargetPaneLayout.SlideOffset(0f, 2f), 3f);
    }

    // ── Row 492 — the command summary's native target group ──────────────

    [Fact]
    public void Command_summary_target_range_has_no_space_before_the_unit()
    {
        // cortTargetRangeString = NumberString(GetRange) + "m" — NO space
        // (dialog.mdl:4615-4628).
        Assert.Equal("1200m", HudStringCache.TargetRange(1199.6f));
        Assert.Equal("0m", HudStringCache.TargetRange(0f));
        Assert.Same(HudStringCache.TargetRange(1200f), HudStringCache.TargetRange(1200f));
    }

    [Fact]
    public void Command_summary_target_speed_prints_native_mps()
    {
        // cortTargetSpeed = NumberString(GetSpeed) + " mps" (dialog.mdl:4630-4656).
        Assert.Equal("179 mps", HudStringCache.TargetSpeed(179.2f));
        Assert.Same(HudStringCache.TargetSpeed(179.2f), HudStringCache.TargetSpeed(179.2f));
    }
}
