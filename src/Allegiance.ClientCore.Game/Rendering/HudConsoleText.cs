namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-9 console text and banner frame (native <c>WinTrek/console.cpp</c>,
/// <c>WinTrek/consoledata.cpp</c> and <c>Artwork/dialog.mdl</c>). One slot per
/// native binding; readings the port cannot obtain stay null rather than
/// becoming fabricated zeroes. Pure data — no fonts, bitmaps or graphics device.
/// </summary>
public sealed record HudConsoleText
{
    /// <summary>Sector location label. Native <c>SectorName</c> is the current
    /// cluster name in flight (<c>console.cpp:870</c> → <c>GetCluster()</c>) and
    /// the station's sector in base (<c>dialog.mdl:1450-1456</c>); null when the
    /// cluster is not yet known.</summary>
    public string? SectorName { get; init; }

    /// <summary>HUD-20 — the sector the commander is LOOKING AT in docked command
    /// view: native <c>SectorName</c> is <c>trekClient.GetCluster()-&gt;GetName()</c>
    /// (<c>console.cpp:868-872</c>), and <c>GetCluster()</c> returns the view
    /// cluster when one is set, otherwise the ship's cluster
    /// (<c>clintlib/clintlib.h:996</c>). The port resolves that as
    /// <c>ViewedClusterId ?? station cluster</c> (<c>ClientWorld.Commanding.cs:10</c>,
    /// the same rule as <c>Game1.Commanding.cs</c> RenderSector). Drawn as
    /// "Currently viewing:" + red name only when docked and in command view
    /// (native <c>ofInStationCommand</c>, <c>WinTrek.cpp:6831-6840</c>,
    /// <c>dialog.mdl:1429-1471,5421-5422</c>).</summary>
    public string? ViewedSectorName { get; init; }

    /// <summary>Turret carrier pilot name. Native <c>GetName(Me)</c> where Me is
    /// the source ship, shown when <see cref="BoardState"/> == 2
    /// (<c>dialog.mdl:1508-1543</c>, <c>console.cpp:797</c>); null otherwise.</summary>
    public string? CarrierName { get; init; }

    /// <summary>Native <c>BoardState</c> (<c>console.cpp:797-804</c>): 0 = not
    /// boarded, 1 = observer (parented but no turret), 2 = turret gunner.</summary>
    public int BoardState { get; init; }

    /// <summary>Accepted order text — native <c>verb + " " + GetModelName(target)</c>
    /// (<c>console.cpp:875-890</c>, <c>dialog.mdl:1623-1640</c>); null when no
    /// accepted order.</summary>
    public string? AcceptedOrder { get; init; }

    /// <summary>Queued order text, same shape as the accepted slot but red
    /// (<c>console.cpp:893-908</c>, <c>dialog.mdl:1605-1620</c>); null when none.</summary>
    public string? QueuedOrder { get; init; }

    /// <summary>Active ballot text (<c>console.cpp:935-937</c>,
    /// <c>dialog.mdl:2114-2120</c>); null when no vote is in flight.</summary>
    public string? VoteText { get; init; }

    /// <summary>Three ordinary tip slots, newest first, retained until
    /// overwritten (<c>dialog.mdl:5347-5349</c>; native ordinary tips are not
    /// cleared by the expiry path, <c>console.cpp:913-931</c>).</summary>
    public IReadOnlyList<string> OrdinaryTips { get; init; } = Array.Empty<string>();

    /// <summary>Up to four critical tip slots, oldest first (the renderer draws
    /// the newest at the bottom, matching native slot 0), each expiring after
    /// <see cref="CriticalTipDurationSeconds"/> (native <c>fTipTextDuration</c>,
    /// <c>console.cpp:913</c>; 30 s during training, not surfaced by this port).
    /// <see cref="HudNotice.Opacity"/> is 1 while active (native has no fade).</summary>
    public IReadOnlyList<HudNotice> CriticalTips { get; init; } = Array.Empty<HudNotice>();

    /// <summary>Mission lives remaining — native
    /// <c>iLives − ppi-&gt;MissionDeaths()</c> (<c>console.cpp:812-818</c>), from
    /// MISSIONDEF's iLives and the PLAYERINFO/KILL_SHIP mission-death counter.
    /// Null while either reading is unknown.</summary>
    public int? Lives { get; init; }

    /// <summary>Mission kills — native <c>ppi-&gt;MissionKills()</c>
    /// (<c>console.cpp:813,818-821</c>), from PLAYERINFO's nKills and kept
    /// current from KILL_SHIP. Null while unknown.</summary>
    public int? MissionKills { get; init; }

    /// <summary>Native <c>MissionParams::IsDeathMatchGame()</c>
    /// (<c>igc.h:1521-1523</c>): the kills badge is drawn only in a deathmatch
    /// (<c>console.cpp:818-821</c>).</summary>
    public bool Deathmatch { get; init; }

    /// <summary>Own side colour (native <c>mySideColor</c>, dialog.mdl:2006/2018/
    /// 2027) for ordinary tips and the accepted order line; null while unknown.</summary>
    public HudColor? SideColor { get; init; }
}

/// <summary>HUD-20 — the docked command-view location block: the yellow
/// "Currently viewing:" label plus the red viewed-sector name.</summary>
public readonly record struct HudLocationBlock(string Label, string Name, HudColor Color);

/// <summary>Pure formatting for the console-text rows; no MonoGame types.</summary>
public static class HudConsoleFormat
{
    /// <summary>HUD-12 — the lives badge is drawn only while the remaining count
    /// is below 10 (native <c>m_pnumberLivesOn</c>, <c>console.cpp:814-817</c>).</summary>
    public static bool LivesVisible(int? lives) => lives is { } n && n < 10;

    /// <summary>HUD-12 — the kills badge is drawn only in a deathmatch
    /// (native <c>m_pnumberKillsOn</c>, <c>console.cpp:818-821</c>).</summary>
    public static bool KillsVisible(bool deathmatch, int? kills) => deathmatch && kills is not null;

    /// <summary>HUD-12 — native remaining-lives arithmetic:
    /// <c>iLives − ppi-&gt;MissionDeaths()</c> (<c>console.cpp:812</c>). Null when
    /// either reading is unknown; the unlimited sentinel (0x7fff) is a real
    /// value and yields a large count (badge hidden by <see cref="LivesVisible"/>).</summary>
    public static int? RemainingLives(short missionLives, short missionDeaths) =>
        missionLives - missionDeaths;

    /// <summary>Native critical-tip lifetime for non-training missions
    /// (<c>fTipTextDuration = Training ? 30.0f : 7.5f</c>, <c>console.cpp:913</c>).</summary>
    public const double CriticalTipDurationSeconds = 7.5;

    public const int OrdinaryTipSlots = 3;   // dialog.mdl:5347-5349
    public const int CriticalTipSlots = 4;   // console.cpp:727-730

    // Native dialog.mdl anchors, expressed in the port's top-left screen space.
    // The MDL uses a lower-left origin with y up (dialog.mdl:5259-5268), so an
    // on point of -37 is 37 px below the TOP edge, not the bottom.
    public const float SectorLabelTop = 37f;         // dialog.mdl:5419 (-37)
    public const float SectorNameBelowLabel = 14f;   // dialog.mdl:1484

    // HUD-20 — the docked command-view location block. Native LookingAtImage is
    // the yellow promptFont "Currently viewing:" plus CurrentViewSectorNameImage
    // (medVerdana, Color(1,0,0), centred 160 wide, translated Point(0,-14)),
    // drawn only under ofInStationCommand (dialog.mdl:1429-1471,5421-5422;
    // WinTrek.cpp:6831-6840).
    public const string CurrentlyViewingLabel = "Currently viewing:";
    /// <summary>Native <c>Color(1,0,0)</c> for the viewed-sector name.
    /// <c>dialog.mdl:1438</c>.</summary>
    public static readonly HudColor ViewedSectorColor = new(1f, 0f, 0f);

    /// <summary>HUD-20 — native <c>trekClient.GetCluster()</c> is the VIEW cluster
    /// when one is set, otherwise the ship's cluster (<c>clintlib/clintlib.h:996</c>).
    /// For the docked command view that is the port's
    /// <c>ViewedClusterId ?? station cluster</c> (ClientWorld.Commanding.cs:10,
    /// the same rule Game1.Commanding.cs RenderSector uses).</summary>
    public static short ViewedSectorId(short? viewedClusterId, short stationClusterId) =>
        viewedClusterId ?? stationClusterId;

    /// <summary>HUD-20 — the "Currently viewing:" block for a resolved viewed
    /// sector name, or null when no name is known (the renderer draws nothing).
    /// Pure and allocation-free, so the per-frame draw path can call it.</summary>
    public static HudLocationBlock? LookingAt(string? viewedSectorName) =>
        string.IsNullOrEmpty(viewedSectorName)
            ? null
            : new HudLocationBlock(CurrentlyViewingLabel, viewedSectorName, ViewedSectorColor);

    /// <summary>HUD-20 — the one gate both skins (and the HUD-11 anchor
    /// reservation) use for the docked "Currently viewing:" block: native
    /// <c>LookingAtImage</c> is hung on <c>ofInStationCommand</c> only
    /// (<c>dialog.mdl:5421-5422</c>), i.e. HUD visible, docked, command view, and
    /// never while the in-flight <c>LocationInCockpitImage</c> (<c>ofInFlight</c>)
    /// owns the anchor (<c>WinTrek.cpp:6831-6840</c>). Null when hidden.</summary>
    public static HudLocationBlock? DockedLookingAt(bool visible, bool flightVisible,
        bool docked, bool commandView, string? viewedSectorName) =>
        visible && !flightVisible && docked && commandView ? LookingAt(viewedSectorName) : null;
    public const float TurretLabelTop = 75f;         // dialog.mdl:5425 (-75)
    public const float OrderLabelTop = 37f;          // dialog.mdl:5451 (-37)
    public const float OrderLabelX = 37f;            // dialog.mdl:1647 (label at -13 from the 50 pane)
    public const float OrderTextX = 57f;             // dialog.mdl:1647-1648 (text at 7 from the 50 pane)
    public const float OrderQueuedBelowLabel = 20f;  // dialog.mdl:1647
    public const float OrderAcceptedBelowLabel = 40f;// dialog.mdl:1648

    // HUD-15 — the native order-line glyphs. LastOrderImage's queued/accepted
    // groups translate ImportImage3D("comqueuedbmp"/"comacceptbmp", …) by
    // Point(-21,-13) (Artwork/dialog.mdl:1605-1640) inside a group the pane
    // places at SideTopLeft(50,-37) with the lines at Point(7,-20)/(7,-40)
    // (dialog.mdl:1641-1652, :5451). Both glyph bitmaps are 16x16
    // (comacceptbmp.mdl/comqueuedbmp.mdl BinarySurfaceInfo), so the glyph's
    // top-left is 21 px left of the line's text edge and 13 px below the line's
    // lower-left, i.e. 3 px above the native line top.
    public const float OrderIconSize = 16f;
    public const float OrderIconLeft = OrderTextX - 21f;   // 36
    public const float OrderIconBelowLine = 13f;           // TranslateImage Point(0,-13)
    public const float RipcordLeft = 75f;            // dialog.mdl:5429 (-75 from centre-x)
    public const float RipcordTop = 15f;             // dialog.mdl:5429 (15 above centre-y)
    public const float RipcordWidth = 150f;          // StringImage(JustifyCenter, 150, …) dialog.mdl:5146
    public const float CriticalTipTopBase = 50f;     // dialog.mdl:5173 (-50)
    public const float CriticalTipSpacing = 14f;     // dialog.mdl:2075-2077
    public const float VoteTopBase = 85f;            // dialog.mdl:5174 (-85)
    public const float ChatLineYStep = 12f;          // dialog.mdl:5172-5174 (12·NumChatLines)

    /// <summary>HUD-15 — top-left of one native order-line glyph in port screen
    /// space (y down), scaled for the skin. <paramref name="accepted"/> selects
    /// the accepted line's Point(7,-40) row or the queued line's Point(7,-20).</summary>
    public static (float X, float Y) OrderIconTopLeft(bool accepted, float scale) => (
        OrderIconLeft * scale,
        (OrderLabelTop + (accepted ? OrderAcceptedBelowLabel : OrderQueuedBelowLabel)
            + OrderIconBelowLine - OrderIconSize) * scale);

    /// <summary>Critical-tip slot top (px from the viewport top). Slot 0 is the
    /// newest tip; older slots stack above it (dialog.mdl:2039-2078, :5173).</summary>
    public static float CriticalTipTop(int chatLines, int slot) =>
        CriticalTipTopBase + ChatLineYStep * chatLines - CriticalTipSpacing * slot;

    /// <summary>Vote banner top (px from the viewport top), chat-line dependent
    /// like native VoteTextImageYOffset (dialog.mdl:5174).</summary>
    public static float VoteTop(int chatLines) => VoteTopBase + ChatLineYStep * chatLines;

    /// <summary>FONT-1c — native lives/kills badge text top. LivesOnImage /
    /// KillsOnImage translate a JustifyCenter 80-wide StringImage by
    /// Point(18,20) / Point(15,20) in y-up from the badge art's LOWER-left
    /// (dialog.mdl:5070-5080, 5107-5120). The y-up origin is the text TOP, so in
    /// screen y-down the text top is <c>artTop + artHeight - 20·scale</c> — it
    /// hangs DOWN from 20 px above the art's bottom edge, inside a 22/25 px
    /// badge. FONT-1b treated the point as the text's bottom, which put the
    /// glyphs above the art.</summary>
    public static float BadgeTextTop(float artTop, float artHeight, float scale)
        => artTop + artHeight - 20f * scale;

    /// <summary>FONT-1c review — native badge x: <c>SideTopLeft</c> end point
    /// <c>Point(40, …)</c> (dialog.mdl:5441-5445). A shown overlay animates to
    /// its END point (<c>Show(true)</c> → <c>MoveEnd()</c>, console.cpp:3089-3096;
    /// trekctrls.cpp:994-998, 1043-1050); the 45 is the START point it returns
    /// to when hidden.</summary>
    public const float BadgeLeft = 40f;
    /// <summary>Native lives / kills badge anchors, px below the viewport top
    /// (<c>SideTopLeft</c> <c>Point(40,-97)</c> / <c>Point(40,-177)</c>,
    /// dialog.mdl:5441-5445; <c>GetSidePoint(ATTopLeft) = (xmin, ymax)</c>,
    /// trekctrls.cpp:1015-1028).</summary>
    public const float LivesBadgeAnchor = 97f, KillsBadgeAnchor = 177f;

    /// <summary>FONT-1c review — the badge art's screen top. The badge group's
    /// <c>ImportImage</c> member is untranslated, so its y-up bounds
    /// <c>Rect(Point(0,0), size)</c> (engine/image.cpp:184-190) put the art's
    /// LOWER-left on the anchor: the art spans <c>[anchor − H, anchor]</c> in
    /// screen y-down, and its text (<see cref="BadgeTextTop"/>) hangs from 20 px
    /// above the anchor.</summary>
    public static float BadgeArtTop(float anchorY, float artHeight) => anchorY - artHeight;

    /// <summary>FONT-1c review — left edge of the centred ripcord countdown
    /// text: native <c>StringImage(JustifyCenter, 150, …)</c> whose box
    /// top-left is <c>SideCenter(-75,15)</c> (dialog.mdl:5145-5151, 5429), so
    /// the glyphs centre on the viewport centre line.</summary>
    public static float RipcordTextLeft(float viewportWidth, float textWidth, float scale)
        => viewportWidth / 2f - RipcordLeft * scale + (RipcordWidth * scale - textWidth) / 2f;

    /// <summary>HUD-11 — native <c>NumChatLines</c> for the banner offsets is the
    /// CONFIGURED pane height (<see cref="HudChat.PaneLines"/>, the
    /// <c>ChatLines</c> preference), not the number of lines currently shown
    /// (WinTrek.cpp:4783-4806, dialog.mdl:5173-5174). Falls back to the native
    /// default 7 when no chat snapshot was supplied.</summary>
    public static int BannerChatLines(HudChat? chat)
        => chat is { PaneLines: > 0 } configured ? configured.PaneLines : ChatPaneLayout.DefaultLines;

    /// <summary>HUD-11 — the sector/turret name line sits one rendered font line
    /// below its label. Native translates it by 14 px because native promptFont
    /// is 14 px high (dialog.mdl:1473-1485); the port substitute font is taller,
    /// so a raw 14 px overlap at 1080p. Callers pass the measured line height.
    /// </summary>
    public static float NameBelowLabel(float labelTop, float lineHeight) => labelTop + lineHeight;

    /// <summary>FONT-1c — the current-order red queued line's TOP is the native
    /// Point(0,-20) translation (dialog.mdl:1647-1648) below the label origin:
    /// 20 native px in y-down screen space, scaled with the pane. The glyph row
    /// already uses the same 20 px (OrderIconTopLeft), so the arrow/check and
    /// its text share one row (FONT-1c fix 4; the old measured-line-height
    /// value was ~13 px at 1080p and the glyph floated ~7 px off its text).</summary>
    public static float OrderQueuedTop(float labelTop, float scale) => labelTop + OrderQueuedBelowLabel * scale;

    /// <summary>FONT-1c — the accepted (cortText) order line's TOP is the native
    /// Point(0,-40) translation (dialog.mdl:1648), 40 native px below the label
    /// origin, scaled with the pane.</summary>
    public static float OrderAcceptedTop(float labelTop, float scale) => labelTop + OrderAcceptedBelowLabel * scale;

    /// <summary>Native ripcord HUD label (<c>dialog.mdl:5139-5154</c>,
    /// <c>consoledata.cpp:521-532</c>): null when inactive or the start time is
    /// unknown (never a fabricated "Ripcord in: 1"), otherwise "Ripcord in: n"
    /// with the native double-0.5 rounding (<c>modeler.cpp:2008</c>).</summary>
    public static string? RipcordLabel(bool active, long? activeSinceLocalMs, float timeLeft)
    {
        if (!active || activeSinceLocalMs is null) return null;
        int seconds = (int)(timeLeft + 1.0);
        return $"Ripcord in: {seconds}";
    }

    /// <summary>Native command verbs, indexed by <c>CommandID</c>
    /// (<c>c_cdAllCommands[cid].szVerb</c>, <c>Igc/common.cpp:41-52</c>).</summary>
    private static readonly string[] Verbs =
        ["goto", "attack", "capture", "defend", "pickup", "goto", "repair", "join", "mine", "build"];

    public static string Verb(short commandId) =>
        commandId >= 0 && commandId < Verbs.Length ? Verbs[commandId] : "";

    /// <summary>Native accepted/queued order text: <c>verb + " " + GetModelName(target)</c>
    /// (<c>console.cpp:875-908</c>).</summary>
    public static string OrderText(short commandId, string? targetName)
    {
        if (commandId < 0) return "";
        string verb = Verb(commandId);
        return string.IsNullOrEmpty(targetName) ? verb : verb + " " + targetName;
    }

    /// <summary>Native board-state wording for the loadout centre line
    /// (<c>dialog.mdl:2086-2108</c>).</summary>
    public static string BoardLabel(int boardState, string? carrierName)
    {
        string name = carrierName ?? "";
        return boardState switch
        {
            1 => "Observer of " + name,
            2 => "Turret of " + name,
            _ => ""
        };
    }
}
