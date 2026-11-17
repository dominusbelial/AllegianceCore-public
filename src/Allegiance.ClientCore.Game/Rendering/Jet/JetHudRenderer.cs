using System;
using System.Globalization;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1036 — Jet story 2/9: the core flight symbology (P972 §4.2, phase 3A).
/// Still a concrete <see cref="IHudSkinRenderer"/> composed exactly like the
/// Classic adapter (the shared <see cref="HudSkinResources"/> passes keep radar,
/// ship tags, pod/death, command view, chat, cargo and flight parts working),
/// but Jet now draws the round flight-path marker, the sector-plane pitch ladder,
/// the heading tape and the moving speed/throttle tapes, plus story 1's broken gun
/// cross and the <c>JET / GREEN - WIP</c> tag.
/// <para><b>Every value is shared-model data:</b> the flight-path marker is the
/// port's existing native velocity-indicator projection
/// (<c>WinTrek/indicator.cpp:128-150</c>, <c>Game1.Camera.ComputeVelocityMarker</c>),
/// attitude comes from <see cref="HudFlightReference"/> (built once in the model
/// adapter from the pose the client already tracks), speed and throttle are the
/// same <see cref="HudFrame"/> values Classic/Modern gauge, and nothing is invented
/// (no altitude, G, Mach or planetary horizon).</para>
/// <para><b>Gates:</b> the gun cross and the flight-path marker follow the native
/// reticle/indicator camera gates (<see cref="HudModel.ReticleVisible"/>,
/// <see cref="HudModel.VelocityMarkerVisible"/>); tapes, ladder and heading follow
/// the instrument group (<see cref="HudModel.InstrumentsVisible"/>), which is
/// exactly what Classic hides in the command view, docked and dead. A lifepod
/// keeps the tapes (pod speed) and shows the ladder/heading only as its data
/// allows.</para>
/// </summary>
public sealed partial class JetHudRenderer : IHudSkinRenderer
{
    /// <summary>The green variant's WIP tag (P1035's shipped text). HUD fonts are
    /// ASCII (<see cref="HudFormat.Printable"/>), so the P972 em dash is a hyphen.
    /// P1041 — each palette names itself; <see cref="TagLabel"/> is the active one.</summary>
    public const string TagText = "JET / GREEN - WIP";
    /// <summary>P1041 — the amber variant's tag (same 17 characters as the green one).</summary>
    public const string TagTextAmber = "JET / AMBER - WIP";

    /// <summary>The <see cref="HudElement"/> ids Jet itself draws: velocity indicator
    /// (flight-path marker), roll (the ladder's bank reference), speed, thrust setting, the
    /// centre reticle, the whole selected-target data family (designator/pane readouts),
    /// the missile lock, lead/aim/chase markers, the order turn arrows, the P1038 stores
    /// block (own-ship rows), the master-caution panel (alert/lifecycle rows) and the Jet
    /// F4 inventory pane (equipment/inventory rows). The shared passes draw the rest and
    /// are not claimed here.</summary>
    private static readonly string[] CoverageIds =
    [
        "Velocity direction marker", "Roll tutorial cue", "Speed", "Thrust setting", "Cockpit centre reticle",
        // P1037 — selected target (P972 §4.3, §5.2).
        "Selected target bracket", "Target name", "Target type description", "Target side identity",
        "Target hull", "Target shield", "Target range or sector", "Target speed", "Target ore",
        "Target kill bonus", "Target in weapon range", "Target time to intercept",
        // P1037 — centre indicators (P972 §4.2, §5.3).
        "Missile lock flower", "Ballistic lead marker", "Aim hit-probability predictor",
        "Chase weapon aim without target", "Accepted and current order turn arrows",
        // P1038 — the stores block (P972 §4.3, §5.1).
        "Hull integrity", "Shield charge", "Energy reserve", "Fuel reserve", "Ammunition reserve",
        "Kill bonus", "Signature percentage", "Detection eye", "Cloak active",
        "Cloak strength and drained state", "Autopilot status", "Vector lock status",
        "Carried flag", "Carried artifact",
        // P1038 — the master-caution panel (P972 §4.4 first paragraph, §5.5 alert rows).
        "Incoming missile warning", "Damage flash timer", "Lifepod oxygen endurance",
        "Boundary and crowd warnings", "Death banner", "Critical tip text lines",
        "Ripcord countdown", "Lives remaining", "Mission kills", "Vote banner",
        // P1038 — the F4 inventory pane (P972 §4.3 second half, §5.6).
        "Flight equipment inventory pane", "Equipment part identity and key",
        "Equipment selected and active state", "Equipment quantity and empty warning",
        "Equipment mounting progress", "Launcher arming progress", "Equipment weapon range",
        "Equipment shot damage", "Equipment burst rate", "Equipment shield strength",
        "Equipment shield recharge seconds", "Equipment afterburner top speed",
        "Equipment afterburner endurance", "Equipment cloak endurance",
        "Inventory turret gunner identity", "Inventory cargo slots", "Inventory own ship name",
        "Equipment empty slot affordances",
        // P1040 — the console, chat/command and lifecycle family (P972 §5.5 rows 1-18,
        // §5.3 training callouts, §5.6 command-view summary). Jet draws these itself now;
        // the P1038 caution-panel ids above stay because the panel still draws them.
        "Chat history", "Chat composition and recipient",
        "Command recipient verb and wing controls",
        "Sector location", "Turret carrier identity", "Accepted order text", "Queued order text",
        "Tip text lines", "Mission countdown", "Eject pod identity panel",
        "Command view ship summary", "Training HUD callouts",
    ];
    private static readonly HashSet<string> CoverageSet = new(CoverageIds, StringComparer.Ordinal);

    /// <summary>The drawn element ids, for the tests and the proof output.</summary>
    public static IReadOnlyList<string> ElementIds => CoverageIds;

    // P1037 — public so the proof's layout audit can identify the caption boxes.
    public const string SpeedCaptionText = "SPD / MPS";
    public const string ThrottleCaptionText = "THR";
    private const string SpeedCaption = SpeedCaptionText;
    private const string ThrottleCaption = ThrottleCaptionText;
    private const string HeadingCaption = "HDG ";
    private const string HeadingUnavailable = "HDG --";
    private const string IdleLabel = "IDLE";
    private const string ReverseLabel = "REV";
    // P1037 — target / lock / order text (pure ASCII: the HUD fonts bake 32..126).
    private const string LockCaption = "LOCK";
    /// <summary>P1039b — the label of the fixed target-data fallback slot (the TGT block).</summary>
    private const string TargetBlockWord = "TGT";
    private const string InRangeLabel = "IN RANGE";
    private const string AcceptedLabel = "ACC";
    private const string CurrentLabel = "CUR";
    private const string SameTargetLabel = "ACC / CUR";
    private const string HullWord = "HULL";
    private const string ShieldWord = "SHLD";
    private const string OreWord = "ORE";
    private const string KillBonusWord = "KB";
    private const string ClosureWord = "CLO";
    private const string TargetSpeedWord = "SPD";
    private const string TypeWord = "TYPE";
    /// <summary>P1038 — the ladder's current-pitch numeral caption (ASCII only).</summary>
    private const string PitchCaption = "PITCH";
    private const string RangeWord = "RNG";

    /// <summary>Numeric ladder labels for −80 … +80 in 10° steps (ASCII only).</summary>
    private static readonly string[] RungLabels =
    [
        "-80", "-70", "-60", "-50", "-40", "-30", "-20", "-10", "00",
        "+10", "+20", "+30", "+40", "+50", "+60", "+70", "+80",
    ];

    /// <summary>Heading labels at the 20° marks, pre-formatted so a frame never
    /// formats a string for a tape tick.</summary>
    private static readonly string[] HeadingLabels =
    [
        "000", "020", "040", "060", "080", "100", "120", "140", "160",
        "180", "200", "220", "240", "260", "280", "300", "320", "340",
    ];

    /// <summary>Throttle labels at the 20 % marks (static: the scale is fixed).</summary>
    private static readonly string[] ThrottleLabels = ["000", "020", "040", "060", "080", "100"];

    /// <summary>Size of the speed-tick label ring: wide enough that the ticks of a
    /// ±180 mps tape (10 labels at 40 mps spacing) all stay cached.</summary>
    private const int SpeedTickCache = 16;

    /// <summary>The 800×480 layout floor: fewer ladder rungs, no IDLE/REV words
    /// (P972 §4.5).</summary>
    private const float CompactHeight = 700f;

    // P972 §4.1 fallbacks when a definition carries no palette block: the Jet
    // green variant's authored colours.
    private static readonly Color GreenPrimary = new(0x79, 0xFF, 0xC0);
    private static readonly Color GreenDim = new(0x3D, 0xA8, 0x7B);
    private static readonly Color GreenHot = new(0xC9, 0xFF, 0xE3);
    private static readonly Color GreenPlate = new(0x06, 0x11, 0x0C);
    private static readonly Color GreenKeyline = new(0x08, 0x0B, 0x09);

    private readonly IJetVectorSurface _surface;
    private readonly JetVectorPainter _painter;
    private readonly HudSkinResources _resources;
    private readonly HudSettings _settings;
    private readonly Action<RadarIconTextures?>? _radarIcons;
    private readonly float _displayScale;
    private readonly string _definitionName;
    /// <summary>P1041 — the palette variant this renderer was built for
    /// (<see cref="JetPaletteIds"/>); drives the WIP tag's own name.</summary>
    private readonly string _palette;

    private JetAttitudeState _attitude;

    // Text caches: a steady frame formats nothing.
    private string _speedText = "0";
    private int _speedShown = int.MinValue;
    private string _throttleText = "0%";
    private int _throttleShown = int.MinValue;
    private string _headingText = HeadingUnavailable;
    private int _headingShown = int.MinValue;
    // P1038 — the current-pitch numeral identifies itself ("PITCH +05") instead of
    // reading as a stray "00" label under the ladder; the position rules are unchanged.
    private string _pitchText = PitchCaption + " 00";
    private int _pitchShown = int.MinValue;
    private readonly int[] _speedTickKeys = new int[SpeedTickCache];
    private readonly string[] _speedTickText = new string[SpeedTickCache];

    // P1037 — target/lock readout caches: a steady frame formats nothing.
    private int _rangeShown = int.MinValue;
    private string _rangeText = "";
    private int _closureShown = int.MinValue;
    private string _closureText = "";
    private int _hullShown = int.MinValue;
    private string _hullText = "";
    private int _shieldShown = int.MinValue;
    private string _shieldText = "";
    private int _oreShown = int.MinValue;
    private string _oreText = "";
    private int _killBonusShown = int.MinValue;
    private string _killBonusText = "";
    private int _targetSpeedShown = int.MinValue;
    private string _targetSpeedText = "";
    private int _lockPercentShown = int.MinValue;
    private string _lockPercentText = "";
    private string _sectorShown = "";
    private string _sectorText = "";
    // P1039 — the compact designator name (truncated at a stated width), cached by source.
    private string _compactNameShown = "";
    private string _compactNameText = "";

    /// <summary>P1038 — the heading readout plate drawn this frame (default when the tape
    /// is hidden). The target readout uses it so a locked designator's text can never land
    /// on the <c>HDG</c> box at 800×480.</summary>
    private RectangleF _headingPlate;
    /// <summary>P1038 — the two tape caption boxes drawn this frame, for the same rule.</summary>
    private RectangleF _speedCaptionBox;
    /// <summary>P1040 — the speed readout plate measured this frame (the console column's
    /// left bound).</summary>
    private RectangleF _speedPlate;
    private RectangleF _throttleCaptionBox;

    /// <summary>
    /// P1039 — the fixed HUD FURNITURE a readout may never be placed over: every ladder
    /// numeral, the <c>PITCH nn</c> caption, the heading plate, both tape captions and
    /// label columns, the <c>ACC</c>/<c>CUR</c> order words, the stores block, the
    /// master-caution panel, the target data block, the F4 inventory pane and the radar
    /// scope. Each block registers its own box as it draws; the readout placement
    /// (<see cref="JetReadoutPlacement"/>) then picks the first clear slot. Static storage,
    /// no per-frame allocation.
    /// </summary>
    private const int MaxFurniture = 96;
    private readonly RectangleF[] _furniture = new RectangleF[MaxFurniture];
    private int _furnitureCount;

