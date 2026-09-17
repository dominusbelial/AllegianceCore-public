using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

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
    public void Draw(HudFrame? frame, CameraMode mode, Viewport viewport, LockBracketView? lockBracket = null)
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

            // Camera-mode tag (bottom-left) — also proves the toggle in captures.
            string modeLabel = mode == CameraMode.Cockpit ? "COCKPIT" : "CHASE";
            _sb.DrawString(_small, modeLabel, new Vector2(16f, h - 26f),
                mode == CameraMode.Cockpit ? Accent : TextDim);

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
            ("FUEL",   f.FuelFraction),
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
        }
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
    /// coordinates (WinTrek/indicator.cpp:514-566): throttle upper-left, fuel
    /// upper-right, energy lower-right, ammo lower-left, hull centre-left,
    /// shield centre-right. Each gauge is its authentic decoded texture drawn
    /// with the native <c>GaugeImage</c> clipped reveal (engine/image.cpp
    /// GaugeImage::Render, JustifyBottom): only the bottom <c>value</c> fraction
    /// of the bitmap is drawn, so the segmented arc lights progressively as the
    /// quantity rises. The native offsets (screen px from centre) are stretched
    /// by a viewport-relative spread so the six gauges clear each other on any
    /// window size; the arrangement is native, the spacing is this port's.
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
        // training overlay; the Y sign is inverted to viewport y-down space
        // (native's "+93.5" means "up" → subtract in screen coords).
        var placements = new (GaugeKind kind, float frac, float ox, float oy)[]
        {
            (GaugeKind.Thrust, ThrottleFraction(f.Throttle), -126.5f, -93.5f),  // upper-left
            (GaugeKind.Fuel,    f.FuelFraction,                +126.5f, -93.5f),  // upper-right
            (GaugeKind.Energy,  f.EnergyFraction,              +126.5f, +112.5f), // lower-right
            (GaugeKind.Ammo,    f.AmmoFraction,                -126.5f, +112.5f), // lower-left
            (GaugeKind.Hull,    f.HullFraction,                -65.5f,  -35.5f),  // centre-left
            (GaugeKind.Shield,  f.ShieldFraction,              +65.5f,  -35.5f),  // centre-right
        };

        foreach (var (kind, frac, ox, oy) in placements)
        {
            var anchor = center + new Vector2(ox, oy) * spread;
            DrawGauge(kind, frac, anchor, gaugeScale);
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
    private void DrawGauge(GaugeKind kind, float fraction, Vector2 anchor, float gaugeScale)
    {
        Texture2D? tex = _gauges?.For(kind);
        float frac = HudFormat.Clamp01(fraction);
        string label = GaugeTextureSource.LabelFor(kind);

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
        _sb.DrawString(_small, pct, new Vector2(anchor.X - psz.X / 2f, ty + lsz.Y + 1f),
            frac <= 0.5f ? Warning : Accent);
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

    /// <summary>Center reticle: a thin circular ring (drawn as short line
    /// segments), four cardinal tick notches, and a centre dot. Drawn in every
    /// camera mode — a first-person view needs one, and it reads cleanly over a
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
