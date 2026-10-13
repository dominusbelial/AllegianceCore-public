using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P625 — failing-first tests for the rebindable key map. They pin:
///   • the defaultinputmap.mdl parser reproduces native's table (20 commands);
///   • user overrides persist and reload (JSON round-trip);
///   • conflict detection + swap;
///   • reset-to-defaults;
///   • the help overlay reflects a changed binding (live);
///   • the F-key fix: F5/F11 are cameras only, screenshot -> PrintScreen, music
///     volume off the F-row (Ctrl + = / -).
/// These are unit tests against freshly-built ActionMap instances (never the
/// process-wide singleton) so no persisted keymap.json can skew them.
/// </summary>
public class P625KeyMapTests
{
    // ── defaultinputmap.mdl parser vs the native table ────────────────────

    [Fact]
    public void Parser_ParsesAllKeyCommandEntries()
    {
        var map = DefaultInputMap.ParseKeyMap(DefaultInputMap.EmbeddedText);
        Assert.Equal(103, map.Count);
    }

    [Theory]
    // (TK number, native VK, modifier bits from defaultinputmap.mdl).
    // Sampled 20 across every native category (decoded in the P625 report).
    [InlineData(21, 81, 0)]   // TK_RollLeft   -> Q, Any
    [InlineData(22, 69, 0)]   // TK_RollRight  -> E, Any
    [InlineData(25, 27, 0)]   // TK_MainMenu   -> Esc, Any
    [InlineData(40, 219, 0)]  // TK_AcceptCommand -> [, none
    [InlineData(44, 65, 0)]   // TK_ThrustLeft -> A, Any
    [InlineData(45, 68, 0)]   // TK_ThrustRight-> D, none
    [InlineData(48, 9, 0)]    // TK_ThrustForward -> Tab, Any
    [InlineData(49, 83, 0)]   // TK_ThrustBackward -> S, Any
    [InlineData(50, 32, 0)]   // TK_FireBooster -> Space, Any
    [InlineData(58, 187, 0)]  // TK_ThrottleUp -> =, Any
    [InlineData(60, 8, 0)]    // TK_ThrottleZero -> Backspace, Any
    [InlineData(63, 220, 0)]  // TK_ThrottleFull -> \, Any
    [InlineData(67, 113, 0)]  // TK_ConModeCombat -> F2, Any
    [InlineData(68, 114, 0)]  // TK_ConModeCommand -> F3, none
    [InlineData(77, 84, 0)]   // TK_TargetFriendly -> T, none
    [InlineData(80, 70, 0)]   // TK_TargetEnemy -> F, none
    [InlineData(92, 68, 3)]   // TK_Suicide -> D, Shift+Control
    [InlineData(94, 71, 0)]   // TK_ToggleCloak -> G, Any
    [InlineData(96, 82, 2)]   // TK_Ripcord -> R, Control
    [InlineData(128, 90, 0)]  // TK_DropChaff -> Z, Any
    [InlineData(204, 112, 0)] // TK_Help -> F1, Any
    [InlineData(218, 44, 0)]  // TK_ScrnShot -> PrintScreen, Any
    [InlineData(211, 86, 0)]  // TK_ToggleMouse -> V, Any
    public void Parser_MatchesNativeDefaultTable(int tk, int vk, int mods)
    {
        var map = DefaultInputMap.ParseKeyMap(DefaultInputMap.EmbeddedText);
        var nb = map.First(b => b.TkNumber == tk);   // a command may have several chords
        Assert.Equal(vk, nb.Vk);
        // Native "Any"(8) collapses to an unmodified chord in the port model.
        Assert.Equal(mods, (int)nb.Modifiers);
    }

    [Fact]
    public void Parser_ScrShotIsPrintScreen_NotF11()
    {
        // Native binds screenshot to the PrintScreen key (defaultinputmap.mdl
        // line 108) — which is exactly where Domingo told us to move it.
        var map = DefaultInputMap.ParseKeyMap(DefaultInputMap.EmbeddedText);
        var shot = map.Single(b => b.TkNumber == 218);
        Assert.Equal(Keys.PrintScreen, DefaultInputMap.VkToKey(shot.Vk));
    }

    // ── user overrides persist & reload ────────────────────────────────────

    [Fact]
    public void Overrides_PersistThroughJsonRoundTrip()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        map.SetBinding("FireWeapon", new KeyBinding(Keys.F15, InputModifiers.None));

        var json = map.ToJson();
        var reloaded = new ActionMap(InputActionCatalog.Definitions);
        reloaded.ApplyJson(json);

