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

    protected readonly SpriteBatch _sb;
    protected readonly Texture2D _white;
    protected readonly SpriteFont _small;
    protected readonly SpriteFont _big;
    private readonly GaugeTextures? _gauges;
    private readonly LockTextures? _lockTextures;
    private readonly StatusTextures? _status;
    private readonly TargetPaneTextures? _targetPaneTextures;

    public HudRenderer(
        SpriteBatch spriteBatch,
        Texture2D whitePixel,
        SpriteFont small,
        SpriteFont big,
        GaugeTextures? gauges = null,
        LockTextures? lockTextures = null,
        StatusTextures? statusTextures = null,
        TargetPaneTextures? targetPaneTextures = null)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
        _gauges = gauges;
        _lockTextures = lockTextures;
        _status = statusTextures;
        _targetPaneTextures = targetPaneTextures;
    }

    /// <summary>
    /// Draw the whole HUD overlay. Self-contained: begins/ends its own
    /// <see cref="SpriteBatch"/> session, so it can be invoked after the 3D scene
    /// has already been drawn to the active render target (backbuffer or an
    /// offscreen capture target). The reticle and camera-mode tag draw even when
    /// <paramref name="frame"/> is null (i.e. before the client owns a ship yet);
    /// the telemetry widgets need a frame, so they are skipped until one exists.
    /// </summary>
    public void Draw(HudFrame? frame, CameraMode mode, Viewport viewport, LockBracketView? lockBracket = null, LeadIndicatorView? leadIndicator = null, string? modeLabelOverride = null, TargetSelectionView? targetSelection = null, Vector2? targetBearing = null, LeaderBotCommand? leaderCommand = null, string? ripcordStatus = null, string? commandeeLabel = null, bool turretView = false, string? lodOverlay = null, IReadOnlyList<LastBaseHudView>? lastBaseObjectives = null)
        => Draw(HudModelAdapter.Create(frame, mode, lockBracket, leadIndicator, modeLabelOverride,
            targetSelection, targetBearing, leaderCommand, ripcordStatus, commandeeLabel, turretView, lodOverlay, lastBaseObjectives: lastBaseObjectives), viewport);

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
        var ripcordStatus = model.FlightVisible ? context.Ripcord : null;
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
                var sz = _small.MeasureString(lodOverlay);
                DrawText(_small, lodOverlay, new Vector2(w - sz.X - 12f, 12f), Color.White);
            }
            // P228 — damage flash: a full-viewport translucent-red wash while the
            // local player's ship is mid flash-window (hull took a hit). Drawn
            // FIRST, beneath the reticle/HUD widgets, so the scene visibly tints
            // red but the telemetry text stays legible above it. Nothing to show
            // until a frame exists (the tracker is fed only the local ship's hull).
            bool flashing = frame is { } flashFrame && flashFrame.Flashing;
            if (flashing)
            { FillRect(0f, 0f, w, h, FlashRed); ElementDrawn("Damage flash timer"); }

            // P221 — firing readout: the reticle turns the warning colour and
            // shows a FIRING tag while StateM carries AllWeapons (group fire).
            // Computed before the null-frame return so the crosshair (drawn
            // even without a ship) can reflect it; the tag just stays inert
            // until a frame exists.
            bool firing = frame is { } ff && ff.Firing;
            if (model.ReticleVisible)
                {
                    DrawCrosshair(new Vector2(w / 2f, h / 2f), firing, turretView);
                    ElementDrawn("Cockpit centre reticle");
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

            // P266 — predictive lead reticle: drawn at the projected lead point
            // whenever the server has granted a lead that is in view. Distinct from
            // the static round crosshair above, colored green→red by the
            // hit-probability (grey when the intercept is out of weapon range).
            if (model.IndicatorsVisible && context.Lead is { } li)
                {
                    DrawLeadIndicator(new(new(li.Center.X, li.Center.Y), new(li.TargetCenter.X, li.TargetCenter.Y), li.InRange, li.Green, li.NowSeconds));
                    ElementDrawn("Ballistic lead marker", "Aim hit-probability predictor");
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
                    var size = _small.MeasureString(objective.Text);
                    var origin = new Vector2(w / 2f - size.X / 2f, 162f + i * 26f);
                    FillRect(origin.X - 8, origin.Y - 3, size.X + 16, size.Y + 6, Color.Black * 0.7f);
                    DrawText(_small, objective.Text, origin, BonusGold);
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
            DrawText(_small, modeLabel, new Vector2(16f, h - 26f),
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

            // HUD-3 — the native bottom target pane (dialog.mdl:5402, shown in
            // cockpit/chase). Present for a selected target even when the
            // projected in-world selection bracket is out of view.
            if (model.TargetPaneVisible && context.TargetPane is { Present: true })
                DrawTargetPane(model, viewport);

            if (frame is not { } f)
                return;   // no ship yet — gauges/readouts have nothing to show

            // P241 — the real original-game segmented gauges when the artwork
            // loaded; otherwise fall back to the P211 procedural panel.
            if (UsesModernGauges || _gauges is { } g && g.AllLoaded)
                DrawGauges(model, viewport);
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
            // P414 — own-ship ripcord countdown tag ("RIPCORDING → <sector>"),
            // driven by the local ship's ripcord state while active. Bright
            // cyan so it reads as an active countdown/activity like the mining
            // tag, placed beside it (a ripcording ship cannot be mining).
            if (ripcordStatus is { } rc) { DrawRipcord(rc, w); ElementDrawn("Ripcord countdown"); }

            // HUD-6 — native centre-HUD own-ship status icons/readouts at their
            // dialog.mdl anchors (kill bonus, signature, detection eye, cloak,
            // autopilot, vector lock, carried flag/artifact).
            DrawOwnStatus(model, viewport);
            DrawRoll(f.RollDegrees, w, h);
            // P442 — the always-visible money readout (top-left, above the kill
            // bonus): the local player's wallet, "$"-formatted as native does.
            // Rendered only once the money is KNOWN (f.Money is null otherwise)
            // — "money unknown" shows nothing, never 0.
            if (f.Money is { } money) { DrawMoney(money, w); ElementDrawn("Money credits"); }

            // P362 — the weapon/systems status list: a small persistent panel
            // (right edge) naming each mounted weapon/missile system with its
            // live ammo and a per-system READY/RELOAD/DEPLETED status derived
            // from P361's auto-reload behaviour. Empty list (loadout not yet
            // known) draws nothing.
            if (f.WeaponSystems is { Count: > 0 } ws)
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
    protected virtual void ElementDrawn(params string[] ids) { }
    protected virtual bool UsesModernGauges => false;
    protected virtual bool DeferWarpFlash => false;
    protected virtual void BeginBatch() => _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
    protected virtual void RenderText(SpriteFont font, string text, Vector2 position, Color color, float scale)
        => _sb.DrawString(font, text, position, color, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
    protected virtual void PrepareLayout(HudModel model, Viewport viewport)
    {
        var (mx, my, mw, mh) = MinimapGeometry.CornerPanel(viewport.Width, viewport.Height);
        Layout.Reserve(new((int)mx, (int)my, (int)mw, (int)mh));
        float spread = Math.Clamp(viewport.Height / 680f, 1.15f, 2.1f);
        foreach (var gauge in model.Gauges.Where(g => g.Visible))
        {
            var p = new Vector2(viewport.Width / 2f + gauge.NativeOffset.X * spread, viewport.Height / 2f + gauge.NativeOffset.Y * spread);
            Layout.Reserve(new Rectangle((int)p.X - 55, (int)p.Y - 50, 110, 150));
        }
        ReserveTargetPane(model, viewport);
    }

    /// <summary>Reserve the native bottom target pane so measured text (radar
    /// labels, notices, projected readouts) is placed around it.</summary>
    protected void ReserveTargetPane(HudModel model, Viewport viewport)
    {
        if (model.TargetPaneVisible && model.Context.TargetPane is { Present: true })
            Layout.Reserve(TargetPaneLayout.ReserveRect(viewport.Width, viewport.Height));
    }
    public Rectangle DrawOverlayText(string text, Vector2 position, Color color) => DrawMeasuredText(_small, text, position, color);
    protected void DrawText(SpriteFont font, string text, Vector2 position, Color color)
        => DrawMeasuredText(font, text, position, color);

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
        var color = RadarLabelColor(contact) * RadarLabelModel.BracketBrightness(contact);
        string? nameLine = view.NameLine;
        string? rangeLine = view.RangeLine;
        int barCount = (label.Fill is not null ? 1 : 0) + (label.Shield is not null ? 1 : 0)
            + (label.Hull is not null ? 1 : 0);
        int lineCount = (nameLine is not null ? 1 : 0) + (rangeLine is not null ? 1 : 0);
        if (lineCount == 0 && barCount == 0) return;

        float lineHeight = _small.LineSpacing * scale;
        float maxText = 0f;
        if (nameLine is not null)
            maxText = MathF.Max(maxText, _small.MeasureString(HudFormat.Printable(nameLine)).X * scale);
        if (rangeLine is not null)
            maxText = MathF.Max(maxText, _small.MeasureString(rangeLine).X * scale);
        float barRow = barCount > 0 ? 5f * scale : 0f;
        float blockWidth = MathF.Max(maxText, barCount * (24f + 4f) * scale) + 8f * scale;
        float blockHeight = lineCount * lineHeight + barRow + 6f * scale;

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
        if (nameLine is not null)
        {
            RenderText(_small, HudFormat.Printable(nameLine), new Vector2(placed.X, y), color, scale);
            y += lineHeight;
        }
        if (rangeLine is not null)
        {
            RenderText(_small, rangeLine, new Vector2(placed.X, y), color, scale);
            y += lineHeight;
        }
        if (barCount > 0)
        {
            float bx = placed.X, by = y + 2f * scale;
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

    public Rectangle DrawMeasuredText(SpriteFont font, string text, Vector2 position, Color color, bool instrument = false, bool centerAtScale = true, float? maxWidth = null, bool background = true)
    {
        // Long dynamic names and chat are fitted by measured width, never by a
        // character-count estimate. Retain the full value in the model.
        text = HudFormat.Printable(text);
        var measured = font.MeasureString(text);
        float scale = TextScale;
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
        var bounds = Layout.Place(text, measured * scale, position, instrument);
        if (background) FillRect(bounds.X - 3, bounds.Y - 1, bounds.Width + 6, bounds.Height + 2, Color.Black * .58f);
        RenderText(font, text, new(bounds.X, bounds.Y), color, scale);
        return bounds;
    }
    protected int _viewportWidth = 1920;
    protected int _viewportHeight = 1080;
    // ── Widgets ───────────────────────────────────────────────────────────

    private void DrawWarnings(IReadOnlyList<string> warnings, float w, float h)
    {
        for (int i = 0; i < warnings.Count; i++)
        {
            var size = _small.MeasureString(warnings[i]);
            DrawText(_small, warnings[i], new Vector2((w - size.X) / 2f, h * 0.19f + i * _small.LineSpacing), Warning);
        }
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
            for (int i = 0; i < lines.Count; i++)
            {
                var size = _small.MeasureString(lines[i]);
                var origin = new Vector2(viewport.Width / 2f - size.X / 2f,
                    viewport.Height - 150f - (lines.Count - 1 - i) * 26f);
                FillRect(origin.X - 8, origin.Y - 3, size.X + 16, size.Y + 6, Color.Black * 0.7f);
                DrawText(_small, lines[i], origin, BonusGold);
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
            var sz = _small.MeasureString(label);
            DrawText(_small, label, new Vector2(panelX, y + barH / 2f - sz.Y / 2f), TextDim);

            DrawBar(rows[i].frac, panelX + labelW + 16f, y, barW, barH);

            // P377 — beside the depleting OXYGEN bar, a real mm:ss countdown of
            // the remaining oxygen (amber under 30 s). Reflects the server's
            // own-ship oxygen fraction each tick.
            if (f.IsAdriftLifepod && i == 3)
            {
                var t = FormatCountdown(f.OxygenSecondsRemaining);
                var tsz = _small.MeasureString(t);
                Color c = f.OxygenSecondsRemaining <= 30 ? Warning : Accent;
                DrawText(_small, t, new Vector2(panelX + labelW + 16f + barW + 12f, y + barH / 2f - tsz.Y / 2f), c);
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
        float w = viewport.Width;
        float h = viewport.Height;
        var center = new Vector2(w / 2f, h / 2f);

        // Viewport-relative spread of the native offsets, and on-screen size of
        // each 128px gauge texture. Tunings live together here (not scattered).
        float spread = Math.Clamp(h / 680f, 1.15f, 2.1f);
        const float gaugeScale = 0.72f;

        foreach (var gauge in model.Gauges)
        {
            if (!gauge.Visible) continue;
            var anchor = center + new Vector2(gauge.NativeOffset.X, gauge.NativeOffset.Y) * spread;
            DrawGauge(gauge.Kind, gauge.Fraction, anchor, gaugeScale, gauge.Label, gauge.Color, gauge.DamageFlash);
        }
    }

    /// <summary>Map the throttle axis -1..+1 to the 0..1 fraction a fill
    /// consumes: idle (reverse) is empty, full-forward is full.</summary>
    private static float ThrottleFraction(float throttle) =>
        HudFormat.Clamp01((throttle + 1f) / 2f);

    /// <summary>
    /// Draw one real gauge at <paramref name="anchor"/> (its on-screen centre)
    /// filled to <paramref name="fraction"/> via the bottom-up clipped reveal,
    /// with a short label and percentage beneath. If the texture for this kind
    /// is missing, draws a small procedural bar instead so the slot never
    /// collapses.
    /// </summary>
    private void DrawGauge(GaugeKind kind, float fraction, Vector2 anchor, float gaugeScale, string? labelOverride = null, HudColor? valueColor = null, bool damageFlash = false)
    {
        Texture2D? tex = _gauges?.For(kind, damageFlash);
        float frac = HudFormat.Clamp01(fraction);
        // P377 — a caller may relabel the slot (e.g. FUEL → OXY for a lifepod).
        string label = labelOverride ?? GaugeTextureSource.LabelFor(kind);

        if (tex is null)
        {
            // Degraded slot: a compact procedural bar keeps the gauge legible.
            const float bw = 96f, bh = 12f;
            DrawBar(frac, anchor.X - bw / 2f, anchor.Y - bh / 2f, bw, bh);
            var lbl = _small.MeasureString(label);
            DrawText(_small, label, new Vector2(anchor.X - lbl.X / 2f, anchor.Y + bh / 2f + 4f), TextDim);
            return;
        }

        float gw = tex.Width * gaugeScale;
        float gh = tex.Height * gaugeScale;

        // Bottom-up reveal of the segment arc: draw only the bottom `frac`
        // fraction of the texture, aligned so its bottom edge sits at the
        // gauge's full-height bottom (native GaugeImage JustifyBottom).
        // Native thrust/fuel use JustifyTop instead (dialog.mdl:2173,2226), so
        // those reveal from the top edge down.
        bool revealFromTop = kind is GaugeKind.Thrust or GaugeKind.Fuel;
        int srcH = (int)MathF.Round(tex.Height * frac);
        if (srcH > 0)
        {
            var src = revealFromTop
                ? new Rectangle(0, 0, tex.Width, srcH)
                : new Rectangle(0, tex.Height - srcH, tex.Width, srcH);
            var dst = new Rectangle(
                (int)MathF.Round(anchor.X - gw / 2f),
                (int)MathF.Round(anchor.Y - gh / 2f + (revealFromTop ? 0f : gh - srcH * gaugeScale)),
                (int)MathF.Round(gw),
                (int)MathF.Round(srcH * gaugeScale));
            _sb.Draw(tex, dst, src, Color.White);
        }

        // Label + percentage underneath.
        string pct = HudFormat.FormatPercent(frac) + "%";
        var lsz = _small.MeasureString(label);
        var psz = _small.MeasureString(pct);
        float ty = anchor.Y + gh / 2f + 4f;
        DrawMeasuredText(_small, label, new Vector2(anchor.X - lsz.X / 2f, ty), TextDim, true);
        // P409 — the warning ramp (low = bad) is only meaningful for genuinely
        // depletable resources (hull/shield/energy/fuel/ammo). The Thrust gauge
        // is an AXIS position (idle throttle = 0%), not a consumable, so an
        // idle throttle must never read "0% WARNING" — always show its accent.
        DrawMeasuredText(_small, pct, new Vector2(anchor.X - psz.X / 2f, ty + lsz.Y + 1f),
            valueColor is { } color ? From(color) : Accent, true);
    }

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

        if (status.KillBonusPercent is { } kb)
        {
            var pos = OwnStatusLayout.ScreenPos("Kill bonus", viewport.Width, viewport.Height);
            var tex = _status?.KillCounter;
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
                DrawCenteredInNativeBox(_small, $"{kb:0}", numberBox, viewport.Height, Color.Black);
            }
            else
            {
                string text = $"KB {kb:0}%";
                DrawMeasuredText(_small, text, pos, BonusGold, true);
            }
            ElementDrawn("Kill bonus");
        }

        if (status.SignaturePercent is { } sig)
        {
            var pos = OwnStatusLayout.ScreenPos("Signature percentage", viewport.Width, viewport.Height);
            string text = $"SIG: {sig:0}%";
            // Native SignatureImage is StringImage(JustifyCenter, 80, …) whose
            // TranslateImage anchor is the box's top-left (dialog.mdl:2595-2602,
            // :2621), so the text centres inside the 80-point box.
            DrawCenteredInNativeBox(_small, text, pos, viewport.Height, TextMain);
            ElementDrawn("Signature percentage");
        }

        if (status.CloakDrained)
        {
            var pos = OwnStatusLayout.ScreenPos("Cloak strength and drained state", viewport.Width, viewport.Height);
            DrawMeasuredText(_small, "CLOAK DRAINED", pos, Warning, true);
            ElementDrawn("Cloak strength and drained state");
        }
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
            DrawMeasuredText(_small, label, pos, TextMain, true);
        }
        ElementDrawn(id);
    }

    /// <summary>Draw a native <c>StringImage(JustifyCenter, 80, …)</c> string: its
    /// box top-left is the TranslateImage anchor and the text is centred inside
    /// the 80-point box (dialog.mdl:2595-2602; Artwork/dialog.mdl:2561-2566).</summary>
    private void DrawCenteredInNativeBox(SpriteFont font, string text, Vector2 boxTopLeft,
        int viewportHeight, Color color)
    {
        var size = font.MeasureString(text) * TextScale;
        float boxWidth = OwnStatusLayout.NativeTextWidth * OwnStatusLayout.Spread(viewportHeight);
        RenderText(font, text, new(boxTopLeft.X + (boxWidth - size.X) / 2f, boxTopLeft.Y), color, TextScale);
    }

    /// <summary>Bottom-center speed readout: large accent numeral + unit label.</summary>
    private void DrawSpeed(float speed, string unit, float w, float cy)
    {
        string txt = HudFormat.FormatSpeed(speed);
        var sz = _big.MeasureString(txt);
        float cx = w / 2f;
        DrawText(_big, txt, new Vector2(cx - sz.X / 2f, cy), Accent);

        var usz = _small.MeasureString(unit);
        DrawText(_small, unit, new Vector2(cx - usz.X / 2f, cy + sz.Y + 2f), TextDim);
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
        var sz = _small.MeasureString(lbl);
        DrawText(_small, lbl, new Vector2(gx - sz.X / 2f, bottom + 14f), TextDim);
    }

    /// <summary>Top-center afterburner status + reactor power readout.</summary>
    private void DrawAfterburner(bool active, float power, float w)
    {
        string ab = active ? "AFTERBURNER ACTIVE" : "AFTERBURNER";
        var color = active ? Accent : TextDim;
        var sz = _small.MeasureString(ab);
        float cx = w / 2f;
        DrawText(_small, ab, new Vector2(cx - sz.X / 2f, 20f), color);

        string pwr = "PWR " + HudFormat.FormatPercent(power) + "%";
        var psz = _small.MeasureString(pwr);
        DrawText(_small, pwr, new Vector2(cx - psz.X / 2f, 20f + sz.Y + 2f), TextDim);
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
        var sz = _small.MeasureString(mode);
        float cx = w / 2f;
        DrawText(_small, mode, new Vector2(cx - sz.X / 2f, 58f), color);
    }

    /// <summary>P255 — the "MINING" tag: the word centred just beneath the
    /// weapon-mode readout, in a warm amber. Pure ASCII (spritefont char-range
    /// pitfall, cf. <see cref="DrawWeaponMode"/>).</summary>
    private void DrawMining(float w)
    {
        const string tag = "MINING";
        var color = new Color(0.98f, 0.74f, 0.24f);   // warm amber — activity, not alarm
        var sz = _small.MeasureString(tag);
        float cx = w / 2f;
        DrawText(_small, tag, new Vector2(cx - sz.X / 2f, 82f), color);
    }

    /// <summary>P414 — the \"RIPCORDING TO &lt;sector&gt;\" countdown tag, drawn
    /// beneath the CLOAKED/MINING activity tags while the local player's own
    /// ship has a ripcord countdown active. Bright cyan (reads as an active
    /// countdown/activity, distinct from amber mining and violet cloak). Pure
    /// ASCII — the label is built by Game1 as \"RIPCORDING TO &lt;name&gt;\" and
    /// sector names are ASCII, so it renders in the ASCII-only spritefont.</summary>
    private void DrawRipcord(string label, float w)
    {
        var color = new Color(0.25f, 0.95f, 0.95f);   // bright cyan — active countdown
        var sz = _small.MeasureString(label);
        float cx = w / 2f;
        DrawText(_small, label, new Vector2(cx - sz.X / 2f, 122f), color);
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
        var sz = _small.MeasureString(label);
        float cx = w / 2f;
        DrawText(_small, label, new Vector2(cx - sz.X / 2f, 16f), color);
    }

    /// <summary>
    /// P366 — the leader-only bot dock/launch prompt: a compact bottom-centre
    /// line (above the throttle) naming the command + the target bot, in the HUD
    /// accent. Present ONLY when the local player is the team leader with a
    /// same-side bot selected — Game1's <see cref="LeaderBotCommand"/> is null
    /// for a non-leader, so this simply never draws for them.</summary>
    private void DrawLeaderCommand(string label, float w, float cy)
    {
        var sz = _small.MeasureString(label);
        float cx = w / 2f;
        // P409 — raised above the bottom-centre speed numeral so the
        // leader-command prompt never overprints it.
        DrawText(_small, label, new Vector2(cx - sz.X / 2f, cy), Accent);
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
        var sz = _small.MeasureString(label);
        float cx = w / 2f;
        var mint = new Color(0.55f, 0.95f, 0.65f);   // mint — distinct from the leader cyan accent
        DrawText(_small, label, new Vector2(cx - sz.X / 2f, cy), mint);
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
        var vsz = _small.MeasureString(val);
        // Value in the teal accent; a dim "MONEY" caption above it.
        DrawText(_small, "MONEY", new Vector2(x, y), TextDim);
        DrawText(_small, val, new Vector2(x, y + vsz.Y + 2f), Accent);
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
        var sz = _small.MeasureString(txt);
        DrawText(_small, txt, new Vector2(cx - sz.X / 2f, top), Accent);

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
        var lsz = _small.MeasureString(lbl);
        DrawText(_small, lbl, new Vector2(cx - lsz.X / 2f, barY + barH + 5f), TextDim);
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
        float line = _small.LineSpacing * scale + 5;
        float panelW = Math.Min(viewport.Width * .34f, 380 * Math.Max(1, scale));
        float x = viewport.Width - panelW - 18;
        float y = Math.Max(150, line * 4);
        FillRect(x, y, panelW, line * (1 + rows.Count * 3), PanelBg);
        DrawMeasuredText(_small, "SYSTEMS", new(x + 8, y), TextDim, centerAtScale: false);
        y += line;
        foreach (var r in rows)
        {
            DrawMeasuredText(_small, r.Name, new(x + 8, y), TextMain, centerAtScale: false);
            y += line;
            var (tag, col) = StatusTag(r.Status);
            string ammo = r.ConsumesAmmo ? $"{r.Ammo}/{r.CapacityAmmo}" : "--";
            // One measured string keeps NOT READY and the magazine distinct.
            DrawMeasuredText(_small, $"{ammo}  |  {tag}", new(x + 8, y), col, centerAtScale: false);
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

    /// <summary>Center reticle: a thin circular ring (drawn as short line
    /// segments), four cardinal tick notches, and a centre dot. Drawn in every
    /// chase view too. When <paramref name="firing"/> (group-fire engaged) the
    /// ring/ticks/dot render in the weapons-hot warning colour and a FIRING
    /// tag appears beneath the ring.</summary>
    protected virtual void DrawCrosshair(Vector2 c, bool firing, bool turret)
    {
        var accent = firing ? Warning : Accent;
        const int segments = 36;
        float radius = turret ? 34f : 26f;

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
                var sz = _small.MeasureString(lbl);
                DrawText(_small, lbl, new Vector2(c.X - sz.X / 2f, c.Y + radius + 20f), Warning);
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
            var sz = _small.MeasureString(lbl);
            DrawText(_small, lbl, new Vector2(c.X - sz.X / 2f, c.Y + radius + 18f), Warning);
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
        float s = LockBracketStyle.HalfSizeForFraction(v.State, v.Fraction, v.ScreenRadius, toEdge);

        Texture2D? tex = _lockTextures?.For(v.State);
        if (tex is { } t)
        {
            // Scale the 64px real sprite so its on-screen span is 2*s px,
            // centred on the target, preserving its real colours (White tint).
            float scale = (2f * s) / t.Width;
            _sb.Draw(t, v.Center, null, Color.White, 0f,
                new Vector2(t.Width / 2f, t.Height / 2f), scale, SpriteEffects.None, 0f);
            return;
        }

        // Real sprite unavailable → procedural corner-L bracket fallback.
        DrawLockBracketProcedural(v, s);
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
        if (v.CharacterName is { } pilotName)
        {
            var nsz = _small.MeasureString(pilotName);
            var nameBox = DrawMeasuredText(_small, pilotName,
                new Vector2(c.X - nsz.X / 2f, y), TextMain);
            y = nameBox.Bottom + 3f;
        }
        if (v.Distance is { } distance)
        {
            string range = $"{distance:0} m";
            var rangeBox = DrawMeasuredText(_small, range, new(c.X - _small.MeasureString(range).X / 2, y), TextDim);
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
            var lsz = _small.MeasureString(lbl);
            DrawText(_small, lbl, new Vector2(c.X - lsz.X / 2f, y), TextMain);
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

        // Decorative oval/word art (native white art; the oval colour layer is
        // side-tinted, dialog.mdl:4165-4191).
        DrawPaneTexture(_targetPaneTextures?.Oval, x0 + 192f * s, y0 + 75f * s, s, Color.White);
        DrawPaneTexture(_targetPaneTextures?.OvalColor, x0 + 256f * s, y0 + 2f * s, s, side);
        DrawPaneTexture(_targetPaneTextures?.OvalWord1, x0 + 193f * s, y0 - 8f * s, s, Color.White);
        DrawPaneTexture(_targetPaneTextures?.OvalWord2, x0 + 320f * s, y0 - 8f * s, s, Color.White);

        // Name + type (native GetName(Target) ": " GetModelTypeDesc(Target),
        // dialog.mdl:4043-4050), centred at the pane centre.
        string nameLine = (tp.Name ?? "unknown") + (tp.TypeDescription is { } td ? ": " + td : "");
        var nameSize = PaneTextSize(_small, nameLine);
        DrawRawText(_small, nameLine,
            new(x0 + TargetPaneLayout.LocalCenterX * s - nameSize.X / 2f,
                y0 - TargetPaneLayout.LocalNameBottom * s - nameSize.Y), side);

        // Range (left) and speed (right), native local y=33. Both use native
        // NumberString ((int)(v+0.5), engine/modeler.cpp:2008) so the unseen
        // speed sentinel -1 reads "0" and exact halves round up.
        if (tp.Distance is { } distance)
        {
            string range = string.Concat("RANGE: ", HudFormat.FormatNativeNumber(distance));
            DrawRawText(_small, range, new(x0 + 400f * s, y0 - 33f * s - PaneTextSize(_small, range).Y), side);
        }
        if (tp.Speed is { } speed)
        {
            string text = string.Concat("SPEED: ", HudFormat.FormatNativeNumber(speed));
            var size = PaneTextSize(_small, text);
            DrawRawText(_small, text, new(x0 + 242f * s - size.X, y0 - 33f * s - size.Y), side);
        }

        // ARMOR / SHIELD labels (native medVerdana; port reuses the HUD font).
        var armorSize = PaneTextSize(_small, "ARMOR");
        DrawRawText(_small, "ARMOR", new(x0 + 228f * s - armorSize.X, y0 - 73f * s - armorSize.Y), side);
        DrawRawText(_small, "SHIELD", new(x0 + 410f * s, y0 - 73f * s - PaneTextSize(_small, "SHIELD").Y), side);

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
    /// the draw path measures and draws them verbatim — no per-frame LINQ.</summary>
    protected Vector2 PaneTextSize(SpriteFont font, string text) =>
        font.MeasureString(text) * TextScale;

    protected void DrawRawText(SpriteFont font, string text, Vector2 topLeft, Color color)
        => RenderText(font, text, topLeft, color, TextScale);

    protected void DrawPaneTexture(Texture2D? tex, float leftX, float bottomY, float scale, Color tint)
    {
        if (tex is null) return;
        var dst = new Rectangle((int)MathF.Round(leftX), (int)MathF.Round(bottomY - tex.Height * scale),
            (int)MathF.Round(tex.Width * scale), (int)MathF.Round(tex.Height * scale));
        _sb.Draw(tex, dst, tint);
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
        var lsz = _small.MeasureString(lbl);
        DrawText(_small, lbl,
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
        // Render colour: green→red ramp by hit-probability when in range, else the
        // native out-of-range grey (translucent so the scene stays readable).
        Color col = v.InRange
            ? new Color(1f - v.Green, v.Green, 0f)
            : new Color(0.33f, 0.33f, 0.33f, 0.5f);

        // Open diamond (rotated square) around the lead point — visually distinct
        // from the round crosshair. Corners of a half-size square rotated 45°.
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

        // Centre dot — the precise aim point.
        FillRect(c.X - 2f, c.Y - 2f, 4f, 4f, col);

        // Faint hollow ring at the target's CURRENT on-screen centre (where it is
        // NOW) — the gap between this ring and the diamond is the real predictive
        // lead, kept visible so the offset is legible rather than decorative.
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
        DrawText(_small, $"MOUSE DZ {(int)(v.Deadzone * 100f)}% {mode}",
            new Vector2(cx - 58f, cy + R + 6f), TextDim);
    }
}
