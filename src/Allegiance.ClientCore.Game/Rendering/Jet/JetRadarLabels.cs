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

    /// <summary>A chosen label block: its rectangle, the slot it took and whether that slot was clear.</summary>
    public readonly record struct Placement(RectangleF Box, Slot Slot, bool Clear, bool Leader);

    /// <summary>
    /// Place one label block of <paramref name="width"/>×<paramref name="height"/> around the
    /// glyph at <paramref name="at"/>: the first slot (and the first of its six bounded slides)
    /// that fits inside <paramref name="inner"/> and touches none of <paramref name="blocks"/>
    /// wins. When nothing is clear the least-covered slot is returned with <c>Clear = false</c>
    /// (only a never-hidden contact uses it).
    /// </summary>
    public static Placement Place(Vector2 at, float clear, float width, float height, float scale,
        RectangleF inner, ReadOnlySpan<RectangleF> blocks)
    {
        float gap = SlotGap * (scale > 0f ? scale : 1f);
        Placement fallback = default;
        bool haveFallback = false;
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
                if (!Overlaps(box, blocks)) return new Placement(box, slot, true, diagonal);
                if (!haveFallback)
                {
                    // The least-obstruction fallback is decided once, so the pass costs one
                    // early-exit test per candidate on a clear frame.
                    fallback = new Placement(box, slot, false, diagonal);
                    haveFallback = true;
                }
            }
        }
        return haveFallback ? fallback : new Placement(default, Slot.Right, false, false);
    }

    /// <summary>
    /// The last-resort placement of a NEVER-HIDDEN label: the first free position of a
    /// scope-wide scan (half a block width across, one block height down, row-major from the
    /// top-left), with a leader stroke back to the glyph. Used only when none of the eight slots
    /// around the glyph is free, so a priority reading is placed rather than stacked on another.
    /// </summary>
    public static Placement Scan(RectangleF inner, float width, float height,
        ReadOnlySpan<RectangleF> blocks)
    {
        if (width <= 0f || height <= 0f || width > inner.Width || height > inner.Height)
            return new Placement(default, Slot.Right, false, true);
        float strideX = MathF.Max(8f, width * 0.5f + 1f), strideY = height + 1f;
        int rows = (int)((inner.Height - height) / strideY) + 1;
        int columns = (int)((inner.Width - width) / strideX) + 1;
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                var box = new RectangleF(inner.X + column * strideX, inner.Y + row * strideY, width, height);
                if (Overlaps(box, blocks)) continue;
                return new Placement(box, Slot.Right, true, true);
            }
        return new Placement(default, Slot.Right, false, true);
    }

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
