namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>The control a Jet <c>CMD</c> panel point belongs to.</summary>
public enum JetCommandControlKind : byte
{
    None = 0,
    /// <summary>A recipient column (0 = Me, 1 = Sector, 2 = Team, 3 = All).</summary>
    Recipient,
    /// <summary>The wing combo (native <c>WingCombo</c>).</summary>
    Wing,
    /// <summary>A verb column (0..8, native <c>c_cidAttack</c> + column).</summary>
    Verb,
}

/// <summary>One resolved control: its kind and its column/index.</summary>
public readonly record struct JetCommandControl(JetCommandControlKind Kind, int Index)
{
    public static readonly JetCommandControl None = new(JetCommandControlKind.None, -1);
    public bool IsNone => Kind == JetCommandControlKind.None;
}

/// <summary>
/// P1040 — the Jet <c>CMD</c> console geometry (P972 §5.5 row 8, phase 3D).
/// <para><b>One source for draw and hit test</b> (P972 §6.1): every rectangle here is
/// derived from <see cref="CommandBarLayout"/>, the same immutable layout
/// <c>Game1.Commanding.TryCommandBarPick</c> hit-tests against, and
/// <see cref="HitTest"/> delegates to the same <c>RecipientColumn</c>/<c>VerbColumn</c>/
/// <c>InWingCombo</c> functions in the same precedence order the production picker uses
/// (wing, then verb, then recipient — <c>Game1.Commanding.cs:399-437</c>). The renderer
/// draws exactly these cells, so a click on a drawn control can only resolve to that
/// control; the P1040 test clicks the centre of every drawn cell and compares the Jet
/// control with the Classic/Modern decision and the action ids.
/// </para>
/// <para>Nothing here changes the fan-out: the panel is presentation only and reports
/// the same column/verb the existing handler already consumes.</para>
/// </summary>
public static class JetCommandConsole
{
    /// <summary>Recipient cells (native <c>RecipientsBB</c> columns 53/106/159/212).</summary>
    public const int RecipientCount = 4;

    /// <summary>Verb cells (native <c>VerbsBB</c>, nine columns).</summary>
    public const int VerbCount = 9;

    /// <summary>The recipient words (native columns 0..3 map to Me/Sector/Team/All,
    /// <c>Game1.Commanding.cs:426-429</c>, <c>ConsoleOverlayMask</c> chat targets).</summary>
    private static readonly string[] RecipientWords = ["ME", "SECTOR", "TEAM", "ALL"];

    /// <summary>The verb words, by CommandID-1 (native <c>c_cdAllCommands</c> order,
    /// <see cref="HudConsoleFormat.Verb"/>); abbreviated to keep the 30 px cell legible
    /// (the authoritative word is the shared one).</summary>
    private static readonly string[] VerbWords = ["ATK", "CAP", "DEF", "PIK", "GO", "REP", "JOIN", "MINE", "BLD"];

    public static string RecipientWord(int column)
        => column >= 0 && column < RecipientWords.Length ? RecipientWords[column] : "";

    public static string VerbWord(int column)
        => column >= 0 && column < VerbWords.Length ? VerbWords[column] : "";

    /// <summary>The native panel origin at a slide progress (0 off, 1 on), in screen px.</summary>
    public static float PanelLeft(float viewportWidth, float progress)
        => CommandBarLayout.Left((int)MathF.Round(viewportWidth), progress);

    public static float PanelTop(float viewportHeight, float progress)
        => CommandBarLayout.Top((int)MathF.Round(viewportHeight), progress);

    /// <summary>The whole panel (native <c>CommandPane</c> 274×92).</summary>
    public static RectangleF Panel(float left, float top)
        => new(left, top, CommandBarLayout.PanelWidth, CommandBarLayout.PanelHeight);

    /// <summary>The recipient strip's DRAWN band: the native button bar's own row of
    /// buttons occupies the top 30 px of <c>RecipientsBB</c> (the rest of that 84 px
    /// canvas is covered by the verb bar, which is hit-tested first), so the drawn band
    /// is entirely outside the verb hit area and a click anywhere in it resolves to a
    /// recipient.</summary>
    public const float RecipientBandHeight = 30f;

    /// <summary>One recipient cell in panel-local px: the hit column's own x span.</summary>
    public static RectangleF RecipientCell(int column)
    {
        if (column < 0 || column >= RecipientCount) return default;
        float left = column == 0 ? CommandBarLayout.RecipientsLeft
            : CommandBarLayout.RecipientsLeft + CommandBarLayout.RecipientColumns[column - 1];
        float right = CommandBarLayout.RecipientsLeft + CommandBarLayout.RecipientColumns[column];
        return new(left, CommandBarLayout.RecipientsTop, right - left, RecipientBandHeight);
    }

    /// <summary>The wing combo cell in panel-local px.</summary>
    public static RectangleF WingCell => new(CommandBarLayout.WingLeft, CommandBarLayout.WingTop,
        CommandBarLayout.WingWidth, CommandBarLayout.WingHeight);

    /// <summary>One verb cell in panel-local px: the hit column's own x span, the full
    /// verb bar height (the verb bar owns that band).</summary>
    public static RectangleF VerbCell(int column)
    {
        if (column < 0 || column >= VerbCount) return default;
        float left = column == 0 ? CommandBarLayout.VerbsLeft
            : CommandBarLayout.VerbsLeft + CommandBarLayout.VerbColumns[column - 1];
        float right = CommandBarLayout.VerbsLeft + CommandBarLayout.VerbColumns[column];
        return new(left, CommandBarLayout.VerbsTop, right - left, CommandBarLayout.VerbsHeight);
    }

    /// <summary>
    /// The control at a panel-local point — the SAME precedence as
    /// <c>Game1.Commanding.TryCommandBarPick</c> (wing, verb, recipient).
    /// </summary>
    public static JetCommandControl HitTest(float panelX, float panelY)
    {
        int x = (int)MathF.Floor(panelX), y = (int)MathF.Floor(panelY);
        if (CommandBarLayout.InWingCombo(x, y))
            return new(JetCommandControlKind.Wing, 0);
        int verb = CommandBarLayout.VerbColumn(x, y);
        if (verb >= 0)
            return new(JetCommandControlKind.Verb, verb);
        int recipient = CommandBarLayout.RecipientColumn(x, y);
        if (recipient >= 0)
            return new(JetCommandControlKind.Recipient, recipient);
        return JetCommandControl.None;
    }

    /// <summary>The action the production picker performs for a resolved control: the
    /// wing advances to the next name, a verb arms that native CommandID, a recipient
    /// selects that column. Pure, so the test can compare Jet's resolution with the
    /// Classic/Modern one without a graphics device.</summary>
    public static string ActionWord(in JetCommandControl control, int currentWing)
        => control.Kind switch
        {
            JetCommandControlKind.Wing => "wing " + CommandBarLayout.WingNames[
                (currentWing + 1) % CommandBarLayout.WingNames.Length],
            JetCommandControlKind.Verb => "verb " + HudConsoleFormat.Verb(CommandBarLayout.VerbCommandId(control.Index)),
            JetCommandControlKind.Recipient => "recipient " + CommandBarLayout.RecipientChatTarget(control.Index),
            _ => "none",
        };
}
