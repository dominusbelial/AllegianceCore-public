using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

// Native hover values: WinTrek/hangarscreen.cpp:37-48,243-300.
public enum HangarAction { None, Launch, Command, Loadout, Invest, Help, Teleport, Team, Exit }

/// <summary>Shared drawing/hit-test geometry for native hangar verbs.</summary>
public static class HangarControls
{
    public static IEnumerable<HangarAction> Actions(bool developments) =>
        Enum.GetValues<HangarAction>().Where(a => a != HangarAction.None && (developments || a != HangarAction.Invest));

    public static Rectangle Bounds(int width, int height, HangarAction action)
    {
        int index = (int)action - 1;
        int cell = Math.Max(1, (width - 32) / 8);
        return new Rectangle(16 + index * cell, height - 72, cell - 4, 30);
    }

    public static HangarAction Hit(int width, int height, int x, int y, bool developments) =>
        Actions(developments).FirstOrDefault(a => Bounds(width, height, a).Contains(x, y));

    public static string Feedback(HangarAction action) => action switch
    {
        HangarAction.Launch => "Launch your ship",
        HangarAction.Command => "View the station's sector in command view",
        HangarAction.Loadout => "Configure your ship's hull and equipment",
        HangarAction.Invest => "Invest in your team's developments and construction",
        HangarAction.Help => "Open help and controls",
        HangarAction.Teleport => "Teleport to a station or board a teammate",
        HangarAction.Team => "View your team roster",
        HangarAction.Exit => "Leave this mission",
        _ => ""
    };
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
