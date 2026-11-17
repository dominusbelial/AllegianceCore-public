using System;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1039b — the Jet radar's LABEL PLACEMENT PASS (P972 §4.4 RWR, follow-up to P1039).
/// <para><b>The problem it fixes.</b> P1039 drew every native-eligible radar label at a fixed
/// pixel offset beside its glyph, so at 1920×1080 ten contacts already stacked their name,
/// range and verb lines into one illegible blob and the range-ring captions crossed them.
/// Native itself draws every eligible label unconditionally — its text loop has no collision
/// test at all (<c>WinTrek/radarimage.cpp:1189-1461</c>: every <c>TextData</c> is drawn at
/// its own offset, name at <c>:1396</c>, range at <c>:1402</c>) — so the pile is native
/// behaviour, not a port bug; it is only tolerable because native's radar is a corner
/// thumbnail with a fixed bitmap font.</para>
/// <para><b>What the pass decides.</b> For each label native's per-kind LOD tier already
/// allowed (<see cref="RadarLabelModel.Resolve"/>) it decides only WHERE the block goes and,
/// when there is no room, whether a shorter form or no label at all is drawn. It never
/// changes which contacts exist or which labels are eligible: the native LOD tier stays the
/// authority (the same rule P828's <see cref="RadarLabelDeclutter"/> keeps for Modern).</para>
/// <para><b>Slot order</b> (tried in this order for every label): right, left, above, below,
/// then the four diagonals (up-right, up-left, down-right, down-left). Inside a slot the
/// block is first tried at the slot's own position, then slid away from the glyph along the
/// slot's axis (one block, then two) and finally sideways (half a block either way), so a
/// side label can keep its side while stepping off a ring caption or a neighbouring label.
/// Native has no slot list — it places the block outside the icon along the radial direction
/// from the scope centre (<c>radarimage.cpp:360-379,1284-1311</c>, direction (0,−1) at the
/// centre) — so the Jet order keeps that "away from the glyph" intent while making the
/// choice deterministic and testable.</para>
/// <para><b>Priority</b> (placed first, and never hidden): (1) the selected target, (2) an
/// order subject, (3) a threat / missile contact, (4) a contact carrying the pilot's own
/// command verb (<c>ACC</c>/<c>QUE</c>); then everything else by nearest range, then by
/// contact index. Native's own priority is weaker but points the same way: target / accepted /
/// threat labels are pushed to the END of its text list (drawn on top) and all others to the
/// front (<c>radarimage.cpp:419-425</c>).</para>
/// <para><b>Fallback ladder</b>: the full native block → the range line alone → nothing (the
/// glyph, its overlays and its <c>ACC</c>/<c>QUE</c> word stay). A never-hidden contact (rank
/// ≤ <see cref="Rank.Command"/>) is never dropped: when every slot is covered it keeps the
/// shortest form in the least-covered slot, and the caller reports that overflow.</para>
/// <para>Pure (no graphics device, no allocation): every slot, rank and overlap rule is
/// unit-pinned headlessly.</para>
/// </summary>
public static class JetRadarLabels
{
    /// <summary>Slots a label can occupy around its glyph, in the order they are tried.</summary>
    public enum Slot : byte
    {
        Right = 0,
        Left = 1,
        Above = 2,
        Below = 3,
        UpRight = 4,
        UpLeft = 5,
        DownRight = 6,
        DownLeft = 7,
    }

    /// <summary>How many slots exist (<see cref="SlotOrder"/>'s length).</summary>
    public const int SlotCount = 8;

    private static readonly Slot[] _slotOrder =
    [
        Slot.Right, Slot.Left, Slot.Above, Slot.Below,
        Slot.UpRight, Slot.UpLeft, Slot.DownRight, Slot.DownLeft,
    ];

    /// <summary>The stated slot order: right, left, above, below, then the diagonals.</summary>
    public static ReadOnlySpan<Slot> SlotOrder => _slotOrder;

