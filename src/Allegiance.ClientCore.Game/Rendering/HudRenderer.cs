using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Classic skin and shared HUD widget composition. HUD-1 resolves the
/// data and visibility; skins choose geometry and glyph rendering. Text is
/// measured and placed through one per-frame collision layout. Modern overrides
/// the native bitmap gauges and target indicators in <see cref="ModernHudRenderer"/>.</summary>
public class HudRenderer
{
    // ── Palette ───────────────────────────────────────────────────────────
    private static readonly Color Accent = From(HudColor.TealAccent);
    private static readonly Color PanelBg = new(0.020f, 0.055f, 0.060f, 0.55f);
    private static readonly Color BorderDim = new(0.14f, 0.34f, 0.32f);
    private static readonly Color TextMain = new(0.88f, 0.96f, 0.95f);
    private static readonly Color TextDim = new(0.48f, 0.64f, 0.62f);
    // P221 — weapons-hot warning colour for the firing indicator (kept apart
    // from the teal accent so a live reticle reads unmistakably as "engaged").
    private static readonly Color Warning = new(0.95f, 0.30f, 0.22f);
    // P228 — damage-flash overlay tint: translucent red over the whole viewport
    // while the local ship is mid flash-window. Moderate alpha on purpose — a
    // "flash", not a "blackout" — so the scene stays legible beneath it and the
    // HUD widgets (drawn on top) remain crisp.
    private static readonly Color FlashRed = new Color(0.90f, 0.05f, 0.045f) * 0.22f;

    // P245 — warp-transit flash tint: full opaque black (a real blackscreen),
    // with the amount set per-frame by HudFrame.WarpFlash (0..1). This is the
    // brief "you got flung through a sector" acknowledgment, drawn LAST over the
    // whole frame (HUD included) — a genuine blackout, unlike the damage wash.
    private static readonly Color WarpBlack = new(0f, 0f, 0f, 1f);

    // P298 — Kill Bonus gold: a warm reward tone (distinct from the teal
    // accents, the red Warning, the amber MINING and the violet CLOAKED tags).
    private static readonly Color BonusGold = new(0.97f, 0.79f, 0.30f);

    // P362 — reload-in-progress amber: a system whose magazine hit zero and is
    // mid auto-reload (P361). Warm activity tone, distinct from Ready (dim) and
    // Depleted (Warning red) — mirrors the MINING tag's amber family.
    private static readonly Color ReloadAmber = new(0.98f, 0.74f, 0.24f);

    // HUD-14b/HUD-14c — native classic-console tints, used under the additive
    // blend. hudColor = pside->GetColor() * g_hudBright (WinTrek/WinTrek.cpp:8681)
    // tints the labels (StringImage hudColor, dialog.mdl:2210 etc.) — the port
    // substitutes its HUD accent (fonts/colours are not the fix round's focus).
    private static readonly Color HUDLabelTint = Accent;

    // HUD-14c/HUD-19c — hudColorshadows = Color(45,36,59) * 0.65, alpha 0.55
    // (WinTrek/WinTrek.cpp:8684-8687; Color*float leaves alpha alone,
    // zlib/color.h:101-109). Native Color takes 0..1 FLOATS (zlib/color.h:33),
    // so 45/36/59 are wildly out of range, and MakeD3DCOLOR packs
    // (MakeInt(255·a)<<24)|(MakeInt(255·r)<<16)|(MakeInt(255·g)<<8)|MakeInt(255·b)
    // with NO clamp or mask (engine/context.h:327-333; MakeInt = int(),
    // zlib/zmath.h:283-296 — the FLOATASM fistp path rounds, same bytes here).
    // r=int(255·29.25)=7458=0x1D22, g=int(255·23.4)=5967=0x174F,
    // b=int(255·38.35)=9779=0x2633, a=int(255·0.55)=140=0x8C. OR-ing the
    // overflowing fields: B byte = 0x33|0 = 51; G byte = 0x4F|(b>>8=0x26) = 0x6F
    // = 111; R byte = 0x22|(g>>8=0x17) = 0x37 = 55; A byte = 0x8C|(r>>8=0x1D) =
    // 0x9D = 157 — i.e. ARGB(157, 55, 111, 51), a dim GREEN (0.216, 0.435,
    // 0.200) at alpha ~0.62. (HUD-14c had R/G swapped.) That is a BUG
    // (out-of-range floats, OR overflow), not a designed colour. Per the
    // standing rule the port keeps the EVIDENT INTENT — a dim shadow tint at
    // the designer's alpha 0.55 — with the native-actual hue so the
    // hull/shield backs stay visible; the near-black /255 reading of 45,36,59
    // is never used. The backs draw in the One/One additive gauge pass, which
    // ignores alpha, so the rgb is pre-scaled by 0.55 (AdditiveTint).
    private static readonly Color HUDShadowTint = AdditiveTint(0.216f, 0.435f, 0.200f, 0.55f);

    // HUD-14c/HUD-19 — native BlendModeAdd is SRCALPHA/ONE on STRAIGHT-alpha
    // textures (engine/RasterD3D9.cpp:295-302), which equals One/One on the
    // PREMULTIPLIED textures the port decodes (MdlTextureDecoder.ToPremultipliedRgba).
    // Because the tint is multiplied into an already-premultiplied texel, a
    // One/One pass must pre-scale the tint's rgb by its own alpha
    // (native: src.rgb·src.a; premultiplied One/One: tex.rgb_premul·tint.rgb →
    // tint.rgb := native.rgb·native.a). Shared by the classic gauge pass
    // (HUD-14b) and the additive centre-indicator pass (HUD-19).
    internal static readonly BlendState AdditiveOneOne = new()
    {
        ColorSourceBlend = Blend.One, AlphaSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.One, AlphaDestinationBlend = Blend.One,
    };

    /// <summary>HUD-19 — the One/One additive tint for a native BlendModeAdd
    /// colour: rgb pre-scaled by alpha (see <see cref="AdditiveOneOne"/>).
    /// Pure so tests pin the pre-scale without a graphics device.</summary>
    internal static Color AdditiveTint(float r, float g, float b, float a) =>
        new(r * a, g * a, b * a, 1f);

    protected readonly SpriteBatch _sb;
    protected readonly Texture2D _white;
    protected readonly SpriteFont _small;
    protected readonly SpriteFont _big;
    private readonly NativeFonts? _nativeFonts;
    private readonly GaugeTextures? _gauges;
    private readonly LockTextures? _lockTextures;
    private readonly StatusTextures? _status;
    private readonly IndicatorTextures? _indicators;
    private readonly Texture2D? _joystickMesh;
    private readonly TargetPaneTextures? _targetPaneTextures;
    private readonly InventoryPaneTextures? _inventoryTextures;
    private readonly HudNativeTextures? _nativeHud;
    private readonly bool _damageWash;
    /// <summary>HUD-14 — native has no percentage numeral on the classic
    /// gauges; the port's % readout is an extension gated by this HUD option
    /// (default off = native). See <see cref="HudSettings.GaugePercentage"/>.</summary>
    private readonly bool _gaugePercentage;

    /// <summary>
    /// HUD-21 — the 3-D target-model preview render target for this frame,
    /// set by the game (Game1) right before <see cref="Draw"/> when an
    /// eligible target rendered a model, else null (the pane then looks
    /// exactly as it does today — the empty fallback). Drawn into
    /// <see cref="TargetModelPreview.PaneRect"/> before the oval/word art, so
    /// the native child order (model painted after the gauges, before the oval)
    /// holds.
    /// </summary>
    public Texture2D? TargetModelPreview { get; set; }

    /// <summary>HUD-21 — pure guard: whether a model preview is present to draw
    /// this frame (null input → the pane draws nothing, the empty fallback).</summary>
    public bool PreviewWillDraw => TargetModelPreview is not null;