    /// <summary>P1039 — the designator readout block placed this frame (default when the
    /// designator is hidden). The lock caption avoids it: the two readouts never share a
    /// pixel.</summary>
    private RectangleF _targetReadoutBox;
    /// <summary>P1039 — the slot the designator readout took (0…3 = right/left/below/above).</summary>
    private JetReadoutPlacement.Placement _targetPlacement;
    /// <summary>P1039 — the slot the lock caption took.</summary>
    private JetReadoutPlacement.Placement _lockPlacement;
    /// <summary>P1039 — the lock caption block placed this frame.</summary>
    private RectangleF _lockReadoutBox;
    /// <summary>P1039 — the master-caution panel drawn this frame, for the F4 pane's top
    /// limit and the readout placement.</summary>
    private RectangleF _cautionPanel;
    /// <summary>P1039b — the measured top edge of the stores block (the TGT slot's anchor).</summary>
    private float _storesTop;
    /// <summary>P1039c — the measured ink box of the compact annunciator row (the word-gap
    /// evidence: it must end clear of the pitch ladder's numeral column).</summary>
    private RectangleF _annunciatorRow;
    /// <summary>P1039 — the Jet radar scope's reserved rectangle this frame (empty when the
    /// scope is suppressed).</summary>
    private RectangleF _radarScope;
    /// <summary>P1039 — the left edge of the throttle tape's readout/IDLE-REV band this
    /// frame (0 when the tape is hidden): the radar scope ends before it.</summary>
    private float _throttleBandLeft;

    /// <summary>P1039 — the left edge of the throttle tape's own band (the leftmost of its
    /// numeric plate and its IDLE/REV words), so the radar scope can never reach it.</summary>
    public float ThrottleBandLeft => _throttleBandLeft;

    private int _viewportWidth = 1920;
    private int _viewportHeight = 1080;
    private bool _disposed;

    /// <summary>P1039 — reset the per-frame furniture/readout registry.</summary>
    private void ClearFurniture()
    {
        _furnitureCount = 0;
        _targetReadoutBox = default;
        _lockReadoutBox = default;
        _cautionPanel = default;
        _radarScope = default;
        _storesTop = 0f;
        _annunciatorRow = default;
    }

    /// <summary>P1039 — register one furniture box for this frame.</summary>
    private void AddFurniture(RectangleF box)
    {
        if (box.Width <= 0f || box.Height <= 0f) return;
        if (_furnitureCount < MaxFurniture) _furniture[_furnitureCount++] = box;
    }

    private ReadOnlySpan<RectangleF> Furniture => _furniture.AsSpan(0, _furnitureCount);


    /// <summary>Test/alternate-surface form: any <see cref="IJetVectorSurface"/>.</summary>
    public JetHudRenderer(IJetVectorSurface surface, HudSettings settings, HudSkinResources resources,
        HudSkinDefinition definition, float displayScale = 1f, Action<RadarIconTextures?>? radarIcons = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(definition);
        _surface = surface;
        _settings = settings;
        _resources = resources;
        _definitionName = definition.Name;
        _radarIcons = radarIcons;
        _displayScale = displayScale > 0f ? displayScale : 1f;
        _palette = JetPaletteIds.Normalize(definition.Variant);
        HudSkinPalette? palette = definition.Palette;
        _painter = new JetVectorPainter(surface,
            Parse(palette?.Primary, GreenPrimary), Parse(palette?.Dim, GreenDim), Parse(palette?.Hot, GreenHot),
            Parse(palette?.Plate, GreenPlate), Parse(palette?.Keyline, GreenKeyline));
    }

    /// <summary>Production form: Game1's shared SpriteBatch, white pixel and fonts.</summary>
    public JetHudRenderer(SpriteBatch batch, Texture2D white, SpriteFont small, SpriteFont large,
        HudSettings settings, HudSkinResources resources, HudSkinDefinition definition,
        float displayScale = 1f, Action<RadarIconTextures?>? radarIcons = null)
        : this(new JetSpriteBatchSurface(batch, white, small, large), settings, resources, definition, displayScale, radarIcons)
    { }

    public string Id => HudSkinIds.Jet;

    /// <summary>The elements Jet itself draws; the shared passes below draw the
    /// rest (they are not claimed here).</summary>
    public IReadOnlySet<string> Coverage => CoverageSet;

    /// <summary>The resolved skin name, for the options row/proof output.</summary>
    public string DefinitionName => _definitionName;

    /// <summary>P1041 — the palette variant this renderer draws
    /// (<see cref="JetPaletteIds.Green"/> / <see cref="JetPaletteIds.Amber"/>).</summary>
    public string Palette => _palette;

    /// <summary>P1041 — the WIP tag for this palette (P972 §9 3E item 5: the tag
    /// names the variant until P1043).</summary>
    public string TagLabel => _palette == JetPaletteIds.Amber ? TagTextAmber : TagText;

    /// <summary>The painter, exposed for the vector-primitive tests.</summary>
    public JetVectorPainter Painter => _painter;

    /// <summary>The attitude resolved for the most recent frame (tests/proofs).</summary>
    public JetAttitude Attitude => _attitude.Current;

    /// <summary>P1039 — the designator readout block placed in the most recent frame
    /// (tests and the proof pin the placement priority against it).</summary>
    public RectangleF TargetReadoutBox => _targetReadoutBox;

    /// <summary>P1039 — the lock caption block placed in the most recent frame.</summary>
    public RectangleF LockReadoutBox => _lockReadoutBox;

    /// <summary>P1039 — the designator readout's placement (slot + clear flag).</summary>
    public JetReadoutPlacement.SlotInfo TargetReadoutSlot
        => new((int)_targetPlacement.Slot, _targetPlacement.Clear);

    /// <summary>P1039 — the lock caption's placement (slot + clear flag).</summary>
    public JetReadoutPlacement.SlotInfo LockReadoutSlot
        => new((int)_lockPlacement.Slot, _lockPlacement.Clear);

    /// <summary>P1039 — the master-caution panel drawn in the most recent frame.</summary>
    public RectangleF CautionPanel => _cautionPanel;

    /// <summary>P1039 — the Jet radar scope reserved in the most recent frame.</summary>
    public RectangleF RadarScope => _radarScope;

    /// <summary>P1039c — the measured ink box of the compact annunciator row of the most
    /// recent frame (default when the full row or no row was drawn).</summary>
    public RectangleF AnnunciatorRow => _annunciatorRow;

    /// <summary>P1039 — the furniture registered in the most recent frame.</summary>
    public ReadOnlySpan<RectangleF> FurnitureBoxes => _furniture.AsSpan(0, _furnitureCount);

    /// <summary>Clear the held heading/roll (proof fixtures start every scene from no
    /// held value; a live client never calls this — the hold is the point).</summary>
    public void ResetFlightState() => _attitude = default;

    public void Draw(HudModel model, in HudDrawFrame frame)
    {
        // P1039 — the Jet radar scope replaces the shared Classic radar pass (P972 §4.4,
        // §5.4): the same contact snapshot and the same four LOD modes, drawn as Jet's own
        // square/circular instrument inside the HUD pass below. Ship side tags stay shared.
        _resources.ShipTags(model, false);

        float scale = ModernHudScale.Core(_displayScale, _settings.Size, frame.Viewport.Height);
        _painter.Scale = scale;
        _viewportWidth = frame.Viewport.Width > 0 ? frame.Viewport.Width : _viewportWidth;
        _viewportHeight = frame.Viewport.Height > 0 ? frame.Viewport.Height : _viewportHeight;
        _headingPlate = default;
        _speedCaptionBox = default;
        _speedPlate = default;
        _throttleCaptionBox = default;
        _throttleBandLeft = 0f;
        _trainingBox = default;
        // P1039 — the per-frame furniture/readout registry the placement rules read.
        ClearFurniture();

        if (model.Visible)
        {
            float w = frame.Viewport.Width;
            float h = frame.Viewport.Height;
            _surface.Begin();
            try
            {
                if (model.InstrumentsVisible)
                {
                    // Attitude is resolved once per frame; a null reference
                    // (docked/dead/command/no own ship) clears it without
                    // inventing values.
                    _attitude.Update(model.Context.FlightReference);
                    // P1040c — the shared training callout's measured text block is resolved
                    // BEFORE the ladder: its anchor is native (indicator.cpp:461-701) and may
                    // not move, so Jet's own ladder furniture yields to it (see DrawPitchLadder).
                    _trainingBox = TryTrainingCallout(model, w, h, scale,
                        out TrainingCalloutGeometry callout, out _) ? callout.Box : default;
                    DrawHeadingTape(w, h, scale);
                    DrawPitchLadder(w, h, scale);
                }

                if (model.ReticleVisible)
                    DrawGunCross(new(w * 0.5f, h * 0.5f), scale);

                // Native velocity indicator: cockpit only, rounded speed non-zero,
                // projected inside the view (indicator.cpp:128-150).
                if (model.VelocityMarkerVisible && model.Context.VelocityMarker is { } marker)
                    DrawFlightPathMarker(new(marker.X, marker.Y), w, h, scale);

                // Native hides the centre instrument group in the command view
                // (HudModel.InstrumentsVisible = flight && !docked/dead/command), so
                // the tapes follow that same gate rather than widening it.
                if (model.InstrumentsVisible && model.Telemetry is { } own)
                {
                    DrawSpeedTape(own.Speed, w, h, scale);
                    DrawThrottleTape(own.Throttle, w, h, scale);
                }

                // P1037 — the shared indicator family (P972 §4.2): order steering,
                // lead/aim predictor and the chase no-target aim, each following the
                // exact gate the shared model computes.
                DrawOrderArrows(model, w, h, scale);
                DrawLeadAndAim(model, scale);
                DrawChaseAim(model, scale);

                // P1038 — the stores block and the master-caution panel (P972 §4.3, §4.4)
                // are drawn BEFORE the target/lock readouts so their boxes are registered
                // as furniture when a readout is placed (P1039: a readout may never be put
                // over the caution panel or the stores block). Each keeps its own gate.
                DrawStores(model, w, h, scale);
                // P1040b — the console boxes the caution stack's compaction reads are resolved
                // BEFORE the panel: `POD OXY` leaves the stack only when the lifepod block the
                // column reserves is actually drawable this frame (`_lifepodDrawable`), not on
                // the pod gate alone (P1040 dropped the line while drawing no panel at
                // 800×480, so the endurance was shown nowhere).
                _chatPane = ChatPaneRect(model, w, scale);
                _lifepodDrawable = LifepodReserved(model, scale);
                DrawCaution(model, w, h, scale);

                // P1040 — the console text family owns the left column below the chat pane
                // and above the stores block. Its band is resolved HERE (stores top known,
                // designator readout not yet placed) so it is furniture when a world-anchored
                // readout is placed: a target readout can never land on the sector/orders
                // rows, and the chat pane's box is the same value DrawConsole re-reads.
                float columnLeft = ConsoleLeftEdge(scale);
                float columnRight = JetConsoleLayout.ColumnRight(w, scale);
                float columnTop = _chatPane.Y + _chatPane.Height + JetConsoleLayout.ChatToColumn * scale;
                float columnBottom = _storesTop > 0f ? _storesTop : h - 166f;
                if (columnRight > columnLeft && columnBottom > columnTop)
                    AddFurniture(new RectangleF(columnLeft, columnTop, columnRight - columnLeft, columnBottom - columnTop));

                // P1039 — the radar scope (P972 §4.4 RWR) is drawn after the fixed flight
                // furniture and before the world-anchored target/lock symbology: the scope's
                // rectangle is furniture for every readout placed later, and the marker
                // strokes that follow it are the stated world-anchored exception.
                DrawRadar(model, w, h, scale);

                // P1037 — the selected target (P972 §4.3): projected designator +
                // off-screen edge chevron + lock, then the native target pane as a
                // Jet data block (present even when the target is out of view).
                // The bottom target pane is drawn first: its rows are furniture for the
                // designator readout placed right after it.
                DrawTargetBlock(model, w, h, scale);
                DrawTargetDesignator(model, w, h, scale);
                DrawOffScreenTarget(model, w, h, scale);
                DrawLock(model, w, h, scale);

                // P1038 — the Jet F4 inventory pane is the modal OVERLAY: drawn last, it
                // owns its band (the readout placement is below its rows and the audit
                // exempts anything it hides).
                DrawInventory(model, w, h, scale);

                // P1040 — the console text family (P972 §5.5 rows 1-5, 15, 17, 18) rides
                // the HUD pass: the chat pane was resolved with the stores block (its box and
                // the column band are furniture from there on), then the vote banner at the
                // native offset, the training callouts and the ladder's roll pointer. The
                // sector/turret/order/mission/lifepod column itself is drawn last so its rows
                // sit over the reserved band they measured.
                DrawTrainingCallout(model, w, h, scale);
                // The F4 inventory pane is the modal overlay and owns the band the roll
                // pointer sits in, so the pointer yields to it (like the WIP tag).
                if (!model.Context.InventoryVisible && model.Context.InventorySlide <= 0f)
                    DrawRollPointer(w, h, scale);
                DrawConsoleColumn(model, w, h, scale);
                DrawVote(model, w, scale);
                DrawControlHint(model, w, scale);

                DrawTag(model, w, h, scale);
            }
            finally
            {
                _surface.End();
            }
        }

        _resources.PodDeath(model);
    }