        Assert.Equal(new KeyBinding(Keys.F15, InputModifiers.None), reloaded.BindingOf("FireWeapon"));
        // A default untouched by the override still holds its catalogue default.
        Assert.Equal(new KeyBinding(Keys.PrintScreen, InputModifiers.None), reloaded.BindingOf("ScreenShot"));
    }

    [Fact]
    public void Overrides_OnlyDifferencesAreSerialized()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        map.SetBinding("FireWeapon", new KeyBinding(Keys.F15, InputModifiers.None));
        var json = map.ToJson();
        Assert.Contains("\"FireWeapon\"", json);
        Assert.DoesNotContain("\"ScreenShot\"", json); // default, not emitted
    }

    [Fact]
    public void Overrides_IgnoreUnknownActionsAndBadCodes()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        map.ApplyJson("{\"NoSuchAction\":[65,0],\"FireWeapon\":[99999,0],\"ThrottleUp\":[38,0]}");
        // Unknown action ignored; out-of-range key code ignored.
        Assert.Equal(new KeyBinding(Keys.Space, InputModifiers.None), map.BindingOf("FireWeapon"));
        // Valid override applied.
        Assert.Equal(new KeyBinding(Keys.Up, InputModifiers.None), map.BindingOf("ThrottleUp"));
    }

    // ── conflict detection ────────────────────────────────────────────────

    [Fact]
    public void Conflict_DetectsTakenKey_AndRequiresForce()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        // FireWeapon is Space; try to give ScreenShot the same Space key.
        var conflict = map.SetBinding("ScreenShot", new KeyBinding(Keys.Space, InputModifiers.None));
        Assert.Equal("FireWeapon", conflict);
        Assert.NotEqual(new KeyBinding(Keys.Space, InputModifiers.None), map.BindingOf("ScreenShot"));

        map.SetBinding("ScreenShot", new KeyBinding(Keys.Space, InputModifiers.None), force: true);
        Assert.Equal(new KeyBinding(Keys.Space, InputModifiers.None), map.BindingOf("ScreenShot"));
    }

    [Fact]
    public void Controller_CaptureFlow_RebindsWithoutConflict()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var ctl = new KeyBindingController(map);

        // Move to FireWeapon and rebind it to F15 (free — no catalogue default
        // owns F15, unlike the old X which is now LeaderCommand's key).
        while (ctl.CurrentAction != "FireWeapon") ctl.MoveDown();
        ctl.SelectCurrent();
        Assert.Equal(KeyBindPhase.Capture, ctl.Phase);
        ctl.CaptureKey(Keys.F15, false, false, false);
        Assert.Equal(KeyBindPhase.Browse, ctl.Phase);
        Assert.Equal(new KeyBinding(Keys.F15, InputModifiers.None), map.BindingOf("FireWeapon"));
    }

    [Fact]
    public void Controller_ConflictFlow_DetectsAndAllowsSwap()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var ctl = new KeyBindingController(map);
        while (ctl.CurrentAction != "ScreenShot") ctl.MoveDown();
        ctl.SelectCurrent();
        // Give ScreenShot the key that Suicide alone owns (K) -> conflict.
        // (K is used rather than the multi-context Space so the owning action is
        // deterministic — the P625b catalogue legitimately shares Space across
        // contexts, but K is Suicide-only.)
        ctl.CaptureKey(Keys.K, false, false, false);
        Assert.Equal(KeyBindPhase.Conflict, ctl.Phase);
        Assert.Equal("Suicide", ctl.ConflictOwner);

        ctl.Answer(true); // swap
        Assert.Equal(KeyBindPhase.Browse, ctl.Phase);
        Assert.Equal(new KeyBinding(Keys.K, InputModifiers.None), map.BindingOf("ScreenShot"));
        Assert.Equal(new KeyBinding(Keys.PrintScreen, InputModifiers.None), map.BindingOf("Suicide"));
    }

    [Fact]
    public void Controller_CancelConflict_LeavesBindingsUnchanged()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var ctl = new KeyBindingController(map);
        while (ctl.CurrentAction != "ScreenShot") ctl.MoveDown();
        ctl.SelectCurrent();
        ctl.CaptureKey(Keys.K, false, false, false);
        Assert.Equal(KeyBindPhase.Conflict, ctl.Phase);
        ctl.Answer(false); // reject the swap
        Assert.Equal(KeyBindPhase.Browse, ctl.Phase);
        Assert.Equal(new KeyBinding(Keys.PrintScreen, InputModifiers.None), map.BindingOf("ScreenShot"));
        Assert.Equal(new KeyBinding(Keys.K, InputModifiers.None), map.BindingOf("Suicide"));
    }

    [Fact]
    public void Controller_ResetConfirm_RestoresDefaults()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var ctl = new KeyBindingController(map);
        map.SetBinding("FireWeapon", new KeyBinding(Keys.X, InputModifiers.None));
        ctl.RequestReset();
        Assert.Equal(KeyBindPhase.ConfirmReset, ctl.Phase);
        ctl.Answer(true);
        Assert.Equal(new KeyBinding(Keys.Space, InputModifiers.None), map.BindingOf("FireWeapon"));
    }

    [Fact]
    public void Controller_Capture_IgnoresBareModifierKey()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var ctl = new KeyBindingController(map);
        while (ctl.CurrentAction != "FireWeapon") ctl.MoveDown();
        ctl.SelectCurrent();
        ctl.CaptureKey(Keys.LeftShift, false, false, false); // ignored
        Assert.Equal(KeyBindPhase.Capture, ctl.Phase);
        Assert.Equal(new KeyBinding(Keys.Space, InputModifiers.None), map.BindingOf("FireWeapon"));
    }

    [Fact]
    public void Reset_RestoresCatalogueDefaults()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        map.SetBinding("FireWeapon", new KeyBinding(Keys.F15, InputModifiers.None), force: true);
        map.SetBinding("ScreenShot", new KeyBinding(Keys.Space, InputModifiers.None), force: true);
        map.ResetToDefaults();

        Assert.Equal(new KeyBinding(Keys.Space, InputModifiers.None), map.BindingOf("FireWeapon"));
        Assert.Equal(new KeyBinding(Keys.PrintScreen, InputModifiers.None), map.BindingOf("ScreenShot"));
    }

    // ── help overlay reflects a changed binding ───────────────────────────

    [Fact]
    public void Help_ReflectsReboundBinding()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var groups = map.ToHelpGroups();
        // Default: screenshot shown on PrintScreen.
        Assert.Contains(groups.SelectMany(g => g.Entries),
            e => e.Key == "PrintScreen" && e.Description.Contains("screen shot", System.StringComparison.OrdinalIgnoreCase));

        map.SetBinding("ScreenShot", new KeyBinding(Keys.F15, InputModifiers.None));
        var groupsAfter = map.ToHelpGroups();
        Assert.Contains(groupsAfter.SelectMany(g => g.Entries),
            e => e.Key == "F15" && e.Description.Contains("screen shot", System.StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(groupsAfter.SelectMany(g => g.Entries), e => e.Key == "PrintScreen");
    }

    [Fact]
    public void Help_GroupsMatchNativeCommandCategories()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var titles = map.ToHelpGroups().Select(g => g.Title).ToList();
        // The established help groups (native command categories plus the
        // P625b additions for panes/menus, free-cam/spectator and system).
        Assert.Equal(14, titles.Count);
        Assert.Contains("MOVEMENT", titles);
        Assert.Contains("WEAPONS", titles);
        Assert.Contains("CAMERA / VIEW", titles);
        Assert.Contains("MUSIC", titles);
        Assert.Contains("PANES / MENUS", titles);
        Assert.Contains("FREE-CAM / SPECTATOR", titles);
        Assert.Contains("SYSTEM", titles);
    }

    // ── F-key fix: cameras own F5/F11; screenshot/music moved ─────────────

    [Fact]
    public void FKeyFix_CamerasOwnF4F5F7F10F11()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        Assert.Equal(new KeyBinding(Keys.F4, InputModifiers.None), map.BindingOf("ViewExternalOrbit"));
        Assert.Equal(new KeyBinding(Keys.F5, InputModifiers.None), map.BindingOf("ViewFlyby"));
        Assert.Equal(new KeyBinding(Keys.F7, InputModifiers.None), map.BindingOf("ViewTarget"));
        Assert.Equal(new KeyBinding(Keys.F10, InputModifiers.None), map.BindingOf("ViewBase"));
        Assert.Equal(new KeyBinding(Keys.F11, InputModifiers.None), map.BindingOf("ViewExternalStation"));
    }

    [Fact]
    public void FKeyFix_ScreenshotIsPrintScreen_NotF11()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        Assert.Equal(new KeyBinding(Keys.PrintScreen, InputModifiers.None), map.BindingOf("ScreenShot"));
    }

    [Fact]
    public void FKeyFix_MusicVolumeOffTheFRow_OnFreeCtrlChord()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        Assert.Equal(new KeyBinding(Keys.OemPlus, InputModifiers.Control), map.BindingOf("MusicVolumeUp"));
        Assert.Equal(new KeyBinding(Keys.OemMinus, InputModifiers.Control), map.BindingOf("MusicVolumeDown"));
        // No music-volume binding may sit on the F-row that native reserves for
        // the cameras / console modes.
        foreach (var f in new[] { Keys.F4, Keys.F5, Keys.F6, Keys.F7, Keys.F10, Keys.F11 })
        {
            Assert.NotEqual(f, map.BindingOf("MusicVolumeUp").Key);
            Assert.NotEqual(f, map.BindingOf("MusicVolumeDown").Key);
        }
        // And no music-volume binding may occupy F5/F11 at all (cameras win).
        var all = map.Definitions
            .Where(d => d.Category == "MUSIC");
        Assert.DoesNotContain(all, d => d.Default.Key is Keys.F5 or Keys.F11);
    }

    [Fact]
    public void FKeyFix_NoBindingSharesF5OrF11ExceptTheirCamera()
    {
        // Across the WHOLE catalogue, F5 and F11 may only be the fly-by and
        // station cameras respectively — the "native wins the F-keys" invariant.
        foreach (var d in InputActionCatalog.Definitions)
        {
            if (d.Default.Key == Keys.F5)
                Assert.Equal("ViewFlyby", d.Name);
            if (d.Default.Key == Keys.F11)
                Assert.Equal("ViewExternalStation", d.Name);
        }
    }
}