    /// <summary>Gap between the glyph's clear extent and a slot, in standard-stroke units (× scale).</summary>
    public const float SlotGap = 5f;

    /// <summary>
    /// P1039c — the pad a band stroke is tested with: half its ink width plus half a pixel, the
    /// same value the proof's stroke audit uses, so the placement pass and the audit can never
    /// disagree about whether a stroke touches a reading (<c>StrokeThroughText</c> is 0).
    /// </summary>
    public const float RingPad = 1f;

    /// <summary>
    /// The glyph's clear radius, in standard-stroke units (× scale): the overlay bracket
    /// (<c>4.5</c> glyph unit + <c>4</c> bracket arm) plus one stroke, so a label never sits on
    /// the selected/accepted corner bracket of its own contact.
    /// </summary>
    public const float GlyphClear = 9.5f;

    /// <summary>How far a leader stroke stops short of the block's ink, in standard-stroke units.</summary>
    public const float LeaderStop = 2.5f;

    /// <summary>True when <paramref name="slot"/> is a diagonal (it gets a leader stroke).</summary>
    public static bool IsDiagonal(Slot slot) => slot >= Slot.UpRight;

    /// <summary>Why a label is in the front of the queue.</summary>
    public enum Rank : byte
    {
        /// <summary>The selected target (the designator's contact).</summary>
        Target = 0,
        /// <summary>An order subject (the commander's recipient set).</summary>
        Subject = 1,
        /// <summary>A threat: damaged by it, flashing, or an incoming missile.</summary>
        Threat = 2,
        /// <summary>The pilot's own accepted/queued command verb rides this contact.</summary>
        Command = 3,
        /// <summary>Everything else — ordered by range.</summary>
        Other = 4,
    }

    /// <summary>The last rank that is never hidden.</summary>
    public const Rank NeverHiddenThrough = Rank.Command;

    /// <summary>
    /// The contact's placement rank: selected target, order subject, threat/missile, own
    /// command verb, then everything else. Never a new inclusion rule — every flag is the
    /// shared <see cref="HudContact"/>'s own.
    /// </summary>
    public static Rank Priority(in HudContact contact)
    {
        if (contact.Selected) return Rank.Target;
        if (contact.Subject) return Rank.Subject;
        if (contact.Threat || contact.ThreatFlash || contact.Kind == HudContactKind.Missile) return Rank.Threat;
        if (contact.CommandId is not null) return Rank.Command;
        return Rank.Other;
    }

    /// <summary>True when this contact's label may never be dropped (rank ≤ command verb).</summary>
    public static bool NeverHidden(in HudContact contact) => Priority(contact) <= NeverHiddenThrough;

    /// <summary>True when <paramref name="a"/> comes before <paramref name="b"/> in the label queue.</summary>
    public static bool Before(in HudContact a, in HudContact b)
    {
        Rank rankA = Priority(a), rankB = Priority(b);
        if (rankA != rankB) return rankA < rankB;
        int rangeA = a.Range ?? int.MaxValue, rangeB = b.Range ?? int.MaxValue;
        return rangeA < rangeB;
    }

    /// <summary>The un-clamped rectangle of one slot around the glyph at <paramref name="at"/>.</summary>
    public static RectangleF SlotRect(Slot slot, Vector2 at, float clear, float width, float height, float scale)
    {
        float gap = SlotGap * (scale > 0f ? scale : 1f);
        float side = clear + gap;
        return slot switch
        {
            Slot.Right => new(at.X + side, at.Y - height * 0.5f, width, height),
            Slot.Left => new(at.X - side - width, at.Y - height * 0.5f, width, height),
            Slot.Above => new(at.X - width * 0.5f, at.Y - side - height, width, height),
            Slot.Below => new(at.X - width * 0.5f, at.Y + side, width, height),
            Slot.UpRight => new(at.X + side, at.Y - side - height, width, height),
            Slot.UpLeft => new(at.X - side - width, at.Y - side - height, width, height),
            Slot.DownRight => new(at.X + side, at.Y + side, width, height),
            _ => new(at.X - side - width, at.Y + side, width, height),
        };
    }