    /// <summary>P1040 — the Jet command composition: the native command-camera chrome
    /// (NORTH marker / drag rectangle, supplied by the host through
    /// <see cref="HudSkinResources.CommandChrome"/>) plus the skin's own vector <c>CMD</c>
    /// console and the command-view ship summary.</summary>
    public void DrawCommand(HudModel model)
    {
        _resources.CommandChrome?.Invoke(model);
        DrawCommandConsole(model);
    }

    /// <summary>P1040 — the Jet console composition. Before this story the skin forwarded
    /// chat/cargo/flight-parts to the Classic passes; it now draws its own chat pane (history,
    /// leader bold, scroll/channels, configured height, five compose states) and the two
    /// shared bottom-left lines in Jet chrome, so no Classic art is drawn over the Jet
    /// vector panes.</summary>
    public void DrawConsole(HudModel model) => DrawConsoleJet(model);

    /// <summary>The shared external radar renderer is Game1's, not ours; forward the
    /// one-time icon attachment exactly like the Classic adapter does.</summary>
    public void SetRadarIcons(RadarIconTextures? icons) => _radarIcons?.Invoke(icons);

    /// <summary>Overlay text inside the caller's active SpriteBatch session
    /// (Game1 begins it), with Jet's keyline under-stroke.</summary>
    public Rectangle DrawOverlayText(string text, Vector2 position, Color color)
    {
        string printable = HudFormat.Printable(text);
        float scale = ModernHudScale.Core(_displayScale, _settings.Size, _viewportHeight);
        _painter.Scale = scale;
        Vector2 size = _painter.Measure(printable, large: false, scale);
        _painter.Text(printable, position, large: false, scale, color);
        return new Rectangle((int)position.X, (int)position.Y,
            (int)MathF.Ceiling(size.X), (int)MathF.Ceiling(size.Y));
    }

    // ── Flight symbology (P1036) ─────────────────────────────────────────
    /// <summary>A small BROKEN cross (P972 §4.2): four arms with a centre gap, no
    /// lead point and no centre dot, so it can never be mistaken for the
    /// flight-path marker or the ballistic lead. Drawn only under the native
    /// internal/chase/missile camera gate the shared model applies
    /// (WinTrek/indicator.cpp:762-797) — the cockpit reticle itself is native
    /// indicator.cpp:149-150.</summary>
    private void DrawGunCross(Vector2 center, float scale)
    {
        float gap = 6f * scale;
        float arm = 17f * scale;
        float stroke = _painter.Stroke;
        Color ink = _painter.Primary;
        _painter.Line(new(center.X - gap - arm, center.Y), new(center.X - gap, center.Y), stroke, ink);
        _painter.Line(new(center.X + gap, center.Y), new(center.X + gap + arm, center.Y), stroke, ink);
        _painter.Line(new(center.X, center.Y - gap - arm), new(center.X, center.Y - gap), stroke, ink);
        _painter.Line(new(center.X, center.Y + gap), new(center.X, center.Y + gap + arm), stroke, ink);
    }

    /// <summary>
    /// The round flight-path marker (P972 §4.2): a circle with two side wings and a
    /// vertical tail, drawn at the port's existing native velocity projection
    /// (see <see cref="JetFlightGeometry.FlightPathMarkerVisible"/>). The circle and
    /// the wings keep it distinct from the broken gun cross and from the ballistic
    /// lead marker; it is hidden exactly when the native velocity indicator is
    /// hidden.
    /// </summary>
    private void DrawFlightPathMarker(Vector2 position, float w, float h, float scale)
    {
        if (position.X < 0f || position.Y < 0f || position.X > w || position.Y > h) return;
        float radius = 7f * scale;
        float wing = 12f * scale;
        float tail = 9f * scale;
        float stroke = _painter.Stroke;
        Color ink = _painter.Primary;
        _painter.Circle(position, radius, stroke, ink);
        _painter.Line(new(position.X - radius - wing, position.Y), new(position.X - radius, position.Y), stroke, ink);
        _painter.Line(new(position.X + radius, position.Y), new(position.X + radius + wing, position.Y), stroke, ink);
        _painter.Line(new(position.X, position.Y - radius - tail), new(position.X, position.Y - radius), stroke, ink);
    }

    /// <summary>The half-width of the centre gap in the horizon (0°) rung, in
    /// standard-stroke units: wide enough for both gun-cross arms (23 × scale from
    /// the boresight) with a margin, so the split horizon line never covers the
    /// cross or the flight-path marker (P1036 review follow-up: real HUDs draw a
    /// split horizon line).</summary>
    private const float HorizonGapHalfWidth = 27f;

    /// <summary>
    /// The sector-plane pitch ladder (P972 §4.2): 10° labelled rungs, with the
    /// optional 5° refinement as short unlabelled ticks at ≥1080p (it stays
    /// uncluttered there; labels remain on the 10° marks), centred on the
    /// boresight, rolled by the ship's bank, dashed below the reference plane,
    /// numeric labels that stay horizontal, and off-scale chevrons when the ladder
    /// runs past ±80°. At 800×480 the span shrinks to ±20° and the 5° ticks are
    /// dropped (P972 §4.5).
    /// <para>P1036 review follow-up: the 0° rung is <b>never</b> skipped and is
    /// drawn as a split horizon line with a centre gap wide enough for the gun
    /// cross and the FPM, so level flight always shows the horizon. Only the other
    /// rungs keep the boresight skip.</para>
    /// </summary>
    private void DrawPitchLadder(float w, float h, float scale)
    {
        if (!_attitude.Current.Available) return;
        float pitch = _attitude.Current.PitchDegrees;
        float roll = _attitude.Current.RollDegrees;
        bool compact = h < CompactHeight;
        bool fineRungs = h >= 1000f;                 // 5° ticks at 1080p and above
        float span = compact ? JetFlightGeometry.CompactLadderHalfSpanDegrees
            : JetFlightGeometry.LadderHalfSpanDegrees;
        float pxPerDegree = JetFlightGeometry.LadderPixelsPerDegree * scale;
        float halfWidth = (compact ? 28f : 46f) * scale;
        float angle = -roll / JetFlightGeometry.RadiansToDegrees;
        float cosA = MathF.Cos(angle), sinA = MathF.Sin(angle);
        Vector2 center = new(w * 0.5f, h * 0.5f);
        float stroke = _painter.Stroke;
        _painter.Element("ladder");
        // P1040c — Jet yields to the shared training callout: while a callout is up, any rung,
        // rung numeral or the current-pitch caption whose INK would cross the callout's measured
        // text block is not drawn this frame (the callout's anchor is native and may not move).
        // Each stroke is tested with its full INK width (the line plus its keyline allowance),
        // which is exactly what the layout audit measures a crossing with, so a stroke whose
        // centre line only grazes the block is withdrawn too.
        bool yields = _trainingBox.Width > 0f && _trainingBox.Height > 0f;
        RectangleF calloutAvoid = yields ? CalloutAvoid(stroke) : default;
        RectangleF calloutAvoidTick = yields ? CalloutAvoid(_painter.Hairline) : default;

        int limit = (int)JetFlightGeometry.LadderLimitDegrees;
        int step = fineRungs ? (int)JetFlightGeometry.LadderFineStepDegrees
            : (int)JetFlightGeometry.LadderStepDegrees;
        for (int rungPitch = -limit; rungPitch <= limit; rungPitch += step)
        {
            if (rungPitch - pitch < -span || rungPitch - pitch > span) continue;
            bool horizon = rungPitch == 0;
            // The rung nearest the current pitch lands on the boresight and would
            // cover the gun cross and the flight-path marker; the pitch numeral
            // below the ladder carries that value instead. The horizon rung is
            // the exception: level flight must always show it, so it is split
            // around the boresight instead of skipped.
            if (!horizon && MathF.Abs(rungPitch - pitch) * pxPerDegree < 24f * scale) continue;
            bool major = rungPitch % (int)JetFlightGeometry.LadderStepDegrees == 0;
            Vector2 at = center + JetFlightGeometry.LadderOffset(pitch, rungPitch, roll, pxPerDegree);
            float width = horizon ? MathF.Max(halfWidth, HorizonGapHalfWidth * scale + 24f * scale) : halfWidth;
            Vector2 half = new(cosA * width, sinA * width);
            if (!major)
            {
                // 5° refinement tick: short, dim, never labelled.
                Vector2 shortHalf = new(cosA * halfWidth * 0.45f, sinA * halfWidth * 0.45f);
                if (yields && JetFlightGeometry.SegmentHitsBox(at - shortHalf, at + shortHalf, calloutAvoidTick))
                    continue;
                _painter.Line(at - shortHalf, at + shortHalf, _painter.Hairline, _painter.Dim);
                continue;
            }
            bool belowPlane = rungPitch < 0;
            Color ink = belowPlane ? _painter.Dim : _painter.Primary;
            if (horizon)
            {
                // Split horizon line: solid, with a centre gap for the gun cross
                // and the FPM (the gap stays centred on the ladder axis, i.e. the
                // boresight column, at every pitch).
                Vector2 gap = new(cosA * HorizonGapHalfWidth * scale, sinA * HorizonGapHalfWidth * scale);
                if (yields && (JetFlightGeometry.SegmentHitsBox(at - half, at - gap, calloutAvoid)
                    || JetFlightGeometry.SegmentHitsBox(at + gap, at + half, calloutAvoid)))
                    continue;
                _painter.Line(at - half, at - gap, stroke, ink);
                _painter.Line(at + gap, at + half, stroke, ink);
            }
            else
            {
                if (yields && JetFlightGeometry.SegmentHitsBox(at - half, at + half, calloutAvoid)) continue;
                if (belowPlane)
                    DashedLine(at - half, at + half, stroke, ink);   // dashed below the sector plane
                else
                    _painter.Line(at - half, at + half, stroke, ink);
            }
            // Labels stay horizontal even when the ladder rolls.
            string label = RungLabels[(rungPitch + limit) / (int)JetFlightGeometry.LadderStepDegrees];
            Vector2 size = _painter.Measure(label, large: false, scale);
            float leftX = at.X - width - size.X - 4f * scale;
            float rightX = at.X + width + 4f * scale;
            float labelY = at.Y - size.Y * 0.5f;
            if (!yields || !JetRadarLabels.Intersects(new RectangleF(leftX, labelY, size.X, size.Y), _trainingBox))
            {
                _painter.Text(label, new(leftX, labelY), large: false, scale, ink);
                // P1039 — every numeral is fixed furniture (a readout may not be placed over it).
                AddFurniture(new RectangleF(leftX, labelY, size.X, size.Y));
            }
            if (!yields || !JetRadarLabels.Intersects(new RectangleF(rightX, labelY, size.X, size.Y), _trainingBox))
            {
                _painter.Text(label, new(rightX, labelY), large: false, scale, ink);
                AddFurniture(new RectangleF(rightX, labelY, size.X, size.Y));
            }
        }

        // Off-scale chevrons: the ladder has run past its last usable rung.
        if (JetFlightGeometry.LadderOffScaleTop(pitch, span))
            Chevron(center + JetFlightGeometry.LadderOffset(pitch, pitch + span, roll, pxPerDegree),
                angle, up: true, scale);
        if (JetFlightGeometry.LadderOffScaleBottom(pitch, span))
            Chevron(center + JetFlightGeometry.LadderOffset(pitch, pitch - span, roll, pxPerDegree),
                angle, up: false, scale);

        // Current pitch numeral below the ladder (presentation only).
        int roundedPitch = (int)MathF.Round(pitch);
        if (roundedPitch != _pitchShown)
        {
            _pitchShown = roundedPitch;
            _pitchText = PitchCaption + " " + FormatSigned(roundedPitch);
        }
        Vector2 pitchSize = _painter.Measure(_pitchText, large: false, scale);
        float pitchX = center.X - pitchSize.X * 0.5f;
        float pitchY = center.Y + span * pxPerDegree + 20f * scale;
        // P1040c — the caption yields to a shared training callout like the rungs do.
        if (yields && JetRadarLabels.Intersects(new RectangleF(pitchX, pitchY, pitchSize.X, pitchSize.Y), _trainingBox))
            return;
        _painter.Text(_pitchText, new(pitchX, pitchY), large: false, scale, _painter.Dim);
        AddFurniture(new RectangleF(pitchX, pitchY, pitchSize.X, pitchSize.Y));
    }

