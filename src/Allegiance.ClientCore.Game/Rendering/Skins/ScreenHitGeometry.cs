using Allegiance.ClientCore.MonoGame;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>
/// P1042 (P972 §6.1) — THE geometry source both drawing and hit testing resolve.
/// Each function delegates to the model that already owns the rectangles:
/// <see cref="IntroLayout"/> for the intro menu, <see cref="HangarScreen"/> for the
/// hangar panes (the pointer hit test's own call), and <see cref="LoadoutV2Layout"/>'s
/// leaves for the loadout (its <c>HitTest</c>). The screen renderers draw these very
/// rectangles, so a control can never be painted somewhere it cannot be clicked.
/// </summary>
public static class ScreenHitGeometry
{
    /// <summary>The intro row rectangle the menu draws and the pointer resolves.</summary>
    public static IntroRect IntroRow(in IntroMainFrame frame, int index) => frame.Layout.Rows[index];

    /// <summary>The row index under a point, or -1. Uses the same row rectangles
    /// <see cref="IntroScreenPainter"/> draws (P946's keyboard menu is unchanged; this is
    /// the geometry a hover/press resolves).</summary>
    public static int IntroRowAt(in IntroMainFrame frame, float x, float y)
    {
        var rows = frame.Layout.Rows;
        for (int i = 0; i < rows.Length; i++)
            if (x >= rows[i].X && x < rows[i].Right && y >= rows[i].Y && y < rows[i].Bottom)
                return i;
        return -1;
    }

    /// <summary>The intro command under a point (<see cref="IntroCommand.None"/> when the
    /// point misses every row) — the same mapping <see cref="IntroScreenModel.Activate"/>
    /// applies to the keyboard selection.</summary>
    public static IntroCommand IntroCommandAt(in IntroMainFrame frame, float x, float y)
    {
        int row = IntroRowAt(frame, x, y);
        return row < 0 ? IntroCommand.None : IntroScreenModel.Activate(row);
    }

    /// <summary>Whether a point is inside the pilot-name field's resolved box.</summary>
    public static bool IntroPilotFieldHit(in IntroMainFrame frame, float x, float y)
        => x >= frame.PilotField.X && x < frame.PilotField.Right
        && y >= frame.PilotField.Y && y < frame.PilotField.Bottom;

    /// <summary>A hangar verb's rectangle — <see cref="HangarScreen.Rect"/>, the renderer's
    /// own call.</summary>
    public static Microsoft.Xna.Framework.Rectangle HangarRect(int width, int height, HangarAction action)
        => HangarScreen.Rect(width, height, action);

    /// <summary>The hangar verb under a point — <see cref="HangarScreen.Hit"/>, exactly the
    /// call <c>Game1.UpdateHangarControls</c> makes.</summary>
    public static HangarAction HangarHit(in HangarScreenFrame frame, int x, int y)
        => HangarScreen.Hit(frame.Width, frame.Height, x, y, frame.Developments);

    /// <summary>The loadout leaf under a point — <see cref="LoadoutV2Layout.HitTest"/>, the
    /// call the pointer and touch paths use.</summary>
    public static LoadoutV2Layout.Element? LoadoutV2Hit(in LoadoutScreenFrame frame, float x, float y)
        => frame.V2Layout?.HitTest(x, y);
}