    public HudRenderer(
        SpriteBatch spriteBatch,
        Texture2D whitePixel,
        SpriteFont small,
        SpriteFont big,
        GaugeTextures? gauges = null,
        LockTextures? lockTextures = null,
        StatusTextures? statusTextures = null,
        IndicatorTextures? indicators = null,
        TargetPaneTextures? targetPaneTextures = null,
        InventoryPaneTextures? inventoryTextures = null,
        bool damageWash = true,
        HudNativeTextures? nativeHudTextures = null,
        bool gaugePercentage = false,
        NativeFonts? nativeFonts = null,
        Texture2D? joystickMesh = null)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
        _nativeFonts = nativeFonts;
        _gauges = gauges;
        _lockTextures = lockTextures;
        _status = statusTextures;
        _indicators = indicators;
        _joystickMesh = joystickMesh;
        _targetPaneTextures = targetPaneTextures;
        _inventoryTextures = inventoryTextures;
        _nativeHud = nativeHudTextures;
        _damageWash = damageWash;
        _gaugePercentage = gaugePercentage;
    }

    /// <summary>
    /// Draw the whole HUD overlay. Self-contained: begins/ends its own
    /// <see cref="SpriteBatch"/> session, so it can be invoked after the 3D scene
    /// has already been drawn to the active render target (backbuffer or an
    /// offscreen capture target). The reticle and camera-mode tag draw even when
    /// <paramref name="frame"/> is null (i.e. before the client owns a ship yet);
    /// the telemetry widgets need a frame, so they are skipped until one exists.
    /// </summary>
    public void Draw(HudFrame? frame, CameraMode mode, Viewport viewport, LockBracketView? lockBracket = null, LeadIndicatorView? leadIndicator = null, string? modeLabelOverride = null, TargetSelectionView? targetSelection = null, Vector2? targetBearing = null, LeaderBotCommand? leaderCommand = null, string? ripcordStatus = null, string? commandeeLabel = null, bool turretView = false, string? lodOverlay = null, IReadOnlyList<LastBaseHudView>? lastBaseObjectives = null, bool showJoystickIndicator = true)
        => Draw(HudModelAdapter.Create(frame, mode, lockBracket, leadIndicator, modeLabelOverride,
            targetSelection, targetBearing, leaderCommand, ripcordStatus, commandeeLabel, turretView, lodOverlay, lastBaseObjectives: lastBaseObjectives, showJoystickIndicator: showJoystickIndicator), viewport);

    /// <summary>Draw the classic skin from a resolved, graphics-independent model.</summary>
    public virtual void Draw(HudModel model, Viewport viewport)
    {
        _viewportWidth = viewport.Width;
        _viewportHeight = viewport.Height;
        Layout.Reset(viewport.Width, viewport.Height);
        PrepareLayout(model, viewport);
        if (!model.Visible) return;
        var frame = model.InstrumentsVisible ? model.Telemetry : null;
        var context = model.Context;
        var mode = context.Camera;
        var lodOverlay = context.Diagnostics;
        var lastBaseObjectives = model.FlightVisible ? context.LastBaseObjectives : null;   // P745b
        var leaderCommand = model.FlightVisible ? context.LeaderCommand : null;
        var commandeeLabel = model.FlightVisible ? context.Commandee : null;
        bool turretView = context.Turret;
        float w = viewport.Width;
        float h = viewport.Height;

        // Precompute the target-pane top edge so the bottom-centre readouts
        // (leader/commandee/speed/mouse-stick) can clear it even though the
        // pane itself is drawn a few steps later.
        float paneTop = model.TargetPaneVisible && context.TargetPane is { Present: true }
            ? TargetPaneLayout.TopY(w, h) : h;

        BeginBatch();
        try
        {
            // P628 — LOD debug overlay: per-tier ship counts, drawn top-right
            // when the caller supplies the line (env ALLEGIANCE_LOD_OVERLAY=1).
            if (lodOverlay is { Length: > 0 })
            {
                var lodFont = NativeFontAt(NativeFont.MedVerdana);
                var sz = lodFont.MeasureString(lodOverlay);
                DrawText(lodFont, lodOverlay, new Vector2(w - sz.X - 12f, 12f), Color.White);
            }
            // P228 — damage flash: a full-viewport translucent-red wash while the
            // local player's ship is mid flash-window (hull took a hit). Drawn
            // FIRST, beneath the reticle/HUD widgets, so the scene visibly tints
            // red but the telemetry text stays legible above it. Nothing to show
            // until a frame exists (the tracker is fed only the local ship's hull).
            bool flashing = frame is { } flashFrame && flashFrame.Flashing;
            if (flashing && _damageWash)
                FillRect(0f, 0f, w, h, FlashRed);
            if (flashing)
                ElementDrawn("Damage flash timer");

            // P221 — firing readout: the reticle turns the warning colour and
            // shows a FIRING tag while StateM carries AllWeapons (group fire).
            // Computed before the null-frame return so the crosshair (drawn
            // even without a ship) can reflect it; the tag just stays inert
            // until a frame exists.
            bool firing = frame is { } ff && ff.Firing;
            // HUD-19 — the native-ADDITIVE centre indicators (the directioninbmp
            // reticle and every centeroutbmp aim crosshair) draw in their own
            // One/One additive pass (indicator.cpp:149-150, 268-276, 361-362,
            // 399-400; engine/RasterD3D9.cpp:295-302). The procedural fallbacks
            // (no artwork) and the Modern skin stay on the alpha batch below.
            DrawCentreIndicatorsAdditive(model, viewport, firing, turretView);
            if (model.ReticleVisible)
                {
                    DrawCrosshair(new Vector2(w / 2f, h / 2f), firing, turretView);
                    ElementDrawn("Cockpit centre reticle");
                }

            // HUD-8 — velocity direction marker (native directionbmp): projected
            // source-velocity direction, cockpit only, hidden at zero speed.
            if (model.VelocityMarkerVisible && context.VelocityMarker is { } velocityMarker)
                {
                    DrawVelocityMarker(new(velocityMarker.X, velocityMarker.Y));
                    ElementDrawn("Velocity direction marker");
                }

            // HUD-8 — joystick turning indicator (native RenderJoystickIndicator).
            if (model.JoystickIndicatorVisible && model.Telemetry is { } joystickTelemetry)
                {
                    DrawJoystickIndicator(new Vector2(w / 2f, h / 2f), joystickTelemetry.TurnYaw, joystickTelemetry.TurnPitch);
                    ElementDrawn("Joystick turning indicator");
                }

            // P230 — missile-lock target box: drawn whenever Game1 has projected a
            // lock target into view, tinted/animated by the current lock state
            // (LockBracketStyle). Independently of a ship frame (a target box can
            // precede the status panel the same way the crosshair does).
            if (model.IndicatorsVisible && context.Lock is { } lb)
                {
                    DrawLockBracket(new(new(lb.Center.X, lb.Center.Y), lb.State, lb.NowSeconds));
                    ElementDrawn("Missile lock flower");
                }

            // P266/HUD-8 — ballistic lead marker: the native leadin/leadout surface
            // at the projected lead point, cockpit only (indicator.cpp:320-330).
            if (model.LeadMarkerVisible && context.Lead is { } leadMarker)
                {
                    DrawLeadIndicator(new(new(leadMarker.Center.X, leadMarker.Center.Y), new(leadMarker.TargetCenter.X, leadMarker.TargetCenter.Y), leadMarker.InRange, leadMarker.Green, leadMarker.NowSeconds));
                    ElementDrawn("Ballistic lead marker");
                }

            // HUD-8 — aim hit-probability predictor: native centre crosshair
            // (indicator.cpp:334-362) in cockpit, projected aim point in chase.
            if (model.AimPredictorVisible && context.Lead is { } aimLead)
                {
                    DrawAimPredictor(new(new(aimLead.Center.X, aimLead.Center.Y), new(aimLead.TargetCenter.X, aimLead.TargetCenter.Y), aimLead.InRange, aimLead.Green, aimLead.NowSeconds), context.Camera, viewport);
                    ElementDrawn("Aim hit-probability predictor");
                }

            // HUD-8 — chase weapon aim without target (indicator.cpp:367-405).
            if (model.ChaseAimVisible && context.ChaseAim is { } chaseAim)
                {
                    DrawChaseAim(new(chaseAim.X, chaseAim.Y));
                    ElementDrawn("Chase weapon aim without target");
                }

            // HUD-8 — accepted/current order turn arrows (indicator.cpp:411-457).
            if (model.OrderArrowsVisible && context.OrderArrows is { } orderArrows)
                {
                    DrawOrderArrows(orderArrows, viewport);
                    ElementDrawn("Accepted and current order turn arrows");
                }

            // P345 — the player's OWN T/G autopilot-target selection box: a full
            // closed rectangle drawn at the selected target's projected centre,
            // brightened while autopilot is engaged. Deliberately drawn AFTER the
            // missile-lock box and lead reticle so the pilot's own selection is the
            // most prominent of the target markers when several overlap. Distinct
            // shape (closed vs open corners) and colour from the lock box.
            if (model.FlightVisible && context.Target is { } ts)
                {
                    DrawTargetSelectionBracket(new(new(ts.Center.X, ts.Center.Y), ts.Engaged, ts.Name, ts.HullFraction, ts.ShieldFraction, ts.IsEnemy, ts.OreFraction, ts.Distance, ts.ClosingSpeed, ts.SideColor));
                    ElementDrawn("Selected target bracket", "Target ore");
                }

            // P359 — the target bearing arrow: a fixed-position compass chevron in
            // the bottom-right corner pointing toward the selected target's REAL
            // relative bearing from the camera. Drawn whenever a target is selected,
            // INCLUDING when the target itself is off-screen (that is exactly when
            // it matters most — the bracket above is null out of view). Applies to
            // every target kind (ship/station/asteroid/warp): pure directional math.
            if (model.IndicatorsVisible && context.TargetBearing is { } tb)
                DrawTargetBearingArrow(new(tb.X, tb.Y), viewport);

            // HUD-5 — radar overlays/labels (native radarimage.cpp:1199-1416);
            // drawn after the target markers so labels yield to those boxes.
            DrawRadarLabels(model, viewport);

            if (lastBaseObjectives is { } objectives)
                for (int i = 0; i < objectives.Count; i++)
                {
                    var objective = objectives[i];
                    var objFont = NativeFontAt(NativeFont.MedVerdana);
                    var size = objFont.MeasureString(objective.Text);
                    var origin = new Vector2(w / 2f - size.X / 2f, 162f + i * 26f);
                    FillRect(origin.X - 8, origin.Y - 3, size.X + 16, size.Y + 6, Color.Black * 0.7f);
                    DrawText(objFont, objective.Text, origin, BonusGold);
                    if (objective.Bracket is { } bracket) DrawTargetSelectionBracket(bracket);
                    if (objective.Bearing is { } bearing)
                        DrawTargetBearingArrow(bearing, viewport, 190 + i * 110, objective.BearingLabel, bottomInset: 330);
                }

            // Camera-mode tag (bottom-left) — also proves the toggle in captures.
            // modeLabelOverride (P306) lets the headless capture show "OVERVIEW"
            // instead of lying about the on-screen camera-mode toggle.
            //
            // P340 (B4) — every camera mode gets its honest label. Previously
            // the ternary collapsed everything but Cockpit into "CHASE", so the
            // P295 ExternalTurret and ExternalMissile views both displayed a
            // label that was false. The names mirror native's view modes
            // (Treki.h:108-126): cmExternalTurret and cmExternalMissile.
            string modeLabel = model.CameraLabel;
            DrawText(NativeFontAt(NativeFont.MedVerdana), modeLabel, new Vector2(16f, h - 26f),
                mode == CameraMode.Cockpit ? Accent : TextDim);

            // P366 — the leader-only bot dock/launch prompt: drawn whenever the
            // local player is the team leader AND the T-selected target is a
            // same-side bot (Game1 resolves it per frame). A compact bottom-centre
            // line under the weapon readout; absent for a non-leader (nothing to
            // draw — the gate is enforced upstream in ResolveLeaderBotCommand).
            if (leaderCommand is { } lc)
                DrawLeaderCommand(lc.Label, w, model.TargetPaneVisible ? paneTop - 70f : h - 140f);

            // P425 — the commanded-ship (commandee) readout: a compact line above
            // the leader-command slot naming the ship/drone the pilot is currently
            // commanding and reminding of the issue-order key. Present ONLY when a
            // commandee is selected (Game1's commandeeLabel is null otherwise), so
            // an idle pilot sees nothing extra. Distinct colour from the leader
            // prompt so the two command affordances read apart.
            if (commandeeLabel is not null)
                DrawCommandee(commandeeLabel, w, model.TargetPaneVisible ? paneTop - 96f : h - 164f);

            DrawWarnings(model.Warnings, w, h);
            if (model.Warnings.Count > 0) ElementDrawn("Boundary and crowd warnings");
            DrawConsoleText(model, viewport);
            DrawLivesKillsBadges(model, viewport);

            // HUD-3 — the native bottom target pane (dialog.mdl:5402, shown in
            // cockpit/chase). Present for a selected target even when the
            // projected in-world selection bracket is out of view.
            if (model.TargetPaneVisible && context.TargetPane is { Present: true })
                DrawTargetPane(model, viewport);

            // HUD-7 — the native flight equipment/inventory pane (F4) does not
            // need the telemetry frame: it is resolved from the loadout/cargo and
            // draws in command view too (native mask ofInventory, dialog.mdl:5237).
            if (model.FlightVisible && (context.InventoryVisible || context.InventorySlide > 0f)
                && context.InventoryPane is { } pane)
                DrawInventoryPane(pane, model, viewport);

            // HUD-12 — the native command-view ship summary (cortCommandModeInfo,
            // dialog.mdl:4997-5027) at its SideLeft anchor. Drawn BEFORE the
            // instruments gate: command view intentionally hides the centre
            // gauges (InstrumentsVisible false) but this summary is the
            // ofInFlightCommand replacement (dialog.mdl:5412).
            if (model.Context.CommandView && model.FlightVisible)
                DrawCommandShipSummary(model, viewport);

            if (frame is not { } f)
                return;   // no ship yet — gauges/readouts have nothing to show

            // P241 — the real original-game segmented gauges when the artwork
            // loaded; otherwise fall back to the P211 procedural panel.
            // HUD-14b — native composes the CLASSIC console additively
            // (hudBlendMode = BlendModeAdd, dialog.mdl:34), so the classic
            // gauge layers draw in a separate additive SpriteBatch pass, then
            // the batch is restored to alpha blend for the rest of the HUD.
            // The MODERN skin is a port invention (not native), so its gauges
            // keep the normal alpha-blended batch.
            bool classicGaugesLoaded = !UsesModernGauges && _gauges is { } g && g.AllLoaded;
            if (classicGaugesLoaded)
            {
                _sb.End();
                // HUD-14c — gauge textures are premultiplied (GaugeTextures.cs
                // ToPremultipliedRgba) and so are the SpriteFont glyphs, so
                // BlendState.Additive (SrcAlpha/One) would apply alpha twice;
                // native BlendModeAdd SRCALPHA/ONE on straight-alpha == One/One
                // here (engine/RasterD3D9.cpp:295-302).
                _sb.Begin(SpriteSortMode.Deferred, AdditiveOneOne);
                DrawGauges(model, viewport);
                _sb.End();
                _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
            }
            else if (UsesModernGauges || _gauges is { } mg && mg.AllLoaded)
            {
                DrawGauges(model, viewport);
            }
            else
            {
                DrawStatusPanel(f, w);
                DrawThrottleGauge(f.Throttle, w, h);
            }
            {
                float speedCy = model.TargetPaneVisible ? paneTop - 40f
                    : model.PodVisible && !UsesModernGauges ? h - 238f : h - 108f;
                DrawSpeed(f.Speed, model.SpeedUnit, w, speedCy);
                ElementDrawn("Speed");
            }
            DrawAfterburner(f.AfterburnerActive, f.Power, w);
            {
                DrawWeaponMode(f, w);
                ElementDrawn("Equipment selected and active state");
            }
            // P255 — a simple, honest "MINING" tag while the local ship is
            // actively drilling a rock (kept modest: just the word, top-centre
            // under the weapon readout, in a warm amber to read as activity
            // rather than danger). The ship's FACING toward the rock is the
            // primary cue (Game1 overrides the own hull orientation); this text
            // just disambiguates it.
            if (f.Mining) DrawMining(w);
            // P618 — the TIME DILATION indicator: drawn at the very top-centre
            // whenever the server's live sim time scale is below full speed
            // (f.TimeDilationPercent < 100). Hidden entirely at full speed.
            if (f.TimeDilationPercent < 99.5f) DrawTimeDilation(f.TimeDilationPercent, w);
            // Ripcord countdown now renders via DrawConsoleText at the native
            // centre anchor (consoledata.cpp:521; dialog.mdl:5150).

            // HUD-6 — native centre-HUD own-ship status icons/readouts at their
            // dialog.mdl anchors (kill bonus, signature, detection eye, cloak,
            // autopilot, vector lock, carried flag/artifact).
            DrawOwnStatus(model, viewport);
            DrawRoll(f.RollDegrees, w, h);
            // HUD-10 — native has no in-flight money readout: moneyPane lives only
            // inside MergedInvestmentsPaneImage under ofInvestment
            // (dialog.mdl:5199-5203,5368), so the old always-visible "$n" line is
            // gone from the flight HUD. The investment pane draws the real
            // creditbox art (Game1.Rendering → DockedScreenRenderer).

            // P362 — the weapon/systems status list: a small persistent panel
            // (right edge) naming each mounted weapon/missile system with its
            // live ammo and a per-system READY/RELOAD/DEPLETED status derived
            // from P361's auto-reload behaviour. Empty list (loadout not yet
            // known) draws nothing. HUD-7: it duplicates the native inventory
            // pane's part identity/quantity columns, so it is hidden while that
            // pane is up (the pane already owns those two parity rows).
            if (f.WeaponSystems is { Count: > 0 } ws
                && !(model.Context.InventoryVisible && model.Context.InventoryPane is { Rows.Count: > 0 }))
                {
                    DrawWeaponStatusList(ws, viewport);
                    ElementDrawn("Equipment part identity and key", "Equipment quantity and empty warning");
                }


            // P506 — virtual-joystick indicator: a small reticle + deadzone
            // circle + stick-position dot, drawn while mouse flight is on (the
            // frame carries a MouseStickView then). Shows the pilot when the
            // stick is centred inside the deadzone so the ship holds steady.
            if (model.MouseStickVisible && f.MouseStick is { } ms)
                {
                    DrawMouseStick(ms, w, model.TargetPaneVisible ? paneTop - 90f : h - 140f);
                    ElementDrawn("Joystick turning indicator");
                }

            // P245 — warp-transit flash, drawn LAST so it blacks out the whole
            // frame (HUD and all) at the moment the local ship changes sector.
            // A genuine "blackscreen" (opaque, alpha = WarpFlash) as opposed to
            // the translucent damage wash above. Opacity is 0 outside the brief
            // window, so this is a no-op for every frame that isn't flashing.
            float warpFlash = f.WarpFlash;
            if (warpFlash > 0f && !DeferWarpFlash)
                FillRect(0f, 0f, w, h, WarpBlack * warpFlash);
        }
        finally
        {
            _sb.End();
        }
    }

    public HudTextLayout Layout { get; } = new();
    // HUD-5b — per-contact cache for the resolved radar labels and their
    // formatted lines (no per-frame string/array allocation at 107v107).
    private readonly RadarLabelCache _radarLabels = new();
    protected virtual float TextScale => 1;
    /// <summary>
    /// FONT-1 — the font+scale a classic-HUD text site uses for a native font
    /// identity at the pane's own position scale (<paramref name="paneScale"/>,
    /// floored at 1 by the registry). Class passes through to the bundled native
    /// bakes; Modern overrides it to keep its own SDF small/big fonts, so Modern
    /// renders exactly as before. Every measurement at a site uses the SAME
    /// returned (font, scale) as its draw.
    /// </summary>
    protected virtual (SpriteFont Font, float Scale) HudFont(NativeFont id, float paneScale)
    {
        if (_nativeFonts is { } native)
            return native.Get(id, paneScale);
        // Fallback (renderer tests that supply only the mono fonts): keep the
        // historic small/big split at TextScale.
        return (id == NativeFont.LgBoldVerdana ? _big : _small, TextScale);
    }

    /// <summary>FONT-1 — resolve a native font at the console/TextScale pane
    /// scale (paneScale == TextScale for the console and port-only widgets),
    /// returning just the font. Pane-scaled sites call
    /// <see cref="HudFont"/> directly to also get the residual scale.</summary>
    protected SpriteFont NativeFontAt(NativeFont id) => HudFont(id, TextScale).Font;

    /// <summary>HUD-11 — the rendered height of one HUD text line. Native
    /// dialogs translate labels by raw 14 px because native promptFont is 14 px
    /// high (dialog.mdl:1473-1485); the port's substitute font is taller, so
    /// stacked console lines use this measured height instead.</summary>
    protected virtual float TextLineHeight => NativeFontAt(NativeFont.MedVerdana).LineSpacing * TextScale;

    /// <summary>HUD-11 — the native top-centre chat pane owns x =
    /// [centre-160, centre+195] (CommPane, dialog.mdl:5394; 355 px wide,
    /// dialog.mdl:180). The port's afterburner/weapons/mining/TiDi cluster is a
    /// port extension (native has no text there), so it moves into the free slot
    /// immediately right of the pane instead of drawing over it.</summary>
    protected float TopStatusCenterX(float viewportWidth)
        => viewportWidth / 2f + (ChatPaneLayout.Width - ChatPaneLayout.LeftFromCentre) + 16f + 110f * TextScale;
    protected virtual void ElementDrawn(params string[] ids) { }
    protected virtual bool UsesModernGauges => false;
    protected virtual bool DeferWarpFlash => false;
    protected virtual void BeginBatch() => _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
    protected virtual void RenderText(SpriteFont font, string text, Vector2 position, Color color, float scale)
        => _sb.DrawString(font, text, position, color, 0, Vector2.Zero, scale, SpriteEffects.None, 0);

    /// <summary>HUD-7 — fit a pane column string to a measured on-screen width by
    /// truncating with an ellipsis (the same binary-search fit as
    /// <see cref="DrawMeasuredText"/>, without its background fill or collision
    /// placement). The full value stays in the model; only the glyphs clip.
    /// </summary>
    protected string FitText(SpriteFont font, string text, float maxWidth, float scale)
        => HudFormat.FitToWidth(text, t => font.MeasureString(t).X * scale, Math.Max(0f, maxWidth));

    protected virtual void PrepareLayout(HudModel model, Viewport viewport)
    {
        var (mx, my, mw, mh) = MinimapGeometry.CornerPanel(viewport.Width, viewport.Height);
        Layout.Reserve(new((int)mx, (int)my, (int)mw, (int)mh));
        if (model.FlightVisible && model.Context.InventoryVisible && model.Context.InventoryPane is { Rows.Count: > 0 } pane)
            Layout.Reserve(InventoryPaneLayout.ReserveRect(pane.Rows.Count, viewport.Width, viewport.Height));
        float spread = Math.Clamp(viewport.Height / 680f, 1.15f, 2.1f);
        foreach (var gauge in model.Gauges.Where(g => g.Visible))
        {
            var p = new Vector2(viewport.Width / 2f + gauge.NativeOffset.X * spread, viewport.Height / 2f + gauge.NativeOffset.Y * spread);
            Layout.Reserve(new Rectangle((int)p.X - 55, (int)p.Y - 50, 110, 150));
        }
        ReserveTargetPane(model, viewport);
        ReserveCommandSummary(model, viewport);
        ReserveConsoleAnchors(model, viewport);
    }

    /// <summary>HUD-12b merge fix — the command-view ship summary draws at its
    /// own fixed native anchor (<c>SideLeft(-200,120)-&gt;(36,120)</c>,
    /// dialog.mdl:5412) and deliberately does not move, so the measured radar
    /// labels must yield to it. HUD-11's label placement and HUD-12's new
    /// summary otherwise collided in command view (hud2-proof's 1920 overlay).
    /// </summary>
    protected void ReserveCommandSummary(HudModel model, Viewport viewport)
    {
        if (!model.Context.CommandView || !model.FlightVisible || model.Telemetry is not { } f) return;
        float spread = Math.Clamp(viewport.Height / 680f, 1.15f, 2.1f);
        float line = TextLineHeight + 1f;
        float top = viewport.Height / 2f - 120f;
        float bottom = top + 20f + 8f * line;   // ship row + six gauges + speed
        if (f.SignaturePercent is not null) bottom += line;
        if (f.KillBonusPercent > 0f || f.KillBonusStreak > 0) bottom += line;
        bottom += 2f * line + 4f;                // money/AP/radar + target rows
        float right = 36f + 118f * spread + 90f * TextScale;   // gauge + percent column
        Layout.Reserve(new Rectangle(20, (int)(top - 8f),
            Math.Max(1, (int)(right - 20f)), Math.Max(1, (int)(bottom - top + 24f))));
    }

    /// <summary>Reserve the native bottom target pane so measured text (radar
    /// labels, notices, projected readouts) is placed around it.</summary>
    protected void ReserveTargetPane(HudModel model, Viewport viewport)
    {
        if (model.TargetPaneVisible && model.Context.TargetPane is { Present: true })
            Layout.Reserve(TargetPaneLayout.ReserveRect(viewport.Width, viewport.Height));
    }

    /// <summary>HUD-9b — reserve the fixed native console anchors before the
    /// crosshair/warning pass so the port's dynamic FIRING/TURRET tag and textual
    /// boundary/crowd warnings yield to them (native has neither, so its anchors
    /// are always free). The console text itself is drawn with a fixed anchor and
    /// ignores these reservations.</summary>
    protected void ReserveConsoleAnchors(HudModel model, Viewport viewport)
    {
        float w = viewport.Width;
        float scale = TextScale;
        float lineHeight = TextLineHeight;
        float cxRight = w - 100f * scale;

        // HUD-11 — the native top-centre chat pane owns its slot; clamped radar
        // labels and dynamic notices yield to it (instead of drawing over the
        // port's former top-centre afterburner/weapons cluster).
        int paneLines = ChatPaneLayout.ConfiguredLines(
            model.Context.Chat?.PaneLines ?? ChatPaneLayout.DefaultLines, model.Context.Docked);
        Layout.Reserve(new Rectangle(ChatPaneLayout.Left(viewport.Width),
            ChatPaneLayout.Top(paneLines), ChatPaneLayout.Width, ChatPaneLayout.Height(paneLines)));
        float statusCx = TopStatusCenterX(w);
        Layout.Reserve(new Rectangle((int)(statusCx - 160f * scale), 6,
            (int)(320f * scale), (int)(120f * scale)));

        if (model.Context.Console is not { } console) return;
        int chatLines = HudConsoleFormat.BannerChatLines(model.Context.Chat);

        // HUD-11 — the native top-right sector block and top-left order block
        // are fixed anchors: reserve their exact measured boxes before radar
        // labels are placed, so an edge-clamped label can never overlap them
        // (and no placement pass can shuffle the block).
        if (model.FlightVisible && console.SectorName is { } sector)
        {
            float labelTop = HudConsoleFormat.SectorLabelTop * scale;
            float nameTop = HudConsoleFormat.NameBelowLabel(labelTop, lineHeight);
            ReserveAnchor("Current location:", cxRight, labelTop, centered: true);
            ReserveAnchor(sector, cxRight, nameTop, centered: true);
        }
        if (model.FlightVisible && console.BoardState == 2 && console.CarrierName is { } carrier)
        {
            float labelTop = HudConsoleFormat.TurretLabelTop * scale;
            float nameTop = HudConsoleFormat.NameBelowLabel(labelTop, lineHeight);
            ReserveAnchor("Turret of:", cxRight, labelTop, centered: true);
            ReserveAnchor(carrier, cxRight, nameTop, centered: true);
        }
        if (console.AcceptedOrder is not null || console.QueuedOrder is not null)
        {
            float labelTop = HudConsoleFormat.OrderLabelTop * scale;
            ReserveAnchor("Current orders:", HudConsoleFormat.OrderLabelX * scale, labelTop, centered: false);
            if (console.QueuedOrder is { } queued)
                ReserveAnchor(queued, HudConsoleFormat.OrderTextX * scale,
                    HudConsoleFormat.OrderQueuedTop(labelTop, lineHeight), centered: false);
            if (console.AcceptedOrder is { } accepted)
                ReserveAnchor(accepted, HudConsoleFormat.OrderTextX * scale,
                    HudConsoleFormat.OrderAcceptedTop(labelTop, lineHeight), centered: false);
        }

        if (model.FlightVisible && model.Context.Ripcord is { } ripcord)
            ReserveAnchor(ripcord, w / 2f - HudConsoleFormat.RipcordLeft * scale,
                viewport.Height / 2f - HudConsoleFormat.RipcordTop * scale, centered: false);
        int criticalCount = console.CriticalTips.Count;
        for (int slot = 0; slot < criticalCount; slot++)
        {
            var tip = console.CriticalTips[criticalCount - 1 - slot];
            ReserveAnchor(tip.Text, w / 2f, HudConsoleFormat.CriticalTipTop(chatLines, slot) * scale, centered: true);
        }
        if (console.VoteText is { } vote)
            ReserveAnchor(vote, w / 2f, HudConsoleFormat.VoteTop(chatLines) * scale, centered: true);
    }

    private void ReserveAnchor(string text, float anchorX, float top, bool centered)
    {
        var size = NativeFontAt(NativeFont.MedVerdana).MeasureString(text) * TextScale;
        float left = centered ? anchorX - size.X / 2f : anchorX;
        Layout.Reserve(new Rectangle((int)left, (int)top, (int)MathF.Ceiling(size.X), (int)MathF.Ceiling(size.Y)));
    }
    public Rectangle DrawOverlayText(string text, Vector2 position, Color color)
        => DrawMeasuredText(NativeFontAt(NativeFont.MedVerdana), text, position, color);
    protected void DrawText(SpriteFont font, string text, Vector2 position, Color color, float? drawScale = null)
        => DrawMeasuredText(font, text, position, color, drawScale: drawScale);

    /// <summary>
    /// HUD-5 — radar name/range/stat labels stacked beside each projected or
    /// edge-clamped contact (native radarimage.cpp:1199-1416). The shared model
    /// already applied the per-kind LOD and important-mask visibility; this only
    /// draws the resolved block and records its measured bounds for the overlap
    /// check. Text routes through <see cref="RenderText"/> so Modern uses its SDF
    /// atlas while Classic keeps the bitmap font.
    /// </summary>
    protected virtual void DrawRadarLabels(HudModel model, Viewport viewport)
    {
        if (!model.FlightVisible) return;
        float scale = TextScale;
        var centre = new Vector2(viewport.Width / 2f, viewport.Height / 2f);
        var contacts = model.Context.Contacts;
        for (int i = 0; i < contacts.Count; i++)
        {
            var contact = contacts[i];
            // Overlay-bracket rows are drawn by the shared RadarBracketRenderer;
            // record them here so Modern's coverage check sees the model-driven
            // brackets as well as the label block below.
            MarkRadarBrackets(contact);
            // HUD-5b — the label block (and every string in it) is cached per
            // contact slot and rebuilt only when the label inputs change.
            var view = _radarLabels.Resolve(i, contact, model.Context.RadarMode, model.Context.CommandView);
            if (view.Label is not { } label) continue;
            DrawRadarLabelBlock(contact, label, view, centre, scale);
        }
    }

    private void MarkRadarBrackets(HudContact contact)
    {
        // Accepted/queued orders are NOT brackets in native (radarimage.cpp:352,
        // 1094-1096): they are a command icon plus a brighter label, recorded via
        // view.Drawn in DrawRadarLabelBlock. Only the native bracket surfaces are
        // marked here, so the coverage report cannot claim a bracket that is not
        // drawn.
        if (contact.NearestEnemy) ElementDrawn("Radar nearest enemy bracket");
        if (contact.Threat || contact.ThreatFlash) ElementDrawn("Radar threat damage flash");
        if (contact.Subject) ElementDrawn("Radar command subjects");
        if (contact.CarriedFlag) ElementDrawn("Radar carried flag marker");
        if (contact.CarriedArtifact) ElementDrawn("Radar carried artifact marker");
        if (contact.Ripcord) ElementDrawn("Radar ripcord marker");
        if (contact.Highlight) ElementDrawn("Radar construction highlight");
    }

    private void DrawRadarLabelBlock(HudContact contact, RadarLabel label, in RadarLabelView view,
        Vector2 centre, float scale)
    {
        // HUD-5b — native colorOther: brackets and label text carry the
        // brightness step (×1 important, ×0.75 queued, ×0.5 otherwise,
        // radarimage.cpp:1094-1096); the icon itself stays full colour.
        // HUD-15 — the native command icon. radarimage.cpp:1248-1253 picks
        // m_psurfaceAccepted/m_psurfaceQueued[data.m_cid]; :1305-1310 draws it at
        // 1:1 in the label block's left column, tinted data.m_color (the full side
        // colour, never the dimmed colorOther). Missing art (or c_cidDefault,
        // which the native load loop skips) keeps the A<id>/Q<id> text prefix.
        Texture2D? commandIcon = label.CommandId is { } labelCommand
            ? _nativeHud?.RadarCommandIcon((short)labelCommand, label.CommandAccepted)
            : null;
        if (!RadarCommandIcons.UseIcon(label.CommandId, commandIcon is not null)) commandIcon = null;
        // HUD-17 — the native friendly detection eye (AWF_FLIGHT_EYE_ICON =
        // "eyebmp", WinTrek/artwork.h:85; loaded once at radarimage.cpp:226).
        // It shares the command icon's left column, drawn BELOW it when both are
        // present (:1333-1352), tinted data.m_color (the full side colour, never
        // the dimmed colorOther). Missing art keeps the legacy '*' fallback in
        // the name line.
        Texture2D? eyeIcon = label.Eye ? _nativeHud?.RadarEyeIcon : null;
        float iconSize = commandIcon is not null ? 16f * scale : 0f;
        float eyeSize = eyeIcon is not null ? 16f * scale : 0f;
        float iconGap = 2f * scale;
        // Native stacks the eye directly under the command icon, no gap
        // (radarimage.cpp:1322-1326, 1342-1344).
        var left = RadarLabelLayout.LeftColumn(commandIcon is not null, iconSize,
            eyeIcon is not null, eyeSize, gap: 0f);
        float textLeft = left.Width + (left.AddsLine ? iconGap : 0f);
        string? nameLine = RadarLabelCache.SelectNameLine(view,
            commandArt: commandIcon is not null, eyeArt: eyeIcon is not null);
        var color = RadarLabelColor(contact) * RadarLabelModel.BracketBrightness(contact);
        string? rangeLine = view.RangeLine;
        int barCount = (label.Fill is not null ? 1 : 0) + (label.Shield is not null ? 1 : 0)
            + (label.Hull is not null ? 1 : 0);
        int lineCount = ((nameLine is not null || left.AddsLine) ? 1 : 0)
            + (rangeLine is not null ? 1 : 0);
        if (lineCount == 0 && barCount == 0) return;

        // FONT-1 — native radar labels use smallFont (WinTrek/radarimage.cpp:1189);
        // label tiers and placement are unchanged (font switch only).
        var radarFont = NativeFontAt(NativeFont.SmallFont);
        float lineHeight = radarFont.LineSpacing * scale;
        float maxText = 0f;
        if (nameLine is not null)
            maxText = MathF.Max(maxText, radarFont.MeasureString(HudFormat.Printable(nameLine)).X * scale);
        if (rangeLine is not null)
            maxText = MathF.Max(maxText, radarFont.MeasureString(rangeLine).X * scale);
        float barRow = barCount > 0 ? 5f * scale : 0f;
        float blockWidth = MathF.Max(maxText + textLeft, barCount * (24f + 4f) * scale + textLeft) + 8f * scale;
        float blockHeight = MathF.Max(lineCount * lineHeight, left.Height) + barRow + 6f * scale;

        // Native stacks labels along the object→centre direction; a contact is
        // kept clear by nudging the block outward from the view centre.
        var offset = new Vector2(contact.Position.X - centre.X, contact.Position.Y - centre.Y);
        if (offset.LengthSquared() < 1f) offset = new Vector2(0f, 1f);
        offset.Normalize();
        var desired = new Rectangle(
            (int)(contact.Position.X + offset.X * 12f * scale - blockWidth / 2f),
            (int)(contact.Position.Y + offset.Y * 12f * scale),
            Math.Max(1, (int)MathF.Ceiling(blockWidth)),
            Math.Max(1, (int)MathF.Ceiling(blockHeight)));
        var placed = Layout.PlaceBlock(view.BlockKey, desired);

        float y = placed.Y;
        if (commandIcon is not null)
        {
            // Native's DrawImage3D uses additive blending; the port's single
            // alpha-blended HUD batch tints the white glyph mask instead (the
            // same approximation HUD-8 records for the additive direction art).
            int side = Math.Max(1, (int)MathF.Round(iconSize));
            _sb.Draw(commandIcon, new Rectangle((int)MathF.Round(placed.X),
                (int)MathF.Round(y + (lineHeight - iconSize) / 2f), side, side),
                RadarLabelColor(contact));
        }
        if (eyeIcon is not null)
        {
            // HUD-17 — below the command icon in the same left column
            // (radarimage.cpp:1333-1352). The texture was decoded to a
            // luminance-alpha mask at load (HudNativeTextures.TryDecode), so this
            // normal alpha-blended draw reproduces native's BlendModeAdd result:
            // black texels add nothing, bright strokes multiply by the full side
            // colour and light up. Same full-colour tint rule as the command icon.
            int side = Math.Max(1, (int)MathF.Round(eyeSize));
            _sb.Draw(eyeIcon, new Rectangle((int)MathF.Round(placed.X),
                (int)MathF.Round(y + left.EyeTop + (lineHeight - eyeSize) / 2f), side, side),
                RadarLabelColor(contact));
        }
        if (nameLine is not null)
        {
            RenderText(radarFont, HudFormat.Printable(nameLine), new Vector2(placed.X + textLeft, y), color, scale);
            y += lineHeight;
        }
        else if (left.AddsLine)
        {
            y += lineHeight;
        }
        if (rangeLine is not null)
        {
            RenderText(radarFont, rangeLine, new Vector2(placed.X + textLeft, y), color, scale);
            y += lineHeight;
        }
        if (barCount > 0)
        {
            float bx = placed.X + textLeft, by = y + 2f * scale;
            if (label.Fill is { } fill)
            {
                FillRect(bx, by, 24f * scale * Math.Clamp(fill, 0f, 1f), 2f * scale, new Color(0.70f, 0.78f, 0.86f));
                bx += (24f + 4f) * scale;
            }
            if (label.Shield is { } shield)
            {
                FillRect(bx, by, 24f * scale * Math.Clamp(shield, 0f, 1f), 2f * scale, new Color(0.30f, 0.55f, 0.95f));
                bx += (24f + 4f) * scale;
            }
            if (label.Hull is { } hull)
                FillRect(bx, by, 24f * scale * Math.Clamp(hull, 0f, 1f), 2f * scale, new Color(0.95f, 0.30f, 0.22f));
        }

        if (view.Drawn.Length > 0) ElementDrawn(view.Drawn);
    }

    /// <summary>The label colour is the contact's side colour (native
    /// <c>pside-&gt;GetColor()</c>, radarimage.cpp:682-685) with the flash
    /// override; <see cref="DrawRadarLabelBlock"/> applies the native
    /// <c>colorOther</c> brightness on top.</summary>
    private static Color RadarLabelColor(HudContact contact)
    {
        if (contact.ThreatFlash) return new Color(1f, 0f, 0f);
        if (contact.SideColor is { } side) return new Color(side.R, side.G, side.B, side.A);
        return new Color(0.88f, 0.96f, 0.95f);
    }

    public Rectangle DrawMeasuredText(SpriteFont font, string text, Vector2 position, Color color, bool instrument = false, bool centerAtScale = true, float? maxWidth = null, bool background = true, bool fixedAnchor = false, float? drawScale = null)
    {
        // Long dynamic names and chat are fitted by measured width, never by a
        // character-count estimate. Retain the full value in the model.
        text = HudFormat.Printable(text);
        var measured = font.MeasureString(text);
        // FONT-1 — pane-scaled sites pass the same scale the pane uses for its
        // positions (via the HudFont hook); console sites default to TextScale.
        float scale = drawScale ?? TextScale;
        float limit = Math.Min(_viewportWidth - 16, maxWidth ?? float.MaxValue);
        if (measured.X * scale > limit)
        {
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (font.MeasureString(text[..mid] + "...").X * scale <= limit) lo = mid;
                else hi = mid - 1;
            }
            text = text[..lo] + "..."; measured = font.MeasureString(text);
        }
        if (centerAtScale) position.X += measured.X * (1 - scale) / 2;
        var bounds = Layout.Place(text, measured * scale, position, instrument, fixedAnchor);
        if (background) FillRect(bounds.X - 3, bounds.Y - 1, bounds.Width + 6, bounds.Height + 2, Color.Black * .58f);
        RenderText(font, text, new(bounds.X, bounds.Y), color, scale);
        return bounds;
    }
    protected int _viewportWidth = 1920;
    protected int _viewportHeight = 1080;
    // ── Widgets ───────────────────────────────────────────────────────────

    private void DrawWarnings(IReadOnlyList<string> warnings, float w, float h)
    {
        var warnFont = NativeFontAt(NativeFont.MedVerdana);
        for (int i = 0; i < warnings.Count; i++)
        {
            var size = warnFont.MeasureString(warnings[i]);
            DrawText(warnFont, warnings[i], new Vector2((w - size.X) / 2f, h * 0.19f + i * warnFont.LineSpacing), Warning);
        }
    }

    /// <summary>HUD-9 — console text and banners at their native dialog.mdl
    /// anchors. Classic draws the port HUD font at the native pane positions;
    /// the eject-pod/death bitmap art stays in <see cref="DrawEjectDeathOverlays"/>.
    /// Overridden by Modern to apply the HUD-2 text scale.</summary>
    protected virtual void DrawConsoleText(HudModel model, Viewport viewport)
    {
        if (!model.Visible || model.Context.Console is not { } console) return;
        float w = viewport.Width, h = viewport.Height;
        var side = console.SideColor is { } sc ? From(sc) : TextMain;
        var cortText = new Color(0.586f, 0.578f, 0.680f);   // native cortText/softText (dialog.mdl:24, :3517)
        var yellow = new Color(1f, 1f, 0f);       // native promptFont yellowText
        var red = new Color(1f, 0f, 0f);          // native critical/queued red
        float scale = TextScale;
        float lineHeight = TextLineHeight;
        float cxRight = w - 100f * scale;
        int chatLines = HudConsoleFormat.BannerChatLines(model.Context.Chat);

        // Sector location — SideTopRight(-180,-37), ofInFlight (dialog.mdl:5419).
        // The image origin is the label's top-left, 37 px below the top edge; the
        // sector name is translated 14 px below the label (dialog.mdl:1473-1485)
        // and uses cortText, not the side colour (dialog.mdl:24, :1438-1445).
        if (model.FlightVisible && console.SectorName is { } sector)
        {
            float labelTop = HudConsoleFormat.SectorLabelTop * scale;
            float nameTop = HudConsoleFormat.NameBelowLabel(labelTop, lineHeight);
            var promptFont = NativeFontAt(NativeFont.PromptFont);
            var medVerdana = NativeFontAt(NativeFont.MedVerdana);
            var psz = promptFont.MeasureString("Current location:");
            var nsz = medVerdana.MeasureString(sector);
            DrawMeasuredText(promptFont, "Current location:", new(cxRight - psz.X / 2, labelTop), yellow, fixedAnchor: true);
            DrawMeasuredText(medVerdana, sector, new(cxRight - nsz.X / 2, nameTop), cortText, fixedAnchor: true);
            ElementDrawn("Sector location");
        }

        // Turret carrier identity — SideTopRight(-180,-75) (dialog.mdl:5439-5440;
        // TurretHeader/PilotNameImage dialog.mdl:1508-1543): label at the 75 px
        // line, carrier name 14 px below it, red as native PilotNameImage.
        if (model.FlightVisible && console.BoardState == 2 && console.CarrierName is { } carrier)
        {
            float labelTop = HudConsoleFormat.TurretLabelTop * scale;
            float nameTop = HudConsoleFormat.NameBelowLabel(labelTop, lineHeight);
            var promptFont = NativeFontAt(NativeFont.PromptFont);
            var medVerdana = NativeFontAt(NativeFont.MedVerdana);
            var hsz = promptFont.MeasureString("Turret of:");
            var nsz = medVerdana.MeasureString(carrier);
            DrawMeasuredText(promptFont, "Turret of:", new(cxRight - hsz.X / 2, labelTop), yellow, fixedAnchor: true);
            DrawMeasuredText(medVerdana, carrier, new(cxRight - nsz.X / 2, nameTop), red, fixedAnchor: true);
            ElementDrawn("Turret carrier identity");
        }

        // Accepted/queued order text — SideTopLeft(50,-37), ofAlways
        // (dialog.mdl:5451; LastOrderImage dialog.mdl:1605-1648). The label is
        // the top element; queued sits 20 px and accepted 40 px below it, red
        // for the queued line and cortText for the accepted line (native
        // AcceptedOrderImage/QueuedOrderImage colours, dialog.mdl:1605-1648).
        if (console.AcceptedOrder is not null || console.QueuedOrder is not null)
        {
            float labelTop = HudConsoleFormat.OrderLabelTop * scale;
            var promptFont = NativeFontAt(NativeFont.PromptFont);
            var medVerdana = NativeFontAt(NativeFont.MedVerdana);
            DrawMeasuredText(promptFont, "Current orders:", new(HudConsoleFormat.OrderLabelX * scale, labelTop), yellow, fixedAnchor: true, centerAtScale: false);
            if (console.QueuedOrder is { } q)
            {
                DrawOrderLineGlyph(accepted: false, scale, red);
                DrawMeasuredText(medVerdana, q, new(HudConsoleFormat.OrderTextX * scale, HudConsoleFormat.OrderQueuedTop(labelTop, lineHeight)), red, fixedAnchor: true, centerAtScale: false);
                ElementDrawn("Queued order text");
            }
            if (console.AcceptedOrder is { } a)
            {
                DrawOrderLineGlyph(accepted: true, scale, yellow);
                DrawMeasuredText(medVerdana, a, new(HudConsoleFormat.OrderTextX * scale, HudConsoleFormat.OrderAcceptedTop(labelTop, lineHeight)), cortText, fixedAnchor: true, centerAtScale: false);
                ElementDrawn("Accepted order text");
            }
        }

        // Vote banner — SideTop + VoteTextImageYOffset (dialog.mdl:5174, :5352;
        // VoteTextImage dialog.mdl:2114-2120), white, 85 + 12·NumChatLines from
        // the top edge.
        if (console.VoteText is { } vote)
        {
            var voteFont = NativeFontAt(NativeFont.MedBoldVerdana);
            var vsz = voteFont.MeasureString(vote);
            DrawMeasuredText(voteFont, vote, new(w / 2f - vsz.X / 2, HudConsoleFormat.VoteTop(chatLines)), Color.White, fixedAnchor: true);
            ElementDrawn("Vote banner");
        }

        // Critical tips — SideTop + CriticalTipImageYOffset (dialog.mdl:5173,
        // :5341; CriticalTipImage dialog.mdl:2039-2078). Slot 0 (newest) is at
        // 50 + 12·NumChatLines from the top; older slots stack 14 px above it.
        int criticalCount = console.CriticalTips.Count;
        var criticalFont = NativeFontAt(NativeFont.MedBoldVerdana);
        for (int slot = 0; slot < criticalCount; slot++)
        {
            var tip = console.CriticalTips[criticalCount - 1 - slot];
            var tsz = criticalFont.MeasureString(tip.Text);
            DrawMeasuredText(criticalFont, tip.Text, new(w / 2f - tsz.X / 2, HudConsoleFormat.CriticalTipTop(chatLines, slot)), red, fixedAnchor: true);
        }
        if (criticalCount > 0) ElementDrawn("Critical tip text lines");

        // Ordinary tips — SideBottom(-300,14/25/34), side colour, retained
        // (dialog.mdl:5347-5349; TipTextImage dialog.mdl:2006-2033). FONT-1 —
        // native TipTextImage slots: slot 0 medBoldVerdana, slot 1 medVerdana,
        // slot 2 smVerdana (dialog.mdl:2006-2031).
        int ordinary = 0;
        foreach (var tip in console.OrdinaryTips)
        {
            var tipFont = NativeFontAt(ordinary switch { 0 => NativeFont.MedBoldVerdana, 1 => NativeFont.MedVerdana, _ => NativeFont.SmVerdana });
            var tsz = tipFont.MeasureString(tip);
            float fromBottom = 14f + ordinary * 11f;
            DrawMeasuredText(tipFont, tip, new(w / 2f - tsz.X / 2, h - fromBottom - tsz.Y), side);
            ordinary++;
        }
        if (ordinary > 0) ElementDrawn("Tip text lines");

        // Ripcord countdown — SideCenter(-75,15): the text's left edge sits at
        // centre-x − 75 and its top 15 px above centre (dialog.mdl:5150-5163,
        // :5429-5430). No centering; Game1 only supplies a label when the
        // ripcord start time is known.
        if (model.FlightVisible && model.Context.Ripcord is { } ripcord)
        {
            var ripcordFont = NativeFontAt(NativeFont.LgBoldVerdana);
            DrawMeasuredText(ripcordFont, ripcord, new(w / 2f - HudConsoleFormat.RipcordLeft, h / 2f - HudConsoleFormat.RipcordTop), TextMain, fixedAnchor: true, centerAtScale: false);
            ElementDrawn("Ripcord countdown");
        }
    }

    /// <summary>HUD-15 — the native order-line glyph (comacceptbmp/comqueuedbmp).
    /// LastOrderImage's two rows each translate the ImportImage3D glyph by
    /// Point(-21,-13) from the line's lower-left (Artwork/dialog.mdl:1605-1640)
    /// and tint it yellowText (accepted) or Color(1,0,0) (queued); the pane sits
    /// at SideTopLeft(50,-37) with the rows at Point(7,-20)/(7,-40)
    /// (dialog.mdl:1641-1652, :5451). <see cref="HudConsoleFormat.OrderIconTopLeft"/>
    /// is the resulting position; the glyph is 16x16 and unscaled by native.
    /// Missing art leaves the text-only line (no invented shape).</summary>
    protected void DrawOrderLineGlyph(bool accepted, float scale, Color tint)
    {
        Texture2D? art = accepted ? _nativeHud?.CommandAcceptedIcon : _nativeHud?.CommandQueuedIcon;
        if (art is null) return;
        var (left, top) = HudConsoleFormat.OrderIconTopLeft(accepted, scale);
        int size = Math.Max(1, (int)MathF.Round(HudConsoleFormat.OrderIconSize * scale));
        _sb.Draw(art, new Rectangle((int)MathF.Round(left), (int)MathF.Round(top), size, size), tint);
    }

    /// <summary>Left status panel: labelled horizontal bars for the six live
    /// 0..1 quantities, each filled proportionally and tinted by
    /// <see cref="HudFormat.StatusColor"/>.</summary>
    /// <summary>Keep the objective warning visible over the fullscreen hangar/investment pane.</summary>
    public void DrawDockedObjectives(IReadOnlyList<string> lines, Viewport viewport)
    {
        if (lines.Count == 0) return;
        BeginBatch();
        try
        {
            var dockFont = NativeFontAt(NativeFont.MedVerdana);
            for (int i = 0; i < lines.Count; i++)
            {
                var size = dockFont.MeasureString(lines[i]);
                var origin = new Vector2(viewport.Width / 2f - size.X / 2f,
                    viewport.Height - 150f - (lines.Count - 1 - i) * 26f);
                FillRect(origin.X - 8, origin.Y - 3, size.X + 16, size.Y + 6, Color.Black * 0.7f);
                DrawText(dockFont, lines[i], origin, BonusGold);
            }
        }
        finally { _sb.End(); }
    }

    private void DrawStatusPanel(in HudFrame f, float w)
    {
        const float panelX = 20f;
        const float panelTop = 168f;
        const float rowHeight = 56f;
        const float labelW = 92f;
        const float barW = 340f;
        const float barH = 16f;

        (string label, float frac)[] rows =
        {
            ("HULL",   f.HullFraction),
            ("SHIELD", f.ShieldFraction),
            ("ENERGY", f.EnergyFraction),
            // P377 — while adrift in a lifepod the FUEL row (a pod has no real
            // fuel) becomes a real OXYGEN countdown fed by the server-computed
            // remaining-oxygen fraction riding in the fuel slot.
            (f.IsAdriftLifepod ? "OXYGEN" : "FUEL", f.FuelFraction),
            ("AMMO",   f.AmmoFraction),
            ("POWER",  f.Power),
        };

        // Glass panel backing behind the whole column.
        float panelBottom = panelTop + rows.Length * rowHeight + 12f;
        FillRect(panelX - 10f, panelTop - 14f, labelW + barW + 30f, panelBottom - (panelTop - 14f), PanelBg);
        // Thin accent spine down the left edge of the panel.
        FillRect(panelX - 10f, panelTop - 14f, 2f, panelBottom - (panelTop - 14f), Accent * 0.65f);

        for (int i = 0; i < rows.Length; i++)
        {
            float y = panelTop + i * rowHeight;
            // Label.
            var label = rows[i].label;
            var statusFont = NativeFontAt(NativeFont.MedVerdana);
            var sz = statusFont.MeasureString(label);
            DrawText(statusFont, label, new Vector2(panelX, y + barH / 2f - sz.Y / 2f), TextDim);

            DrawBar(rows[i].frac, panelX + labelW + 16f, y, barW, barH);

            // P377 — beside the depleting OXYGEN bar, a real mm:ss countdown of
            // the remaining oxygen (amber under 30 s). Reflects the server's
            // own-ship oxygen fraction each tick.
            if (f.IsAdriftLifepod && i == 3)
            {
                var t = FormatCountdown(f.OxygenSecondsRemaining);
                var tsz = statusFont.MeasureString(t);
                Color c = f.OxygenSecondsRemaining <= 30 ? Warning : Accent;
                DrawText(statusFont, t, new Vector2(panelX + labelW + 16f + barW + 12f, y + barH / 2f - tsz.Y / 2f), c);
            }
        }
    }

    /// <summary>P377 — format whole seconds as <c>m:ss</c> (e.g. <c>4:32</c>)
    /// for the lifepod oxygen countdown.</summary>
    private static string FormatCountdown(int seconds)
    {
        int m = Math.Max(0, seconds) / 60;
        int s = Math.Max(0, seconds) % 60;
        return $"{m}:{s:D2}";
    }

    /// <summary>A single labelled bar: dark recessed track, accent-coloured fill
    /// proportional to the fraction (tinted by status), 1px accent border.</summary>
    private void DrawBar(float fraction, float x, float y, float w, float h)
    {
        float frac = HudFormat.Clamp01(fraction);
        var fill = From(HudFormat.StatusColor(frac));

        FillRect(x, y, w, h, new Color(0f, 0f, 0f, 0.45f));                 // recessed track
        float inset = 2f;
        if (frac > 0f)
            FillRect(x + inset, y + inset, MathF.Max(0f, (w - 2f * inset) * frac), h - 2f * inset, fill);
        OutlineRect(x, y, w, h, BorderDim);                                 // border
        FillRect(x, y, w, 1f, Accent * 0.8f);                               // top glow highlight
    }

    /// <summary>
    /// P241 — the six real original-game segmented gauges laid out symmetrically
    /// around the viewport centre, per native's own training-tutorial overlay
    /// coordinates (WinTrek/indicator.cpp:514-566): throttle lower-left, fuel
    /// lower-right, energy upper-right, ammo upper-left, hull lower-centre-left,
    /// shield lower-centre-right (P395: native's Point is Windows Y-DOWN, so the
    /// offsets are used verbatim — the earlier port wrongly negated every Y,
    /// which stuck throttle/fuel up above the crosshair as "horns"). Each gauge
    /// is its authentic decoded texture drawn with the native <c>GaugeImage</c>
    /// clipped reveal (engine/image.cpp GaugeImage::Render, JustifyBottom): only
    /// the bottom <c>value</c> fraction of the bitmap is drawn, so the segmented
    /// arc lights progressively as the quantity rises. The native offsets
    /// (screen px from centre) are stretched by a viewport-relative spread so
    /// the six gauges clear each other on any window size; the arrangement is
    /// native, the spacing is this port's.
    /// </summary>
    protected virtual void DrawGauges(HudModel model, Viewport viewport)
    {
        if (!model.InstrumentsVisible) return;
        var center = new Vector2(viewport.Width / 2f, viewport.Height / 2f);
        foreach (var gauge in model.Gauges)
        {
            if (!gauge.Visible) continue;
            var spec = ClassicGaugeTable.Spec(gauge.Kind);
            // The gauge's group origin on screen. HUD-14b uses the single native
            // pane scale for BOTH the console offset and the layer geometry so
            // the layers cannot drift apart (see ClassicGaugeLayout.PaneScale).
            var anchor = ClassicGaugeLayout.Anchor(center, spec, PaneScale);
            DrawClassicGauge(gauge, spec, anchor, PaneScale);
        }
    }

    /// <summary>The single pane scale for the classic gauges, applied to both
    /// the console offsets and every layer's local geometry (native scales the
    /// whole pane by one factor, dialog.mdl:5397). See
    /// <see cref="ClassicGaugeLayout.PaneScale"/>.</summary>
    private const float PaneScale = ClassicGaugeLayout.PaneScale;

    /// <summary>
    /// Draw one native classic gauge from its spec-table record at
    /// <paramref name="anchor"/> (its group origin on screen). Native composes
    /// each gauge as a GroupImage: frame, back, revealed fill, spine, label
    /// (dialog.mdl:2173-2516); the fill is revealed through the exact
    /// GaugeImageRect source rect with the native justification
    /// (engine/image.cpp GaugeImage::Render) and switches to the red variant on
    /// the damage flash (dialog.mdl:2404-2506). Only the % numeral is a port
    /// extension, gated behind HudSettings.GaugePercentage (default off).
    /// </summary>
    private void DrawClassicGauge(HudGauge gauge, ClassicGaugeSpec spec, Vector2 anchor, float scale)
    {
        var fill = _gauges?.Fill(gauge.Kind);
        var gaugeFont = NativeFontAt(NativeFont.SmVerdana);   // native smVerdana labels
        if (fill is null)
        {
            // Degraded slot: a compact procedural bar keeps the gauge legible.
            const float bw = 96f, bh = 12f;
            DrawBar(gauge.Fraction, anchor.X - bw / 2f, anchor.Y - bh / 2f, bw, bh);
            var lbl = gaugeFont.MeasureString(gauge.Label);
            DrawText(gaugeFont, gauge.Label, new Vector2(anchor.X - lbl.X / 2f, anchor.Y + bh / 2f + 4f), TextDim);
            return;
        }

        // Red damage-flash variant: native SwitchImage(Flash, Green, [(1,Red)])
        // for hull/shield (dialog.mdl:2418-2440, :2498-2510).
        Texture2D reveal = ClassicGaugeTable.UseRedFill(gauge.DamageFlash, spec)
            && _gauges?.Red(gauge.Kind) is { } red
            ? red : fill;

        // Layer order mirrors the native GroupImage (back-to-front).
        if (_gauges?.Frame(spec.Kind) is { } frame) DrawPlain(frame, spec.FramePoint, anchor, scale);
        DrawBack(spec, anchor, scale);
        DrawRevealed(reveal, spec, gauge.Fraction, anchor, scale);
        if (_gauges?.Spine(spec.Kind) is { } spine) DrawPlain(spine, spec.SpinePoint, anchor, scale);
        DrawGaugeLabel(gauge, spec, anchor, scale);

        // The port % numeral is an extension native has none of (default off).
        if (_gaugePercentage)
        {
            string pct = HudFormat.FormatPercent(gauge.Fraction) + "%";
            var psz = gaugeFont.MeasureString(pct);
            float top = ClassicGaugeLayout.LayerTopY(anchor, spec.FillPoint, spec.SourceRect.Height, scale)
                + (spec.SourceRect.Height + 12f) * scale;
            DrawMeasuredText(gaugeFont, pct, new Vector2(anchor.X - psz.X / 2f, top),
                From(gauge.Color), true);
        }
    }

    /// <summary>
    /// Draw the gauge's back layer. Native arc backs are <c>GaugeImageRect</c>
    /// bands at value 1 sharing the fill's rect and TranslateImage point
    /// (thrustGaugeBack dialog.mdl:2175-2185, fuel :2228, ammo :2280, energy
    /// :2333), so they are revealed through the same band path as the fill —
    /// HUD-14's <c>DrawPlain</c> stamped the whole 128px texture at BackPoint,
    /// sitting the back (15,10)·scale away from the fill. Hull/shield backs are
    /// full <c>ImportImage3D</c> textures tinted hudColorshadows (dialog.mdl:2432
    /// hullGaugeBack, :2515 shieldGaugeBack) and stay full-bleed.
    /// </summary>
    private void DrawBack(ClassicGaugeSpec spec, Vector2 anchor, float scale)
    {
        if (_gauges?.Back(spec.Kind) is not { } back) return;
        // HUD-19c — the destination comes from BackDestination (the same rect
        // the back==fill test pins), so the test exercises the drawn path.
        var dst = BackDestination(spec, anchor, scale);
        if (IsArcGauge(spec.Kind))
            _sb.Draw(back, dst, ClassicGaugeTable.RevealSource(spec.SourceRect, spec.Reveal, 1f), Color.White);
        else
            _sb.Draw(back, dst, null, HUDShadowTint);
    }

    private static bool IsArcGauge(GaugeKind kind)
        => kind is GaugeKind.Thrust or GaugeKind.Fuel or GaugeKind.Ammo or GaugeKind.Energy;

    /// <summary>HUD-14c/HUD-19c — the screen destination the back layer is
    /// drawn into for <paramref name="spec"/>; <see cref="DrawBack"/> draws into
    /// exactly this rect. For the arc gauges that is the <c>GaugeImageRect</c>
    /// band at value 1 (<see cref="ClassicGaugeLayout.RevealDest"/> at fraction
    /// 1, the same helper the fill's <see cref="DrawRevealed"/> uses);
    /// hull/shield backs are the full-bleed plain rect at
    /// <see cref="ClassicGaugeSpec.BackPoint"/>. Device-free so a renderer-level
    /// test can pin back==fill origin.</summary>
    internal Rectangle BackDestination(ClassicGaugeSpec spec, Vector2 anchor, float scale)
    {
        if (IsArcGauge(spec.Kind))
            return ClassicGaugeLayout.RevealDest(spec, 1f, anchor, scale);
        var back = _gauges?.Back(spec.Kind);
        float w = back?.Width ?? 0f, h = back?.Height ?? 0f;
        return new Rectangle(
            (int)MathF.Round(ClassicGaugeLayout.LayerLeftX(anchor, spec.BackPoint, scale)),
            (int)MathF.Round(ClassicGaugeLayout.LayerTopY(anchor, spec.BackPoint, h, scale)),
            (int)MathF.Round(w * scale), (int)MathF.Round(h * scale));
    }

    /// <summary>
    /// Draw a full native layer (frame/spine/hull&shield back) at its group
    /// TranslateImage point (engine/image.cpp TranslateImage = child origin).
    /// Native's lower-left origin (engine/context.cpp:552) puts the child point
    /// at the texture's y-up BOTTOM, so its TOP on screen is
    /// <c>anchor.Y − (point.Y + tex.Height)·scale</c>.
    /// </summary>
    private void DrawPlain(Texture2D tex, Vector2 point, Vector2 anchor, float scale, Color tint = default)
    {
        float top = ClassicGaugeLayout.LayerTopY(anchor, point, tex.Height, scale);
        var dst = new Rectangle(
            (int)MathF.Round(ClassicGaugeLayout.LayerLeftX(anchor, point, scale)),
            (int)MathF.Round(top),
            (int)MathF.Round(tex.Width * scale),
            (int)MathF.Round(tex.Height * scale));
        _sb.Draw(tex, dst, null, tint == default ? Color.White : tint);
    }

    /// <summary>
    /// Draw the native <c>GaugeImageRect</c> reveal. The source rect comes from
    /// <see cref="ClassicGaugeTable.RevealSource"/> (only <c>yoffset</c> rows of
    /// the band, from the justified edge). The band is placed in native y-up:
    /// the fill's TranslateImage child point (<c>spec.FillPoint</c>) is the
    /// band's lower-left and the band spans up to
    /// <c>FillPoint.Y + SourceRect.Height</c>; in screen y-down that makes the
    /// band top = <c>anchor.Y − (FillPoint.Y + bandH)·scale</c> and the band
    /// bottom = <c>anchor.Y − FillPoint.Y·scale</c>. JustifyTop anchors the
    /// reveal to the band TOP (grows down); JustifyBottom anchors to the band
    /// BOTTOM (grows up), matching native GaugeImageRect.
    /// </summary>
    private void DrawRevealed(Texture2D tex, ClassicGaugeSpec spec, float fraction, Vector2 anchor, float scale)
    {
        float frac = HudFormat.Clamp01(fraction);
        int yoff = (int)(spec.SourceRect.Height * frac);
        if (yoff <= 0) return;
        // Screen placement is the shared native y-up→y-down conversion (see
        // ClassicGaugeLayout.RevealDest), identical to what the tests pin.
        var dst = ClassicGaugeLayout.RevealDest(spec, frac, anchor, scale);
        _sb.Draw(tex, dst, ClassicGaugeTable.RevealSource(spec.SourceRect, spec.Reveal, frac), Color.White);
    }

    /// <summary>
    /// Draw the native StringImage label at its group point with the native
    /// justification (right-aligned for THRUST/AMMO/SHIELD, left for
    /// FUEL/ENERGY/ARMOR, dialog.mdl cited per gauge) and the hudColor tint
    /// (native StringImage hudColor, dialog.mdl:2210/2262/2315/2368/2396/2468).
    /// The substitute HUD font stays in place of native smVerdana (fonts are out
    /// of HUD-14/14b scope). HUD-14b: the drawn text is the gauge's OWN label
    /// (<see cref="ResolveGaugeLabel"/>), so the lifepod OXY override
    /// (HudModel.cs:279) is what shows.
    /// </summary>
    private void DrawGaugeLabel(HudGauge gauge, ClassicGaugeSpec spec, Vector2 anchor, float scale)
    {
        string label = ResolveGaugeLabel(gauge);
        // LabelPoint is the text's UPPER-LEFT: native StringImage::Render does
        // y -= size.Y() before DrawString (engine/image.cpp:384-393; bounds
        // Rect(0,-ysize,w,0), :353), so the text hangs below the point in y-up
        // and its screen top is anchor.Y − LabelPoint.Y·s (HUD-19c: reverts the
        // HUD-14c "minus text height" change).
        var gaugeFont = NativeFontAt(NativeFont.SmVerdana);   // native smVerdana (dialog.mdl:2204-2467)
        var size = gaugeFont.MeasureString(label);
        float posY = ClassicGaugeLayout.LabelTopY(anchor, spec.LabelPoint, scale);
        // Native StringImage box is 100 wide (dialog.mdl); JustifyRight means
        // the text's right edge meets the box's right edge at Point.X+100.
        float x = spec.LabelRightAlign
            ? ClassicGaugeLayout.LayerLeftX(anchor, spec.LabelPoint, scale) + 100f * scale - size.X * TextScale
            : ClassicGaugeLayout.LayerLeftX(anchor, spec.LabelPoint, scale);
        RenderText(gaugeFont, label, new Vector2(x, posY), HUDLabelTint, TextScale);
    }

    /// <summary>
    /// The label the renderer draws for a gauge. Native draws the spec's fixed
    /// StringImage string, but the port lets the model override it per gauge
    /// (the lifepod swaps FUEL for OXY, HudModel.cs:279) — so the renderer uses
    /// the gauge's own label, and a renderer-level test can pin the OXY case.
    /// </summary>
    internal string ResolveGaugeLabel(HudGauge gauge) => gauge.Label;

    /// <summary>
    /// HUD-6 — native centre-HUD own-ship status (<c>DefaultCenterHudOn</c>,
    /// dialog.mdl:2604-2623). Icons use the real native bitmaps at their
    /// dialog.mdl anchors; missing art falls back to a short text label.
    /// Kill bonus uses native <c>killcounterbmp</c> + black integer
    /// (dialog.mdl:2563-2567); signature uses the native <c>SIG: n%</c> string
    /// (dialog.mdl:2595-2602).
    /// </summary>
    protected virtual void DrawOwnStatus(HudModel model, Viewport viewport)
    {
        var status = model.Status;
        if (!status.Visible) return;

        DrawStatusIcon("Detection eye", status.DetectionEyeVisible, _status?.Eye, viewport);
        DrawStatusIcon("Cloak active", status.CloakActive, _status?.Cloak, viewport);
        DrawStatusIcon("Autopilot status", status.AutopilotEngaged, _status?.Autopilot, viewport);
        DrawStatusIcon("Vector lock status", status.VectorLockEngaged, _status?.VectorLock, viewport);
        DrawStatusIcon("Carried flag", status.CarriedFlag, _status?.Flag, viewport);
        DrawStatusIcon("Carried artifact", status.CarriedArtifact, _status?.Artifact, viewport);

        float spread = OwnStatusLayout.Spread(viewport.Height);   // FONT-1 own-status pane scale
        if (status.KillBonusPercent is { } kb)
        {
            var pos = OwnStatusLayout.ScreenPos("Kill bonus", viewport.Width, viewport.Height);
            var tex = _status?.KillCounter;
            var (killFont, killFs) = HudFont(NativeFont.MedBoldVerdana, spread);   // native medBoldVerdana (dialog.mdl:2556)
            if (tex is not null)
            {
                float scale = 0.72f;
                // Native ExperienceImage group: the bitmap member is translated
                // (-23,-65) from the group anchor (dialog.mdl:2563-2567).
                var bitmapPos = pos + OwnStatusLayout.KillCounterBitmapOffset(viewport.Height);
                _sb.Draw(tex, bitmapPos, null, Color.White, 0f,
                    Vector2.Zero, scale, SpriteEffects.None, 0f);
                // Native draws the integer in black in a centred 80-wide box at
                // the group-relative Point(0,5) (dialog.mdl:2561-2566).
                var numberBox = pos + OwnStatusLayout.KillCounterNumberOffset(viewport.Height);
                DrawCenteredInNativeBox(killFont, HudStringCache.KillBonusNumber(kb), numberBox, viewport.Height, Color.Black, killFs);
            }
            else
            {
                DrawMeasuredText(killFont, HudStringCache.KillBonusBadge(kb), pos, BonusGold, true, drawScale: killFs);
            }
            ElementDrawn("Kill bonus");
        }

        if (status.SignaturePercent is { } sig)
        {
            var pos = OwnStatusLayout.ScreenPos("Signature percentage", viewport.Width, viewport.Height);
            string text = HudStringCache.Signature(sig);
            // Native SignatureImage is StringImage(JustifyCenter, 80, …) whose
            // TranslateImage anchor is the box's top-left (dialog.mdl:2595-2602,
            // :2621), so the text centres inside the 80-point box.
            var (sigFont, sigFs) = HudFont(NativeFont.MedVerdana, spread);   // native medVerdana (dialog.mdl:2595)
            DrawCenteredInNativeBox(sigFont, text, pos, viewport.Height, TextMain, sigFs);
            ElementDrawn("Signature percentage");
        }

        // HUD-12 — continuous native cloak strength, 100·(1−GetCloaking())
        // (consoledata.cpp:480-489). Native's CortUI glass gauge draws this as a
        // swept glass shape (dialog.mdl:570-597); the port's HUD shows the same
        // value as a compact bar + percentage, with the native drained marker at
        // energy <= .2 (dialog.mdl:651-656). HUD-12b — before the first
        // OwnShipCloakUpdate(220) the strength is unknown, so the legacy
        // "CLOAK DRAINED" label must still appear from the state-bit fallback.
        if (status.CloakActive
            && HudStringCache.CloakStatusText(status.CloakDrained, status.CloakStrengthPercent) is { } cloakText)
        {
            var pos = OwnStatusLayout.ScreenPos("Cloak strength and drained state", viewport.Width, viewport.Height);
            var (cloakFont, cloakFs) = HudFont(NativeFont.MedVerdana, spread);   // port addition (native uses CortUI glass)
            DrawMeasuredText(cloakFont, cloakText, pos, status.CloakDrained ? Warning : TextMain, true, drawScale: cloakFs);
            ElementDrawn("Cloak strength and drained state");
        }
    }

    /// <summary>HUD-12 — the native lives/kills badge group (dialog.mdl:5056-5128,
    /// SideTopLeft anchors at dialog.mdl:5441-5445). Lives show only below 10
    /// (<c>console.cpp:814-817</c>); kills only in a deathmatch
    /// (<c>console.cpp:818-821</c>). The badge art is <c>livesbmp</c> /
    /// <c>hudKillsbmp</c>; the native centred 80-wide text box sits at the group
    /// offsets (18,20) / (15,20) (dialog.mdl:5070-5080,5107-5120).
    /// </summary>
    private void DrawLivesKillsBadges(HudModel model, Viewport viewport)
    {
        if (!model.FlightVisible || model.Context.Console is not { } console) return;
        if (HudConsoleFormat.LivesVisible(console.Lives) && console.Lives is { } lives)
        {
            DrawConsoleBadge(_status?.LivesBadge, HudStringCache.Lives(lives), 45f, 97f, 18f);
            ElementDrawn("Lives remaining");
        }
        if (HudConsoleFormat.KillsVisible(console.Deathmatch, console.MissionKills)
            && console.MissionKills is { } kills)
        {
            DrawConsoleBadge(_status?.KillsBadge, HudStringCache.Kills(kills), 45f, 177f, 15f);
            ElementDrawn("Mission kills");
        }
    }

    /// <summary>Draw one native badge group: the bitmap at the native anchor and
    /// the centred 80-wide text box 20 px above its lower edge (dialog.mdl:5070-5080).</summary>
    private void DrawConsoleBadge(Texture2D? art, string text, float x, float top, float textOffsetX)
    {
        float artHeight = art?.Height ?? 34f;
        if (art is not null)
            _sb.Draw(art, new Vector2(x, top), null, Color.White, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
        // FONT-1 — native lives/kills badges use medBoldVerdana (dialog.mdl:5070-5128)
        // at the own-status pane scale.
        var (font, fs) = HudFont(NativeFont.MedBoldVerdana, OwnStatusLayout.Spread(_viewportHeight));
        var size = font.MeasureString(text) * fs;
        float boxWidth = 80f * TextScale;
        float boxLeft = x + textOffsetX * TextScale;
        float boxBottom = top + artHeight - 20f * TextScale;
        FillRect(boxLeft - 2, boxBottom - size.Y - 1, boxWidth + 4, size.Y + 2, Color.Black * .58f);
        RenderText(font, text, new(boxLeft + (boxWidth - size.X) / 2f, boxBottom - size.Y), Color.White, fs);
    }

    /// <summary>HUD-12 — the native command-view ship summary
    /// (<c>cortCommandModeInfo</c>, <c>Artwork/dialog.mdl:4997-5027</c>), shown
    /// under <c>ofInFlightCommand</c> at <c>SideLeft(-200,120)→(36,120)</c>
    /// (dialog.mdl:5412). It reads the same console data native feeds every frame
    /// (<c>WinTrek/console.cpp:778-800</c>): the source ship
    /// (<c>m_pmodelDataMe</c>), its shields/hull, the energy/ammo/fuel displays,
    /// thrust/speed/signature/kill bonus, and the selected target — plus the
    /// console's money, autopilot and radar-mode readouts. The native group uses
    /// <c>softgaugebmp</c> for every gauge (dialog.mdl:4735-4840); the port draws
    /// the same bars when the art is loaded and plain bars otherwise. Typography
    /// and exact row metrics are an approximation of the native medVerdana font.
    /// </summary>
    protected virtual void DrawCommandShipSummary(HudModel model, Viewport viewport)
    {
        if (model.Telemetry is not { } f) return;
        float w = viewport.Width, h = viewport.Height;
        // Native SideLeft anchor: x = left + 36; the group's lower-left edge is
        // 120 px above the vertical centre in y-up, i.e. screen y = h/2 - 120,
        // and its rows run downward (negative y translations).
        float x = 36f;
        float y = h / 2f - 120f;
        // Native rows are 12 px apart in medVerdana; the port's HUD spritefont
        // is taller, so advance by its measured line height to keep the rows
        // readable (the native GROUP ORDER is preserved).
        float line = NativeFontAt(NativeFont.MedVerdana).LineSpacing * TextScale + 1f;
        // FONT-1 — native command summary labels/values use medVerdana
        // (dialog.mdl:4726-4910), ship type / station name medBoldVerdana
        // (dialog.mdl:4717-4721, :4988-4992), at the own-status pane scale.
        float spread = OwnStatusLayout.Spread(_viewportHeight);
        var (summaryFont, summaryFs) = HudFont(NativeFont.MedVerdana, spread);
        var (summaryBold, summaryBoldFs) = HudFont(NativeFont.MedBoldVerdana, spread);
        var soft = _nativeHud?.SoftGauge;
        Color side = model.Context.TargetPane?.SideColor is { } sc
            ? new Color(sc.R, sc.G, sc.B) : Accent;

        string type = model.Telemetry?.ShipTypeName ?? "ship";
        DrawSummaryText(summaryBold, summaryBoldFs, HudStringCache.ShipSummary(type), new Vector2(x, y + 3f), side);
        y += 20f;
        DrawCommandGauge("Shields:", f.ShieldFraction, ref y, x, soft, line);
        DrawCommandGauge("Hull:", f.HullFraction, ref y, x, soft, line);
        DrawCommandGauge("Energy:", f.EnergyFraction, ref y, x, soft, line);
        DrawCommandGauge("Ammo:", f.AmmoFraction, ref y, x, soft, line);
        DrawCommandGauge("Fuel:", f.FuelFraction, ref y, x, soft, line);
        DrawCommandGauge("Thrust:", (f.Throttle + 1f) * 0.5f, ref y, x, soft, line);
        y += 4f;
        DrawSummaryText(summaryFont, summaryFs, HudStringCache.Speed(f.Speed, model.SpeedUnit), new Vector2(x, y), TextMain);
        y += line;
        if (f.SignaturePercent is { } sig)
        {
            DrawSummaryText(summaryFont, summaryFs, HudStringCache.Signature(sig), new Vector2(x, y), TextMain);
            y += line;
        }
        if (f.KillBonusPercent > 0f || f.KillBonusStreak > 0)
        {
            DrawSummaryText(summaryFont, summaryFs, HudStringCache.KillBonusLine(f.KillBonusPercent), new Vector2(x, y), BonusGold);
            y += line;
        }
        // Money / autopilot / radar mode are the console's other per-frame
        // readouts (console.cpp:791-793); native draws them in their own HUD
        // slots, so the command summary echoes them here in one compact line.
        DrawSummaryText(summaryFont, summaryFs, HudStringCache.SummaryLine(f.Money, f.AutopilotEngaged, model.Context.RadarMode),
            new Vector2(x, y), TextDim);
        y += line + 4f;
        string? target = model.Context.Target?.Name ?? model.Context.TargetPane?.Name;
        if (target is { Length: > 0 })
            DrawSummaryText(summaryFont, summaryFs, HudStringCache.TargetSummary(target), new Vector2(x, y), TextMain);
        ElementDrawn("Command view ship summary");
    }

    /// <summary>Draw a command-summary line at its OWN fixed native anchor (the
    /// pane lays its rows out itself; the shared collision layout must not
    /// relocate them, or the native vertical order breaks).</summary>
    private void DrawSummaryText(SpriteFont font, float scale, string text, Vector2 position, Color color) =>
        DrawMeasuredText(font, text, position, color, instrument: true,
            background: false, fixedAnchor: true, drawScale: scale);

    private void DrawCommandGauge(string label, float fraction, ref float y, float x,
        Texture2D? art, float line)
    {
        fraction = HudFormat.Clamp01(fraction);
        float spread = OwnStatusLayout.Spread(_viewportHeight);
        var (font, fs) = HudFont(NativeFont.MedVerdana, spread);
        DrawSummaryText(font, fs, label, new Vector2(x, y), TextDim);
        // Native softgaugebmp is 52x4 pixels (its real GaugeImage size); keep that
        // aspect instead of stretching it, revealing the left fraction (native
        // GaugeImage JustifyLeft). Scale with the HUD cluster's spread so the
        // summary matches the other centre gauges.
        float bx = x + 66f * spread, by = y + 2f;
        float gw = (art?.Width ?? 52f) * spread, gh = MathF.Max(3f, (art?.Height ?? 4f) * spread);
        if (art is not null)
        {
            int srcW = Math.Max(1, (int)MathF.Round(art.Width * fraction));
            _sb.Draw(art, new Rectangle((int)bx, (int)by, (int)MathF.Round(srcW * spread), (int)gh),
                new Rectangle(0, 0, srcW, art.Height), Color.White);
        }
        else
        {
            FillRect(bx, by, gw, gh, new Color(0f, 0f, 0f, .45f));
            FillRect(bx, by, gw * fraction, gh, Accent);
        }
        DrawSummaryText(font, fs, HudStringCache.Percent(fraction), new Vector2(bx + gw + 6f, y), TextMain);
        y += line;
    }

    private void DrawStatusIcon(string id, bool shown, Texture2D? tex, Viewport viewport)
    {
        if (!shown) return;
        var pos = OwnStatusLayout.ScreenPos(id, viewport.Width, viewport.Height);
        if (tex is not null)
        {
            // The PNG icons (autopilot/vector lock) are full-bleed 32px glyphs;
            // the MDL icons are large canvases with a small centred glyph, drawn
            // at the same 0.72 scale the six gauges use. The native
            // TranslateImage anchor is the image CORNER (engine/image.cpp:834-845),
            // so draw from the top-left without centring or nudging.
            float scale = tex.Width <= 64 ? 1f : 0.72f;
            _sb.Draw(tex, pos, null, Color.White, 0f,
                Vector2.Zero, scale, SpriteEffects.None, 0f);
        }
        else
        {
            string label = id switch
            {
                "Detection eye" => "EYE",
                "Cloak active" => "CLOAK",
                "Autopilot status" => "AUTOPILOT",
                "Vector lock status" => "VECTOR LOCK",
                "Carried flag" => "FLAG",
                "Carried artifact" => "ARTIFACT",
                _ => id,
            };
            var (font, fs) = HudFont(NativeFont.MedVerdana, OwnStatusLayout.Spread(viewport.Height));
            DrawMeasuredText(font, label, pos, TextMain, true, drawScale: fs);
        }
        ElementDrawn(id);
    }

    /// <summary>Draw a native <c>StringImage(JustifyCenter, 80, …)</c> string: its
    /// box top-left is the TranslateImage anchor and the text is centred inside
    /// the 80-point box (dialog.mdl:2595-2602; Artwork/dialog.mdl:2561-2566).</summary>
    private void DrawCenteredInNativeBox(SpriteFont font, string text, Vector2 boxTopLeft,
        int viewportHeight, Color color, float? scale = null)
    {
        float textScale = scale ?? TextScale;
        var size = font.MeasureString(text) * textScale;
        float boxWidth = OwnStatusLayout.NativeTextWidth * OwnStatusLayout.Spread(viewportHeight);
        RenderText(font, text, new(boxTopLeft.X + (boxWidth - size.X) / 2f, boxTopLeft.Y), color, textScale);
    }

    /// <summary>Bottom-center speed readout: large accent numeral + unit label.
    /// FONT-1 — native SpeedImage uses lgBoldVerdana with
    /// <c>NumberString(speed) + " mps"</c> (dialog.mdl:2546-2551); the port's
    /// model carries the numeral and unit separately, so the numeral stays
    /// lgBoldVerdana and the unit line uses smVerdana (position still differs —
    /// the Speed row stays partial).</summary>
    private void DrawSpeed(float speed, string unit, float w, float cy)
    {
        string txt = HudFormat.FormatSpeed(speed);
        var bigFont = NativeFontAt(NativeFont.LgBoldVerdana);
        var sz = bigFont.MeasureString(txt);
        float cx = w / 2f;
        DrawText(bigFont, txt, new Vector2(cx - sz.X / 2f, cy), Accent);

        var unitFont = NativeFontAt(NativeFont.SmVerdana);
        var usz = unitFont.MeasureString(unit);
        DrawText(unitFont, unit, new Vector2(cx - usz.X / 2f, cy + sz.Y + 2f), TextDim);
    }

    /// <summary>Right-side vertical throttle gauge: -1 (idle) at the bottom,
    /// +1 (full forward) at the top, centre tick at 0, moving marker line.</summary>
    private void DrawThrottleGauge(float throttle, float w, float h)
    {
        float gx = w - 74f;
        float top = h * 0.22f;
        float bottom = h * 0.72f;
        float t = HudFormat.ThrottlePercent(throttle) / 100f;   // 0..1, 0=idle(bottom)
        float markY = bottom + (top - bottom) * t;

        // Centre tick at 0.
        float zeroY = bottom + (top - bottom) * 0.5f;
        FillRect(gx - 8f, zeroY, 16f, 2f, BorderDim);

        // Track.
        FillRect(gx - 1.5f, top, 3f, bottom - top, new Color(0f, 0f, 0f, 0.5f));
        OutlineRect(gx - 6f, top, 12f, bottom - top, BorderDim);

        // Marker (fills full track width).
        FillRect(gx - 14f, markY - 1.5f, 28f, 3f, Accent);

        string lbl = "THR";
        var throttleFont = NativeFontAt(NativeFont.MedVerdana);
        var sz = throttleFont.MeasureString(lbl);
        DrawText(throttleFont, lbl, new Vector2(gx - sz.X / 2f, bottom + 14f), TextDim);
    }

    /// <summary>Top-center afterburner status + reactor power readout.</summary>
    private void DrawAfterburner(bool active, float power, float w)
    {
        string ab = active ? "AFTERBURNER ACTIVE" : "AFTERBURNER";
        var color = active ? Accent : TextDim;
        var abFont = NativeFontAt(NativeFont.MedVerdana);
        var sz = abFont.MeasureString(ab);
        float cx = TopStatusCenterX(w);
        DrawText(abFont, ab, new Vector2(cx - sz.X / 2f, 20f), color);

        string pwr = "PWR " + HudFormat.FormatPercent(power) + "%";
        var psz = abFont.MeasureString(pwr);
        DrawText(abFont, pwr, new Vector2(cx - psz.X / 2f, 20f + sz.Y + 2f), TextDim);
    }

    /// <summary>
    /// P225 — the fire-mode / weapon-selection readout, a single top-center
    /// label just beneath the afterburner block. Shows "WEAPONS: ALL" in
    /// group-fire mode or "WEAPONS: MOUNT n" (the selected fixed mount) in
    /// single-weapon mode — the pilot-facing complement to the reticle FIRING
    /// tag. Pure ASCII (see the P214 spritefont char-range pitfall).
    /// </summary>
    private void DrawWeaponMode(in HudFrame f, float w)
    {
        string mode = f.GroupFire
            ? "WEAPONS: ALL"
            : $"WEAPONS: MOUNT {f.SelectedMount}";
        // Highlight the non-default single-weapon mode (accent), dim the default.
        var color = f.GroupFire ? TextDim : Accent;
        var modeFont = NativeFontAt(NativeFont.MedVerdana);
        var sz = modeFont.MeasureString(mode);
        float cx = TopStatusCenterX(w);
        DrawText(modeFont, mode, new Vector2(cx - sz.X / 2f, 58f), color);
    }

    /// <summary>P255 — the "MINING" tag: the word centred just beneath the
    /// weapon-mode readout, in a warm amber. Pure ASCII (spritefont char-range
    /// pitfall, cf. <see cref="DrawWeaponMode"/>).</summary>
    private void DrawMining(float w)
    {
        const string tag = "MINING";
        var color = new Color(0.98f, 0.74f, 0.24f);   // warm amber — activity, not alarm
        var miningFont = NativeFontAt(NativeFont.MedVerdana);
        var sz = miningFont.MeasureString(tag);
        float cx = TopStatusCenterX(w);
        DrawText(miningFont, tag, new Vector2(cx - sz.X / 2f, 82f), color);
    }

    /// <summary>
    /// P618 — the native-styled TIME DILATION indicator, drawn prominently at the
    /// top-centre of the flight HUD whenever the server has slowed the game clock
    /// below full speed (scale &lt; 100%). Warm amber — a system/performance
    /// condition (like EVE Online's TiDi readout), distinct from the cyan
    /// ripcord / violet cloak / amber-mining activity tags below it. Shows the
    /// live scale as a whole percent so a battle's slow-motion is legible at a
    /// glance. Pure ASCII (spritefont char-range pitfall).
    /// </summary>
    private void DrawTimeDilation(float percent, float w)
    {
        int pct = (int)Math.Round(percent);
        string label = pct >= 100 ? "TIME DILATION 100%" : $"TIME DILATION {pct}%";
        var color = new Color(0.98f, 0.66f, 0.20f);   // warm amber — performance/system condition
        var tidiFont = NativeFontAt(NativeFont.MedVerdana);
        var sz = tidiFont.MeasureString(label);
        float cx = TopStatusCenterX(w);
        DrawText(tidiFont, label, new Vector2(cx - sz.X / 2f, 16f), color);
    }

    /// <summary>
    /// P366 — the leader-only bot dock/launch prompt: a compact bottom-centre
    /// line (above the throttle) naming the command + the target bot, in the HUD
    /// accent. Present ONLY when the local player is the team leader with a
    /// same-side bot selected — Game1's <see cref="LeaderBotCommand"/> is null
    /// for a non-leader, so this simply never draws for them.</summary>
    private void DrawLeaderCommand(string label, float w, float cy)
    {
        var leaderFont = NativeFontAt(NativeFont.MedVerdana);
        var sz = leaderFont.MeasureString(label);
        float cx = w / 2f;
        // P409 — raised above the bottom-centre speed numeral so the
        // leader-command prompt never overprints it.
        DrawText(leaderFont, label, new Vector2(cx - sz.X / 2f, cy), Accent);
    }

    /// <summary>
    /// P425 — the commanded-ship (commandee) readout: a compact bottom-centre
    /// line naming the same-side ship/drone the pilot is currently commanding
    /// plus the issue-order key. Drawn JUST ABOVE the leader-command slot
    /// (h - 164) in a distinct mint colour so the two command affordances never
    /// collide or read the same. Present only while a commandee is selected
    /// (Game1's <c>commandeeLabel</c> is null otherwise).</summary>
    private void DrawCommandee(string label, float w, float cy)
    {
        var cmdFont = NativeFontAt(NativeFont.MedVerdana);
        var sz = cmdFont.MeasureString(label);
        float cx = w / 2f;
        var mint = new Color(0.55f, 0.95f, 0.65f);   // mint — distinct from the leader cyan accent
        DrawText(cmdFont, label, new Vector2(cx - sz.X / 2f, cy), mint);
    }

    /// <summary>
    /// P442 — the always-visible money readout (top-left, above the kill
    /// bonus): a small "MONEY  $X" line showing the local player's wallet,
    /// formatted via <see cref="HudFormat.FormatMoney"/> exactly as native
    /// presents money ("$%d", no grouping, teampane.cpp:208,226). The caller
    /// only invokes it when the wallet is KNOWN (HudFrame.Money non-null), so
    /// an unknown wallet renders nothing rather than a misleading 0.
    /// </summary>
    private void DrawMoney(int money, float w)
    {
        const float x = 24f;
        const float y = 18f;
        string val = HudFormat.FormatMoney(money);
        var moneyFont = NativeFontAt(NativeFont.MedVerdana);
        var vsz = moneyFont.MeasureString(val);
        // Value in the teal accent; a dim "MONEY" caption above it.
        DrawText(moneyFont, "MONEY", new Vector2(x, y), TextDim);
        DrawText(moneyFont, val, new Vector2(x, y + vsz.Y + 2f), Accent);
    }

    /// <summary>
    /// P298 — the Roll gauge (top-right): the ship's real current roll angle in
    /// degrees about its forward axis (client-derived from the orientation this
    /// client already tracks for the chase/cockpit cameras), shown as a centred
    /// numeral plus a horizontal deflection dial whose marker swings left/right
    /// with the bank (±180° full travel, centre tick at 0°/level). Native has no
    /// persistent roll instrument — only the <c>RollTrainingOverlay</c> tutorial
    /// text (WinTrek/indicator.cpp:634-641, "PUSH Q AND E TO ROLL LEFT AND
    /// RIGHT" at centre +(−110,−170)) — so this is the port's live readout of an
    /// attitude axis the HUD otherwise never surfaces. Positive degrees = the
    /// bank direction native's <c>Orientation.Roll(+θ)</c> produces.
    /// </summary>
    private void DrawRoll(float rollDegrees, float w, float h)
    {
        const float halfW = 70f;
        float cx = w - 96f;
        float top = h * 0.085f;
        const float barH = 4f;

        // Numeric readout centred above the dial (pure ASCII — the HUD font
        // bakes only 32..126, no degree glyph, so spell it out).
        string txt = $"{(int)MathF.Round(rollDegrees):+000;-000;+000}deg";
        var rollFont = NativeFontAt(NativeFont.MedVerdana);
        var sz = rollFont.MeasureString(txt);
        DrawText(rollFont, txt, new Vector2(cx - sz.X / 2f, top), Accent);

        // Horizontal dial track with a centre tick (0° = level).
        float barY = top + sz.Y + 8f;
        FillRect(cx - halfW, barY, 2f * halfW, barH, new Color(0f, 0f, 0f, 0.5f));   // track
        OutlineRect(cx - halfW, barY, 2f * halfW, barH, BorderDim);
        FillRect(cx - 1f, barY - 3f, 2f, barH + 6f, TextDim);                          // centre tick
        FillRect(cx - halfW, barY - 1.5f, 2f, barH + 3f, TextDim);                     // -90
        FillRect(cx + halfW - 2f, barY - 1.5f, 2f, barH + 3f, TextDim);                // +90

        // Deflection marker: swings with the bank, clamped to the track.
        float t = Math.Clamp(rollDegrees / 180f, -1f, 1f);
        float mx = cx + t * (halfW - 2f);
        FillRect(mx - 8f, barY - 2f, 16f, barH + 4f, rollDegrees == 0f ? TextDim : Accent);

        // Caption under the dial.
        string lbl = "ROLL";
        var lsz = rollFont.MeasureString(lbl);
        DrawText(rollFont, lbl, new Vector2(cx - lsz.X / 2f, barY + barH + 5f), TextDim);
    }

    /// <summary>
    /// P362 — the weapon/systems status list: a small persistent glass panel on
    /// the right edge naming each mounted weapon/missile system (ASCII-safe
    /// short name) with its live ammo count and a per-system status derived from
    /// P361's auto-reload behaviour. Each row shows the system name, an ammo
    /// numeral plus a thin <see cref="DrawBar"/> fraction (its share of the live
    /// shared magazine), and a colour-coded status tag — dim <c>READY</c>,
    /// amber <c>RELOAD</c> (magazine hit zero, auto-reload in flight), red
    /// <c>DEPLETED</c> (auto-reload failed, no spare in cargo). Non-ammo systems
    /// (energy weapons, missile racks) render their always-ready <c>READY</c>
    /// with a <c>--</c> ammo, so distinct mounts show genuinely independent
    /// statuses. Reuses the same font, colours and <see cref="DrawBar"/>/
    /// <see cref="FillRect"/> primitives as every other widget — no new asset.
    /// Must be called inside an active <see cref="SpriteBatch"/> session.
    /// </summary>
    private void DrawWeaponStatusList(IReadOnlyList<WeaponStatusRow> rows, Viewport viewport)
    {
        float scale = TextScale;
        var wsFont = NativeFontAt(NativeFont.MedVerdana);
        float line = wsFont.LineSpacing * scale + 5;
        float panelW = Math.Min(viewport.Width * .34f, 380 * Math.Max(1, scale));
        float x = viewport.Width - panelW - 18;
        float y = Math.Max(150, line * 4);
        FillRect(x, y, panelW, line * (1 + rows.Count * 3), PanelBg);
        DrawMeasuredText(wsFont, "SYSTEMS", new(x + 8, y), TextDim, centerAtScale: false);
        y += line;
        foreach (var r in rows)
        {
            DrawMeasuredText(wsFont, r.Name, new(x + 8, y), TextMain, centerAtScale: false);
            y += line;
            var (tag, col) = StatusTag(r.Status);
            string ammo = r.ConsumesAmmo ? $"{r.Ammo}/{r.CapacityAmmo}" : "--";
            // One measured string keeps NOT READY and the magazine distinct.
            DrawMeasuredText(wsFont, $"{ammo}  |  {tag}", new(x + 8, y), col, centerAtScale: false);
            y += line;
            DrawBar(r.CapacityAmmo > 0 ? (float)r.Ammo / r.CapacityAmmo : 0, x + 8, y, panelW - 16, 3);
            y += line;
        }
    }

    /// <summary>The per-system status tag text + colour (P362).</summary>
    private static (string tag, Color color) StatusTag(WeaponStatus s) => s switch
    {
        WeaponStatus.Reloading => ("RELOAD", ReloadAmber),
        WeaponStatus.NotReady => ("NOT READY", ReloadAmber),
        WeaponStatus.Depleted => ("DEPLETED", Warning),
        _ => ("READY", TextDim),
    };

    /// <summary>
    /// HUD-19 — draw the native-additive centre indicators through one One/One
    /// pass (<see cref="AdditiveOneOne"/>): the cockpit <c>directioninbmp</c>
    /// reticle and every <c>centeroutbmp</c> draw (cockpit aim predictor,
    /// chase/missile aim point, chase aim with no target). Only the REAL
    /// artwork participates here; the procedural fallbacks and the Modern skin
    /// stay on the alpha batch. Begins/ends its own additive SpriteBatch
    /// session inside the outer alpha batch (same pattern as the gauge pass).
    /// </summary>
    private void DrawCentreIndicatorsAdditive(HudModel model, Viewport viewport, bool firing, bool turretView)
    {
        // Native BlendModeAdd draws only when the artwork decoded (indicator.cpp
        // falls back to a 2-D DrawImage for no art, which inherits the context
        // blend — the port keeps those fallbacks on alpha).
        bool hasReticle = DrawsAdditiveReticle(model, turretView, _indicators?.Reticle is not null);
        bool hasAim = model.AimPredictorVisible && _indicators?.CrosshairOut is not null;
        bool hasChase = model.ChaseAimVisible && _indicators?.CrosshairOut is not null;
        if (!hasReticle && !hasAim && !hasChase) return;

        _sb.End();
        _sb.Begin(SpriteSortMode.Deferred, AdditiveOneOne);
        try
        {
            if (hasReticle)
            {
                // Native directioninbmp reticle at view centre,
                // Color(0.4,0.4,0.4,0.4) BlendModeAdd (indicator.cpp:149-150).
                // One/One on the premultiplied texture ⇒ tint rgb = 0.4·0.4.
                DrawCentered(_indicators!.Reticle!,
                    new Vector2(viewport.Width / 2f, viewport.Height / 2f),
                    AdditiveTint(0.4f, 0.4f, 0.4f, 0.4f));
            }
            if (hasAim)
            {
                var aim = model.Context.Lead!.Value;
                Vector2 center = model.Context.Camera == CameraMode.Cockpit
                    ? new Vector2(viewport.Width / 2f, viewport.Height / 2f)
                    : new Vector2(aim.Center.X, aim.Center.Y);
                // Native centeroutbmp (1-g,g,0) in range / (0.33,0.33,0.33,0.5)
                // out of range, BlendModeAdd (indicator.cpp:361-362; chase/missile
                // aim point indicator.cpp:268-276).
                Color tint = aim.InRange
                    ? AdditiveTint(1f - aim.Green, aim.Green, 0f, 1f)
                    : AdditiveTint(0.33f, 0.33f, 0.33f, 0.5f);
                DrawCentered(_indicators!.CrosshairOut!, center, tint);
            }
            if (hasChase)
            {
                var chase = model.Context.ChaseAim!.Value;
                // Native centeroutbmp (0.33,0.33,0.33,0.5) BlendModeAdd
                // (indicator.cpp:399-400).
                DrawCentered(_indicators!.CrosshairOut!,
                    new Vector2(chase.X, chase.Y),
                    AdditiveTint(0.33f, 0.33f, 0.33f, 0.5f));
            }
        }
        finally
        {
            _sb.End();
            _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        }
    }

    /// <summary>HUD-19c — whether the additive directioninbmp reticle draws.
    /// Native draws it only inside <c>if (GetCameraMode() == cmCockpit)</c>
    /// (WinTrek/indicator.cpp:132, :149-150), which the port models as
    /// <see cref="HudModel.ReticleVisible"/>; the turret view and missing
    /// artwork also suppress it. Pure so a headless test pins the cockpit gate
    /// (HUD-19 dropped it, drawing the reticle in chase/external views).</summary>
    internal static bool DrawsAdditiveReticle(HudModel model, bool turretView, bool hasArtwork)
        => model.ReticleVisible && !turretView && hasArtwork;

    protected virtual void DrawCrosshair(Vector2 c, bool firing, bool turret)
    {
        var accent = firing ? Warning : Accent;
        var crossFont = NativeFontAt(NativeFont.MedVerdana);   // port FIRING/TURRET tag
        const int segments = 36;
        float radius = turret ? 34f : 26f;

        // HUD-19 — the native directioninbmp reticle itself is now drawn
        // additively in DrawCentreIndicatorsAdditive (indicator.cpp:149-150);
        // here, when that artwork is present, only the port's FIRING tag
        // remains (the reticle's grey/additive tint never touches the alpha
        // batch). With no artwork the procedural ring fallback below runs.
        bool nativeReticle = !turret && _indicators?.Reticle is not null;
        if (nativeReticle)
        {
            if (firing)
            {
                string lbl = "FIRING";
                var sz = crossFont.MeasureString(lbl);
                DrawText(crossFont, lbl, new Vector2(c.X - sz.X / 2f, c.Y + radius + 18f), Warning);
            }
            return;
        }

        // P464 — a seated gunner gets a distinct, larger turret reticle so the
        // aim marker reads clearly as "turret" (a bold double-ring cross with a
        // centre pip) rather than the pilot crosshair. Drawn at the same screen
        // centre, in the same accent, and it too turns the warning colour +
        // shows a TURRET tag while the fire button is held.
        if (turret)
        {
            // Outer ring.
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * MathHelper.TwoPi / segments;
                float a1 = (i + 1) * MathHelper.TwoPi / segments;
                var p0 = c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius;
                var p1 = c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius;
                DrawLine(p0, p1, 1.4f, accent * 0.9f);
            }
            // Inner ring (half radius).
            float ir = radius * 0.55f;
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * MathHelper.TwoPi / segments;
                float a1 = (i + 1) * MathHelper.TwoPi / segments;
                var p0 = c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * ir;
                var p1 = c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * ir;
                DrawLine(p0, p1, 1.1f, accent * 0.7f);
            }
            // Four cardinal cross bars spanning the two rings.
            foreach (var dir in new[] { new Vector2(0f, -1f), new Vector2(0f, 1f), new Vector2(-1f, 0f), new Vector2(1f, 0f) })
            {
                var inner = c + dir * ir;
                var outer = c + dir * radius;
                DrawLine(inner, outer, 1.6f, accent);
            }
            FillRect(c.X - 2f, c.Y - 2f, 4f, 4f, accent);
            if (firing)
            {
                string lbl = "TURRET";
                var sz = crossFont.MeasureString(lbl);
                DrawText(crossFont, lbl, new Vector2(c.X - sz.X / 2f, c.Y + radius + 20f), Warning);
            }
            return;
        }

        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathHelper.TwoPi / segments;
            float a1 = (i + 1) * MathHelper.TwoPi / segments;
            var p0 = c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius;
            var p1 = c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius;
            DrawLine(p0, p1, 1.3f, accent * 0.9f);
        }

        // Four cardinal ticks outside the ring.
        foreach (var dir in new[] { new Vector2(0f, -1f), new Vector2(0f, 1f), new Vector2(-1f, 0f), new Vector2(1f, 0f) })
        {
            var inner = c + dir * radius;
            var outer = c + dir * (radius + 6f);
            DrawLine(inner, outer, 1.5f, accent);
        }

        FillRect(c.X - 2f, c.Y - 2f, 4f, 4f, accent);

        // P221 — FIRING tag while group-fire is engaged (below the ring).
        if (firing)
        {
            string lbl = "FIRING";
            var sz = crossFont.MeasureString(lbl);
            DrawText(crossFont, lbl, new Vector2(c.X - sz.X / 2f, c.Y + radius + 18f), Warning);
        }
    }

    // ── Primitive helpers ─────────────────────────────────────────────────

    /// <summary>
    /// P230/P428 — draw the missile-lock indicator around a projected on-screen
    /// centre. Since P428 this is the REAL original-game sprite swap native
    /// performs (radarimage.cpp:217-218,330-332): partial draws
    /// <c>plockbmp.mdl</c> (the orange corner brackets), full draws
    /// <c>lockedbmp.mdl</c> (the same brackets plus the green 4-petal
    /// flower-burst — <see cref="LockTextures"/>). No lock is drawn at all —
    /// native shows nothing for <c>lock == 0</c> (radarimage.cpp:330). The
    /// sprite is scaled by <see cref="LockBracketStyle.HalfSizeFor"/>'s gentle
    /// pulse (the P230 animation kept so a live lock reads as working, not a
    /// frozen sticker); its own fixed orange/green colours are preserved
    /// (drawn untinted). If the real asset is unavailable the old procedural
    /// corner-L brackets remain as the graceful fallback.
    /// Must be called inside an active <see cref="SpriteBatch"/> session (i.e.
    /// from <see cref="Draw"/>).
    /// </summary>
    protected virtual void DrawLockBracket(in LockBracketView v)
    {
        // Native draws no lock indicator for lock == 0 (radarimage.cpp:330
        // `if (lock != 0.0f)`), so a no-lock state renders nothing.
        if (v.State == MissileLockFeedback.LockState.None)
            return;

        // HUD-4 — native continuous lock sizing (radarimage.cpp:325-332), with
        // the native 1.05× radiusBracket clipped to the screen edge (308-319).
        float toEdge = MathF.Min(MathF.Min(v.Center.X, v.Center.Y),
            MathF.Min(_viewportWidth - v.Center.X, _viewportHeight - v.Center.Y));
        float radius = LockBracketStyle.HalfSizeForFraction(v.State, v.Fraction, v.ScreenRadius, toEdge);

        Texture2D? tex = _lockTextures?.For(v.State);
        if (tex is { } t)
        {
            // HUD-19 — native draws the lock surface as four CENTRED
            // quarter-bitmaps at (±delta, ±delta) (radarimage.cpp:255-277):
            // the quarters keep their native pixel size and MOVE APART as the
            // radius grows — they never scale (the old code squashed the whole
            // 64px sprite to a 2·s span). White, alpha blend (native forces
            // BlendModeSourceAlpha, radarimage.cpp:271).
            float delta = RadarBracketRenderer.ExpandedBlipDelta(t.Width, t.Height, radius);
            for (int q = 0; q < 4; q++)
            {
                _sb.Draw(t, ExpandedBlipQuadrantDest(q, t.Width, t.Height, v.Center, delta),
                    RadarBracketRenderer.ExpandedBlipQuadrant(q, t.Width, t.Height), Color.White);
            }
            return;
        }

        // Real sprite unavailable → procedural corner-L bracket fallback.
        DrawLockBracketProcedural(v, radius);
    }

    /// <summary>HUD-19 — the screen destination of native DrawExpandedBlip
    /// quadrant <paramref name="quadrant"/> (0=TL, 1=TR, 2=BR, 3=BL) for a
    /// <c>w×h</c> surface: each QUARTER bitmap is drawn CENTRED (bCentered,
    /// radarimage.cpp:272-275) at (±delta, ±delta) from <paramref name="center"/>,
    /// so its top-left is <c>center + (±delta, ±delta) − (hw, hh)</c>. Pure and
    /// GPU-free so the native quadrant placement is unit-testable.</summary>
    internal static Rectangle ExpandedBlipQuadrantDest(int quadrant, int w, int h, Vector2 center, float delta)
    {
        // Each QUARTER (w/2 × h/2) is drawn centred at (±delta, ±delta)
        // (radarimage.cpp:272-275), so its top-left is offset − (quarterW/2, quarterH/2).
        int qw = w / 2, qh = h / 2;
        float dx = quadrant is 1 or 2 ? delta : -delta;
        float dy = quadrant is 2 or 3 ? delta : -delta;
        return new Rectangle(
            (int)MathF.Round(center.X + dx - qw / 2f),
            (int)MathF.Round(center.Y + dy - qh / 2f),
            qw, qh);
    }

    /// <summary>
    /// P428 fallback — the pre-P428 procedural four corner-L bracket, retained
    /// so a missing/undecodable artwork asset still leaves a visible lock
    /// indicator (the port's established graceful-degradation convention).
    /// Tinted per-state only in this fallback path; the real sprites (primary)
    /// carry their own colours.
    /// </summary>
    protected virtual void DrawLockBracketProcedural(in LockBracketView v, float s)
    {
        const float arm = 13f;
        const float thickness = 2.5f;
        Color col = v.State switch
        {
            MissileLockFeedback.LockState.Partial => new(1.00f, 0.72f, 0.10f),   // amber
            _ => new(1.00f, 0.20f, 0.10f),                                        // hot red
        };

        var corners = new (float sx, float sy)[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) };
        foreach (var (sx, sy) in corners)
        {
            var corner = new Vector2(sx * s, sy * s) + v.Center;
            var d1 = new Vector2(-sx, 0f);
            var d2 = new Vector2(0f, -sy);
            DrawLine(corner, corner + d1 * arm, thickness, col);
            DrawLine(corner, corner + d2 * arm, thickness, col);
        }
    }

    /// <summary>
    /// P345 — draw the player's OWN autopilot-target selection box around a
    /// projected on-screen centre: a FULL CLOSED rectangular outline (contrast the
    /// missile-lock box's open corner-L arms, <see cref="DrawLockBracket"/>). Its
    /// colour is the <see cref="TargetSelectionStyle.ColorFor"/> friend/enemy pick
    /// (P376): RED for an enemy ship, BLUE for a friendly same-side ship, neutral
    /// aqua for a non-ship target — with a small centre dot. Brighter / thicker
    /// while autopilot is ENGAGED toward the target, dimmer / thinner while merely
    /// SELECTED — so the flying-toward state reads at a glance within whichever
    /// colour family (red/blue/aqua) applies.
    /// Must be called inside an active <see cref="SpriteBatch"/> session (i.e. from
    /// <see cref="Draw"/>). Drawn as four rotated thin rects reusing the same 1x1
    /// white pixel, so no new asset is needed.
    /// </summary>
    protected virtual void DrawTargetSelectionBracket(in TargetSelectionView v)
    {
        // HUD-4 — native projected-radius bracket (radarimage.cpp:296-319): the
        // closed box now scales to the object's projected radius and clamps to the
        // viewport edge, and uses the actual side colour when known.
        float half = v.ScreenRadius is { } radius and > 0f ? MathF.Max(radius * 1.05f, 8f) : TargetSelectionStyle.HalfSize;
        float toEdge = MathF.Min(MathF.Min(v.Center.X, v.Center.Y), MathF.Min(_viewportWidth - v.Center.X, _viewportHeight - v.Center.Y));
        if (half > toEdge + 10f) half = MathF.Max(toEdge + 10f, 8f);
        float thickness = TargetSelectionStyle.ThicknessFor(v.Engaged);
        Color col = v.SideColor is { } side ? new(side.R, side.G, side.B, side.A) : TargetSelectionStyle.ColorFor(v.Engaged, v.IsEnemy);

        var c = v.Center;
        // Top / right / bottom / left — a full continuous border (no open corners).
        DrawLine(c + new Vector2(-half, -half), c + new Vector2(+half, -half), thickness, col);
        DrawLine(c + new Vector2(+half, -half), c + new Vector2(+half, +half), thickness, col);
        DrawLine(c + new Vector2(+half, +half), c + new Vector2(-half, +half), thickness, col);
        DrawLine(c + new Vector2(-half, +half), c + new Vector2(-half, -half), thickness, col);

        // Small centre dot — the precise aim point of the selection.
        FillRect(c.X - 2f, c.Y - 2f, 4f, 4f, col);

        // P359 + P423 — the target-info readout (native's "pilot name + small health
        // bar below the reticle"). Native's real gate for the health bar is NOT "is
        // it a ship": <c>ModelData::GetPercentHitPoints</c> (consoledata.cpp:202-221)
        // gates only on the generic <c>c_mtDamagable</c> attribute, which asteroids
        // and stations share with ships, so the generic hull/shield bars render for
        // ANY damageable target (ship, station, asteroid). A pilot NAME still renders
        // only for a ship (only a ship has a pilot). P423 additionally renders a
        // distinct ORE/He3 readout (a labelled bar) ONLY when the target is an
        // asteroid — native's <c>ModelData::GetOre</c> (consoledata.cpp:449-460) is
        // gated specifically on <c>OT_asteroid</c>, a separate UI data-binding from
        // the generic hit-points fraction. Centred beneath the bracket; every bar
        // reuses the same <see cref="DrawBar"/> fraction primitive the player's own
        // gauges use, so the readout is visually consistent with the rest of the HUD.
        float barW = 72f, barH = 5f;
        float barX = c.X - barW / 2f;
        float y = c.Y + TargetSelectionStyle.HalfSize + 8f;
        var bracketFont = NativeFontAt(NativeFont.MedVerdana);   // port readout text
        if (v.CharacterName is { } pilotName)
        {
            var nsz = bracketFont.MeasureString(pilotName);
            var nameBox = DrawMeasuredText(bracketFont, pilotName,
                new Vector2(c.X - nsz.X / 2f, y), TextMain);
            y = nameBox.Bottom + 3f;
        }
        if (v.Distance is { } distance)
        {
            string range = $"{distance:0} m";
            var rangeBox = DrawMeasuredText(bracketFont, range, new(c.X - bracketFont.MeasureString(range).X / 2, y), TextDim);
            y = rangeBox.Bottom + 4;
        }
        if (v.HullFraction is { } hullFrac)
        {
            DrawBar(hullFrac, barX, y, barW, barH);
            y += barH + 2f;
        }
        if (v.ShieldFraction is { } shieldFrac)
        {
            DrawBar(shieldFrac, barX, y, barW, barH - 1f);
            y += (barH - 1f) + 2f;
        }
        if (v.OreFraction is { } oreFrac)
        {
            // P423 — asteroid-only He3/ore level readout (native ModelData::GetOre,
            // OT_asteroid-gated). A labelled bar so the "level of He3" is legible;
            // the bar itself is the same DrawBar primitive for visual consistency.
            string lbl = "ORE " + HudFormat.FormatPercent(oreFrac) + "%";
            var lsz = bracketFont.MeasureString(lbl);
            DrawText(bracketFont, lbl, new Vector2(c.X - lsz.X / 2f, y), TextMain);
            y += lsz.Y + 2f;
            DrawBar(oreFrac, barX, y, barW, barH);
        }
    }

    /// <summary>
    /// HUD-3 — the native bottom target pane (<c>Artwork/dialog.mdl:4217-4242</c>).
    /// Classic skin: the real target gauge / oval / range bitmaps at their
    /// native pane-local anchors with side-coloured text
    /// (<c>WinTrek/consoledata.cpp:40-199</c>). The projected in-world selection
    /// bracket is drawn separately; this pane stays visible for a selected
    /// target even when that target is out of view (native
    /// <c>IsNotNull(Target)</c>).
    /// </summary>
    protected virtual void DrawTargetPane(HudModel model, Viewport viewport)
    {
        var tp = model.Context.TargetPane!.Value;
        float w = viewport.Width, h = viewport.Height;
        float s = TargetPaneLayout.Scale(w, h);
        float x0 = TargetPaneLayout.OriginX(w, h);
        float y0 = TargetPaneLayout.OriginY(w, h);
        var side = From(tp.SideColor);

        // HUD-21 — the 3-D model preview, painted under the oval/word art
        // (native GroupImage child order: model after the gauges, before the
        // oval, dialog.mdl:4218-4242; empty fallback when the input is null).
        DrawTargetModelPreview(x0, y0, s);

        // Decorative oval/word art (native white art; the oval colour layer is
        // side-tinted, dialog.mdl:4165-4191).
        DrawPaneTexture(_targetPaneTextures?.Oval, x0 + 192f * s, y0 + 75f * s, s, Color.White);
        DrawPaneTexture(_targetPaneTextures?.OvalColor, x0 + 256f * s, y0 + 2f * s, s, side);
        DrawPaneTexture(_targetPaneTextures?.OvalWord1, x0 + 193f * s, y0 - 8f * s, s, Color.White);
        DrawPaneTexture(_targetPaneTextures?.OvalWord2, x0 + 320f * s, y0 - 8f * s, s, Color.White);

        // Name + type (native GetName(Target) ": " GetModelTypeDesc(Target),
        // dialog.mdl:4043-4050), centred at the pane centre. FONT-1 — native
        // TargetName uses medVerdana; range/speed smVerdana; ARMOR/SHIELD
        // medVerdana — at the pane scale s (so the text grows with its pane).
        var (nameFont, nameFs) = HudFont(NativeFont.MedVerdana, s);
        var (rangeFont, rangeFs) = HudFont(NativeFont.SmVerdana, s);
        var (armorFont, armorFs) = HudFont(NativeFont.MedVerdana, s);
        string nameLine = (tp.Name ?? "unknown") + (tp.TypeDescription is { } td ? ": " + td : "");
        var nameSize = PaneTextSize(nameFont, nameLine, nameFs);
        DrawRawText(nameFont, nameLine,
            new(x0 + TargetPaneLayout.LocalCenterX * s - nameSize.X / 2f,
                y0 - TargetPaneLayout.LocalNameBottom * s - nameSize.Y), side, nameFs);

        // Range (left) and speed (right), native local y=33. Both use native
        // NumberString ((int)(v+0.5), engine/modeler.cpp:2008) so the unseen
        // speed sentinel -1 reads "0" and exact halves round up.
        if (tp.Distance is { } distance)
        {
            string range = string.Concat("RANGE: ", HudFormat.FormatNativeNumber(distance));
            DrawRawText(rangeFont, range, new(x0 + 400f * s, y0 - 33f * s - PaneTextSize(rangeFont, range, rangeFs).Y), side, rangeFs);
        }
        else if (tp.SectorName is { } sectorName)
        {
            // HUD-18 — native TargetRangeString's other branch:
            // "LOCATION:" + GetSectorName(Target), no trailing space
            // (dialog.mdl:4053-4060). Drawn at the same native Point(400,33)
            // anchor and cached per sector name (no per-frame concat).
            string location = HudStringCache.Location(sectorName);
            DrawRawText(rangeFont, location, new(x0 + 400f * s, y0 - 33f * s - PaneTextSize(rangeFont, location, rangeFs).Y), side, rangeFs);
        }
        if (tp.Speed is { } speed)
        {
            string text = string.Concat("SPEED: ", HudFormat.FormatNativeNumber(speed));
            var size = PaneTextSize(rangeFont, text, rangeFs);
            DrawRawText(rangeFont, text, new(x0 + 242f * s - size.X, y0 - 33f * s - size.Y), side, rangeFs);
        }

        // ARMOR / SHIELD labels (native medVerdana; port reuses the HUD font).
        var armorSize = PaneTextSize(armorFont, "ARMOR", armorFs);
        DrawRawText(armorFont, "ARMOR", new(x0 + 228f * s - armorSize.X, y0 - 73f * s - armorSize.Y), side, armorFs);
        DrawRawText(armorFont, "SHIELD", new(x0 + 410f * s, y0 - 73f * s - PaneTextSize(armorFont, "SHIELD", armorFs).Y), side, armorFs);

        // Dedicated target hull/shield gauges (dialog.mdl:4123-4135), bottom-up
        // reveal of the real native gauge art.
        DrawTargetGauge(_targetPaneTextures?.Hull, TargetPaneTextures.HullRect, tp.HullFraction ?? 0f,
            x0 + 235f * s, y0 - 33f * s, s);
        DrawTargetGauge(_targetPaneTextures?.Shield, TargetPaneTextures.ShieldRect, tp.ShieldFraction ?? 0f,
            x0 + 367f * s, y0 - 33f * s, s);

        // Native turret-range icon (dialog.mdl:4073-4081), only when in range.
        if (tp.InRange)
            DrawPaneTexture(_targetPaneTextures?.Range, x0 + 200f * s, y0 - 90f * s, s, Color.White);

        ElementDrawn("Target name", "Target type description", "Target side identity",
            "Target hull", "Target shield", "Target range or sector", "Target speed", "Target in weapon range");
    }

    /// <summary>Pane strings are sanitised once by
    /// <see cref="TargetPaneModel"/> (or are ASCII literals/numbers here), so
    /// the draw path measures and draws them verbatim — no per-frame LINQ.
    /// FONT-1: <paramref name="scale"/> is the pane's own position scale (the
    /// same one <see cref="HudFont"/> returned), so text grows with its pane.</summary>
    protected Vector2 PaneTextSize(SpriteFont font, string text, float? scale = null) =>
        font.MeasureString(text) * (scale ?? TextScale);

    protected void DrawRawText(SpriteFont font, string text, Vector2 topLeft, Color color, float? scale = null)
        => RenderText(font, text, topLeft, color, scale ?? TextScale);

    protected void DrawPaneTexture(Texture2D? tex, float leftX, float bottomY, float scale, Color tint)
    {
        if (tex is null) return;
        var dst = new Rectangle((int)MathF.Round(leftX), (int)MathF.Round(bottomY - tex.Height * scale),
            (int)MathF.Round(tex.Width * scale), (int)MathF.Round(tex.Height * scale));
        _sb.Draw(tex, dst, tint);
    }

    /// <summary>
    /// HUD-21 — draw the 3-D target-model preview into its 96·s pane rect
    /// (<c>TargetModelPreview.PaneRect</c>, native y-up lower-left (270,9)),
    /// alpha-blended with a white tint. When <see cref="TargetModelPreview"/>
    /// is null nothing is drawn (the native empty fallback — the pane looks
    /// exactly as before). Painted BEFORE the oval/word art so the native
    /// <c>GroupImage::Render</c> child order (model under the oval,
    /// <c>image.cpp:954-972</c>, <c>dialog.mdl:4218-4242</c>) holds.
    /// </summary>
    protected virtual void DrawTargetModelPreview(float x0, float y0, float s)
    {
        if (TargetModelPreview is null) return;
        var dst = global::Allegiance.ClientCore.MonoGame.Rendering.TargetModelPreview.PaneRect(x0, y0, s);
        _sb.Draw(TargetModelPreview, dst, Color.White);
        ElementDrawn("Target model preview");
    }

    protected void DrawTargetGauge(Texture2D? tex, Rectangle srcFull, float fraction,
        float leftX, float bottomY, float scale)
    {
        if (tex is null) return;
        float frac = HudFormat.Clamp01(fraction);
        int srcH = (int)MathF.Round(srcFull.Height * frac);
        if (srcH <= 0) return;
        var src = new Rectangle(srcFull.X, srcFull.Y + (srcFull.Height - srcH), srcFull.Width, srcH);
        var dst = new Rectangle((int)MathF.Round(leftX), (int)MathF.Round(bottomY - srcH * scale),
            (int)MathF.Round(srcFull.Width * scale), (int)MathF.Round(srcH * scale));
        _sb.Draw(tex, dst, src, Color.White);
    }

    /// <summary>
    /// P359 — the target bearing arrow: a fixed-position compass chevron in the
    /// bottom-right corner whose pointing direction is the selected target's REAL
    /// relative bearing from the camera, supplied as a normalized screen-space unit
    /// vector (right = +X, up = -Y) by <c>Game1.ComputeTargetBearing</c>.
    /// Reuses the same rotated-thin-rect line primitive as the bracket — no new
    /// asset. An anchor dot marks the fixed pivot; the chevron fans toward the
    /// bearing and a short "TGT" caption sits beneath it so the element reads as a
    /// target-direction compass. Shown whenever a target is selected (any kind,
    /// on- or off-screen). Must be called inside an active <see cref="SpriteBatch"/>
    /// session (i.e. from <see cref="Draw"/>).
    /// </summary>
    private void DrawTargetBearingArrow(Vector2 dir, Viewport viewport, float rightInset = 96f, string label = "TGT", float bottomInset = 56f)
    {
        // Fixed pivot near the bottom-right corner (clear of the right throttle
        // gauge and the centre-bottom speed readout).
        var anchor = new Vector2(viewport.Width - rightInset, viewport.Height - bottomInset);

        float len = 14f;          // chevron reach from the pivot
        float spread = 7f;        // arrowhead half-width perpendicular
        var tip = anchor + dir * len;
        var side = new Vector2(-dir.Y, dir.X) * spread;
        var back = anchor - dir * len;

        var col = TargetSelectionStyle.Engaged;   // icy-cyan, matches the bracket

        // Arrowhead: two fins from the spread base corners to the tip.
        DrawLine(back + side, tip, 2.6f, col);
        DrawLine(back - side, tip, 2.6f, col);
        // Shaft: a fainter line along the bearing back to the pivot.
        DrawLine(back, anchor, 1.8f, col * 0.55f);

        // Pivot dot.
        FillRect(anchor.X - 2f, anchor.Y - 2f, 4f, 4f, TextDim);

        // "TGT" caption beneath the pivot (pure ASCII — the spritefont bakes only
        // 32..126, same constraint as the other HUD labels).
        string lbl = label;
        var tgtFont = NativeFontAt(NativeFont.MedVerdana);
        var lsz = tgtFont.MeasureString(lbl);
        DrawText(tgtFont, lbl,
            new Vector2(anchor.X - lsz.X / 2f, anchor.Y + len + 6f), TextDim);
    }

    /// <summary>
    /// P266 — the predictive lead reticle (distinct from the static round
    /// crosshair): an open DIAMOND (rotated-square outline) with a centre dot,
    /// drawn at the projected lead point — where a shot fired now must aim to
    /// intercept the selected moving target, i.e. genuinely offset from where the
    /// target currently appears. Colour follows the server-computed hit-
    /// probability <c>g</c> as native's ramp <c>(1-g, g, 0)</c> (green → red);
    /// when the intercept is OUT of weapon range the diamond renders in native's
    /// dimmed grey (<c>0.33,0.33,0.33,0.5</c>, indicator.cpp:333-335) in place of
    /// its in-range/out-of-range surface swap. A faint hollow ring is drawn at the
    /// target's CURRENT on-screen centre so the lateral lead offset is legible (the
    /// ring is where the target is now; the diamond is where it will be when the
    /// Must be called inside an active <see cref="SpriteBatch"/>
    /// session (i.e. from <see cref="Draw"/>). Public so the offscreen
    /// verification harness can render the reticle to a RenderTarget2D directly.
    /// </summary>
    public virtual void DrawLeadIndicator(LeadIndicatorView v)
    {
        // Native ballistic-lead surface swap (indicator.cpp:328): leadinbmp in
        // range, leadoutbmp out of range. Procedural diamond is the fallback.
        Texture2D? surface = v.InRange ? _indicators?.LeadIn : _indicators?.LeadOut;
        if (surface is { } leadSurface)
        {
            DrawCentered(leadSurface, v.LeadCenter, Color.White);
        }
        else
        {
            Color col = v.InRange
                ? new Color(1f - v.Green, v.Green, 0f)
                : new Color(0.33f, 0.33f, 0.33f, 0.5f);

            const float half = 14f;
            var c = v.LeadCenter;
            var corners = new[]
            {
                c + new Vector2(0f, -half),
                c + new Vector2(half, 0f),
                c + new Vector2(0f, half),
                c + new Vector2(-half, 0f),
            };
            for (int i = 0; i < corners.Length; i++)
                DrawLine(corners[i], corners[(i + 1) % corners.Length], 2.2f, col);
            FillRect(c.X - 2f, c.Y - 2f, 4f, 4f, col);
        }

        // Port extension: a faint hollow ring at the target's CURRENT on-screen
        // centre makes the real predictive offset legible (native draws only the
        // lead surface, not the target-now ring).
        const float tHalf = 8f;
        const int seg = 24;
        var tcol = new Color(0.9f, 0.9f, 0.9f, 0.40f);
        for (int i = 0; i < seg; i++)
        {
            float a0 = i * MathHelper.TwoPi / seg;
            float a1 = (i + 1) * MathHelper.TwoPi / seg;
            var p0 = v.TargetCenter + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * tHalf;
            var p1 = v.TargetCenter + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * tHalf;
            DrawLine(p0, p1, 1.2f, tcol);
        }
    }

    /// <summary>
    /// HUD-8 — the aim hit-probability predictor (native
    /// <c>centeroutbmp</c> crosshair, indicator.cpp:334-362). In cockpit it is
    /// the centre crosshair tinted <c>(1-g, g, 0)</c>; in chase/missile it is
    /// the projected aim point (green ramp in range, out-of-range grey).
    /// </summary>
    public virtual void DrawAimPredictor(LeadIndicatorView v, CameraMode camera, Viewport viewport)
    {
        Vector2 center = camera == CameraMode.Cockpit
            ? new Vector2(viewport.Width / 2f, viewport.Height / 2f)
            : v.LeadCenter;
        Color col = v.InRange
            ? new Color(1f - v.Green, v.Green, 0f)
            : new Color(0.33f, 0.33f, 0.33f, 0.5f);

        // HUD-19 — with the real centeroutbmp art the crosshair is drawn
        // ADDITIVELY in DrawCentreIndicatorsAdditive; here only the no-art
        // procedural fallback (alpha) runs.
        if (_indicators?.CrosshairOut is not null)
            return;

        DrawCircleOutline(center.X, center.Y, 12f, col);
        FillRect(center.X - 2f, center.Y - 2f, 4f, 4f, col);
    }

    /// <summary>
    /// HUD-8 — native velocity direction marker (<c>directionbmp</c>,
    /// indicator.cpp:145), drawn at the projected velocity direction.
    /// </summary>
    protected virtual void DrawVelocityMarker(Vector2 position)
    {
        if (_indicators?.VelocityMarker is { } marker)
        {
            DrawCentered(marker, position, Color.White);
            return;
        }
        DrawLine(position + new Vector2(0f, -6f), position + new Vector2(0f, 6f), 1.6f, Accent);
        DrawLine(position + new Vector2(-6f, 0f), position + new Vector2(6f, 0f), 1.6f, Accent);
    }

    /// <summary>
    /// HUD-8/HUD-19 — native joystick turning indicator (indicator.cpp:154-177):
    /// translated to centre − 100·(yaw,pitch) in y-UP image space (screen:
    /// centre − 100·yaw horizontally, centre + 100·pitch vertically), rotated by
    /// atan(pitch/yaw)+π/2 (+π if yaw&lt;0; HUD-19c native form, see
    /// <see cref="JoystickLayout.Angle"/>), scaled |control|/2 + 0.5, alpha
    /// 0.70 (texture) × |control|. HUD-19 renders the REAL native 6-vertex
    /// <c>joystickindicator.mdl</c> chevron (baked once at load,
    /// <see cref="JoystickMeshRasterizer"/>) instead of stroked lines, and
    /// fixes the y-mirrored position/rotation (image space is y-up, so the
    /// pitch term is +100·pitch on screen, not −).
    /// </summary>
    protected virtual void DrawJoystickIndicator(Vector2 center, float yaw, float pitch)
    {
        float magnitude = JoystickLayout.Magnitude(yaw, pitch);
        if (magnitude <= 0f) return;   // |control| = 0 → native alpha 0 (invisible)
        var position = JoystickLayout.Position(center, yaw, pitch);
        float scale = JoystickLayout.Scale(yaw, pitch);

        if (_joystickMesh is { } mesh)
        {
            float rotation = JoystickLayout.Rotation(JoystickLayout.ApexDirection(yaw, pitch));
            // Premultiplied white bake (alpha 0.70); tint by |control| so the
            // combined alpha is 0.70·|control| (indicator.cpp:172 diffuse alpha).
            _sb.Draw(mesh, position, null, Color.White * magnitude, rotation,
                new Vector2(mesh.Width / 2f, mesh.Height / 2f), scale, SpriteEffects.None, 0f);
            return;
        }

        // No baked mesh (test/reduced construction) — stroked-chevron fallback
        // using the same native layout.
        var apex = JoystickLayout.ApexDirection(yaw, pitch);
        var side = new Vector2(-apex.Y, apex.X);
        float length = 14f * scale;
        var tip = position + apex * length;
        var back = position - apex * length;
        var colour = Color.White * magnitude;
        DrawLine(back + side * (6f * scale), tip, 2f, colour);
        DrawLine(back - side * (6f * scale), tip, 2f, colour);
        DrawLine(back, position, 1.5f, colour * 0.6f);
    }

    /// <summary>
    /// HUD-8 — chase weapon aim without target (indicator.cpp:367-405): a grey
    /// projected weapon-range aim marker drawn in external chase/missile views.
    /// </summary>
    protected virtual void DrawChaseAim(Vector2 position)
    {
        var grey = new Color(0.33f, 0.33f, 0.33f, 0.5f);
        // HUD-19 — with the real centeroutbmp art the marker is drawn
        // ADDITIVELY in DrawCentreIndicatorsAdditive; here only the no-art
        // procedural fallback (alpha) runs.
        if (_indicators?.CrosshairOut is not null)
            return;
        const float half = 10f;
        var corners = new[]
        {
            position + new Vector2(0f, -half),
            position + new Vector2(half, 0f),
            position + new Vector2(0f, half),
            position + new Vector2(-half, 0f),
        };
        for (int i = 0; i < corners.Length; i++)
            DrawLine(corners[i], corners[(i + 1) % corners.Length], 2f, grey);
        FillRect(position.X - 2f, position.Y - 2f, 4f, 4f, grey);
    }

    /// <summary>
    /// HUD-8 — accepted/current order turn arrows (indicator.cpp:411-457): arrows
    /// placed at centre + 108·normalized(target screen offset). Same target = one
    /// yellow arrow, otherwise accepted green + current red. Native's continuously
    /// rotating 3-D <c>pointer</c> geometry is approximated by flat chevrons.
    /// </summary>
    protected virtual void DrawOrderArrows(HudOrderArrows arrows, Viewport viewport)
    {
        var center = new Vector2(viewport.Width / 2f, viewport.Height / 2f);
        if (arrows.SameTarget && arrows.AcceptedDirection is { } sameDirection)
        {
            DrawTurnArrow(center, sameDirection, new Color(1f, 1f, 0f));
            return;
        }
        if (arrows.AcceptedDirection is { } acceptedDirection)
            DrawTurnArrow(center, acceptedDirection, new Color(0f, 1f, 0f));
        if (arrows.CurrentDirection is { } currentDirection)
            DrawTurnArrow(center, currentDirection, new Color(1f, 0f, 0f));
    }

    private void DrawTurnArrow(Vector2 center, HudPoint direction, Color color)
    {
        var d = new Vector2(direction.X, direction.Y);
        if (d.LengthSquared() < 1e-6f) return;
        d.Normalize();
        var anchor = center + d * 108f;
        var tip = anchor + d * 12f;
        var side = new Vector2(-d.Y, d.X) * 7f;
        var back = anchor - d * 4f;
        DrawLine(back + side, tip, 2.4f, color);
        DrawLine(back - side, tip, 2.4f, color);
        DrawLine(back, anchor, 1.6f, color * 0.6f);
    }

    /// <summary>Draw a decoded HUD bitmap centred at <paramref name="center"/> at
    /// its native 1:1 size (native <c>Context::DrawImage(…, true, …)</c>).</summary>
    protected void DrawCentered(Texture2D texture, Vector2 center, Color color) =>
        _sb.Draw(texture, center, null, color, 0f,
            new Vector2(texture.Width / 2f, texture.Height / 2f), 1f, SpriteEffects.None, 0f);

    /// <summary>Convert a plain <see cref="HudColor"/> to a MonoGame <c>Color</c>.</summary>
    private static Color From(HudColor c) => new(c.R, c.G, c.B, c.A);

    /// <summary>Solid axis-aligned fill via a tinted, stretched 1x1 white pixel.</summary>
    protected void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)MathF.Round(x), (int)MathF.Round(y),
            (int)MathF.Round(w), (int)MathF.Round(h)), color);

    /// <summary>Thin rectangular outline (four 1px lines) around an axis-aligned box.</summary>
    protected void OutlineRect(float x, float y, float w, float h, Color color)
    {
        FillRect(x, y, w, 1f, color);
        FillRect(x, y + h - 1f, w, 1f, color);
        FillRect(x, y, 1f, h, color);
        FillRect(x + w - 1f, y, 1f, h, color);
    }

    /// <summary>An arbitrarily-oriented thin line (from a→b) drawn as a rotated,
    /// stretched 1x1 white pixel — SpriteBatch has no native line primitive.</summary>
    protected void DrawLine(Vector2 a, Vector2 b, float thickness, Color color)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (len < 0.001f)
            return;
        float angle = MathF.Atan2(d.Y, d.X);
        _sb.Draw(_white, a + d * 0.5f, null, color, angle,
            new Vector2(0.5f, 0.5f), new Vector2(len, thickness), SpriteEffects.None, 0f);
    }

    /// <summary>P506 — outline a circle of radius <paramref name="r"/> about a
    /// centre point as a polyline of short <see cref="DrawLine"/> segments
    /// (SpriteBatch has no arc primitive).</summary>
    private void DrawCircleOutline(float cx, float cy, float r, Color color)
    {
        if (r < 0.5f)
            return;
        const int seg = 48;
        for (int i = 0; i < seg; i++)
        {
            float a0 = i * MathHelper.TwoPi / seg;
            float a1 = (i + 1) * MathHelper.TwoPi / seg;
            var p0 = new Vector2(cx + MathF.Cos(a0) * r, cy + MathF.Sin(a0) * r);
            var p1 = new Vector2(cx + MathF.Cos(a1) * r, cy + MathF.Sin(a1) * r);
            DrawLine(p0, p1, 1.2f, color);
        }
    }

    /// <summary>
    /// P506 — the virtual-joystick indicator: a small reticle showing the current
    /// integrated deflection (the stick dot) and the deadzone circle, so the
    /// pilot can SEE when they are centred (and therefore holding steady). The
    /// range circle marks unit deflection; the inner circle is the active
    /// deadzone (the stick must exit it before the ship turns). The dot turns
    /// the teal accent colour once it leaves the deadzone. A caption beneath
    /// states the deadzone % and the curve mode (QUAD default / LIN).
    /// </summary>
    private void DrawMouseStick(in MouseStickView v, float w, float cy)
    {
        const float R = 38f;
        float cx = w / 2f;

        // Range (unit-deflection) circle + light cross-hairs.
        DrawCircleOutline(cx, cy, R, BorderDim);
        DrawLine(new Vector2(cx - R, cy), new Vector2(cx + R, cy), 1f, BorderDim);
        DrawLine(new Vector2(cx, cy - R), new Vector2(cx, cy + R), 1f, BorderDim);

        // Active deadzone circle.
        float dzR = Math.Clamp(v.Deadzone, 0f, 1f) * R;
        if (dzR >= 1f)
            DrawCircleOutline(cx, cy, dzR, TextDim);

        // Stick dot at the current integrated deflection. Screen mapping: a
        // mouse push right reads a negative yaw deflection, so screen X = cx − yaw·R
        // moves the dot right when the stick is pushed right; a push down reads a
        // negative pitch, so screen Y = cy − pitch·R moves the dot down. Matches
        // the physical direction the pilot feels.
        bool active = MathF.Abs(v.Yaw) > v.Deadzone || MathF.Abs(v.Pitch) > v.Deadzone;
        Color dot = active ? Accent : TextDim;
        float dotX = cx - v.Yaw * R;
        float dotY = cy - v.Pitch * R;
        FillRect(dotX - 3f, dotY - 3f, 7f, 7f, dot);

        // Caption: deadzone % + curve mode, so the settings are legible on screen.
        string mode = v.LinearControls ? "LIN" : "QUAD";
        DrawText(NativeFontAt(NativeFont.MedVerdana), $"MOUSE DZ {(int)(v.Deadzone * 100f)}% {mode}",
            new Vector2(cx - 58f, cy + R + 6f), TextDim);
    }

    /// <summary>
    /// HUD-7 — the native flight equipment/inventory pane at
    /// <c>SideBottomRight (-36, 40)</c> (dialog.mdl:5387). Classic skin: the
    /// real CortUI classic top/line/bottom backgrounds + native icons and
    /// mounting/arming bar fills, with the port HUD font for the text columns
    /// (the native <c>medVerdana</c>/<c>smVerdana</c> fonts are not shipped, so
    /// text-bearing rows remain partial — the same HUD-3 convention).</summary>
    protected virtual void DrawInventoryPane(HudInventoryPane pane, HudModel model, Viewport viewport)
    {
        float w = viewport.Width, h = viewport.Height;
        int rowCount = pane.Rows.Count;
        float scale = InventoryPaneLayout.Scale(h);
        float left = InventoryPaneLayout.LeftX(w, h);
        // HUD-10 — native SideBottomRight(-36,-150)→(-36,40) slide: the pane
        // travels 190 px as it comes on screen (dialog.mdl:5387, .5 s).
        float top = InventoryPaneLayout.TopY(rowCount, w, h)
            + (1f - Math.Clamp(model.Context.InventorySlide, 0f, 1f)) * 190f;
        float lineW = InventoryPaneLayout.LineWidth * scale;
        float capH = InventoryPaneLayout.CapHeight * scale;
        float rowH = InventoryPaneLayout.RowHeight * scale;

        var art = _inventoryTextures is { } tex && tex.AllLoaded ? tex : null;
        if (art is not null)
        {
            _sb.Draw(art.Top!, new Rectangle((int)MathF.Round(left), (int)MathF.Round(top), (int)MathF.Round(lineW), (int)MathF.Round(capH)), Color.White);
            float y = top + capH;
            for (int i = 0; i < rowCount; i++)
            {
                _sb.Draw(art.Line!, new Rectangle((int)MathF.Round(left), (int)MathF.Round(y), (int)MathF.Round(lineW), (int)MathF.Round(rowH)), Color.White);
                y += rowH;
            }
            _sb.Draw(art.Bottom!, new Rectangle((int)MathF.Round(left), (int)MathF.Round(y), (int)MathF.Round(lineW), (int)MathF.Round(capH)), Color.White);
        }
        else
        {
            FillRect(left, top, lineW, capH * 2f + rowH * rowCount, PanelBg);
            OutlineRect(left, top, lineW, capH * 2f + rowH * rowCount, BorderDim);
        }

        // Own-ship name header (native ShipNameImage, dialog.mdl:1803-1807).
        // FONT-1 — native inventory own-ship name uses smallBoldFont.
        if (pane.ShipName.Length > 0)
        {
            var (shipNameFont, shipNameFs) = HudFont(NativeFont.SmallBoldFont, scale);
            var name = shipNameFont.MeasureString(pane.ShipName) * shipNameFs;
            float nx = left + MathF.Max(0f, (lineW - name.X) / 2f);
            RenderText(shipNameFont, pane.ShipName, new Vector2(nx, top + capH * 0.5f - name.Y * 0.5f), TextMain, shipNameFs);
        }

        float rowTop = top + capH;
        for (int i = 0; i < rowCount; i++)
        {
            DrawInventoryRow(pane.Rows[i], art, left, rowTop, scale);
            rowTop += rowH;
        }
        ElementDrawn("Flight equipment inventory pane", "Equipment part identity and key",
            "Equipment selected and active state", "Equipment quantity and empty warning",
            "Equipment mounting progress", "Launcher arming progress", "Equipment weapon range",
            "Equipment shot damage", "Equipment burst rate", "Equipment shield strength",
            "Equipment shield recharge seconds", "Equipment afterburner top speed",
            "Equipment afterburner endurance", "Equipment cloak endurance",
            "Inventory turret gunner identity", "Inventory cargo slots", "Inventory own ship name",
            "Equipment empty slot affordances");
    }

    private void DrawInventoryRow(HudInventoryRow row, InventoryPaneTextures? art, float left, float rowTop, float scale)
    {
        float rowH = InventoryPaneLayout.RowHeight * scale;
        float textY = rowTop + 2f * scale;
        var white = new Color(1f, 1f, 1f);
        var grey = new Color(0.75f, 0.75f, 0.75f);
        var yellow = new Color(1f, 1f, 0f);
        // FONT-1 — native inventory row strings (StringPane default
        // TrekResources::SmallFont(), WinTrek/trekmdl.cpp:390) use smallFont at
        // the pane scale.
        var (rowFont, rowFs) = HudFont(NativeFont.SmallFont, scale);

        // Icon (native inv*.mdl Point(5,5)).
        Texture2D? icon = row.Kind switch
        {
            HudInventoryRowKind.Weapon => art?.WeaponIcon,
            HudInventoryRowKind.EmptyWeapon => art?.WeaponIcon,
            _ => art?.EquipmentIcon,
        };
        if (icon is { } ic)
            _sb.Draw(ic, new Vector2(left + InventoryPaneLayout.IconX * scale, rowTop + (rowH - ic.Height * scale) / 2f), null, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);

        // Key (native Point(23,5)); yellow when active, grey for empty slots.
        Color keyColor = row.Active ? yellow : row.IsEmpty ? grey : white;
        if (row.Key.Length > 0)
            RenderText(rowFont, row.Key, new Vector2(left + InventoryPaneLayout.KeyX * scale, textY), keyColor, rowFs);

        // Name (native Point(35,5)); white when selected, dim grey otherwise.
        // Fit to the gap before the stats column (a small margin keeps the two
        // columns from colliding).
        Color nameColor = row.Selected ? white : row.IsEmpty ? grey : new Color(0.88f, 0.96f, 0.95f);
        if (row.Name.Length > 0)
        {
            // HUD-11 — a row with no ammo/stats has no right-hand columns, so its
            // name may use the whole line (native <cargo mount> fits at
            // 1080p instead of clipping to "<cargo moun…").
            bool hasRightColumn = row.OutOfAmmo || row.StatsText is not null || row.ReadyState is 1 or 2;
            float nameMax = Math.Max(0f, ((hasRightColumn ? InventoryPaneLayout.StatsX : InventoryPaneLayout.LineWidth)
                - InventoryPaneLayout.NameX - (hasRightColumn ? 2f : 3f)) * scale);
            RenderText(rowFont, FitText(rowFont, row.Name, nameMax, rowFs),
                new Vector2(left + InventoryPaneLayout.NameX * scale, textY), nameColor, rowFs);
        }

        // Out-of-ammo red "A" (native Point(110,5), invsweapon.mdl:90).
        if (row.OutOfAmmo)
            RenderText(rowFont, "A", new Vector2(left + InventoryPaneLayout.AmmoX * scale, textY), Warning, rowFs);

        // Ready stats or mounting/arming progress (native Point(130,5)).
        if (row.ReadyState is 1 or 2)
        {
            float bx = left + InventoryPaneLayout.StatsX * scale;
            float by = rowTop + 2f * scale;
            float bw = 64f * scale;
            float bh = 14f * scale;
            Texture2D? fill = row.ReadyState == 2 ? art?.ArmingBar : art?.MountingBar;
            // HUD-15 — native's mounting track is the real
            // mountingbarbkgndbmp (the word "Mounting" behind the fill), an
            // ImagePane the GaugePane is nested in at Point(0,0)
            // (Artwork/invdefault.mdl:43-52; the pane places the group at
            // Point(130,5) of the 64x14 row slot, invdefault.mdl:59-66). The
            // dark rect is only the fallback when that art is missing.
            if (row.ReadyState == 1 && art?.MountingBarBackground is { } track)
                _sb.Draw(track, new Rectangle((int)MathF.Round(bx), (int)MathF.Round(by),
                    (int)MathF.Round(bw), (int)MathF.Round(bh)), Color.White);
            else
                FillRect(bx, by, bw, bh, new Color(0f, 0f, 0f, 0.85f));
            if (fill is { } f && row.MountedFraction > 0f)
            {
                // Native GaugePane::Paint (effect/panes.cpp:31-40) BitBlts the left
                // m_value = int(xsize·v) columns of the fill surface — a
                // left-to-right reveal, not a horizontal squeeze. Keep the
                // 64x14 art at its native columns.
                int srcW = (int)(f.Width * HudFormat.Clamp01(row.MountedFraction));
                if (srcW > 0)
                    _sb.Draw(f, new Rectangle((int)MathF.Round(bx), (int)MathF.Round(by),
                            Math.Max(1, (int)MathF.Round(bw * srcW / (float)f.Width)), (int)MathF.Round(bh)),
                        new Rectangle(0, 0, srcW, f.Height), Color.White);
            }
            OutlineRect(bx, by, bw, bh, BorderDim);
        }
        else if (row.StatsText is { } stats)
        {
            // HUD-11 — native StatsBar lives at Point(130,5) in a 214-wide row
            // (invsweapon.mdl); the port font is wider, so a raw fit clipped the
            // reading. Scale the stats glyphs into the native stats slot instead
            // (HudFormat.FitToWidth is the tested width contract).
            float statsMax = Math.Max(0f, (InventoryPaneLayout.LineWidth - InventoryPaneLayout.StatsX - 3f) * scale);
            float statsScale = InventoryPaneLayout.FitFontScale(stats, statsMax, rowFont, rowFs);
            RenderText(rowFont, stats, new Vector2(left + InventoryPaneLayout.StatsX * scale, textY),
                white, rowFs * statsScale);
        }
    }
}