    /// <summary>P1040c — the training callout's block inflated by the full ink width of a
    /// <paramref name="width"/>-px stroke (half the line plus its keyline allowance), so a
    /// ladder mark whose centre line only grazes the callout is withdrawn as well.</summary>
    private RectangleF CalloutAvoid(float width)
    {
        float ink = MathF.Max(width, _painter.Hairline);
        float pad = ink * 0.5f + _painter.LineKeylineAllowance(ink);
        return new RectangleF(_trainingBox.X - pad, _trainingBox.Y - pad,
            _trainingBox.Width + 2f * pad, _trainingBox.Height + 2f * pad);
    }

    /// <summary>One off-scale chevron: two strokes forming a "V" along the ladder's
    /// rolled axis, pointing away from the drawn rungs.</summary>
    private void Chevron(Vector2 at, float angle, bool up, float scale)
    {
        float arm = 8f * scale;
        float spread = 7f * scale;
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        float tipY = up ? -arm : arm;
        float sideY = up ? arm * 0.25f : -arm * 0.25f;
        // (x·cos − y·sin, x·sin + y·cos)
        Vector2 tip = new(at.X - tipY * sin, at.Y + tipY * cos);
        Vector2 left = new(at.X - spread * cos - sideY * sin, at.Y - spread * sin + sideY * cos);
        Vector2 right = new(at.X + spread * cos - sideY * sin, at.Y + spread * sin + sideY * cos);
        _painter.Line(left, tip, _painter.Stroke, _painter.Primary);
        _painter.Line(tip, right, _painter.Stroke, _painter.Primary);
    }

    /// <summary>
    /// The heading tape across the top (P972 §4.2): 10° ticks, 20° numeric marks, a
    /// caret at the current heading and a boxed numeric readout. Inside the vertical
    /// singularity the state holder keeps the last valid heading but marks it
    /// unavailable, so the readout becomes <c>HDG --</c> instead of jumping.
    /// <para>P1036 review follow-up: the whole block is laid out top-down from the
    /// measured label height, so no label (or its keyline) can touch the top screen
    /// edge, and the caret sits in the gap between the rail and the readout plate
    /// instead of inside the <c>HDG</c> box.</para>
    /// </summary>
    private void DrawHeadingTape(float w, float h, float scale)
    {
        float cx = w * 0.5f;
        // Label box, rail, ticks, caret and plate stack with explicit gaps.
        float labelH = _painter.Measure("000", large: false, scale).Y;
        float labelTop = MathF.Max(6f, 5f * scale) + _painter.KeylineWidth;
        float y = labelTop + labelH + 3f * scale;
        float caretApex = y + 7f * scale;
        float caretFoot = y + 13f * scale;
        float boxTop = y + 17f * scale;
        float pxPerDegree = JetFlightGeometry.HeadingTapeScale(w);
        bool haveHeading = _attitude.Current.Available && _attitude.Current.HeadingValid;
        float halfSpanPx = MathF.Max(0f, MathF.Min(cx - 70f * scale, 340f * scale));
        Color caret = haveHeading ? _painter.Primary : _painter.Dim;
        _painter.Element("hdg");
        // P1039 — the whole heading band (labels, rail, caret, plate) is furniture.
        AddFurniture(new RectangleF(cx - halfSpanPx, labelTop, halfSpanPx * 2f,
            MathF.Max(labelH, boxTop + labelH * 2f - labelTop)));

        if (haveHeading)
        {
            float heading = _attitude.Current.HeadingDegrees;
            float halfSpanDegrees = halfSpanPx / pxPerDegree;

            // Horizontal tape rail.
            _painter.Line(new(cx - halfSpanPx, y), new(cx + halfSpanPx, y), _painter.Hairline, _painter.Dim);

            int first = (int)MathF.Ceiling((heading - halfSpanDegrees) / JetFlightGeometry.HeadingTickStep)
                * (int)JetFlightGeometry.HeadingTickStep;
            int last = (int)MathF.Floor((heading + halfSpanDegrees) / JetFlightGeometry.HeadingTickStep)
                * (int)JetFlightGeometry.HeadingTickStep;
            for (int degrees = first; degrees <= last; degrees += (int)JetFlightGeometry.HeadingTickStep)
            {
                float x = cx + (degrees - heading) * pxPerDegree;
                if (x < 24f * scale || x > w - 24f * scale) continue;
                int normalized = ((degrees % 360) + 360) % 360;
                bool major = normalized % (int)JetFlightGeometry.HeadingLabelStep == 0;
                _painter.Line(new(x, y), new(x, y + (major ? 6f : 3f) * scale), _painter.Hairline,
                    major ? _painter.Primary : _painter.Dim);
                if (major)
                {
                    string text = HeadingLabels[normalized / (int)JetFlightGeometry.HeadingLabelStep];
                    Vector2 size = _painter.Measure(text, large: false, scale);
                    _painter.Text(text, new(x - size.X * 0.5f, labelTop + (labelH - size.Y) * 0.5f),
                        large: false, scale, _painter.Dim);
                }
            }

            int rounded = (int)MathF.Round(heading) % 360;
            if (rounded < 0) rounded += 360;
            if (rounded != _headingShown)
            {
                _headingShown = rounded;
                _headingText = HeadingCaption + rounded.ToString("000", CultureInfo.InvariantCulture);
            }
        }
        else
        {
            _headingShown = int.MinValue;
            _headingText = HeadingUnavailable;
        }

        // Caret in the gap between the rail ticks and the plate, pointing up at the
        // current heading (never inside the numeric box).
        _painter.Line(new(cx - 5f * scale, caretFoot), new(cx, caretApex), _painter.Stroke, caret);
        _painter.Line(new(cx, caretApex), new(cx + 5f * scale, caretFoot), _painter.Stroke, caret);

        // Boxed numeric below the caret, top-anchored so the gap is guaranteed.
        _headingPlate = DrawReadout(_headingText, cx, boxTop, scale, ReadoutAlign.Center, topAnchored: true);
        AddFurniture(_headingPlate);
    }

    /// <summary>
    /// The left SPEED tape (P972 §4.2): a fixed moving tape of 20 mps ticks with
    /// 40 mps numeric marks and a boxed numeric readout at the boresight row. The
    /// value is the shared model's speed (a lifepod reads the same field).
    /// <para>P1036 review follow-up: the caption owns the band above the tape, so
    /// no tick label may rise into it, and the rail is clipped to the ticks actually
    /// drawn instead of running past the top of its scale.</para>
    /// </summary>
    private void DrawSpeedTape(float speed, float w, float h, float scale)
    {
        float tapeX = 76f * scale;
        float cy = h * 0.5f;
        float halfSpan = h * 0.30f;
        float perMps = JetFlightGeometry.SpeedTapeScale(h);
        float stroke = _painter.Hairline;
        float labelH = _painter.Measure("000", large: false, scale).Y;
        // The first row a tick/label may occupy: below the caption row.
        float tapeTop = cy - halfSpan + labelH + 3f * scale;
        float tapeBottom = cy + halfSpan;

        _speedCaptionBox = DrawCaption(SpeedCaption, new(tapeX - 8f * scale, cy - halfSpan), scale);
        _painter.Element("spd");
        // P1039 — the caption and the tick-label column are fixed furniture.
        AddFurniture(_speedCaptionBox);
        AddFurniture(new RectangleF(tapeX - 40f * scale, tapeTop, 40f * scale, MathF.Max(0f, tapeBottom - tapeTop)));

        float visible = halfSpan / perMps;
        int from = (int)MathF.Max(0f, MathF.Floor((speed - visible) / JetFlightGeometry.SpeedTickStep))
            * (int)JetFlightGeometry.SpeedTickStep;
        int to = (int)(speed + visible);
        int labelSlot = 0;
        float firstY = float.NaN, lastY = float.NaN;
        for (int value = from; value <= to; value += (int)JetFlightGeometry.SpeedTickStep)
        {
            float y = cy - (value - speed) * perMps;
            if (y < tapeTop || y > tapeBottom) continue;    // clip to the drawn scale
            bool major = value % (int)JetFlightGeometry.SpeedLabelStep == 0;
            _painter.Line(new(tapeX, y), new(tapeX + (major ? 7f : 4f) * scale, y), stroke,
                major ? _painter.Primary : _painter.Dim);
            if (major)
            {
                string text = SpeedTickText(value, labelSlot++ % SpeedTickCache);
                Vector2 size = _painter.Measure(text, large: false, scale);
                _painter.Text(text, new(tapeX - 6f * scale - size.X, y - size.Y * 0.5f),
                    large: false, scale, _painter.Dim);
            }
            if (float.IsNaN(firstY) || y < firstY) firstY = y;
            if (float.IsNaN(lastY) || y > lastY) lastY = y;
        }

        // Rail spans exactly the drawn ticks (no stub past the scale ends).
        if (!float.IsNaN(firstY))
            _painter.Line(new(tapeX, firstY), new(tapeX, lastY), stroke, _painter.Dim);

        // Boxed numeric at the boresight row, right of the tape.
        int rounded = (int)MathF.Round(speed);
        if (rounded != _speedShown) { _speedShown = rounded; _speedText = rounded.ToString(CultureInfo.InvariantCulture); }
        // P1040 — the plate's measured box is furniture: the console column starts right of
        // it so no console row can strike through the speed readout.
        _speedPlate = DrawReadout(_speedText, tapeX + 10f * scale, cy, scale, ReadoutAlign.LeftEdge);
        AddFurniture(_speedPlate);
    }

    /// <summary>
    /// The right THROTTLE tape (P972 §4.2): the same −1…+1 throttle the shared model
    /// maps to <c>(throttle+1)/2</c> (HudModel.cs:421, native
    /// <c>jsValues[c_axisThrottle]*0.5+0.5</c> WinTrek/WinTrek.cpp:8466), so idle is
    /// 50 %, full forward 100 % and the turret's forced −1 reads 0 %. The idle
    /// detent is a doubled tick at 50 % and the reverse band below it is a dashed dim
    /// rail with a REV label, so the numeric percent cannot conceal the detent.
    /// Afterburner and movement-button overrides stay in the shared model — the tape
    /// just reads the resulting value.
    /// </summary>
    private void DrawThrottleTape(float throttle, float w, float h, float scale)
    {
        float tapeX = w - 76f * scale;
        float cy = h * 0.5f;
        float halfSpan = h * 0.30f;
        float perPercent = JetFlightGeometry.ThrottleTapeScale(h);
        float stroke = _painter.Hairline;
        bool compact = h < CompactHeight;
        float labelH = _painter.Measure("000", large: false, scale).Y;
        // The first row a tick/label may occupy: below the caption row.
        float tapeTop = cy - halfSpan + labelH + 3f * scale;
        float tapeBottom = cy + halfSpan;

        int percent = JetFlightGeometry.ThrottlePercent(throttle);

        _throttleCaptionBox = DrawCaption(ThrottleCaption, new(tapeX + 8f * scale, cy - halfSpan), scale, rightAligned: true);
        _painter.Element("thr");
        AddFurniture(_throttleCaptionBox);
        AddFurniture(new RectangleF(tapeX + 4f * scale, tapeTop, 40f * scale, MathF.Max(0f, tapeBottom - tapeTop)));

        // The boxed readout owns the left margin at the boresight row; IDLE/REV
        // are right-aligned clear of it so the plate can never cover a word
        // (P1036 review follow-up: the boxed percent used to sit on top of REV).
        if (percent != _throttleShown)
        {
            _throttleShown = percent;
            _throttleText = percent.ToString(CultureInfo.InvariantCulture) + "%";
        }
        float readoutWidth = _painter.Measure(_throttleText, true, scale).X + 18f * scale;
        float wordRight = tapeX - 10f * scale - readoutWidth - 6f * scale;

        // Reverse band (0…50 %) as a dashed dim rail below the idle detent.
        float reverseTop = cy - (50f - percent) * perPercent;
        float reverseBottom = cy - (0f - percent) * perPercent;
        DashedLine(new(tapeX - 5f * scale, reverseTop), new(tapeX - 5f * scale, reverseBottom), stroke, _painter.Dim);
        if (!compact)
        {
            // REV and IDLE share the outer left margin, clear of the readout box.
            Vector2 reverseSize = _painter.Measure(ReverseLabel, large: false, scale);
            _painter.Text(ReverseLabel,
                new(wordRight - reverseSize.X,
                    cy - (25f - percent) * perPercent - reverseSize.Y * 0.5f),
                large: false, scale, _painter.Dim);
        }

        float visible = halfSpan / perPercent;
        int from = (int)MathF.Max(0f, MathF.Floor((percent - visible) / JetFlightGeometry.ThrottleTickStep))
            * (int)JetFlightGeometry.ThrottleTickStep;
        int to = (int)MathF.Min(100f, percent + visible);
        float firstY = float.NaN, lastY = float.NaN;
        for (int value = from; value <= to; value += (int)JetFlightGeometry.ThrottleTickStep)
        {
            float y = cy - (value - percent) * perPercent;
            if (y < tapeTop || y > tapeBottom) continue;    // clip to the drawn scale
            bool major = value % (int)JetFlightGeometry.ThrottleLabelStep == 0;
            _painter.Line(new(tapeX - (major ? 7f : 4f) * scale, y), new(tapeX, y), stroke,
                major ? _painter.Primary : _painter.Dim);
            if (major)
            {
                string text = ThrottleLabels[value / (int)JetFlightGeometry.ThrottleLabelStep];
                Vector2 size = _painter.Measure(text, large: false, scale);
                _painter.Text(text, new(tapeX + 10f * scale, y - size.Y * 0.5f), large: false, scale, _painter.Dim);
            }
            if (float.IsNaN(firstY) || y < firstY) firstY = y;
            if (float.IsNaN(lastY) || y > lastY) lastY = y;
        }

        // Rail spans exactly the drawn ticks: at 800×480 it no longer continues
        // below the 000 mark (the low end is clipped to the scale).
        if (!float.IsNaN(firstY))
            _painter.Line(new(tapeX, firstY), new(tapeX, lastY), stroke, _painter.Dim);

        // Idle detent: a doubled tick across the tape at 50 %.
        float idleY = cy - (50f - percent) * perPercent;
        _painter.Line(new(tapeX - 9f * scale, idleY - 1f * scale), new(tapeX + 9f * scale, idleY - 1f * scale),
            _painter.Stroke, _painter.Primary);
        _painter.Line(new(tapeX - 9f * scale, idleY + 1f * scale), new(tapeX + 9f * scale, idleY + 1f * scale),
            _painter.Stroke, _painter.Primary);
        if (!compact)
        {
            Vector2 idleSize = _painter.Measure(IdleLabel, large: false, scale);
            _painter.Text(IdleLabel, new(wordRight - idleSize.X, idleY - idleSize.Y * 0.5f),
                large: false, scale, _painter.Dim);
        }

        RectangleF throttlePlate = DrawReadout(_throttleText, tapeX - 10f * scale, cy, scale, ReadoutAlign.RightEdge);
        // P1039 — the radar scope ends left of this whole band (plate + IDLE/REV words).
        float bandLeft = throttlePlate.X;
        if (!compact)
        {
            float idleW = _painter.Measure(IdleLabel, large: false, scale).X;
            float revW = _painter.Measure(ReverseLabel, large: false, scale).X;
            bandLeft = MathF.Min(bandLeft, wordRight - MathF.Max(idleW, revW));
        }
        _throttleBandLeft = bandLeft;
        AddFurniture(throttlePlate);
    }

