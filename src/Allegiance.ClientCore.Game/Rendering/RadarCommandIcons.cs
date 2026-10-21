namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-15 — the native radar command-icon surfaces. Native loads one accepted
/// and one queued 16×16 bitmap per command from the shared command table:
/// <code>
/// for (CommandID i = c_cidAttack; (i &lt; c_cidMax); i++)
/// {
///     if (c_cdAllCommands[i].szAccepted[0] != '\0')
///         m_psurfaceAccepted[i] = pmodeler->LoadSurface(c_cdAllCommands[i].szAccepted, true);
///
///     if (c_cdAllCommands[i].szQueued[0] != '\0')
///         m_psurfaceQueued[i]   = pmodeler->LoadSurface(c_cdAllCommands[i].szQueued, true);
/// }
/// </code>
/// (<c>WinTrek/radarimage.cpp:228-235</c>), reading
/// <c>c_cdAllCommands[c_cidMax]</c> (<c>Igc/common.cpp:41-52</c>, constants
/// <c>Igc/igc.h:893-913</c>). The label draw picks
/// <c>m_psurfaceAccepted[data.m_cid]</c> or <c>m_psurfaceQueued[data.m_cid]</c>
/// from the contact's command slot (<c>radarimage.cpp:1248-1253</c>).
/// <para>
/// The native load loop starts at <c>c_cidAttack</c> (1), so
/// <c>c_cidDefault</c> (0) has <b>no</b> radar icon: the table's
/// <c>acdefaultbmp</c>/<c>qudefaultbmp</c> entries are loaded only by the
/// command bar's own image namespace, never into the radar arrays. A missing
/// surface (or a command id outside 1..9) keeps the port's historical
/// <c>A&lt;id&gt;</c>/<c>Q&lt;id&gt;</c> label prefix — the graceful fallback.
/// </para>
/// Pure data: no graphics device, so the mapping and the fallback rule are
/// unit-testable headless.
/// </summary>
public static class RadarCommandIcons
{
    /// <summary><c>c_cidDefault</c> (<c>Igc/igc.h:898</c>) — the table names
    /// art, but native's radar loader never loads it.</summary>
    public const short DefaultCommandId = 0;

    /// <summary><c>c_cidAttack</c> (<c>Igc/igc.h:899</c>) — the first id the
    /// native radar load loop covers (<c>radarimage.cpp:228</c>).</summary>
    public const short FirstArtCommandId = 1;

    /// <summary><c>c_cidMax</c> (<c>Igc/igc.h:911</c>) — the exclusive end of the
    /// native loop and of <c>c_cdAllCommands</c>.</summary>
    public const short CommandIdCount = 10;

    /// <summary><c>c_cdAllCommands[i].szAccepted</c> (<c>Igc/common.cpp:41-52</c>).
    /// Index 0 is the table's real stem, but native's radar never loads it; see
    /// <see cref="ArtStem"/> which returns null there, exactly like the array
    /// entry native leaves NULL.</summary>
    private static readonly string?[] AcceptedArt =
    [
        null,             // c_cidDefault  — acdefaultbmp is not a radar surface
        "ackillbmp",      // c_cidAttack
        "accptbmp",       // c_cidCapture
        "acdefendbmp",    // c_cidDefend
        "acpickupbmp",    // c_cidPickup
        "acgotobmp",      // c_cidGoto
        "acrepairbmp",    // c_cidRepair
        "acjoinbmp",      // c_cidJoin
        "acminebmp",      // c_cidMine
        "acbuildbmp",     // c_cidBuild
    ];

    /// <summary><c>c_cdAllCommands[i].szQueued</c> (<c>Igc/common.cpp:41-52</c>),
    /// same native load range as <see cref="AcceptedArt"/>.</summary>
    private static readonly string?[] QueuedArt =
    [
        null,             // c_cidDefault  — qudefaultbmp is not a radar surface
        "qukillbmp",      // c_cidAttack
        "qucptbmp",       // c_cidCapture
        "qudefendbmp",    // c_cidDefend
        "qupickupbmp",    // c_cidPickup
        "qugotobmp",      // c_cidGoto
        "qurepairbmp",    // c_cidRepair
        "qujoinbmp",      // c_cidJoin
        "quminebmp",      // c_cidMine
        "qubuildbmp",     // c_cidBuild
    ];

    /// <summary>The surface stem native loads for one command slot, or null when
    /// that slot has no radar art (the native array entry is NULL).</summary>
    public static string? ArtStem(short commandId, bool accepted) =>
        commandId >= FirstArtCommandId && commandId < CommandIdCount
            ? (accepted ? AcceptedArt[commandId] : QueuedArt[commandId])
            : null;

    /// <summary>
    /// Whether the radar label draws the native command icon instead of the
    /// <c>A&lt;id&gt;</c>/<c>Q&lt;id&gt;</c> text prefix. <paramref name="artLoaded"/>
    /// is the caller's lookup result for the mapped stem
    /// (<c>c_cdAllCommands[cid].szAccepted/szQueued</c> decoded successfully);
    /// false keeps the text prefix, so missing artwork never hides the order.
    /// </summary>
    public static bool UseIcon(int? commandId, bool artLoaded) =>
        commandId is { } cid && cid is >= FirstArtCommandId and < CommandIdCount
        && ArtStem((short)cid, accepted: true) is not null && artLoaded;
}