    /// <summary>The glyph's own half-size, in standard-stroke units (× scale): the 4.5-unit
    /// mark plus the overlay bracket's 4-unit arm.</summary>
    public const float GlyphBracket = 8.5f;

    /// <summary>
    /// P1039c — the OBSTACLE box of one contact's glyph and every overlay it carries: the
    /// glyph mark, the selected/accepted/queued corner bracket, the nearest-enemy outer
    /// bracket, the subject square, the threat-flash circle, the carried flag's pole, the
    /// carried artifact's mark and the ripcord stroke. A label may never be placed on this
    /// box of ANOTHER contact (the brief's glyph obstacle); a contact's own box is excluded
    /// from its own placement because the label's slot already keeps
    /// <see cref="GlyphClear"/> of sky around it.
    /// </summary>
    public static RectangleF GlyphBox(in HudContact contact, Vector2 at, float scale)
    {
        float s = scale > 0f ? scale : 1f;
        float bracket = GlyphBracket * s;
        float halfX = bracket, halfY = bracket;
        if (contact.NearestEnemy)
        {
            halfX += 2f * s;
            halfY += 2f * s;
        }
        // The flag's pennant reaches 7 units right of the bracket; the artifact's diamond 8.
        if (contact.CarriedFlag) halfX = MathF.Max(halfX, bracket + 7f * s);
        if (contact.CarriedArtifact) halfX = MathF.Max(halfX, bracket + 8f * s);
        // The ripcord stroke runs 5 units above the bracket.
        if (contact.Ripcord) halfY = MathF.Max(halfY, bracket + 5f * s);
        return new RectangleF(at.X - halfX, at.Y - halfY, halfX * 2f, halfY * 2f);
    }

    /// <summary>
    /// P1039c — the four range-band strokes of one scope, as geometry: a circle per band on
    /// the round variant, a square outline per band on the square variant. A label slot (and
    /// a scanned cell) is REJECTED when a band's stroke would run through its ink, so no ring
    /// line can read as a strikethrough: the stated rule for defect 1 (slot test, not a
    /// backing plate). A default value (all radii 0) means "no bands".
    /// </summary>
    public readonly record struct Rings(Vector2 Centre, bool Round, float Radius1, float Radius2,
        float Radius3, float Radius4)
    {
        /// <summary>No band strokes (a caller that only tests text obstacles).</summary>
        public static Rings None => default;

        /// <summary>The radius of band <paramref name="band"/> (0…3).</summary>
        public float Radius(int band) => band switch
        {
            0 => Radius1,
            1 => Radius2,
            2 => Radius3,
            _ => Radius4,
        };

        /// <summary>True when the stroke of any band passes through <paramref name="box"/>
        /// (its ink widened by <paramref name="pad"/> on every side).</summary>
        public bool Hits(RectangleF box, float pad)
        {
            for (int band = 0; band < 4; band++)
            {
                float radius = Radius(band);
                if (radius <= 0f) continue;
                if (Round)
                {
                    if (CircleCrosses(box, radius, pad)) return true;
                }
                else
                {
                    float x0 = Centre.X - radius, x1 = Centre.X + radius;
                    float y0 = Centre.Y - radius, y1 = Centre.Y + radius;
                    if (SegmentHitsBox(new(x0, y0), new(x1, y0), box, pad)
                        || SegmentHitsBox(new(x1, y0), new(x1, y1), box, pad)
                        || SegmentHitsBox(new(x1, y1), new(x0, y1), box, pad)
                        || SegmentHitsBox(new(x0, y1), new(x0, y0), box, pad)) return true;
                }
            }
            return false;
        }

        /// <summary>True when the circle of <paramref name="radius"/> around the centre passes
        /// through the box: its nearest point is inside the radius and its farthest corner is
        /// outside it (the exact circle-vs-rectangle test).</summary>
        private bool CircleCrosses(RectangleF box, float radius, float pad)
        {
            float nx = Math.Clamp(Centre.X, box.X, box.X + box.Width);
            float ny = Math.Clamp(Centre.Y, box.Y, box.Y + box.Height);
            float dx = nx - Centre.X, dy = ny - Centre.Y;
            float nearest = MathF.Sqrt(dx * dx + dy * dy);
            float far = 0f;
            for (int corner = 0; corner < 4; corner++)
            {
                float cx = corner is 0 or 3 ? box.X : box.X + box.Width;
                float cy = corner is 0 or 1 ? box.Y : box.Y + box.Height;
                float ddx = cx - Centre.X, ddy = cy - Centre.Y;
                far = MathF.Max(far, MathF.Sqrt(ddx * ddx + ddy * ddy));
            }
            float inner = MathF.Max(0f, radius - pad), outer = radius + pad;
            return nearest <= outer && far >= inner;
        }
    }