    // ── Target, lock, lead and order steering (P1037) ────────────────────
    /// <summary>
    /// The in-view target designator (P972 §4.3): four corner strokes sized from the
    /// shared model's projected <c>ScreenRadius</c> (clamped to
    /// <see cref="JetTargetGeometry.MinimumHalfPx"/>…<see cref="JetTargetGeometry.MaximumHalfPx"/>
    /// and to the viewport edge), with the relationship, name, range, closure and
    /// hull/shield readouts beside it. The relationship is carried by corner pattern
    /// and the word — enemy = solid corners + inward ticks + <c>HOSTILE</c>, friend =
    /// dashed corners + <c>FRIEND</c>, non-ship = plain corners + <c>NEUTRAL</c> — and
    /// never by a second hue (P972 §4.1).
    /// </summary>
    private void DrawTargetDesignator(HudModel model, float w, float h, float scale)
    {
        if (!model.FlightVisible || !model.Context.SelectionBracket || model.Context.Target is not { } target) return;
        _painter.Element("target");
        float cx = target.Center.X, cy = target.Center.Y;
        if (cx < 0f || cy < 0f || cx > w || cy > h) return;

        float toEdge = MathF.Min(MathF.Min(cx, cy), MathF.Min(w - cx, h - cy));
        float half = JetTargetGeometry.DesignatorHalf(target.ScreenRadius, scale, toEdge);
        // P1038c — the designator keeps its native size (P1038b rule) and the lock cue is
        // a compact four-corner arc set that hugs its corners (DrawLock); the readout clears
        // the cue's axis-aligned extent, which is now much smaller than the old ring radius.
        float cueExtent = LockCueExtentAt(model, cx, cy, half, scale);
        JetTargetGeometry.TargetRelationship relationship = JetTargetGeometry.Relationship(target.IsEnemy);
        // Engaged autopilot (the shared model's P345 flag) brightens the designator;
        // the shape stays identical.
        Color ink = target.Engaged ? _painter.Primary : _painter.Dim;
        var box = new RectangleF(cx - half, cy - half, half * 2f, half * 2f);
        float arm = MathF.Min(half * 0.5f, 20f * scale);
        DrawDesignatorFrame(box, arm, relationship, ink, scale);

        // Precise aim point: a tiny plus (never the lock ring or the gun cross).
        float dot = 2f * scale;
        _painter.Line(new(cx - dot, cy), new(cx + dot, cy), _painter.Stroke, ink);
        _painter.Line(new(cx, cy - dot), new(cx, cy + dot), _painter.Stroke, ink);

        // Readout beside the bracket. P1039 — the block is PLACED by the priority order
        // (right → left → below → above, clamped in the viewport gutter, clear of the fixed
        // furniture and of the lock caption), never nudged by a pixel offset. At 800×480 the
        // name is truncated to the stated compact width with an ASCII ellipsis.
        string relationshipWord = JetTargetGeometry.RelationshipWord(relationship);
        string? name = target.Name;
        bool compactReadout = JetStoresPanel.Compact(h);
        if (compactReadout && name is not null)
            name = FitCompactName(name, JetTargetGeometry.CompactNameWidth * scale, scale);
        string? range = target.Distance is { } distance ? RangeText(distance) : null;
        string? closure = target.ClosingSpeed is { } closing ? ClosureText(closing) : null;
        string? hull = target.HullFraction is { } hullFraction && hullFraction > 0f
            ? PercentText(hullFraction, ref _hullShown, ref _hullText) : null;
        string? shield = target.ShieldFraction is { } shieldFraction && shieldFraction > 0f
            ? PercentText(shieldFraction, ref _shieldShown, ref _shieldText) : null;

        float rowH = _painter.Measure("X", large: false, scale).Y + 2f * scale;
        float row1 = MeasureRuns(scale, " / ", relationshipWord, name, null, null);
        float row2 = MeasureRuns(scale, "  ", range, range is null ? null : ClosureWord, closure, null);
        float row3 = MeasureRuns(scale, "  ", hull is null ? null : HullWord, hull,
            shield is null ? null : ShieldWord, shield);
        float textWidth = MathF.Max(row1, MathF.Max(row2, row3));
        int blockRows = 1 + (range is not null || closure is not null ? 1 : 0)
            + (hull is not null || shield is not null ? 1 : 0);
        bool hasBars = hull is not null || shield is not null;
        float barBlock = 2f * (3f * scale) + 2f * scale;
        float blockHeight = rowH * blockRows + (hasBars ? barBlock : 0f);
        // The block clears the designator half-span AND the lock cue's axis extent.
        float clear = MathF.Max(half, cueExtent);
        JetReadoutPlacement.Placement placed = JetReadoutPlacement.Place(
            JetReadoutPlacement.DesignatorOrder, cx, cy, box.Y, clear, textWidth, blockHeight,
            scale, w, h, Furniture, default);
        // P1039b — when NO slot beside the target is free the readout does not fall through to
        // wherever the viewport clamp lands (P1039's 800×480 defect: `HOSTILE En …` parked in
        // the top-left corner beside the heading tape). It takes the fixed, LABELLED target-data
        // slot in the reserved bottom-left band (JetReadoutPlacement.TargetSlot): clear of the
        // heading tape, the stores block, the master-caution panel, the boresight column and the
        // radar scope by construction. At the compact window the band is only as wide as the
        // stores-to-ladder gap, so the hull and shield readings take one line each — no field is
        // dropped, the block just gets narrower.
        bool tagged = !placed.Clear;
        bool splitBars = tagged && compactReadout && hasBars;
        float blockWidth = textWidth;
        // P1039c — DUPLICATE TARGET DATA. The bottom-centre block (DrawTargetBlock, the native
        // target pane's rows) already prints the target's name, range/LOC, closure and
        // hull/shield. When no slot beside the target is clear the designator readout falls to
        // the TGT slot, and it must not print those same readings a second time: it prints only
        // what the pane LACKS — always the relationship word (HOSTILE/FRIEND/NEUTRAL, which the
        // pane never carries), plus any row whose value the pane itself has no data for. Native
        // shows the target data once (the pane); a clear slot beside the designator keeps the
        // P1037 readout, the fallback never repeats the pane.
        bool paneCarries = model.TargetPaneVisible && model.Context.TargetPane is { Present: true };
        bool complement = tagged && paneCarries;
        string? gapName = null, gapRange = null, gapClosure = null, gapHull = null, gapShield = null;
        int complementRows = 1;
        if (complement)
        {
            HudTargetPane pane = model.Context.TargetPane!.Value;
            if (pane.Name is null) gapName = name;
            if (pane.Distance is null && pane.SectorName is null) gapRange = range;
            if (pane.ClosingSpeed is null) gapClosure = closure;
            if (pane.HullFraction is not { } paneHull || paneHull <= 0f) gapHull = hull;
            if (pane.ShieldFraction is not { } paneShield || paneShield <= 0f) gapShield = shield;
            blockWidth = MeasureRuns(scale, " / ", relationshipWord, gapName, null, null);
            blockWidth = MathF.Max(blockWidth, MeasureRuns(scale, "  ", gapRange,
                gapRange is null ? null : ClosureWord, gapClosure, null));
            blockWidth = MathF.Max(blockWidth, MeasureRuns(scale, "  ",
                gapHull is null ? null : HullWord, gapHull,
                gapShield is null ? null : ShieldWord, gapShield));
            complementRows = 1 + (gapRange is not null || gapClosure is not null ? 1 : 0)
                + (gapHull is not null || gapShield is not null ? 1 : 0);
            splitBars = false;
        }
        if (tagged)
        {
            float tagHeight = complement
                ? complementRows * rowH + rowH
                : blockHeight + (splitBars ? rowH : 0f) + rowH;
            placed = JetReadoutPlacement.TargetSlot(w, h, scale, _storesTop, blockWidth, tagHeight);
        }
        float x = placed.X, y = placed.Y;
        _targetPlacement = placed;
        _targetReadoutBox = new RectangleF(x, y, blockWidth, complement
            ? complementRows * rowH + rowH
            : blockHeight + (splitBars ? rowH : 0f) + (tagged ? rowH : 0f));
        if (tagged)
        {
            _painter.Text(TargetBlockWord, new(x, y), large: false, scale, _painter.Dim);
            y += rowH;
        }
        if (complement)
        {
            DrawRuns(x, y, scale, _painter.Primary, " / ", relationshipWord, gapName, null, null);
            y += rowH;
            if (gapRange is not null || gapClosure is not null)
            {
                DrawRuns(x, y, scale, _painter.Dim, "  ", gapRange,
                    gapRange is null ? null : ClosureWord, gapClosure, null);
                y += rowH;
            }
            if (gapHull is not null || gapShield is not null)
                DrawRuns(x, y, scale, _painter.Dim, "  ", gapHull is null ? null : HullWord, gapHull,
                    gapShield is null ? null : ShieldWord, gapShield);
            return;
        }

        DrawRuns(x, y, scale, _painter.Primary, " / ", relationshipWord, name, null, null);
        y += rowH;
        if (range is not null || closure is not null)
        {
            DrawRuns(x, y, scale, _painter.Dim, "  ", range, range is null ? null : ClosureWord, closure, null);
            y += rowH;
        }
        if (hasBars)
        {
            if (splitBars)
            {
                if (hull is not null)
                {
                    DrawRuns(x, y, scale, _painter.Dim, "  ", HullWord, hull, null, null);
                    y += rowH;
                }
                if (shield is not null)
                {
                    DrawRuns(x, y, scale, _painter.Dim, "  ", ShieldWord, shield, null, null);
                    y += rowH;
                }
            }
            else
            {
                DrawRuns(x, y, scale, _painter.Dim, "  ", hull is null ? null : HullWord, hull,
                    shield is null ? null : ShieldWord, shield);
                y += rowH;
            }
            DrawTargetBars(x, y, scale, target.HullFraction, target.ShieldFraction);
        }
    }

