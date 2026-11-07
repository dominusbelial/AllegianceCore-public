using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

// Native hover values: WinTrek/hangarscreen.cpp:36-48,243-300.
public enum HangarAction { None, Launch, Buy, Command, Loadout, Default, Invest, Help, Teleport, Team, Exit }

/// <summary>Shared drawing/hit-test geometry for native hangar verbs.
///
/// <para>P945 — the geometry is now the native <c>hangar.mdl</c> screen (800×600
/// panes scaled uniformly and centred, see <see cref="HangarScreen"/>), not the
/// pre-P945 bottom row of text cells. Every rectangle here is the one the
/// renderer draws, so hit-testing and drawing cannot disagree.</para></summary>
public static class HangarControls
{
    public static IEnumerable<HangarAction> Actions(bool developments) =>
        Enum.GetValues<HangarAction>().Where(a => a != HangarAction.None && (developments || a != HangarAction.Invest));

    /// <summary>The viewport rectangle of a hangar verb (native pane for the
    /// native verbs, the letterbox cell for the port-only ones).</summary>
    public static Rectangle Bounds(int width, int height, HangarAction action) =>
        HangarScreen.Rect(width, height, action);

    public static HangarAction Hit(int width, int height, int x, int y, bool developments) =>
        HangarScreen.Hit(width, height, x, y, developments);

    /// <summary>The hover text for a verb — the native strings
    /// (<c>hangar.mdl:26-72</c>) with the port's wording only for the two verbs
    /// native's hangar has no button for.</summary>
    public static string Feedback(HangarAction action) =>
        HangarScreen.HoverText.TryGetValue(action, out var text) ? text : "";
}

/// <summary>Native clintlib.h:1389-1393; appmsg.cpp:1929-1938:
/// launch only after the authoritative parent-clear acknowledgement.</summary>
public sealed class HangarLaunchSequence
{
    public bool AwaitingDisembark { get; private set; }
    public bool Begin(short parentId)
    {
        if (AwaitingDisembark) return false;
        AwaitingDisembark = parentId >= 0;
        return true;
    }
    public bool Acknowledged(short parentId, bool docked)
    {
        if (!AwaitingDisembark || parentId >= 0) return false;
        AwaitingDisembark = false;
        return docked;
    }
    public void Cancel() => AwaitingDisembark = false;
}
