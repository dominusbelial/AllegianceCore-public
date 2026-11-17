using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>
/// P1042 (P972 slice 1F, §6.1) — the screen-chrome boundary. One skin supplies the
/// chrome of the intro menu, the docked hangar and the loadout/shop screen; the
/// SCREEN MODELS and every ACTION stay where they are.
///
/// <para><b>Contract.</b> A renderer receives a read-only <see cref="IntroMainFrame"/>,
/// <see cref="HangarScreenFrame"/> or <see cref="LoadoutScreenFrame"/> that already
/// carries the resolved rectangles hit testing uses (a <see cref="IntroLayout"/> result,
/// <see cref="HangarScreen"/> rectangles, a <see cref="LoadoutV2Layout"/>). It draws
/// inside the caller's live <see cref="SpriteBatch"/> session only where the family
/// documents it, and it can never buy, launch, sort, join, navigate or change focus:
/// no model, cursor, scroll or command is reachable from these frames (every member is
/// a value or a read-only reference). Classic/Modern are pure adapters over the drawing
/// that existed before this interface; Jet layers vector chrome over the same
/// rectangles (P972 §6.1: one geometry source, no duplicated "visual" and "click"
/// rectangle).</para>
/// </summary>
public interface IScreenSkinRenderer : IDisposable
{
    /// <summary>The canonical skin id (<see cref="HudSkinIds"/>).</summary>
    string Id { get; }

    /// <summary>Whether the unchanged native hangar artwork can be drawn for this
    /// skin. False keeps <c>Game1</c>'s pre-P945 text-row fallback.</summary>
    bool HangarArtAvailable { get; }

    /// <summary>Draw the intro screen's main menu (the 13 native commands). The
    /// caller already opened its SpriteBatch session.</summary>
    void DrawIntro(in IntroMainFrame frame);

    /// <summary>Draw the docked hangar screen. A skin that keeps the native screen
    /// draws the artwork itself; a vector skin draws the artwork unchanged and layers
    /// its chrome over the same <see cref="HangarScreen"/> rectangles.</summary>
    void DrawHangar(in HangarScreenFrame frame);

    /// <summary>Draw the loadout/shop screen: the V2 composition when
    /// <see cref="LoadoutScreenFrame.V2Layout"/> is present, else the native-layout
    /// docked shop.</summary>
    void DrawLoadout(in LoadoutScreenFrame frame);
}

/// <summary>
/// P1042 — optional audit channel for screen chrome. A proof surface implements it to
/// receive the exact rectangle of every ACTION the screen chrome drew, so the
/// <c>hit-vs-draw</c> audit can compare it with the rectangle hit testing uses. The
/// production surfaces do not implement it, so the cost is one type test per control.
/// </summary>
public interface IScreenRectAudit
{
    /// <summary>One drawn control: <paramref name="family"/> is the screen family,
    /// <paramref name="id"/> the semantic action (the intro command name, the hangar
    /// verb, or the loadout element kind/index).</summary>
    void Control(string family, string id, RectangleF drawn);

    /// <summary>One plate/frame the chrome drew around a control (its own box, so the
    /// proof can check a label plate really backs the text it carries).</summary>
    void Plate(string family, string id, RectangleF box);
}

/// <summary>P1042 — the semantic action ids of the intro main menu, in
/// <see cref="IntroScreenModel.MainLabels"/> order. Pure data so a proof can map a
/// drawn row to the hit row without touching the model. The element ids are
/// precomputed constants: a screen frame never formats a string for the audit.</summary>
public static class ScreenActions
{
    public const string Intro = "intro";
    public const string Hangar = "hangar";
    public const string Loadout = "loadout";

    /// <summary>The intro's non-row chrome elements (title, subtitle, pilot field, identity
    /// status line, key hint). Constants: the draw path never formats a string.</summary>
    public const string IntroTitle = "intro.title";
    public const string IntroSubtitle = "intro.subtitle";
    public const string IntroPilot = "intro.pilot";
    public const string IntroIdentity = "intro.identity";
    public const string IntroHint = "intro.hint";
    /// <summary>The hangar's native hover string element.</summary>
    public const string HangarHover = "hangar.hover";

    private static readonly string[] IntroNames =
    [
        "SinglePlayer", "Lobby", "FindServer", "DirectConnect", "Options", "Credits",
        "ReplayIntro", "Help", "Exit", "Benchmark", "Career", "HostLan", "HostInternet",
    ];

    private static readonly string[] IntroElementIds =
    [
        "intro.SinglePlayer", "intro.Lobby", "intro.FindServer", "intro.DirectConnect",
        "intro.Options", "intro.Credits", "intro.ReplayIntro", "intro.Help", "intro.Exit",
        "intro.Benchmark", "intro.Career", "intro.HostLan", "intro.HostInternet",
    ];

    /// <summary>Hangar verb ids, indexed by <see cref="HangarAction"/>.</summary>
    private static readonly string[] HangarElementIds =
    [
        "hangar.None", "hangar.Launch", "hangar.Buy", "hangar.Command", "hangar.Loadout",
        "hangar.Default", "hangar.Invest", "hangar.Help", "hangar.Teleport", "hangar.Team",
        "hangar.Exit",
    ];

    /// <summary>Loadout element ids, indexed by <see cref="LoadoutV2Layout.Kind"/>.</summary>
    private static readonly string[] LoadoutElementIds =
    [
        "loadout.Title", "loadout.Money", "loadout.HullHeading", "loadout.SlotHeading",
        "loadout.Hull", "loadout.Preview", "loadout.Slot", "loadout.Offer", "loadout.Stat",
        "loadout.Price", "loadout.Default", "loadout.Revert", "loadout.Buy", "loadout.Launch",
        "loadout.Back", "loadout.Undock",
    ];

    public static string IntroRow(int index) =>
        index >= 0 && index < IntroNames.Length ? IntroNames[index] : "None";

    /// <summary>The audit element id of an intro row (a constant, never formatted).</summary>
    public static string IntroElement(int index) =>
        index >= 0 && index < IntroElementIds.Length ? IntroElementIds[index] : "intro.None";

    /// <summary>The audit element id of a hangar verb (a constant).</summary>
    public static string HangarElement(HangarAction action)
    {
        int index = (int)action;
        return index >= 0 && index < HangarElementIds.Length ? HangarElementIds[index] : "hangar.None";
    }

    /// <summary>The audit element id of a loadout leaf (a constant).</summary>
    public static string LoadoutElement(LoadoutV2Layout.Kind kind)
    {
        int index = (int)kind;
        return index >= 0 && index < LoadoutElementIds.Length ? LoadoutElementIds[index] : "loadout.None";
    }
}