    /// <summary>
    /// P1039 — the compact designator name: truncated with an ASCII ellipsis at the stated
    /// width (<see cref="JetTargetGeometry.CompactNameWidth"/> native px × scale) so the
    /// 800×480 readout block fits a slot clear of the ladder numerals. The HUD fonts bake
    /// ASCII 32…126, so <c>...</c> stands in for the typographic ellipsis exactly as the
    /// native radar clips its labels. Cached by the source name: a steady frame formats
    /// nothing.
    /// </summary>
    private string FitCompactName(string name, float maxWidth, float scale)
    {
        if (string.Equals(name, _compactNameShown, StringComparison.Ordinal)) return _compactNameText;
        _compactNameShown = name;
        string text = name;
        if (_painter.Measure(name, large: false, scale).X > maxWidth)
        {
            int length = name.Length;
            while (length > 0 && _painter.Measure(name[..length] + " ...", large: false, scale).X > maxWidth)
                length--;
            text = length > 0 ? name[..length] + " ..." : "";
        }
        _compactNameText = text;
        return text;
    }

    /// <summary>The designator's four corner strokes: solid + inward ticks for a
    /// hostile target, dashed/open for a friend, plain for a non-ship (relationship
    /// by shape and line pattern, P972 §4.1).</summary>
    private void DrawDesignatorFrame(RectangleF box, float arm,
        JetTargetGeometry.TargetRelationship relationship, Color ink, float scale)
    {
        float x0 = box.X, y0 = box.Y, x1 = box.X + box.Width, y1 = box.Y + box.Height;
        if (!JetTargetGeometry.DashedDesignator(relationship))
        {
            _painter.CornerBrackets(box, arm, _painter.Stroke, ink);
            if (relationship != JetTargetGeometry.TargetRelationship.Enemy) return;
            // Hostile: a short inward tick at each corner.
            float tick = MathF.Min(arm * 0.55f, 8f * scale);
            _painter.Line(new(x0, y0), new(x0 + tick, y0 + tick), _painter.Stroke, ink);
            _painter.Line(new(x1, y0), new(x1 - tick, y0 + tick), _painter.Stroke, ink);
            _painter.Line(new(x0, y1), new(x0 + tick, y1 - tick), _painter.Stroke, ink);
            _painter.Line(new(x1, y1), new(x1 - tick, y1 - tick), _painter.Stroke, ink);
            return;
        }
        // Friendly: open (dashed) corner arms — no closed box, no hue change.
        DashedLine(new(x0, y0), new(x0 + arm, y0), _painter.Stroke, ink);
        DashedLine(new(x0, y0), new(x0, y0 + arm), _painter.Stroke, ink);
        DashedLine(new(x1, y0), new(x1 - arm, y0), _painter.Stroke, ink);
        DashedLine(new(x1, y0), new(x1, y0 + arm), _painter.Stroke, ink);
        DashedLine(new(x0, y1), new(x0 + arm, y1), _painter.Stroke, ink);
        DashedLine(new(x0, y1), new(x0, y1 - arm), _painter.Stroke, ink);
        DashedLine(new(x1, y1), new(x1 - arm, y1), _painter.Stroke, ink);
        DashedLine(new(x1, y1), new(x1, y1 - arm), _painter.Stroke, ink);
    }

    /// <summary>Two short monochrome bars under the designator readout: hull (ink)
    /// and shield (dim). Nothing is drawn for an unknown fraction.</summary>
    private void DrawTargetBars(float x, float y, float scale, float? hull, float? shield)
    {
        float barW = 54f * scale;
        float barH = 3f * scale;
        if (hull is { } h && h > 0f) Bar(x, y, barW, barH, h, _painter.Primary);
        if (shield is { } s && s > 0f) Bar(x, y + barH + 2f * scale, barW, barH, s, _painter.Dim);
    }

    /// <summary>One fraction bar: dim rail plus an ink fill of the fraction width.</summary>
    private void Bar(float x, float y, float width, float height, float fraction, Color ink)
    {
        float filled = width * Math.Clamp(fraction, 0f, 1f);
        _painter.Surface.Fill(new(x, y), new(width, height), _painter.Dim * 0.35f);
        if (filled > 0f) _painter.Surface.Fill(new(x, y), new(filled, height), ink);
    }

    /// <summary>P1038 — push a readout block below <paramref name="box"/> when the two would
    /// intersect (the heading plate and the two tape captions are fixed HUD furniture the
    /// target readout must never cover).</summary>
    private static float BelowIfCrossed(float y, float x, float width, float height, RectangleF box, float scale)
    {
        if (box.Width <= 0f) return y;
        const float margin = 2f;
        bool overlaps = x < box.X + box.Width + margin * scale && box.X < x + width + margin * scale
            && y < box.Y + box.Height + margin * scale && box.Y < y + height;
        return overlaps ? box.Y + box.Height + 4f * scale : y;
    }

    /// <summary>P1038c — the lock cue's axis-aligned extent when the shared lock is centred on this
    /// target (in a real frame both the designator and the lock bracket come from the same
    /// projected target radius: <c>Game1.Camera.cs</c> <c>ComputeTargetSelectionBracket</c> /
    /// <c>ComputeLockBracket</c>). Returns 0 when no lock is shown or the cue sits
    /// elsewhere, so the designator keeps its native size and the readout its old offset.
    /// </summary>
    /// <para>The corner arc distance is <c>√2·designatorHalf + clearance + 40·(1−lock)</c>
    /// (native closing, radarimage.cpp:325-332); the axis extent is that distance ×
    /// cos 15° (the arc spans 30° centred on the diagonal), plus one stroke width so a
    /// readout placed at the extent cannot touch the arc ink.</para>
    /// </summary>
    private float LockCueExtentAt(HudModel model, float cx, float cy, float designatorHalf, float scale)
    {
        if (model.Context.Lock is not { } lockState) return 0f;
        if (lockState.State == MissileLockFeedback.LockState.None) return 0f;
        if (MathF.Abs(lockState.Center.X - cx) > 1.5f || MathF.Abs(lockState.Center.Y - cy) > 1.5f) return 0f;
        // P1039 — the readout clears the cue's OUTER RADIUS in every slot, not only its axis
        // extent: a slot that slides can otherwise sit inside a corner arc's diagonal (where
        // the 60° axis gap does not apply).
        float distance = JetTargetGeometry.LockCueCornerDistance(designatorHalf, lockState.Fraction, scale);
        return distance + _painter.Stroke;
    }

    /// <summary>
    /// The off-screen target chevron (P972 §4.3): a chevron on the viewport edge
    /// pointing at a selected target that does not project into view, using the same
    /// normalized bearing the Classic target arrow reads
    /// (<see cref="HudContext.TargetBearing"/>, <c>Game1.ComputeTargetBearing</c>).
    /// The target data block already carries the readings, so no number is repeated
    /// here — only the direction.
    /// </summary>
    private void DrawOffScreenTarget(HudModel model, float w, float h, float scale)
    {
        if (!model.IndicatorsVisible || model.Context.Target is not null) return;
        if (model.Context.TargetBearing is not { } bearing) return;
        if (model.Context.TargetPane is not { Present: true }) return;
        _painter.Element("target");
        Vector2 at = JetTargetGeometry.EdgeChevronAnchor(bearing, w, h, scale);
        var direction = new Vector2(bearing.X, bearing.Y);
        if (direction.LengthSquared() < 1e-6f) direction = new(0f, -1f);
        direction.Normalize();
        var side = new Vector2(-direction.Y, direction.X);
        float len = 11f * scale;
        float spread = 7f * scale;
        Vector2 tip = at + direction * len;
        _painter.Line(at + side - direction * len * 0.4f, tip, _painter.Stroke, _painter.Primary);
        _painter.Line(at - side - direction * len * 0.4f, tip, _painter.Stroke, _painter.Primary);
        // Fixed pivot dot so the chevron reads as an edge pointer.
        _painter.Line(new(at.X - 2f * scale, at.Y), new(at.X + 2f * scale, at.Y), _painter.Hairline, _painter.Dim);
    }

    /// <summary>
    /// The missile lock cue (P972 §4.3 "segmented diamond/arc"): four corner arcs hugging
    /// the native-size designator's diagonals (P1038c). Each arc spans
    /// <see cref="JetTargetGeometry.LockCornerSweep"/> (30°) centred on a diagonal, so the
    /// four arcs cover the four corners and leave a 60° gap at each axis direction where
    /// the readouts live. The distance is
    /// <c>√2·designatorHalf + clearance + 40·scale·(1−lock)</c>, so the native
    /// <c>radiusBracket + 40·(1−lock)</c> closing (radarimage.cpp:325-332) still drives it
    /// and a full lock sits just outside the corners. Partial = every other 7.5° dash
    /// (open/dashed, marching with the shared <c>NowSeconds</c> feedback clock) plus
    /// <c>LOCK nn%</c>; full = the whole 30° arc per corner (closed) in the hot colour plus
    /// <c>LOCK</c>; none = nothing. No lock tone, timer or audio state is created or
    /// restarted here — the tone stays with <see cref="MissileLockFeedback"/> and native
    /// dispatch.
    /// </summary>
    private void DrawLock(HudModel model, float w, float h, float scale)
    {
        if (!model.IndicatorsVisible || model.Context.Lock is not { } lockState) return;
        if (lockState.State == MissileLockFeedback.LockState.None) return;
        var center = new Vector2(lockState.Center.X, lockState.Center.Y);
        if (center.X < 0f || center.Y < 0f || center.X > w || center.Y > h) return;
        _painter.Element("lock");

        float toEdge = MathF.Min(MathF.Min(center.X, center.Y), MathF.Min(w - center.X, h - center.Y));
        // Native radiusBracket = 1.05·radiusObject clipped to the edge (radarimage.cpp:302-319):
        // the SAME native-size half the designator draws with, so the cue hugs its corners.
        float designatorHalf = JetTargetGeometry.DesignatorHalf(lockState.ScreenRadius, scale, toEdge);
        float distance = JetTargetGeometry.LockCueCornerDistance(designatorHalf, lockState.Fraction, scale);
        bool full = lockState.State == MissileLockFeedback.LockState.Full;
        Color ink = full ? _painter.Hot : _painter.Dim;
        float step = JetTargetGeometry.LockCornerSweep / JetTargetGeometry.LockCornerDashes;
        // P1039b — the radar scope is fixed furniture drawn BEFORE this world-anchored cue: an
        // arc that would overwrite the instrument's frame, bands or readings is clipped (native
        // clips radiusBracket at the screen edge the same way, radarimage.cpp:308-319). The cue's
        // radius, state and timing are untouched — only the ink that would land on the scope.
        float clipPad = _painter.Stroke * 2f;
        for (int corner = 0; corner < JetTargetGeometry.LockCornerCount; corner++)
        {
            float angle = JetTargetGeometry.LockCornerAngle(corner);
            float start = angle - JetTargetGeometry.LockCornerSweep * 0.5f;
            if (full)
            {
                if (JetTargetGeometry.ArcEntersRect(center, distance, start,
                        JetTargetGeometry.LockCornerSweep, _radarScope, clipPad)) continue;
                _painter.CueArc(center, distance, start, JetTargetGeometry.LockCornerSweep,
                    _painter.Stroke, ink);
                continue;
            }
            for (int dash = 0; dash < JetTargetGeometry.LockCornerDashes; dash++)
            {
                if (!JetTargetGeometry.LockSegmentVisible(
                        corner * JetTargetGeometry.LockCornerDashes + dash,
                        JetTargetGeometry.LockCornerDashes, lockState.State, lockState.NowSeconds))
                    continue;
                float dashStart = start + dash * step;
                if (JetTargetGeometry.ArcEntersRect(center, distance, dashStart, step,
                        _radarScope, clipPad)) continue;
                _painter.CueArc(center, distance, dashStart, step, _painter.Stroke, ink);
            }
        }

        // Text clear of the cue: P1039 places the caption by the priority order
        // (above → below → right → left), clamped in the viewport, avoiding the cue's own
        // ink AND the designator readout block already committed this frame, so `LOCK nn%`
        // can never land on the `HOSTILE …` line again.
        // P1039 — the caption clears the cue's OUTER RADIUS in every slot (not only its
        // axis extent): a caption that slides to a side would otherwise sit inside a corner
        // arc's diagonal, which is where the 60° axis gap does not apply.
        float extent = distance + _painter.Stroke;
        if (full)
        {
            Vector2 size = _painter.Measure(LockCaption, large: false, scale);
            JetReadoutPlacement.Placement placed = JetReadoutPlacement.Place(
                JetReadoutPlacement.CaptionOrder, center.X, center.Y, center.Y - extent,
                extent, size.X, size.Y, scale, w, h, Furniture, _targetReadoutBox);
            _lockReadoutBox = new RectangleF(placed.X, placed.Y, size.X, size.Y);
            _painter.CueReadout(_lockReadoutBox);
            _painter.Text(LockCaption, new(placed.X, placed.Y), large: false, scale, _painter.Hot);
            return;
        }
        string percent = LockText(lockState.Fraction);
        float rowH = _painter.Measure("X", large: false, scale).Y + 2f * scale;
        float width = MeasureRuns(scale, " ", LockCaption, percent, null, null);
        JetReadoutPlacement.Placement caption = JetReadoutPlacement.Place(
            JetReadoutPlacement.CaptionOrder, center.X, center.Y, center.Y - extent,
            extent, width, rowH, scale, w, h, Furniture, _targetReadoutBox);
        _lockPlacement = caption;
        _lockReadoutBox = new RectangleF(caption.X, caption.Y, width, rowH);
        _painter.CueReadout(_lockReadoutBox);
        DrawRuns(caption.X, caption.Y, scale, _painter.Dim, " ", LockCaption, percent, null, null);
    }

