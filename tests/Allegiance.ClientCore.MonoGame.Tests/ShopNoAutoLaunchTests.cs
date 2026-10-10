using System;
using System.Linq;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P519 (Bug 1) — the proof-of-concept 30 s auto-launch is REMOVED: a docked
/// player launches only on their own action (L / pad Y / touch), matching
/// native (a docked player stays docked until they launch; the server's own
/// auto-launch is NPC-only). Two pure checks: the timer symbols are gone from
/// Game1, and the shop model is inert without an explicit user action.
/// </summary>
public class ShopNoAutoLaunchTests
{
    /// <summary>⭐ P519 — the POC auto-launch timer (<c>DockFallbackMs</c> and the
    /// <c>_dockSinceMs</c> stamp) no longer exist on <see cref="Game1"/>, so the
    /// docked screen cannot spontaneously undock a player. Failing (red) before
    /// the removal.</summary>
    [Fact]
    public void Game1_has_no_dock_fallback_auto_launch_symbols()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        Assert.Null(typeof(Game1).GetField("DockFallbackMs", flags));
        Assert.Null(typeof(Game1).GetField("_dockSinceMs", flags));
    }

    /// <summary>⭐ P519 — the shop model is inert without user action: reading /
    /// laying out the model many times (simulating idle docked frames) never
    /// alters the pending loadout or the effective hull. There is no path for the
    /// model itself to initiate a launch.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Shop_model_is_inert_without_user_action()
    {
        Require.Artwork();
        var cat = ShopCatalog.TryLoad(
            ExternalPaths.Resolve(ExternalResourceKind.Artwork))
            ?? throw new InvalidOperationException("core not loadable");
        var hull = cat.Hulls.OrderBy(h => h.HullId).First();
        var m = new ShopModel(cat, hull.HullId, Array.Empty<ExpandedPartData>());

        short effBefore = m.EffectiveHullId;
        int partsBefore = m.ChosenParts.Count;

        // Hundreds of idle "frames": lay out and read every page repeatedly.
        for (int i = 0; i < 500; i++)
        {
            m.SwitchTab(ShopTab.Hulls);
            _ = ShopLayout.Build(m);
            m.SwitchTab(ShopTab.Parts);
            _ = ShopLayout.Build(m);
            if (m.Slots.Count > 0)
            {
                m.MoveCursor(0);
                _ = ShopLayout.Build(m);
            }
        }

        Assert.Equal(effBefore, m.EffectiveHullId);   // no spontaneous hull change
        Assert.Equal(partsBefore, m.ChosenParts.Count); // no spontaneous loadout change
    }
}