    /// <summary>True when the segment a→b crosses <paramref name="box"/> widened by
    /// <paramref name="pad"/> (Liang–Barsky slab clip; the same test the proof's stroke audit
    /// uses).</summary>
    public static bool SegmentHitsBox(Vector2 a, Vector2 b, RectangleF box, float pad)
    {
        float x0 = box.X - pad, y0 = box.Y - pad;
        float x1 = box.X + box.Width + pad, y1 = box.Y + box.Height + pad;
        if (x1 < x0 || y1 < y0) return false;
        float dx = b.X - a.X, dy = b.Y - a.Y;
        if (MathF.Abs(dx) < 1e-5f && MathF.Abs(dy) < 1e-5f)
            return a.X >= x0 && a.X <= x1 && a.Y >= y0 && a.Y <= y1;
        float t0 = 0f, t1 = 1f;
        if (!Clip(-dx, a.X - x0, ref t0, ref t1)) return false;
        if (!Clip(dx, x1 - a.X, ref t0, ref t1)) return false;
        if (!Clip(-dy, a.Y - y0, ref t0, ref t1)) return false;
        if (!Clip(dy, y1 - a.Y, ref t0, ref t1)) return false;
        return t0 <= t1;
    }

    private static bool Clip(float p, float q, ref float t0, ref float t1)
    {
        if (MathF.Abs(p) < 1e-6f) return q >= 0f;
        float r = q / p;
        if (p < 0f)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }
        return true;
    }

    // ── P1039c: the label budget (a rule of scope size, never of the resolution) ──────

    /// <summary>
    /// The clear scope area ONE shortened label block claims, in px² at scale 1 (its 4–6 digit
    /// ink ≈ 30×22 px plus one row of sky on every side, which is what makes two neighbouring
    /// readings separable). The scope's label budget is its inner area divided by this cell, so
    /// the rule reads the instrument's own size and never the window's name: the 140 px compact
    /// scope holds 9 blocks, the 341 px 1080p scope 24.
    /// </summary>
    public const float LabelCellPixels = 4800f;

    /// <summary>
    /// The cells one FULL four-row block claims: four rows (4 cells) × roughly three cells of
    /// width and sky = <c>12</c>. Used only to decide whether a scope may carry full blocks at
    /// all (see <see cref="FullFormAllowed"/>).
    /// </summary>
    public const int FullBlockCells = 12;

    /// <summary>
    /// The number of full blocks a scope must be able to hold before a NON-priority label may
    /// keep its full multi-row form: two. Below that (24 cells) the full native block is not a
    /// readable unit of the instrument and only a priority class keeps it, every other label
    /// being reduced to its shortest form. At 800×480's 9-cell scope this is false; at the
    /// 1080p scope's 24 cells it is true.
    /// </summary>
    public const int MinFullBlocks = 2;

    /// <summary>
    /// How many free cells of a scan are tested for a clear leader (the leader is a preference,
    /// not a requirement: the draw pass re-tests it against the final plan). Bounds the cost of a
    /// crowded scan — 20 cells is far below the 200+ a dense 1080p scope offers.
    /// </summary>
    public const int LeaderSearchCells = 20;

    /// <summary>The number of label blocks this scope can hold (pixels per label), ≥ 1.</summary>
    public static int LabelBudget(RectangleF inner, float scale)
    {
        float s = scale > 0f ? scale : 1f;
        int budget = (int)(inner.Width * inner.Height / (LabelCellPixels * s * s));
        return Math.Max(1, budget);
    }

    /// <summary>True when the scope is large enough to carry a non-priority label in its full
    /// multi-row form: its label budget must hold <see cref="MinFullBlocks"/> full blocks
    /// (<see cref="FullBlockCells"/> cells each). A function of the scope's own size.</summary>
    public static bool FullFormAllowed(RectangleF inner, float scale)
        => LabelBudget(inner, scale) >= FullBlockCells * MinFullBlocks;

    /// <summary>The rect a label may occupy inside the scope (the scope inset by one stroke).</summary>
    public static RectangleF Inner(RectangleF scope, float scale)
    {
        float pad = 2f * (scale > 0f ? scale : 1f);
        return new RectangleF(scope.X + pad, scope.Y + pad,
            MathF.Max(0f, scope.Width - pad * 2f), MathF.Max(0f, scope.Height - pad * 2f));
    }

    /// <summary>True when <paramref name="box"/> lies fully inside <paramref name="inner"/>.</summary>
    public static bool Fits(RectangleF inner, RectangleF box)
        => box.X >= inner.X - 0.5f && box.Y >= inner.Y - 0.5f
        && box.X + box.Width <= inner.X + inner.Width + 0.5f
        && box.Y + box.Height <= inner.Y + inner.Height + 0.5f;

    /// <summary>True when two text boxes intersect (the audit's own rule, 1 px tolerance).</summary>
    public static bool Intersects(RectangleF a, RectangleF b)
        => a.X < b.X + b.Width - 1f && b.X < a.X + a.Width - 1f
        && a.Y < b.Y + b.Height - 1f && b.Y < a.Y + a.Height - 1f;

    /// <summary>
    /// True when <paramref name="box"/> touches any of <paramref name="blocks"/> — the cheap
    /// early-exit test the placement pass uses for every candidate (the summed area is only
    /// needed when a least-covered fallback has to be chosen). 0 B, index loop.
    /// </summary>
    public static bool Overlaps(RectangleF box, ReadOnlySpan<RectangleF> blocks)
    {
        float x0 = box.X, y0 = box.Y, x1 = box.X + box.Width, y1 = box.Y + box.Height;
        for (int i = 0; i < blocks.Length; i++)
        {
            RectangleF other = blocks[i];
            if (x0 < other.X + other.Width && other.X < x1
                && y0 < other.Y + other.Height && other.Y < y1) return true;
        }
        return false;
    }

    /// <summary>The area of <paramref name="box"/> covered by any of <paramref name="blocks"/>.</summary>
    public static float Covered(RectangleF box, ReadOnlySpan<RectangleF> blocks)
    {
        float cover = 0f;
        for (int i = 0; i < blocks.Length; i++)
        {
            RectangleF other = blocks[i];
            float w = MathF.Min(box.X + box.Width, other.X + other.Width) - MathF.Max(box.X, other.X);
            float h = MathF.Min(box.Y + box.Height, other.Y + other.Height) - MathF.Max(box.Y, other.Y);
            if (w > 0f && h > 0f) cover += w * h;
        }
        return cover;
    }

    /// <summary>A chosen label block: its rectangle, the slot it took, whether that slot was
    /// clear of text and glyphs, whether a leader stroke is drawn, and whether the block needs
    /// a backing plate to interrupt a range band under it (P1039c).</summary>
    public readonly record struct Placement(RectangleF Box, Slot Slot, bool Clear, bool Leader, bool Plate = false);

    /// <summary>
    /// Place one label block of <paramref name="width"/>×<paramref name="height"/> around the
    /// glyph at <paramref name="at"/>: the first slot (and the first of its six bounded slides)
    /// that fits inside <paramref name="inner"/>, touches none of <paramref name="blocks"/> and
    /// none of the glyph boxes of the OTHER contacts (<paramref name="glyphs"/> except
    /// <paramref name="ownGlyph"/>), and is not crossed by a range band
    /// (<paramref name="rings"/>) wins. When nothing is clear the least-covered slot is
    /// returned with <c>Clear = false</c> (only a never-hidden contact uses it).
    /// </summary>
    public static Placement Place(Vector2 at, float clear, float width, float height, float scale,
        RectangleF inner, ReadOnlySpan<RectangleF> blocks, ReadOnlySpan<RectangleF> glyphs,
        int ownGlyph, in Rings rings)
    {
        float gap = SlotGap * (scale > 0f ? scale : 1f);
        float pad = RingPad;
        Placement fallback = default;
        bool haveFallback = false;
        Placement plated = default;
        bool havePlated = false;
        for (int i = 0; i < _slotOrder.Length; i++)
        {
            Slot slot = _slotOrder[i];
            RectangleF baseSlot = SlotRect(slot, at, clear, width, height, scale);
            bool stack = slot is Slot.Above or Slot.Below;
            bool diagonal = IsDiagonal(slot);
            bool right = slot is Slot.Right or Slot.UpRight or Slot.DownRight;
            bool up = slot is Slot.Above or Slot.UpRight or Slot.UpLeft;
            for (int variant = 0; variant < 6; variant++)
            {
                // The axis slide always moves AWAY from the glyph, so a label never slides
                // back over the marker it belongs to.
                float axisX = right ? width + gap : -width - gap;
                float axisY = up ? -height - gap : height + gap;
                (float dx, float dy) = variant switch
                {
                    1 => diagonal ? (axisX, axisY) : stack ? (0f, axisY) : (axisX, 0f),
                    2 => diagonal ? (2f * axisX, 2f * axisY) : stack ? (0f, 2f * axisY) : (2f * axisX, 0f),
                    3 => stack ? (width * 0.5f + gap, 0f) : (0f, height * 0.5f + gap),
                    4 => stack ? (-width * 0.5f - gap, 0f) : (0f, -height * 0.5f - gap),
                    _ => diagonal ? (axisX * 0.5f, axisY * 0.5f) : stack ? (width * 0.5f + gap, axisY) : (axisX, height * 0.5f + gap),
                };
                var box = new RectangleF(baseSlot.X + dx, baseSlot.Y + dy, width, height);
                if (!Fits(inner, box)) continue;
                if (Blocked(box, blocks, glyphs, ownGlyph, Rings.None, pad))
                {
                    if (!haveFallback)
                    {
                        // The least-obstruction fallback is decided once, so the pass costs one
                        // early-exit test per candidate on a clear frame.
                        fallback = new Placement(box, slot, false, diagonal);
                        haveFallback = true;
                    }
                    continue;
                }
                // A diagonal placement draws a leader stroke: the candidate is only taken when
                // that stroke crosses no other label, no other glyph and no band either.
                bool leader = diagonal;
                if (leader && LeaderCrosses(at, clear, box, scale, blocks, glyphs, ownGlyph))
                {
                    if (!haveFallback) { fallback = new Placement(box, slot, false, diagonal); haveFallback = true; }
                    continue;
                }
                // P1039c — a cell a band runs through is REPLACED by a plated cell only when the
                // scope has no ring-free one (at 800×480's 144 px scope two neighbouring bands
                // are 15.8 px apart, less than one 22 px text line), so the plate rule serves
                // the small instrument while the large one stays plate-free.
                if (rings.Hits(box, pad))
                {
                    if (!havePlated) { plated = new Placement(box, slot, true, leader, true); havePlated = true; }
                    continue;
                }
                return new Placement(box, slot, true, leader);
            }
        }
        if (havePlated) return plated;
        return haveFallback ? fallback : new Placement(default, Slot.Right, false, false);
    }

    /// <summary>P1039b — the placement test with text obstacles only (no glyph boxes, no
    /// bands): kept for the pure slot-order tests.</summary>
    public static Placement Place(Vector2 at, float clear, float width, float height, float scale,
        RectangleF inner, ReadOnlySpan<RectangleF> blocks)
        => Place(at, clear, width, height, scale, inner, blocks, default, -1, Rings.None);

    /// <summary>True when <paramref name="box"/> touches a text obstacle, another contact's
    /// glyph box, or a range band's stroke.</summary>
    public static bool Blocked(RectangleF box, ReadOnlySpan<RectangleF> blocks,
        ReadOnlySpan<RectangleF> glyphs, int ownGlyph, in Rings rings, float pad)
    {
        if (Overlaps(box, blocks)) return true;
        for (int i = 0; i < glyphs.Length; i++)
        {
            if (i == ownGlyph) continue;
            if (Intersects(box, glyphs[i])) return true;
        }
        return rings.Hits(box, pad);
    }

    /// <summary>True when the leader stroke of a block placed at <paramref name="box"/> would
    /// cross another text obstacle, another contact's glyph box, or a band stroke (the brief:
    /// a leader must not cross another label or glyph).</summary>
    public static bool LeaderCrosses(Vector2 at, float clear, RectangleF box, float scale,
        ReadOnlySpan<RectangleF> blocks, ReadOnlySpan<RectangleF> glyphs, int ownGlyph)
    {
        var (from, to) = Leader(at, clear, box, scale);
        if ((to - from).LengthSquared() < 1f) return false;
        float pad = RingPad;
        for (int i = 0; i < blocks.Length; i++)
        {
            RectangleF other = blocks[i];
            if (other == box) continue;
            if (SegmentHitsBox(from, to, other, pad)) return true;
        }
        for (int i = 0; i < glyphs.Length; i++)
        {
            if (i == ownGlyph) continue;
            if (SegmentHitsBox(from, to, glyphs[i], pad)) return true;
        }
        return false;
    }

    /// <summary>
    /// The last-resort placement of a NEVER-HIDDEN label: the first free position of a
    /// scope-wide scan (half a block width across, one block height down, row-major from the
    /// top-left), with a leader stroke back to the glyph. Used only when none of the eight slots
    /// around the glyph is free, so a priority reading is placed rather than stacked on another.
    /// A cell is only taken when its leader is clear too; when no cell has a clear leader the
    /// first free cell is used WITHOUT a leader stroke (a placed reading beats a line through
    /// someone else's ink).
    /// </summary>
    public static Placement Scan(RectangleF inner, float width, float height,
        ReadOnlySpan<RectangleF> blocks, ReadOnlySpan<RectangleF> glyphs, int ownGlyph, in Rings rings,
        Vector2 at, float clear, float scale)
    {
        if (width <= 0f || height <= 0f || width > inner.Width || height > inner.Height)
            return new Placement(default, Slot.Right, false, true);
        float pad = RingPad;
        float strideX = MathF.Max(8f, width * 0.5f + 1f), strideY = height + 1f;
        int rows = (int)((inner.Height - height) / strideY) + 1;
        int columns = (int)((inner.Width - width) / strideX) + 1;
        // P1039c — the scan starts at the cell nearest the GLYPH and works outward, so a
        // priority label that cannot take a slot still lands beside its own contact (a short
        // leader is then non-crossing and can be drawn) instead of in a far corner column.
        int glyphRow = Math.Clamp((int)MathF.Round((at.Y - height * 0.5f - inner.Y) / strideY), 0, rows - 1);
        int glyphColumn = Math.Clamp((int)MathF.Round((at.X - inner.X) / strideX), 0, columns - 1);
        // The outward walk must be long enough to reach both edges from the glyph's cell.
        int rowSteps = 2 * Math.Max(glyphRow, rows - 1 - glyphRow) + 1;
        int columnSteps = 2 * Math.Max(glyphColumn, columns - 1 - glyphColumn) + 1;
        Placement free = default, platedFree = default, plated = default;
        bool haveFree = false, havePlatedFree = false, havePlated = false;
        // The leader test is the expensive part of a scan; it is tried for the first cells only
        // (the scan's leader is a preference — the draw pass re-tests every leader against the
        // final plan anyway, RadarLeaderBlocked), so a crowded frame stays cheap.
        int leaderTries = 0;
        for (int rowStep = 0; rowStep < rowSteps; rowStep++)
        {
            int row = (rowStep & 1) == 0 ? glyphRow - rowStep / 2 : glyphRow + (rowStep + 1) / 2;
            if (row < 0 || row >= rows) continue;
            for (int columnStep = 0; columnStep < columnSteps; columnStep++)
            {
                int column = (columnStep & 1) == 0 ? glyphColumn - columnStep / 2 : glyphColumn + (columnStep + 1) / 2;
                if (column < 0 || column >= columns) continue;
                var box = new RectangleF(inner.X + column * strideX, inner.Y + row * strideY, width, height);
                if (Blocked(box, blocks, glyphs, ownGlyph, Rings.None, pad)) continue;
                bool clearLeader = leaderTries++ < LeaderSearchCells
                    && !LeaderCrosses(at, clear, box, scale, blocks, glyphs, ownGlyph);
                if (!rings.Hits(box, pad))
                {
                    if (clearLeader) return new Placement(box, Slot.Right, true, true);
                    if (!haveFree) { free = new Placement(box, Slot.Right, true, true); haveFree = true; }
                    continue;
                }
                // A band runs through the cell: usable only with a backing plate (P1039c).
                var placed = new Placement(box, Slot.Right, true, clearLeader, true);
                if (clearLeader && !havePlatedFree) { platedFree = placed; havePlatedFree = true; }
                if (!havePlated) { plated = placed; havePlated = true; }
            }
        }
        if (haveFree) return free with { Leader = false };          // a placed reading beats a line
        if (havePlatedFree) return platedFree;
        if (havePlated) return plated with { Leader = false };
        return new Placement(default, Slot.Right, false, true);
    }

    /// <summary>P1039b — the scope-wide scan with text obstacles only (pure tests).</summary>
    public static Placement Scan(RectangleF inner, float width, float height,
        ReadOnlySpan<RectangleF> blocks)
        => Scan(inner, width, height, blocks, default, -1, Rings.None, default, 0f, 1f);

    /// <summary>
    /// The short leader stroke of a label: from the glyph's bracket edge toward the nearest
    /// point of the block, stopped <see cref="LeaderStop"/> short of its ink. Works for a slot
    /// block and for a scanned block alike.
    /// </summary>
    public static (Vector2 From, Vector2 To) Leader(Vector2 at, float clear, RectangleF box, float scale)
    {
        float stop = LeaderStop * (scale > 0f ? scale : 1f);
        float nx = Math.Clamp(at.X, box.X, box.X + box.Width);
        float ny = Math.Clamp(at.Y, box.Y, box.Y + box.Height);
        var direction = new Vector2(nx - at.X, ny - at.Y);
        float length = direction.Length();
        if (length < 1e-3f) return (at, at);
        direction /= length;
        Vector2 from = at + direction * clear;
        Vector2 to = new Vector2(nx, ny) - direction * stop;
        return (from, to);
    }
}