    /// <summary>P1038b/c — the lock caption's y: above the cue's top ink (its axis extent
    /// plus one stroke), or below the cue's bottom ink when the top would leave the
    /// viewport.</summary>
    private static float LockCaptionY(float centerY, float radius, float textHeight, float viewportHeight, float scale)
    {
        float gutter = 4f * scale;
        float above = centerY - radius - textHeight - 4f * scale;
        if (above >= gutter) return above;
        float below = centerY + radius + 4f * scale;
        float bottomLimit = viewportHeight - gutter - textHeight;
        return MathF.Min(MathF.Max(below, gutter), MathF.Max(gutter, bottomLimit));
    }

    /// <summary>
    /// Lead marker + aim predictor (P972 §4.2): the ballistic lead is an open circle
    /// at the solved lead point (closed + <c>IN RANGE</c> when the intercept is in
    /// weapon range, dashed/dim when out of it), the aim predictor is a bracket set
    /// at the cockpit boresight (or the projected aim point in chase). Both map
    /// native's green hit ramp to the one Jet hue's dim → hot; both are drawn only
    /// under the model's own gates, so Jet cannot disagree with Classic about
    /// visibility. They never reuse the gun-cross or FPM shapes.
    /// </summary>
    private void DrawLeadAndAim(HudModel model, float scale)
    {
        if (model.Context.Lead is not { } lead) return;
        _painter.Element("lead");
        // In range the marker is hot (the ramp floored at half, P972 §4.2: "in-range
        // shown by shape/hot colour"); out of range it stays on the dim half of the
        // ramp (native's dimmed leadout surface).
        Color ramp = RampColor(lead.InRange ? MathF.Max(lead.Green, .5f) : lead.Green * .5f);
        if (model.LeadMarkerVisible)
        {
            var center = new Vector2(lead.Center.X, lead.Center.Y);
            float radius = JetTargetGeometry.LeadRadius * scale;
            if (lead.InRange)
            {
                _painter.Circle(center, radius, _painter.Stroke, ramp);
                Vector2 size = _painter.Measure(InRangeLabel, large: false, scale);
                _painter.Text(InRangeLabel, new(center.X - size.X * 0.5f, center.Y + radius + 3f * scale),
                    large: false, scale, ramp);
            }
            else
            {
                // Out of range: open (broken) ring; native's dimmed leadout surface.
                const int segments = 8;
                for (int i = 0; i < segments; i += 2)
                    _painter.Arc(center, radius, i * MathHelper.TwoPi / segments,
                        MathHelper.TwoPi / segments * 0.55f, _painter.Stroke, _painter.Dim);
            }
            // Distinguishing mark from the FPM circle: a top tick.
            _painter.Line(new(center.X, center.Y - radius - 4f * scale),
                new(center.X, center.Y - radius), _painter.Stroke, ramp);
        }

        if (model.AimPredictorVisible)
        {
            // Native cockpit predictor sits on the centre crosshair; in chase/missile
            // it is the projected aim point (indicator.cpp:334-362). Same split as
            // the Classic renderer.
            Vector2 center = model.Context.Camera == CameraMode.Cockpit
                ? new(_viewportWidth * 0.5f, _viewportHeight * 0.5f)
                : new Vector2(lead.Center.X, lead.Center.Y);
            float half = JetTargetGeometry.AimRadius * scale;
            var box = new RectangleF(center.X - half, center.Y - half, half * 2f, half * 2f);
            _painter.CornerBrackets(box, half * 0.55f, _painter.Stroke, ramp);
        }
    }

    /// <summary>Native chase/missile no-target aim (indicator.cpp:367-405): an open
    /// grey cross, distinct from every other Jet marker (P972 §4.2).</summary>
    private void DrawChaseAim(HudModel model, float scale)
    {
        if (!model.ChaseAimVisible || model.Context.ChaseAim is not { } aim) return;
        var center = new Vector2(aim.X, aim.Y);
        float arm = 8f * scale;
        _painter.Line(new(center.X - arm, center.Y), new(center.X - arm * 0.35f, center.Y), _painter.Stroke, _painter.Dim);
        _painter.Line(new(center.X + arm * 0.35f, center.Y), new(center.X + arm, center.Y), _painter.Stroke, _painter.Dim);
        _painter.Line(new(center.X, center.Y - arm), new(center.X, center.Y - arm * 0.35f), _painter.Stroke, _painter.Dim);
        _painter.Line(new(center.X, center.Y + arm * 0.35f), new(center.X, center.Y + arm), _painter.Stroke, _painter.Dim);
    }

    /// <summary>
    /// Accepted/current order steering (P972 §4.2): open chevron + <c>ACC</c> for the
    /// accepted turn, closed chevron + <c>CUR</c> for the current one, and a double
    /// chevron when both name the same target. Directions, anchors
    /// (centre + 108·normalized offset) and the visibility gate are exactly the
    /// shared model's <see cref="HudOrderArrows"/>.
    /// </summary>
    private void DrawOrderArrows(HudModel model, float w, float h, float scale)
    {
        if (!model.OrderArrowsVisible || model.Context.OrderArrows is not { } arrows) return;
        _painter.Element("order");
        var center = new Vector2(w * 0.5f, h * 0.5f);
        if (arrows.SameTarget && arrows.AcceptedDirection is { } same)
        {
            Vector2? direction = JetTargetGeometry.Direction(same);
            if (direction is { } d)
            {
                Vector2 anchor = JetTargetGeometry.OrderAnchor(center, d, scale);
                OpenChevron(anchor, d, scale, _painter.Primary, offset: 0f);
                OpenChevron(anchor, d, scale, _painter.Primary, offset: 5f * scale);
                DrawOrderLabel(anchor, d, scale, SameTargetLabel, _painter.Primary);
            }
            return;
        }
        if (arrows.AcceptedDirection is { } accepted && JetTargetGeometry.Direction(accepted) is { } accDir)
        {
            Vector2 anchor = JetTargetGeometry.OrderAnchor(center, accDir, scale);
            OpenChevron(anchor, accDir, scale, _painter.Primary, offset: 0f);
            DrawOrderLabel(anchor, accDir, scale, AcceptedLabel, _painter.Primary);
        }
        if (arrows.CurrentDirection is { } current && JetTargetGeometry.Direction(current) is { } curDir)
        {
            Vector2 anchor = JetTargetGeometry.OrderAnchor(center, curDir, scale);
            ClosedChevron(anchor, curDir, scale, _painter.Dim);
            DrawOrderLabel(anchor, curDir, scale, CurrentLabel, _painter.Dim);
        }
    }

    /// <summary>An open chevron: two strokes meeting short of the tip (native's
    /// accepted-turn arrow, indicator.cpp:411-457).</summary>
    private void OpenChevron(Vector2 anchor, Vector2 direction, float scale, Color ink, float offset)
    {
        var side = new Vector2(-direction.Y, direction.X);
        Vector2 tip = anchor + direction * (12f * scale + offset);
        Vector2 back = anchor - direction * 4f * scale;
        float spread = 7f * scale;
        _painter.Line(back + side * spread * 0.6f + direction * 2f * scale, tip, _painter.Stroke, ink);
        _painter.Line(back - side * spread * 0.6f + direction * 2f * scale, tip, _painter.Stroke, ink);
    }

    /// <summary>A closed chevron: a filled-outline triangle (native's current-turn
    /// arrow) — closed shape, so it reads apart from the open accepted chevron.
    /// Three strokes, not a collection expression, so a frame allocates nothing.</summary>
    private void ClosedChevron(Vector2 anchor, Vector2 direction, float scale, Color ink)
    {
        var side = new Vector2(-direction.Y, direction.X);
        Vector2 tip = anchor + direction * 12f * scale;
        Vector2 back = anchor - direction * 5f * scale;
        float spread = 6f * scale;
        Vector2 left = back + side * spread;
        Vector2 right = back - side * spread;
        _painter.Line(left, tip, _painter.Stroke, ink);
        _painter.Line(tip, right, _painter.Stroke, ink);
        _painter.Line(right, left, _painter.Stroke, ink);
    }

    /// <summary>The order-arrow word beyond the chevron (ACC / CUR), keylined.</summary>
    private void DrawOrderLabel(Vector2 anchor, Vector2 direction, float scale, string text, Color ink)
    {
        Vector2 at = anchor + direction * 22f * scale;
        Vector2 size = _painter.Measure(text, large: false, scale);
        _painter.Text(text, new(at.X, at.Y), large: false, scale, ink);
        // P1039 — the ACC/CUR word is fixed furniture (a readout may not cover it).
        AddFurniture(new RectangleF(at.X, at.Y, size.X, size.Y));
    }

