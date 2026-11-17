using System;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1039 — the Jet readout PLACEMENT PRIORITY (P1039 item 0). A readout block (the
/// selected-target designator's rows, the <c>LOCK nn%</c> caption) is never nudged by a
/// pixel offset: it is placed in the first ALLOWED SLOT of a stated order that is clear
/// of the fixed furniture (ladder numerals, <c>PITCH</c>, <c>CUR</c>/<c>ACC</c>, the
/// heading plate and the tape caption/label columns), clear of the other readout already
/// committed this frame, and inside the viewport gutter — falling through to the next slot
/// only when a slot is rejected. Pure (no graphics types), so the order is unit-pinned.
/// <para><b>Slot order</b> (the brief's list, right/left/below/above): 1 right of the
/// anchor, 2 left, 3 below, 4 above. The designator readout tries them in that order; the
/// lock caption tries above/below first (it is a one-line caption hugging the cue) and then
/// right/left. Whichever slot is taken, the block is clamped into the viewport gutter, and
/// if every slot is blocked the least-covered slot is used (still clamped on screen: the
/// on-screen rule wins, exactly as P1038c stated for the bottom clamp).</para>
/// </summary>
public static class JetReadoutPlacement
{
    /// <summary>The gap between the anchor's clear extent and a side slot, in
    /// standard-stroke units (× scale).</summary>
    public const float SideGap = 8f;

    /// <summary>The gap between the anchor's clear extent and a below/above slot.</summary>
    public const float StackGap = 6f;

    /// <summary>The viewport edge gutter every clamped block keeps.</summary>
    public const float Gutter = 4f;

    /// <summary>The slot index and whether the slot was clear of all furniture.</summary>
    public readonly record struct SlotInfo(int Slot, bool Clear);

    /// <summary>The slot a block was placed in (order as tried).</summary>
    public enum Slot
    {
        Right = 0,
        Left = 1,
        Below = 2,
        Above = 3,
    }

    /// <summary>A placed readout block and the slot it took.</summary>
    public readonly record struct Placement(float X, float Y, Slot Slot, bool Clear);

    /// <summary>The slot order a designator readout tries (right, left, below, above).</summary>
    public static ReadOnlySpan<Slot> DesignatorOrder => _designatorOrder;

    /// <summary>The slot order a one-line caption tries (above, below, right, left):
    /// the caption belongs to the cue, so it hugs the cue's top/bottom edge first.</summary>
    public static ReadOnlySpan<Slot> CaptionOrder => _captionOrder;

    private static readonly Slot[] _designatorOrder = [Slot.Right, Slot.Left, Slot.Below, Slot.Above];
    private static readonly Slot[] _captionOrder = [Slot.Above, Slot.Below, Slot.Right, Slot.Left];

    /// <summary>
    /// The raw (pre-clamp) rectangle of one slot around <paramref name="anchor"/> —
    /// the designator/cue centre — with <paramref name="clear"/> the axis-aligned extent
    /// the anchor's ink occupies (the designator half-span or the lock cue's extent).
    /// <paramref name="anchorTop"/> is the top edge of the designator box (a side slot
    /// aligns with it instead of the centre).
    /// </summary>
    public static Placement Rect(Slot slot, float anchorX, float anchorY, float anchorTop,
        float clear, float width, float height, float scale)
    {
        float side = clear + SideGap * scale;
        float stack = clear + StackGap * scale;
        return slot switch
        {
            Slot.Right => new(anchorX + side, anchorTop, slot, false),
            Slot.Left => new(anchorX - side - width, anchorTop, slot, false),
            Slot.Below => new(anchorX - width * 0.5f, anchorY + stack, slot, false),
            _ => new(anchorX - width * 0.5f, anchorY - stack - height, slot, false),
        };
    }

    /// <summary>
    /// Choose the block's final rectangle: try <paramref name="order"/> in turn; a slot is
    /// accepted when, after clamping into the viewport gutter, it intersects neither the
    /// furniture list nor <paramref name="avoid"/> (the other readout already placed this
    /// frame). Inside one slot the search is bounded and deterministic: the preferred
    /// position first, then the block slid along the slot's own axis by one block, then
    /// slid sideways by half a block — a slot is "below" (or "right") even when it has to
    /// slide to keep off a numeral.
    /// <para><b>P1039b — a clamped candidate is never "clear".</b> A slot that the viewport
    /// gutter has to move is not a slot BESIDE the anchor any more (it is the block the old
    /// code let fall into a screen corner: P1039's 800×480 <c>HOSTILE En …</c> beside the
    /// heading tape). It is returned with <c>Clear = false</c> and only as the least-covered
    /// fallback, so the designator readout goes to its fixed TGT slot instead. A caller that
    /// has no fallback (the one-line lock caption) still gets the least-covered clamped
    /// position, exactly as before.</para>
    /// </summary>
    public static Placement Place(ReadOnlySpan<Slot> order,
        float anchorX, float anchorY, float anchorTop, float clear,
        float width, float height, float scale,
        float viewportWidth, float viewportHeight,
        ReadOnlySpan<RectangleF> furniture, RectangleF avoid)
    {
        float gutter = MathF.Max(0f, Gutter * scale);
        float gap = MathF.Max(1f, SideGap * scale);
        Placement bestFree = default, bestClamped = default;
        float bestFreeCover = float.MaxValue, bestClampedCover = float.MaxValue;
        bool haveFree = false, haveClamped = false;
        for (int i = 0; i < order.Length; i++)
        {
            Placement baseSlot = Rect(order[i], anchorX, anchorY, anchorTop, clear, width, height, scale);
            bool stack = order[i] is Slot.Below or Slot.Above;
            for (int variant = 0; variant < 6; variant++)
            {
                // The axis slide always moves AWAY from the anchor (an "above" block slides
                // further up, never back into the cue), so a slot can never re-enter the ink
                // it was placed to clear.
                float axisX = order[i] == Slot.Right ? width + gap : -width - gap;
                float axisY = order[i] == Slot.Below ? height + gap : -height - gap;
                (float dx, float dy) = variant switch
                {
                    // A stack (below/above) slides sideways; a side slot slides vertically.
                    1 => stack ? (0f, axisY) : (axisX, 0f),
                    2 => stack ? (0f, 2f * axisY) : (2f * axisX, 0f),
                    3 => stack ? (width * 0.5f + gap, 0f) : (0f, height * 0.5f + gap),
                    4 => stack ? (-width * 0.5f - gap, 0f) : (0f, -height * 0.5f - gap),
                    _ => stack ? (width * 0.5f + gap, axisY) : (axisX, height * 0.5f + gap),
                };
                float rawX = baseSlot.X + dx, rawY = baseSlot.Y + dy;
                float x = Math.Clamp(rawX, gutter, MathF.Max(gutter, viewportWidth - gutter - width));
                float y = Math.Clamp(rawY, gutter, MathF.Max(gutter, viewportHeight - gutter - height));
                bool clamped = MathF.Abs(x - rawX) > 0.5f || MathF.Abs(y - rawY) > 0.5f;
                var box = new RectangleF(x, y, width, height);
                float cover = CoveredArea(box, furniture, avoid);
                if (cover <= 0f && !clamped)
                    return new Placement(x, y, order[i], true);
                if (clamped)
                {
                    if (!haveClamped || cover < bestClampedCover)
                    {
                        bestClamped = new Placement(x, y, order[i], false);
                        bestClampedCover = cover;
                        haveClamped = true;
                    }
                    continue;
                }
                if (!haveFree || cover < bestFreeCover)
                {
                    bestFree = new Placement(x, y, order[i], false);
                    bestFreeCover = cover;
                    haveFree = true;
                }
            }
        }
        if (haveFree) return bestFree;
        return haveClamped ? bestClamped : new Placement(gutter, gutter, Slot.Right, false);
    }

    /// <summary>Summed area of the furniture and the avoided block that
    /// <paramref name="box"/> covers (0 = clear).</summary>
    public static float CoveredArea(RectangleF box, ReadOnlySpan<RectangleF> furniture, RectangleF avoid)
    {
        float cover = OverlapArea(box, avoid);
        for (int i = 0; i < furniture.Length; i++) cover += OverlapArea(box, furniture[i]);
        return cover;
    }

    /// <summary>The area two rectangles share (0 when they do not intersect).</summary>
    public static float OverlapArea(RectangleF a, RectangleF b)
    {
        if (a.Width <= 0f || a.Height <= 0f || b.Width <= 0f || b.Height <= 0f) return 0f;
        float w = MathF.Min(a.X + a.Width, b.X + b.Width) - MathF.Max(a.X, b.X);
        float h = MathF.Min(a.Y + a.Height, b.Y + b.Height) - MathF.Max(a.Y, b.Y);
        return w > 0f && h > 0f ? w * h : 0f;
    }

    // ── P1039b: the fixed TGT fallback slot ─────────────────────────────

    /// <summary>Gap between the stores block's right edge and the TGT slot.</summary>
    public const float TargetSlotGap = 6f;

    /// <summary>Margin the TGT slot keeps from the boresight column (the pitch ladder's left
    /// numeral column starts ≈ 80 px left of the centre at the reference density).</summary>
    public const float TargetSlotCentreMargin = 80f;

    /// <summary>
    /// P1039b — the TGT slot's reserved ZONE, stated once so the renderer and its tests cannot
    /// drift: the BOTTOM-LEFT data band between the stores block and the boresight column,
    /// BOTTOM-ANCHORED on the stores block's own top edge (<paramref name="storesTop"/>, the
    /// measured header top the renderer records). It overlaps nothing fixed: the stores block is
    /// its bottom neighbour (its compact annunciator row can run wider than the block, which is
    /// why the TGT block sits ABOVE it, not beside it), the pitch ladder / <c>PITCH</c> column is
    /// its right bound, the heading tape and the master-caution panel are at the top of the
    /// frame, the shared cargo/flight-parts lines are below the stores block, and the radar scope
    /// and the sector mini-map are the bottom-right corner. Returned un-clamped for the block
    /// size given; <see cref="TargetSlot"/> fits the block inside it.
    /// </summary>
    public static RectangleF TargetZone(float viewportWidth, float viewportHeight, float scale,
        float storesTop)
    {
        float s = scale > 0f ? scale : 1f;
        float left = (JetStoresPanel.LeftColumn + JetStoresPanel.BlockWidth + TargetSlotGap) * s;
        float right = viewportWidth * 0.5f - TargetSlotCentreMargin * s;
        float bottom = storesTop > 0f ? storesTop - TargetSlotGap * s : viewportHeight - JetStoresPanel.BottomInset;
        return new RectangleF(left, 0f, MathF.Max(0f, right - left), MathF.Max(0f, bottom));
    }

    /// <summary>
    /// P1039b — the FIXED, LABELLED target-data slot: a block of
    /// <paramref name="width"/>×<paramref name="height"/> whose bottom edge sits on the TGT
    /// zone's bottom edge and whose left edge is the zone's left edge. Used only when every
    /// slot beside the target is blocked, so the readout is a stable, labelled block in a
    /// reserved zone instead of wherever the viewport clamp happened to land (P1039's
    /// 800×480 defect: <c>HOSTILE En …</c> parked in the top-left corner next to the heading
    /// tape). <c>Clear</c> reports whether the block fits the zone.
    /// </summary>
    public static Placement TargetSlot(float viewportWidth, float viewportHeight, float scale,
        float storesTop, float width, float height)
    {
        RectangleF zone = TargetZone(viewportWidth, viewportHeight, scale, storesTop);
        float x = zone.X;
        float y = zone.Y + MathF.Max(0f, zone.Height - height);
        bool clear = width <= zone.Width + 0.5f && zone.Height >= height - 0.5f;
        return new Placement(x, y, Slot.Left, clear);
    }
}
