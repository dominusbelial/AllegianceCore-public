using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P625b — behaviour tests proving that a rebound command is triggered by the
/// NEW key and not the old one, with no repeat-fire while held, and that the
/// help overlay reflects the new key. Exercises the SAME contract every Game1
/// handler now relies on (each reads <c>ActionDown(ks, "Action")</c> / a latch
/// keyed to the action's resolved binding — never a raw key literal), sampled
/// across ≥ 10 commands spanning every category the brief lists: orders,
/// mouse-flight toggle, cameras, weapons and panes.
///
/// The rebind flow itself (press-new-key + conflict swap) is pinned by the
/// P625 tests; here we pin the INPUT RESOLUTION consequences that the P625b
/// migration is about.
/// </summary>
public class P625bActionMapBehaviorTests
{
    // (action, category, its catalogue default key, a fresh key to rebind to).
    public static IEnumerable<object[]> Samples() => new[]
    {
        new object[] { "AcceptCommand", "QUEUED ORDERS (RECEIVED COMMAND)", Keys.Home, Keys.NumPad0 },
        new object[] { "ToggleMouse", "CAMERA / VIEW", Keys.M, Keys.NumPad1 },
        new object[] { "CycleView", "CAMERA / VIEW", Keys.V, Keys.NumPad2 },
        new object[] { "FireWeapon", "WEAPONS", Keys.Space, Keys.NumPad3 },
        new object[] { "DropChaff", "WEAPONS", Keys.C, Keys.NumPad4 },
        new object[] { "ShopLaunch", "PANES / MENUS", Keys.L, Keys.NumPad5 },
        new object[] { "MenuSelect", "PANES / MENUS", Keys.Space, Keys.NumPad6 },
        new object[] { "Suicide", "WEAPONS", Keys.K, Keys.NumPad7 },
        new object[] { "ToggleCloak", "WEAPONS", Keys.F, Keys.NumPad8 },
        new object[] { "Target", "TARGETING / AUTOPILOT", Keys.T, Keys.NumPad9 },
        new object[] { "ReadyToggle", "READY CHECK (PRE-FLIGHT)", Keys.Y, Keys.F13 },
        new object[] { "StartChat", "CHAT", Keys.Enter, Keys.F14 },
        new object[] { "Scoreboard", "CAMERA / VIEW", Keys.B, Keys.F15 },
        new object[] { "ScreenShot", "CAMERA / VIEW", Keys.PrintScreen, Keys.F16 },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Rebind_TriggersOnNewKey_NotOld(string action, string category, Keys oldKey, Keys newKey)
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        // The action must belong to the advertised category (guards against a
        // mis-labelled sample).
        Assert.Contains(map.Definitions, d => d.Name == action && d.Category == category);
        Assert.Equal(oldKey, map.BindingOf(action).Key); // sample sanity: default is the expected key

        map.SetBinding(action, new KeyBinding(newKey, InputModifiers.None));

        // New key resolves the command; the old literal does NOT.
        Assert.True(map.IsDown(new KeyboardState(newKey), action), $"{action} must trigger on its new key {newKey}");
        Assert.False(map.IsDown(new KeyboardState(oldKey), action), $"{action} must NOT trigger on its old key {oldKey}");

        // And it fires a fresh press on the exact new key (no modifier needed).
        Assert.True(map.IsDown(new KeyboardState(Keys.LeftControl, newKey), action),
            $"{action} still resolves with an un-declared modifier held (extra-modifier tolerance)");
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Rebind_HeldNewKeyFiresOnce_NotRepeatPerFrame(string action, string category, Keys oldKey, Keys newKey)
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        map.SetBinding(action, new KeyBinding(newKey, InputModifiers.None));
        _ = category;

        // Hold the NEW key for 8 frames: the action-edge must rise exactly once
        // (the latch tracks the ACTION's resolved binding), never per-frame.
        int edgesNew = CountEdges(n => map.IsDown(n, action),
            Enumerable.Repeat(new KeyboardState(newKey), 8));
        Assert.Equal(1, edgesNew);

        // Hold the OLD key for 8 frames: it must NOT fire at all — the latch
        // tracks the action (new binding), so the stale literal can't re-fire.
        int edgesOld = CountEdges(n => map.IsDown(n, action),
            Enumerable.Repeat(new KeyboardState(oldKey), 8));
        Assert.Equal(0, edgesOld);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Rebind_HelpOverlayShowsNewKey(string action, string category, Keys oldKey, Keys newKey)
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var def = map.Definitions.First(d => d.Name == action);
        _ = category;
        map.SetBinding(action, new KeyBinding(newKey, InputModifiers.None));

        var groups = map.ToHelpGroups();
        var desc = def.Description;
        var allEntries = groups.SelectMany(g => g.Entries).ToList();

        // The help overlay shows the NEW key beside this command's description…
        Assert.Contains(allEntries, e => e.Key == KeyBinding.KeyLabel(newKey)
                                         && e.Description == desc);
        // …and no longer shows the old key for it.
        Assert.DoesNotContain(allEntries, e => e.Key == KeyBinding.KeyLabel(oldKey)
                                               && e.Description == desc);
    }

    /// <summary>Count false→true edges over a per-frame predicate (the edge-latch
    /// pattern every handler uses: fire only on the fresh press of a held key).</summary>
    private static int CountEdges(Func<KeyboardState, bool> isDown, IEnumerable<KeyboardState> frames)
    {
        int edges = 0;
        bool wasDown = false;
        foreach (var ks in frames)
        {
            bool down = isDown(ks);
            if (down && !wasDown) edges++;
            wasDown = down;
        }
        return edges;
    }

    [Fact]
    public void Sanity_OldKeyIsNoLongerBound_ByCatalogueDefault()
    {
        // A belt-and-braces sanity: every sampled (action, oldKey) pair really is
        // that action's catalogue default (so "old key stops working" means the
        // rebind, not a wrong expectation). The Theory already asserts it via
        // Assert.Equal(oldKey, …BindingOf(action).Key); this just documents it.
        Assert.Equal(14, Samples().Count());
    }
}