    /// <summary>
    /// The native bottom target pane as a Jet data block (P972 §4.5, §5.2): name,
    /// type, side, range or the native <c>LOC &lt;sector&gt;</c> fallback, closure,
    /// speed, hull/shield, ore, kill bonus and TTI — each only when the shared model
    /// supplies it, never a zero for an unknown. At 800×480 it collapses to
    /// name + range/closure + the two bars (compact layout).
    /// </summary>
    private void DrawTargetBlock(HudModel model, float w, float h, float scale)
    {
        if (!model.TargetPaneVisible || model.Context.TargetPane is not { } pane || !pane.Present) return;
        _painter.Element("target-pane");
        bool compact = h < CompactHeight;
        float rowH = _painter.Measure("X", large: false, scale).Y + 2f * scale;
        float barH = 3f * scale;
        float barBlock = 2f * barH + 2f * scale;

        // Row content first: rows only exist for the fields the shared model supplies
        // (never a zero for an unknown, never a fabricated row).
        string? range = pane.Distance is { } distance ? RangeText(distance) : null;
        if (range is null && pane.SectorName is { } sector) range = SectorText(sector);
        string? closure = pane.ClosingSpeed is { } closing ? ClosureText(closing) : null;
        string? hull = pane.HullFraction is { } hullFraction && hullFraction > 0f
            ? PercentText(hullFraction, ref _hullShown, ref _hullText) : null;
        string? shield = pane.ShieldFraction is { } shieldFraction && shieldFraction > 0f
            ? PercentText(shieldFraction, ref _shieldShown, ref _shieldText) : null;
        bool hasType = !compact && (pane.TypeDescription is not null || pane.SideName is not null);
        bool hasRange = range is not null || closure is not null;
        bool hasHull = hull is not null || shield is not null;
        bool hasSpeed = !compact && pane.Speed is { } sp && sp > 0f;
        bool hasOre = !compact && pane.OreFraction is { } ore && ore > 0f;
        bool hasKillBonus = !compact && pane.KillBonusPercent is not null;
        bool hasTti = !compact && pane.TimeToIntercept is not null;
        bool hasInRange = !compact && pane.InRange;

        // Lay the block out top-down from a measured height, bottom-anchored above the
        // tag line: every row (and the bar block) has its own slot, so nothing overlaps.
        int rows = (pane.Name is null ? 0 : 1) + (hasType ? 1 : 0) + (hasRange ? 1 : 0) + (hasHull ? 1 : 0)
            + (hasSpeed ? 1 : 0) + (hasOre ? 1 : 0) + (hasKillBonus ? 1 : 0) + (hasTti ? 1 : 0) + (hasInRange ? 1 : 0);
        if (rows == 0) return;
        float height = rows * rowH + (hasHull ? barBlock : 0f);
        // Offset right of the boresight column so the block can never cover the ladder's
        // centred current-pitch numeral (it still reads as the bottom target pane).
        float x = w * 0.5f + 70f * scale;
        float y = h - 34f * scale - height;
        // P1039 — the block's own rows are furniture (no readout may be placed over them).
        // P1039b — the width is the MEASURED widest row: the old fixed 140·scale under-reported
        // a long pilot name (264 px at 800×480), so a readout could be placed over ink the
        // furniture list did not cover.
        float widest = MathF.Max(140f * scale,
            pane.Name is null ? 0f : _painter.Measure(pane.Name, large: false, scale).X);
        if (hasType)
            widest = MathF.Max(widest, MeasureRuns(scale, " / ", pane.TypeDescription, pane.SideName, null, null));
        if (hasRange)
            widest = MathF.Max(widest, MeasureRuns(scale, "  ", range, range is null ? null : ClosureWord, closure, null));
        if (hasHull)
            widest = MathF.Max(widest, MeasureRuns(scale, "  ", hull is null ? null : HullWord, hull,
                shield is null ? null : ShieldWord, shield));
        AddFurniture(new RectangleF(x, y, widest, height));

        if (pane.Name is { } name)
        {
            _painter.Text(name, new(x, y), large: false, scale, _painter.Primary);
            y += rowH;
        }
        if (hasType)
        {
            DrawRuns(x, y, scale, _painter.Dim, " / ", pane.TypeDescription, pane.SideName, null, null);
            y += rowH;
        }
        if (hasRange)
        {
            DrawRuns(x, y, scale, _painter.Primary, "  ", range, range is null ? null : ClosureWord, closure, null);
            y += rowH;
        }
        if (hasHull)
        {
            DrawRuns(x, y, scale, _painter.Dim, "  ", hull is null ? null : HullWord, hull,
                shield is null ? null : ShieldWord, shield);
            y += rowH;
            // Native's 0 is its "not visible / not live" sentinel (consoledata.cpp:202-212),
            // so a bar is only drawn for a real fraction.
            DrawTargetBars(x, y, scale, pane.HullFraction, pane.ShieldFraction);
            y += barBlock;
        }
        if (hasSpeed)
        {
            DrawRuns(x, y, scale, _painter.Dim, "  ", TargetSpeedWord, TargetSpeedText(pane.Speed!.Value), null, null);
            y += rowH;
        }
        if (hasOre)
        {
            DrawRuns(x, y, scale, _painter.Primary, "  ", OreWord,
                PercentText(pane.OreFraction!.Value, ref _oreShown, ref _oreText), null, null);
            y += rowH;
        }
        if (hasKillBonus)
        {
            DrawRuns(x, y, scale, _painter.Dim, "  ", KillBonusWord,
                KillBonusText(pane.KillBonusPercent!.Value), null, null);
            y += rowH;
        }
        if (hasTti)
        {
            _painter.Text(pane.TimeToIntercept!, new(x, y), large: false, scale, _painter.Dim);
            y += rowH;
        }
        // In weapon range: a word, no hue change (P972 §5.2).
        if (hasInRange)
            _painter.Text(InRangeLabel, new(x, y), large: false, scale, _painter.Primary);
    }

    // ── P1037 text helpers (allocation-free on a steady frame) ───────────
    /// <summary>Native hit-probability ramp mapped to the single Jet hue:
    /// <c>green = 0</c> → dim, <c>green = 1</c> → hot (P972 §4.2).</summary>
    private Color RampColor(float green)
    {
        float t = JetTargetGeometry.Ramp(green);
        return Color.Lerp(_painter.Dim, _painter.Hot, t);
    }

    /// <summary>Width of up to four runs joined by <paramref name="separator"/>.</summary>
    private float MeasureRuns(float scale, string separator, string? a, string? b, string? c, string? d)
    {
        float sep = _painter.Measure(separator, large: false, scale).X;
        float width = 0f;
        int count = 0;
        if (a is not null) { width += _painter.Measure(a, large: false, scale).X; count++; }
        if (b is not null) { width += _painter.Measure(b, large: false, scale).X; count++; }
        if (c is not null) { width += _painter.Measure(c, large: false, scale).X; count++; }
        if (d is not null) { width += _painter.Measure(d, large: false, scale).X; count++; }
        return count > 1 ? width + sep * (count - 1) : width;
    }

    /// <summary>Draw up to four runs joined by <paramref name="separator"/> from
    /// <paramref name="x"/> at <paramref name="y"/> (top-left anchored). Unrolled on
    /// purpose: a capturing local function would allocate a display class every
    /// frame.</summary>
    private void DrawRuns(float x, float y, float scale, Color ink, string separator,
        string? a, string? b, string? c, string? d)
    {
        float sep = _painter.Measure(separator, large: false, scale).X;
        float cursor = x;
        bool first = true;
        if (a is not null)
        {
            _painter.Text(a, new(cursor, y), large: false, scale, ink);
            cursor += _painter.Measure(a, large: false, scale).X;
            first = false;
        }
        if (b is not null)
        {
            if (!first) cursor += sep;
            _painter.Text(b, new(cursor, y), large: false, scale, ink);
            cursor += _painter.Measure(b, large: false, scale).X;
            first = false;
        }
        if (c is not null)
        {
            if (!first) cursor += sep;
            _painter.Text(c, new(cursor, y), large: false, scale, ink);
            cursor += _painter.Measure(c, large: false, scale).X;
            first = false;
        }
        if (d is not null)
        {
            if (!first) cursor += sep;
            _painter.Text(d, new(cursor, y), large: false, scale, ink);
        }
    }

    /// <summary>The native <c>LOCATION:&lt;sector&gt;</c> fallback text, rebuilt only
    /// when the model's sector name changes (never per frame).</summary>
    private string SectorText(string sector)
    {
        if (!string.Equals(sector, _sectorShown, StringComparison.Ordinal))
        {
            _sectorShown = sector;
            _sectorText = "LOC " + sector;
        }
        return _sectorText;
    }

    /// <summary>A rounded distance readout, cached by its integer value.</summary>
    private string RangeText(float distance)
    {
        int rounded = (int)MathF.Round(distance);
        if (rounded != _rangeShown)
        {
            _rangeShown = rounded;
            _rangeText = rounded.ToString(CultureInfo.InvariantCulture) + " m";
        }
        return _rangeText;
    }

    /// <summary>A signed closure readout, cached by its integer value. Positive means
    /// separation decreasing (TargetClosingSpeed), so it is printed as-is.</summary>
    private string ClosureText(float closure)
    {
        int rounded = (int)MathF.Round(closure);
        if (rounded != _closureShown)
        {
            _closureShown = rounded;
            _closureText = rounded.ToString("+0;-0;0", CultureInfo.InvariantCulture);
        }
        return _closureText;
    }

    /// <summary>A target speed readout, cached by its integer value.</summary>
    private string TargetSpeedText(float speed)
    {
        int rounded = (int)MathF.Round(speed);
        if (rounded != _targetSpeedShown)
        {
            _targetSpeedShown = rounded;
            _targetSpeedText = rounded.ToString(CultureInfo.InvariantCulture) + " mps";
        }
        return _targetSpeedText;
    }

    /// <summary>A kill-bonus readout (whole percent, no invented decimal).</summary>
    private string KillBonusText(float bonus)
    {
        int rounded = (int)MathF.Round(bonus);
        if (rounded != _killBonusShown)
        {
            _killBonusShown = rounded;
            _killBonusText = rounded.ToString(CultureInfo.InvariantCulture);
        }
        return _killBonusText;
    }

    /// <summary>The partial lock's quality readout (<c>LOCK nn%</c>), cached by its
    /// integer percent so a steady frame formats nothing.</summary>
    private string LockText(float fraction)
    {
        int percent = (int)MathF.Round(Math.Clamp(fraction, 0f, 1f) * 100f);
        if (percent != _lockPercentShown)
        {
            _lockPercentShown = percent;
            _lockPercentText = percent.ToString(CultureInfo.InvariantCulture) + "%";
        }
        return _lockPercentText;
    }

    /// <summary>A zero-padded 0…100 fraction percentage (native pane digits are
    /// three wide), cached by its integer value.</summary>
    private string PercentText(float fraction, ref int shown, ref string text)
    {
        int percent = (int)MathF.Round(Math.Clamp(fraction, 0f, 1f) * 100f);
        if (percent != shown)
        {
            shown = percent;
            text = percent.ToString("000", CultureInfo.InvariantCulture);
        }
        return text;
    }

    // ── shared readout helpers ──────────────────────────────────────────
    /// <summary>How a readout box is anchored to its tape.</summary>
    private enum ReadoutAlign { Center, LeftEdge, RightEdge }

    /// <summary>A boxed numeric on a plate with a keyline tick facing the tape.
    /// <paramref name="topAnchored"/> places the plate's top edge at
    /// <paramref name="y"/> instead of centring it (the heading tape stacks
    /// downwards from the caret so gaps are guaranteed).</summary>
    private RectangleF DrawReadout(string text, float x, float y, float scale, ReadoutAlign align, bool topAnchored = false)
    {
        Vector2 ink = _painter.Measure(text, large: true, scale);
        float pad = 7f * scale;
        float tick = 4f * scale;
        float boxWidth = ink.X + pad * 2f + tick;
        float boxHeight = ink.Y + pad * 1.2f;
        float left = align switch
        {
            ReadoutAlign.Center => x - boxWidth * 0.5f,
            ReadoutAlign.LeftEdge => x,
            _ => x - boxWidth,
        };
        var box = new RectangleF(left, topAnchored ? y : y - boxHeight * 0.5f, boxWidth, boxHeight);
        _painter.FillPlate(box, 0.82f);
        float tickX = align == ReadoutAlign.RightEdge
            ? box.X + boxWidth - tick * 0.5f
            : box.X + tick * 0.5f;
        _painter.Line(new(tickX, box.Y), new(tickX, box.Y + box.Height), _painter.Stroke, _painter.Dim);
        _painter.Text(text, new(box.X + pad, box.Y + (box.Height - ink.Y) * 0.5f),
            large: true, scale, _painter.Primary);
        return box;
    }

    private RectangleF DrawCaption(string text, Vector2 anchor, float scale, bool rightAligned = false)
    {
        Vector2 ink = _painter.Measure(text, large: false, scale);
        Vector2 position = rightAligned ? new(anchor.X - ink.X, anchor.Y - ink.Y) : new(anchor.X, anchor.Y - ink.Y);
        _painter.Text(text, position, large: false, scale, _painter.Dim);
        return new RectangleF(position.X, position.Y, ink.X, ink.Y);
    }

    /// <summary>A dashed line of alternating segments (no allocation).</summary>
    private void DashedLine(Vector2 a, Vector2 b, float width, Color color)
    {
        Vector2 delta = b - a;
        const int segments = 5;
        for (int i = 0; i < segments; i += 2)
        {
            _painter.Line(a + delta * (i / (float)segments), a + delta * ((i + 1) / (float)segments), width, color);
        }
    }

    /// <summary>
    /// The WIP announcement, bottom-centre. P1039 — suppressed while the F4 inventory pane
    /// is present: that overlay owns the bottom band and its sliding rows pass through the
    /// tag's line, so the skin marker yields to the table (the marker is not a HUD reading).
    /// </summary>
    private void DrawTag(HudModel model, float w, float h, float scale)
    {
        if (model.Context.InventoryVisible || model.Context.InventorySlide > 0f) return;
        _painter.Element("tag");
        Vector2 ink = _painter.Measure(TagLabel, large: false, scale);
        _painter.Text(TagLabel, new(w * 0.5f, h - 12f * scale - ink.Y * 0.5f), large: false, scale, _painter.Dim, centered: true);
    }

    /// <summary>Cached speed-tape tick label: each visible label owns a stable ring
    /// slot, so a steady tape formats nothing after the first frame.</summary>
    private string SpeedTickText(int value, int slot)
    {
        if (_speedTickText[slot] is { } cached && _speedTickKeys[slot] == value) return cached;
        _speedTickKeys[slot] = value;
        _speedTickText[slot] = value.ToString(CultureInfo.InvariantCulture);
        return _speedTickText[slot];
    }

    private static string FormatSigned(int degrees)
        => degrees > 0 ? "+" + degrees.ToString("00", CultureInfo.InvariantCulture)
            : degrees < 0 ? degrees.ToString("00", CultureInfo.InvariantCulture)
            : "00";

    private static Color Parse(string? hex, Color fallback)
        => HudSkinColor.TryParseHex(hex, out var color) ? new Color(color.R, color.G, color.B) : fallback;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The surface borrows Game1's SpriteBatch/fonts; nothing here owns a
        // disposable resource.
    }
}
