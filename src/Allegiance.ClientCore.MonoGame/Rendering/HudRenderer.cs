using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Renders the 2D HUD overlay on top of the 3D scene, via <see cref="SpriteBatch"/>
/// with procedurally-drawn shapes (a 1x1 white <see cref="Texture2D"/> tinted and
/// scaled for fills, and thin rotated rectangles for lines/arcs) plus two baked
/// <c>.spritefont</c> bitmaps. Since P241 the six core flight gauges are REAL
/// original-game textures (the <c>*Gaugebmp.mdl</c> segmented-arc gauges, decoded
/// at runtime by <see cref="MdlTextureDecoder"/> and drawn by
/// <see cref="GaugeTextures"/>) with the native bottom-up clipped reveal — not the
/// procedural bars of the P211 "modern glass-panel" design, which remains only as
/// a degraded fallback when the artwork is unavailable.
/// </summary>
/// <remarks>
/// <para>Modern sci-fi glass-panel aesthetic: thin glowing accent lines, dark
/// translucent panels, high-contrast monospace numerals. The accent colour is
/// <see cref="HudColor.TealAccent"/> — the Bios Scout's teal/cyan-green energy
/// conduits — so the overlay reads as part of this ship rather than an arbitrary
/// palette. This is a first-pass visual design; the exact spacing/tints are easy
/// to iterate on once seen live.</para>
/// <para>Layout (viewport-sized): a center reticle (always, regardless of
/// camera mode), a left status panel (hull/shield/energy/fuel/ammo/power bars),
/// a bottom-center speed readout, a right-side throttle gauge, a top-center
/// afterburner/power indicator, and a small camera-mode tag.</para>
/// </remarks>
public sealed class HudRenderer
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
    private static readonly Color FlashRed = new(0.90f, 0.05f, 0.045f, 0.22f);

    // P245 — warp-transit flash tint: full opaque black (a real blackscreen),
    // with the amount set per-frame by HudFrame.WarpFlash (0..1). This is the
    // brief "you got flung through a sector" acknowledgment, drawn LAST over the
    // whole frame (HUD included) — a genuine blackout, unlike the damage wash.
    private static readonly Color WarpBlack = new(0f, 0f, 0f, 1f);

    // P270 — own-ship cloak colour reused in the CLOAKED tag.
    private static readonly Color CloakViolet = new(0.62f, 0.55f, 0.98f);
    // P298 — Kill Bonus gold: a warm reward tone (distinct from the teal
    // accents, the red Warning, the amber MINING and the violet CLOAKED tags).
    private static readonly Color BonusGold = new(0.97f, 0.79f, 0.30f);

    // P362 — reload-in-progress amber: a system whose magazine hit zero and is
    // mid auto-reload (P361). Warm activity tone, distinct from Ready (dim) and
    // Depleted (Warning red) — mirrors the MINING tag's amber family.
    private static readonly Color ReloadAmber = new(0.98f, 0.74f, 0.24f);

    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;
    private readonly GaugeTextures? _gauges;

    public HudRenderer(
        SpriteBatch spriteBatch,
        Texture2D whitePixel,
        SpriteFont small,
        SpriteFont big,
        GaugeTextures? gauges = null)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
        _gauges = gauges;
    }

    /// <summary>
    /// Draw the whole HUD overlay. Self-contained: begins/ends its own
    /// <see cref="SpriteBatch"/> session, so it can be invoked after the 3D scene
    /// has already been drawn to the active render target (backbuffer or an
    /// offscreen capture target). The reticle and camera-mode tag draw even when
    /// <paramref name="frame"/> is null (i.e. before the client owns a ship yet);
    /// the telemetry widgets need a frame, so they are skipped until one exists.
    /// </summary>
    public void Draw(HudFrame? frame, CameraMode mode, Viewport viewport, LockBracketView? lockBracket = null, LeadIndicatorView? leadIndicator = null, string? modeLabelOverride = null, TargetSelectionView? targetSelection = null, Vector2? targetBearing = null, LeaderBotCommand? leaderCommand = null, string? ripcordStatus = null)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            // P228 — damage flash: a full-viewport translucent-red wash while the
            // local player's ship is mid flash-window (hull took a hit). Drawn
            // FIRST, beneath the reticle/HUD widgets, so the scene visibly tints
            // red but the telemetry text stays legible above it. Nothing to show
            // until a frame exists (the tracker is fed only the local ship's hull).
            bool flashing = frame is { } flashFrame && flashFrame.Flashing;
            if (flashing)
                FillRect(0f, 0f, w, h, FlashRed);

            // P221 — firing readout: the reticle turns the warning colour and
            // shows a FIRING tag while StateM carries AllWeapons (group fire).
            // Computed before the null-frame return so the crosshair (drawn
            // even without a ship) can reflect it; the tag just stays inert
            // until a frame exists.
            bool firing = frame is { } ff && ff.Firing;
            DrawCrosshair(new Vector2(w / 2f, h / 2f), firing);

            // P230 — missile-lock target box: drawn whenever Game1 has projected a
            // lock target into view, tinted/animated by the current lock state
            // (LockBracketStyle). Independently of a ship frame (a target box can
            // precede the status panel the same way the crosshair does).
            if (lockBracket is { } lb)
                DrawLockBracket(lb);

            // P266 — predictive lead reticle: drawn at the projected lead point
            // whenever the server has granted a lead that is in view. Distinct from
            // the static round crosshair above, colored green→red by the
            // hit-probability (grey when the intercept is out of weapon range).
            if (leadIndicator is { } li)
                DrawLeadIndicator(li);

            // P345 — the player's OWN T/G autopilot-target selection box: a full
            // closed rectangle drawn at the selected target's projected centre,
            // brightened while autopilot is engaged. Deliberately drawn AFTER the
            // missile-lock box and lead reticle so the pilot's own selection is the
            // most prominent of the target markers when several overlap. Distinct
            // shape (closed vs open corners) and colour from the lock box.
            if (targetSelection is { } ts)
                DrawTargetSelectionBracket(ts);

            // P359 — the target bearing arrow: a fixed-position compass chevron in
            // the bottom-right corner pointing toward the selected target's REAL
            // relative bearing from the camera. Drawn whenever a target is selected,
            // INCLUDING when the target itself is off-screen (that is exactly when
            // it matters most — the bracket above is null out of view). Applies to
            // every target kind (ship/station/asteroid/warp): pure directional math.
            if (targetBearing is { } tb)
                DrawTargetBearingArrow(tb, viewport);

            // Camera-mode tag (bottom-left) — also proves the toggle in captures.
            // modeLabelOverride (P306) lets the headless capture show "OVERVIEW"
            // instead of lying about the on-screen camera-mode toggle.
            //
            // P340 (B4) — every camera mode gets its honest label. Previously
            // the ternary collapsed everything but Cockpit into "CHASE", so the
            // P295 ExternalTurret and ExternalMissile views both displayed a
            // label that was false. The names mirror native's view modes
            // (Treki.h:108-126): cmExternalTurret and cmExternalMissile.
            string modeLabel = modeLabelOverride ?? (mode switch
            {
                CameraMode.Cockpit => "COCKPIT",
                CameraMode.ExternalTurret => "EXT TURRET",
                CameraMode.ExternalMissile => "EXT MISSILE",
                _ => "CHASE",
            });
            _sb.DrawString(_small, modeLabel, new Vector2(16f, h - 26f),
                mode == CameraMode.Cockpit ? Accent : TextDim);

            // P366 — the leader-only bot dock/launch prompt: drawn whenever the
            // local player is the team leader AND the T-selected target is a
            // same-side bot (Game1 resolves it per frame). A compact bottom-centre
            // line under the weapon readout; absent for a non-leader (nothing to
            // draw — the gate is enforced upstream in ResolveLeaderBotCommand).
            if (leaderCommand is { } lc)
                DrawLeaderCommand(lc.Label, w, h);

            if (frame is not { } f)
                return;   // no ship yet — gauges/readouts have nothing to show

            // P241 — the real original-game segmented gauges when the artwork
            // loaded; otherwise fall back to the P211 procedural panel.
            if (_gauges is { } g && g.AllLoaded)
                DrawGauges(f, viewport);
            else
            {
                DrawStatusPanel(f, w);
                DrawThrottleGauge(f.Throttle, w, h);
            }
            DrawSpeed(f.Speed, w, h);
            DrawAfterburner(f.AfterburnerActive, f.Power, w);
            DrawWeaponMode(f, w);
            // P255 — a simple, honest "MINING" tag while the local ship is
            // actively drilling a rock (kept modest: just the word, top-centre
            // under the weapon readout, in a warm amber to read as activity
            // rather than danger). The ship's FACING toward the rock is the
            // primary cue (Game1 overrides the own hull orientation); this text
            // just disambiguates it.
            if (f.Mining) DrawMining(w);
            // P270 — own-ship cloak status tag (a teammate/enemy cloak renders
            // translucent via CloakVisual; this tells the local pilot THEIR OWN
            // hull is cloaked, which otherwise has no on-screen cue in cockpit
            // view). Cool violet — distinct from the amber MINING activity tag.
            if (f.Cloaked) DrawCloak(w);
            // P414 — own-ship ripcord countdown tag ("RIPCORDING → <sector>"),
            // driven by the local ship's ripcord state while active. Bright
            // cyan so it reads as an active countdown/activity like the mining
            // tag, placed beside it (a ripcording ship cannot be mining).
            if (ripcordStatus is { } rc) DrawRipcord(rc, w);

            // P298 — the two new readouts native only hints at via training
            // overlays (KillBonusTrainingOverlay/RollTrainingOverlay,
            // WinTrek/indicator.cpp:625-642) but this port surfaces as real
            // live gauges: the ship's Kill Bonus (top-left, warm gold = reward)
            // and its current roll angle (top-right, teal). Drawn whenever a
            // frame exists — 0% KB / level 0° are honest readouts, not clutter.
            DrawKillBonus(f, w);
            DrawRoll(f.RollDegrees, w, h);

            // P362 — the weapon/systems status list: a small persistent panel
            // (right edge) naming each mounted weapon/missile system with its
            // live ammo and a per-system READY/RELOAD/DEPLETED status derived
            // from P361's auto-reload behaviour. Empty list (loadout not yet
            // known) draws nothing.
            if (f.WeaponSystems is { Count: > 0 } ws)
                DrawWeaponStatusList(ws, viewport);

            // P245 — warp-transit flash, drawn LAST so it blacks out the whole
            // frame (HUD and all) at the moment the local ship changes sector.
            // A genuine "blackscreen" (opaque, alpha = WarpFlash) as opposed to
            // the translucent damage wash above. Opacity is 0 outside the brief
            // window, so this is a no-op for every frame that isn't flashing.
            float warpFlash = f.WarpFlash;
            if (warpFlash > 0f)
                FillRect(0f, 0f, w, h, WarpBlack * warpFlash);
        }
        finally
        {
            _sb.End();
        }
    }

    // ── Widgets ───────────────────────────────────────────────────────────

    /// <summary>Left status panel: labelled horizontal bars for the six live
    /// 0..1 quantities, each filled proportionally and tinted by
    /// <see cref="HudFormat.StatusColor"/>.</summary>
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
            _sb.DrawString(_small, label, new Vector2(panelX, y + barH / 2f - sz.Y / 2f), TextDim);

            DrawBar(rows[i].frac, panelX + labelW + 16f, y, barW, barH);

            // P377 — beside the depleting OXYGEN bar, a real mm:ss countdown of
            // the remaining oxygen (amber under 30 s). Reflects the server's
            // own-ship oxygen fraction each tick.
            if (f.IsAdriftLifepod && i == 3)
            {
                var t = FormatCountdown(f.OxygenSecondsRemaining);
                var tsz = _small.MeasureString(t);
                Color c = f.OxygenSecondsRemaining <= 30 ? Warning : Accent;
                _sb.DrawString(_small, t, new Vector2(panelX + labelW + 16f + barW + 12f, y + barH / 2f - tsz.Y / 2f), c);
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
    private void DrawGauges(in HudFrame f, Viewport viewport)
    {
        float w = viewport.Width;
        float h = viewport.Height;
        var center = new Vector2(w / 2f, h / 2f);

        // Viewport-relative spread of the native offsets, and on-screen size of
        // each 128px gauge texture. Tunings live together here (not scattered).
        float spread = Math.Clamp(h / 680f, 1.15f, 2.1f);
        const float gaugeScale = 0.72f;

        // (kind, fraction, native X offset, native Y offset) — offsets from the
        // training overlay, used AS-IS. Native's 2D Point (zlib/point.h) is the
        // Windows POINT convention — top-left origin, +Y DOWN — which is exactly
        // MonoGame's screen space, so no Y inversion is applied. The P241 port
        // wrongly negated every Y (assuming native was Y-up); P395 removes that
        // so throttle/fuel sit BELOW the crosshair (a compact cluster) instead
        // of sticking up like horns.
        var placements = new (GaugeKind kind, float frac, float ox, float oy)[]
        {
            (GaugeKind.Thrust, ThrottleFraction(f.Throttle), -126.5f, +93.5f),   // lower-left
            (GaugeKind.Fuel,    f.FuelFraction,                +126.5f, +93.5f),   // lower-right
            (GaugeKind.Energy,  f.EnergyFraction,              +126.5f, -112.5f),  // upper-right
            (GaugeKind.Ammo,    f.AmmoFraction,                -126.5f, -112.5f),  // upper-left
            (GaugeKind.Hull,    f.HullFraction,                -65.5f,  +35.5f),   // lower-centre-left
            (GaugeKind.Shield,  f.ShieldFraction,              +65.5f,  +35.5f),   // lower-centre-right
        };

        foreach (var (kind, frac, ox, oy) in placements)
        {
            var anchor = center + new Vector2(ox, oy) * spread;
            // P377 — while adrift in a lifepod the upper-right FUEL gauge (a
            // pod has no real fuel) reads as the depleting OXYGEN supply.
            string? labelOverride = (kind == GaugeKind.Fuel && f.IsAdriftLifepod) ? "OXY" : null;
            DrawGauge(kind, frac, anchor, gaugeScale, labelOverride);
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
    private void DrawGauge(GaugeKind kind, float fraction, Vector2 anchor, float gaugeScale, string? labelOverride = null)
    {
        Texture2D? tex = _gauges?.For(kind);
        float frac = HudFormat.Clamp01(fraction);
        // P377 — a caller may relabel the slot (e.g. FUEL → OXY for a lifepod).
        string label = labelOverride ?? GaugeTextureSource.LabelFor(kind);

        if (tex is null)
        {
            // Degraded slot: a compact procedural bar keeps the gauge legible.
            const float bw = 96f, bh = 12f;
            DrawBar(frac, anchor.X - bw / 2f, anchor.Y - bh / 2f, bw, bh);
            var lbl = _small.MeasureString(label);
            _sb.DrawString(_small, label, new Vector2(anchor.X - lbl.X / 2f, anchor.Y + bh / 2f + 4f), TextDim);
            return;
        }

        float gw = tex.Width * gaugeScale;
        float gh = tex.Height * gaugeScale;

        // Bottom-up reveal of the segment arc: draw only the bottom `frac`
        // fraction of the texture, aligned so its bottom edge sits at the
        // gauge's full-height bottom (native GaugeImage JustifyBottom).
        int srcH = (int)MathF.Round(tex.Height * frac);
        if (srcH > 0)
        {
            var src = new Rectangle(0, tex.Height - srcH, tex.Width, srcH);
            var dst = new Rectangle(
                (int)MathF.Round(anchor.X - gw / 2f),
                (int)MathF.Round(anchor.Y - gh / 2f + (gh - srcH * gaugeScale)),
                (int)MathF.Round(gw),
                (int)MathF.Round(srcH * gaugeScale));
            _sb.Draw(tex, dst, src, Color.White);
        }

        // Label + percentage underneath.
        string pct = HudFormat.FormatPercent(frac) + "%";
        var lsz = _small.MeasureString(label);
        var psz = _small.MeasureString(pct);
        float ty = anchor.Y + gh / 2f + 4f;
        _sb.DrawString(_small, label, new Vector2(anchor.X - lsz.X / 2f, ty), TextDim);
        // P409 — the warning ramp (low = bad) is only meaningful for genuinely
        // depletable resources (hull/shield/energy/fuel/ammo). The Thrust gauge
        // is an AXIS position (idle throttle = 0%), not a consumable, so an
        // idle throttle must never read "0% WARNING" — always show its accent.
        bool warn = kind != GaugeKind.Thrust && frac <= 0.5f;
        _sb.DrawString(_small, pct, new Vector2(anchor.X - psz.X / 2f, ty + lsz.Y + 1f),
            warn ? Warning : Accent);
    }

    /// <summary>Bottom-center speed readout: large accent numeral + unit label.</summary>
    private void DrawSpeed(float speed, float w, float h)
    {
        string txt = HudFormat.FormatSpeed(speed);
        var sz = _big.MeasureString(txt);
        float cx = w / 2f;
        float cy = h - 108f;
        _sb.DrawString(_big, txt, new Vector2(cx - sz.X / 2f, cy), Accent);

        string unit = "U/S";
        var usz = _small.MeasureString(unit);
        _sb.DrawString(_small, unit, new Vector2(cx - usz.X / 2f, cy + sz.Y + 2f), TextDim);
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
        _sb.DrawString(_small, lbl, new Vector2(gx - sz.X / 2f, bottom + 14f), TextDim);
    }

    /// <summary>Top-center afterburner status + reactor power readout.</summary>
    private void DrawAfterburner(bool active, float power, float w)
    {
        string ab = active ? "AFTERBURNER ACTIVE" : "AFTERBURNER";
        var color = active ? Accent : TextDim;
        var sz = _small.MeasureString(ab);
        float cx = w / 2f;
        _sb.DrawString(_small, ab, new Vector2(cx - sz.X / 2f, 20f), color);

        string pwr = "PWR " + HudFormat.FormatPercent(power) + "%";
        var psz = _small.MeasureString(pwr);
        _sb.DrawString(_small, pwr, new Vector2(cx - psz.X / 2f, 20f + sz.Y + 2f), TextDim);
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
        _sb.DrawString(_small, mode, new Vector2(cx - sz.X / 2f, 58f), color);
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
        _sb.DrawString(_small, tag, new Vector2(cx - sz.X / 2f, 82f), color);
    }

    /// <summary>P270 — a small "CLOAKED" tag just below the weapon readout
    /// while the local player's own ship carries the cloak bit. Pure ASCII
    /// (spritefont char-range pitfall), cool violet to read as stealth rather
    /// than damage or activity.</summary>
    private void DrawCloak(float w)
    {
        const string tag = "CLOAKED";
        var color = CloakViolet;   // cool violet — stealth, not alarm
        var sz = _small.MeasureString(tag);
        float cx = w / 2f;
        _sb.DrawString(_small, tag, new Vector2(cx - sz.X / 2f, 102f), color);
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
        _sb.DrawString(_small, label, new Vector2(cx - sz.X / 2f, 122f), color);
    }

    /// <summary>
    /// P366 — the leader-only bot dock/launch prompt: a compact bottom-centre
    /// line (above the throttle) naming the command + the target bot, in the HUD
    /// accent. Present ONLY when the local player is the team leader with a
    /// same-side bot selected — Game1's <see cref="LeaderBotCommand"/> is null
    /// for a non-leader, so this simply never draws for them.</summary>
    private void DrawLeaderCommand(string label, float w, float h)
    {
        var sz = _small.MeasureString(label);
        float cx = w / 2f;
        // P409 — raised above the bottom-centre speed numeral (h - 108) so the
        // leader-command prompt never overprints the speed readout.
        _sb.DrawString(_small, label, new Vector2(cx - sz.X / 2f, h - 140f), Accent);
    }

    /// <summary>
    /// P298 — the Kill Bonus gauge (top-left): the real live KB readout P278
    /// computes (native <c>100·(GetExperienceMultiplier()−1)</c>, 0..50 cap 50)
    /// presented as a labelled gold readout — the percentage numeral, a
    /// proportional 0..50% bar, and the raw streak tally. Native only flags this
    /// as a HUD element via the <c>KillBonusTrainingOverlay</c> callout
    /// (WinTrek/indicator.cpp:625-631, a red arrow labeled "KILL BONUS" at
    /// centre +(−15,−160.5)) and prints the number only to console
    /// (WinTrek/console.cpp:826 <c>showexperience</c>); this port gives the pilot
    /// the live readout the training hint points at. Gold = reward, distinct
    /// from every other widget colour. Always drawn when a frame exists — 0%
    /// (a fresh ship) is an honest readout, not clutter.
    /// </summary>
    private void DrawKillBonus(in HudFrame f, float w)
    {
        const float x = 24f;
        const float y = 96f;
        const float barW = 120f;
        const float barH = 8f;

        // Percent numeral (the headline value) — the actual bonus, 0..50
        // (native 100·(mult−1)), in gold; 0% renders dim.
        string pct = $"{f.KillBonusPercent:0}%";
        var psz = _small.MeasureString(pct);
        _sb.DrawString(_small, pct, new Vector2(x, y), f.KillBonusPercent > 0f ? BonusGold : TextDim);

        // 0..50% proportional bar (native's multiplier scaled to the 50 cap,
        // so it fills toward 100% as the bonus approaches +50%).
        DrawBar(f.KillBonusPercent / 50f, x, y + psz.Y + 6f, barW, barH);

        // Raw streak tally under the bar ("N KILLS", singular for one).
        string streak = f.KillBonusStreak == 1 ? "1 KILL" : $"{f.KillBonusStreak} KILLS";
        _sb.DrawString(_small, streak, new Vector2(x, y + psz.Y + 6f + barH + 5f), TextDim);

        // Small "KILL BONUS" caption above the numeral so the block reads.
        var csz = _small.MeasureString("KILL BONUS");
        _sb.DrawString(_small, "KILL BONUS", new Vector2(x, y - csz.Y - 2f), TextDim);
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
        _sb.DrawString(_small, txt, new Vector2(cx - sz.X / 2f, top), Accent);

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
        _sb.DrawString(_small, lbl, new Vector2(cx - lsz.X / 2f, barY + barH + 5f), TextDim);
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
        const float panelW = 292f;
        const float rowH = 30f;
        const float top = 150f;                 // just below the top-right Roll gauge
        float w = viewport.Width;
        float right = w - 18f;
        float x = right - panelW;
        float panelH = 34f + rows.Count * rowH;

        // Glass panel backing + accent spine down the left edge.
        FillRect(x, top, panelW, panelH, PanelBg);
        FillRect(x, top, 2f, panelH, Accent * 0.65f);

        const string title = "SYSTEMS";
        var tsz = _small.MeasureString(title);
        _sb.DrawString(_small, title, new Vector2(x + 8f, top + 6f), TextDim);

        float ty = top + 28f;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            float y = ty + i * rowH;

            string name = r.Name.Length > 17 ? r.Name[..16] + "~" : r.Name;
            _sb.DrawString(_small, name, new Vector2(x + 8f, y), TextMain);

            if (r.ConsumesAmmo)
            {
                var (tag, col) = StatusTag(r.Status);
                var tagSz = _small.MeasureString(tag);
                _sb.DrawString(_small, tag, new Vector2(right - tagSz.X - 6f, y), col);

                string ammoTxt = $"{r.Ammo}/{r.CapacityAmmo}";
                var asz = _small.MeasureString(ammoTxt);
                _sb.DrawString(_small, ammoTxt, new Vector2(x + 8f + panelW - 86f, y), TextDim);

                DrawBar(r.CapacityAmmo > 0 ? (float)r.Ammo / r.CapacityAmmo : 0f,
                    x + 8f, y + asz.Y + 6f, panelW - 16f, 4f);
            }
            else
            {
                const string ok = "READY";
                var tagSz = _small.MeasureString(ok);
                _sb.DrawString(_small, ok, new Vector2(right - tagSz.X - 6f, y), TextDim);
                _sb.DrawString(_small, "--", new Vector2(x + 8f + panelW - 86f, y), TextDim);
            }
        }
    }

    /// <summary>The per-system status tag text + colour (P362).</summary>
    private static (string tag, Color color) StatusTag(WeaponStatus s) => s switch
    {
        WeaponStatus.Reloading => ("RELOAD", ReloadAmber),
        WeaponStatus.Depleted => ("DEPLETED", Warning),
        _ => ("READY", TextDim),
    };

    /// <summary>Center reticle: a thin circular ring (drawn as short line
    /// segments), four cardinal tick notches, and a centre dot. Drawn in every
    /// chase view too. When <paramref name="firing"/> (group-fire engaged) the
    /// ring/ticks/dot render in the weapons-hot warning colour and a FIRING
    /// tag appears beneath the ring.</summary>
    private void DrawCrosshair(Vector2 c, bool firing)
    {
        var accent = firing ? Warning : Accent;

        const int segments = 36;
        const float radius = 26f;
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
            _sb.DrawString(_small, lbl, new Vector2(c.X - sz.X / 2f, c.Y + radius + 18f), Warning);
        }
    }

    // ── Primitive helpers ─────────────────────────────────────────────────

    /// <summary>
    /// P230 — draw the missile-lock target box around a projected on-screen centre:
    /// four corner "L" brackets forming a square, tinted/animated by the lock state
    /// via <see cref="LockBracketStyle"/> (no lock = dim static; partial = amber
    /// pulsing scale; full = hot red stronger pulse + slow acquisition spin — the
    /// port's reading of native's "animated squares around the target ship"). Must
    /// be called inside an active <see cref="SpriteBatch"/> session (i.e. from
    /// <see cref="Draw"/>). Draws the corner arms as rotated thin rects reusing the
    /// same 1x1 white pixel, so no new asset is needed.
    /// </summary>
    private void DrawLockBracket(in LockBracketView v)
    {
        float s = LockBracketStyle.HalfSizeFor(v.State, v.NowSeconds);
        const float arm = 13f;
        const float thickness = 2.5f;
        float rot = LockBracketStyle.RotationFor(v.State, v.NowSeconds);
        Color col = LockBracketStyle.ColorFor(v.State);

        // The four box corners (TL, TR, BL, BR) in local square coords, each with
        // its two arm directions (open side facing OUTWARD, away from the box).
        var corners = new (float sx, float sy)[]
        {
            (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f),
        };

        foreach (var (sx, sy) in corners)
        {
            var corner = Rotate(new Vector2(sx * s, sy * s), rot) + v.Center;
            var d1 = Rotate(new Vector2(-sx, 0f), rot);   // horizontal arm
            var d2 = Rotate(new Vector2(0f, -sy), rot);   // vertical arm
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
    private void DrawTargetSelectionBracket(in TargetSelectionView v)
    {
        float half = TargetSelectionStyle.HalfSize;
        float thickness = TargetSelectionStyle.ThicknessFor(v.Engaged);
        Color col = TargetSelectionStyle.ColorFor(v.Engaged, v.IsEnemy);

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
            _sb.DrawString(_small, pilotName,
                new Vector2(c.X - nsz.X / 2f, y), TextMain);
            y += nsz.Y + 3f;
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
            _sb.DrawString(_small, lbl, new Vector2(c.X - lsz.X / 2f, y), TextMain);
            y += lsz.Y + 2f;
            DrawBar(oreFrac, barX, y, barW, barH);
        }
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
    private void DrawTargetBearingArrow(Vector2 dir, Viewport viewport)
    {
        // Fixed pivot near the bottom-right corner (clear of the right throttle
        // gauge and the centre-bottom speed readout).
        var anchor = new Vector2(viewport.Width - 96f, viewport.Height - 56f);

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
        const string lbl = "TGT";
        var lsz = _small.MeasureString(lbl);
        _sb.DrawString(_small, lbl,
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
    public void DrawLeadIndicator(LeadIndicatorView v)
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

    /// <summary>Rotate a 2-D vector by <paramref name="ang"/> radians.</summary>
    private static Vector2 Rotate(Vector2 v, float ang)
    {
        float c = MathF.Cos(ang), s = MathF.Sin(ang);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    /// <summary>Convert a plain <see cref="HudColor"/> to a MonoGame <c>Color</c>.</summary>
    private static Color From(HudColor c) => new(c.R, c.G, c.B, c.A);

    /// <summary>Solid axis-aligned fill via a tinted, stretched 1x1 white pixel.</summary>
    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)MathF.Round(x), (int)MathF.Round(y),
            (int)MathF.Round(w), (int)MathF.Round(h)), color);

    /// <summary>Thin rectangular outline (four 1px lines) around an axis-aligned box.</summary>
    private void OutlineRect(float x, float y, float w, float h, Color color)
    {
        FillRect(x, y, w, 1f, color);
        FillRect(x, y + h - 1f, w, 1f, color);
        FillRect(x, y, 1f, h, color);
        FillRect(x + w - 1f, y, 1f, h, color);
    }

    /// <summary>An arbitrarily-oriented thin line (from a→b) drawn as a rotated,
    /// stretched 1x1 white pixel — SpriteBatch has no native line primitive.</summary>
    private void DrawLine(Vector2 a, Vector2 b, float thickness, Color color)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (len < 0.001f)
            return;
        float angle = MathF.Atan2(d.Y, d.X);
        _sb.Draw(_white, a + d * 0.5f, null, color, angle,
            new Vector2(0.5f, 0.5f), new Vector2(len, thickness), SpriteEffects.None, 0f);
    }
}
