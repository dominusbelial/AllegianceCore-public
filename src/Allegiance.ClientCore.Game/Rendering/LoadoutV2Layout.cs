using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P1013: device-free, viewport-pixel geometry shared by both loadout V2 styles.
/// Presentation only: native WinTrek/load.cpp:785-790 supplies a ship preview,
/// :797-820 supplies loadout buttons, and :2010-2043 supplies hull-specific slots.
/// ShopModel remains the authority for all row contents and purchase rules.
/// </summary>
public sealed class LoadoutV2Layout
{
    /// <summary>The existing docked Game1 footer reservation, in physical pixels.</summary>
    public const float DockedFooterHeight = 84f;

    public enum Kind
    {
        Title, Money, HullHeading, SlotHeading, Hull, Preview, Slot, Offer, Stat,
        Price, Default, Revert, Buy, Launch,
    }

    /// <summary>
    /// A visible leaf. Index is the original model row index (never a viewport
    /// index); SlotIndex identifies the owner of an offer. TextBounds is the
    /// padded text budget. Containers are separate and are never hit targets.
    /// </summary>
    public readonly record struct Element(Kind Kind, TouchRect Bounds, TouchRect TextBounds,
        int Index = -1, int SlotIndex = -1);

    public TouchRect Viewport { get; private init; }
    public TouchRect Header { get; private init; }
    public TouchRect HullList { get; private init; }
    public TouchRect ShipPreview { get; private init; }
    public TouchRect SlotList { get; private init; }
    public TouchRect StatsBar { get; private init; }
    public TouchRect PriceBox { get; private init; }
    public TouchRect ButtonRow { get; private init; }
    public TouchRect Footer { get; private init; }
    public TouchRect HullViewport { get; private init; }
    public TouchRect SlotViewport { get; private init; }
    /// <summary>Inline child of SlotViewport; empty when no offer rows are open.</summary>
    public TouchRect OfferViewport { get; private init; }

    /// <summary>Disjoint major regions, including the reserved footer.</summary>
    public IReadOnlyList<TouchRect> Regions { get; private init; } = [];
    /// <summary>Only fully visible leaves are returned; no partial text rows.</summary>
    public IReadOnlyList<Element> Elements { get; private init; } = [];
    public float Scale { get; private init; }
    public float RowPitch { get; private init; }
    public int ExpandedSlot { get; private init; }
    public int HullCapacity { get; private init; }
    public int SlotCapacity { get; private init; }
    public int OfferCapacity { get; private init; }
    /// <summary>Scroll offsets and limits are in rows, not pixels.</summary>
    public int HullScroll { get; private init; }
    public int SlotScroll { get; private init; }
    public int OfferScroll { get; private init; }
    public int MaxHullScroll { get; private init; }
    public int MinSlotScroll { get; private init; }
    public int MaxSlotScroll { get; private init; }
    public int MaxOfferScroll { get; private init; }

    /// <summary>
    /// uiScale is the caller's resolved UI scale (HudSettings.Size and display
    /// density, e.g. ModernHudScale.Core), used identically by both styles. It
    /// is capped only to preserve space on small windows; larger windows do not
    /// inflate it. Minimum supported viewport: 640x480.
    ///
    /// Counts come from the shared model. offerRowCount includes any "make slot
    /// empty" row supplied by the model. The picker occupies up to half the
    /// slot viewport, inserts those rows below its owner and pushes later slots
    /// down. Long offers scroll independently. While open, the outer scroll
    /// range keeps the entire owner + picker visible; close it to scroll away.
    /// </summary>
    public static LoadoutV2Layout Compute(float width, float height, float uiScale,
        int hullRowCount, int slotRowCount, int offerRowCount, int expandedSlot = -1,
        int hullScroll = 0, int slotScroll = 0, int offerScroll = 0, int statCount = 8)
    {
        if (!float.IsFinite(width) || width < 640f)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(height) || height < 480f)
            throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentOutOfRangeException.ThrowIfNegative(hullRowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(slotRowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(offerRowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(statCount);

        float requested = float.IsFinite(uiScale) && uiScale > 0f ? uiScale : 1f;
        float scale = MathF.Min(Math.Clamp(requested, ModernHudScale.MinScale, ModernHudScale.MaxScale),
            MathF.Min(width / 1000f, (height - DockedFooterHeight) / 600f));
        float pad = 16f * scale, gap = 12f * scale, inset = 10f * scale;
        float headingHeight = 30f * scale, pitch = 36f * scale;
        float innerWidth = width - 2f * pad;
        var header = new TouchRect(pad, pad, innerWidth, 44f * scale);
        var buttons = new TouchRect(pad, height - DockedFooterHeight - pad - 44f * scale,
            innerWidth, 44f * scale);
        float statsY = buttons.Y - gap - 104f * scale;
        float priceWidth = innerWidth * .24f;
        var stats = new TouchRect(pad, statsY, innerWidth - gap - priceWidth, 104f * scale);
        var price = new TouchRect(stats.Right + gap, statsY, priceWidth, stats.Height);
        float mainY = header.Bottom + gap, mainHeight = stats.Y - gap - mainY;
        float columnsWidth = innerWidth - 2f * gap;
        var hulls = new TouchRect(pad, mainY, columnsWidth * .27f, mainHeight);
        var preview = new TouchRect(hulls.Right + gap, mainY, columnsWidth * .36f, mainHeight);
        var slots = new TouchRect(preview.Right + gap, mainY,
            width - pad - preview.Right - gap, mainHeight);
        var hullViewport = new TouchRect(hulls.X + inset, hulls.Y + inset + headingHeight,
            hulls.Width - 2f * inset, hulls.Height - 2f * inset - headingHeight);
        var slotViewport = new TouchRect(slots.X + inset, slots.Y + inset + headingHeight,
            slots.Width - 2f * inset, slots.Height - 2f * inset - headingHeight);
        int hullCapacity = (int)MathF.Floor(hullViewport.Height / pitch);
        int slotCapacity = (int)MathF.Floor(slotViewport.Height / pitch);
        int open = expandedSlot >= 0 && expandedSlot < slotRowCount ? expandedSlot : -1;
        int pickerRows = open >= 0 ? Math.Min(offerRowCount, Math.Max(1, slotCapacity / 2)) : 0;
        int maxHull = Math.Max(0, hullRowCount - hullCapacity);
        int maxSlot = (int)Math.Min(int.MaxValue,
            Math.Max(0L, (long)slotRowCount + pickerRows - slotCapacity));
        int minSlot = 0;
        if (open >= 0)
        {
            // Restrict the outer window to positions that show the complete
            // open header and its inline child. No floating/sticky overlay.
            minSlot = (int)Math.Max(0L, (long)open + 1 + pickerRows - slotCapacity);
            maxSlot = Math.Min(maxSlot, open);
        }
        int hs = Math.Clamp(hullScroll, 0, maxHull);
        int ss = Math.Clamp(slotScroll, minSlot, maxSlot);
        int maxOffer = pickerRows == 0 ? 0 : Math.Max(0, offerRowCount - pickerRows);
        int os = Math.Clamp(offerScroll, 0, maxOffer);
        var offerViewport = pickerRows == 0 ? default : new TouchRect(
            slotViewport.X + inset, slotViewport.Y + ((long)open + 1 - ss) * pitch,
            slotViewport.Width - inset, pickerRows * pitch);

        var elements = new List<Element>();
        void Add(Kind kind, TouchRect bounds, int index = -1, int owner = -1)
            => elements.Add(new(kind, bounds, Inset(bounds, 6f * scale), index, owner));
        TouchRect Row(TouchRect area, int index) => new(area.X, area.Y + index * pitch,
            area.Width, pitch - 3f * scale);
        Add(Kind.Title, new(header.X, header.Y, header.Width * .62f, header.Height));
        Add(Kind.Money, new(header.X + header.Width * .62f + gap, header.Y,
            header.Width * .38f - gap, header.Height));
        Add(Kind.HullHeading, new(hulls.X + inset, hulls.Y + inset,
            hullViewport.Width, headingHeight));
        Add(Kind.SlotHeading, new(slots.X + inset, slots.Y + inset,
            slotViewport.Width, headingHeight));
        Add(Kind.Preview, preview);
        for (int i = 0; i < Math.Min(hullCapacity, hullRowCount - hs); i++)
            Add(Kind.Hull, Row(hullViewport, i), hs + i);
        for (int i = 0; i < slotCapacity; i++)
        {
            long streamIndex = (long)ss + i;
            if (open >= 0 && streamIndex > open && streamIndex <= (long)open + pickerRows)
                continue;
            long slot = streamIndex - (open >= 0 && streamIndex > open ? pickerRows : 0);
            if (slot >= slotRowCount) break;
            Add(Kind.Slot, Row(slotViewport, i), (int)slot);
        }
        for (int i = 0; i < pickerRows; i++)
            Add(Kind.Offer, Row(offerViewport, i), os + i, open);
        // Responsive stat grid; the renderer owns labels, values and deltas.
        int statColumns = Math.Max(1, (int)(stats.Width / (140f * scale)));
        statColumns = Math.Min(statColumns, Math.Max(1, statCount));
        int statRows = statCount == 0 ? 0 : (int)(((long)statCount + statColumns - 1) / statColumns);
        for (int i = 0; i < statCount; i++)
        {
            float cellWidth = stats.Width / statColumns, cellHeight = stats.Height / statRows;
            Add(Kind.Stat, new(stats.X + i % statColumns * cellWidth,
                stats.Y + i / statColumns * cellHeight, cellWidth, cellHeight), i);
        }
        Add(Kind.Price, price);
        Kind[] actions = [Kind.Default, Kind.Revert, Kind.Buy, Kind.Launch];
        float buttonWidth = (buttons.Width - 3f * gap) / 4f;
        for (int i = 0; i < actions.Length; i++)
            Add(actions[i], new(buttons.X + i * (buttonWidth + gap), buttons.Y, buttonWidth, buttons.Height));

        var footer = new TouchRect(0f, height - DockedFooterHeight, width, DockedFooterHeight);
        return new()
        {
            Viewport = new(0, 0, width, height), Header = header, HullList = hulls,
            ShipPreview = preview, SlotList = slots, StatsBar = stats, PriceBox = price,
            ButtonRow = buttons, Footer = footer, HullViewport = hullViewport,
            SlotViewport = slotViewport, OfferViewport = offerViewport,
            Regions = Array.AsReadOnly(new[] { header, hulls, preview, slots, stats, price, buttons, footer }),
            Elements = elements.AsReadOnly(), Scale = scale, RowPitch = pitch, ExpandedSlot = open,
            HullCapacity = hullCapacity, SlotCapacity = slotCapacity, OfferCapacity = pickerRows,
            HullScroll = hs, SlotScroll = ss, OfferScroll = os, MaxHullScroll = maxHull,
            MinSlotScroll = minSlot, MaxSlotScroll = maxSlot, MaxOfferScroll = maxOffer,
        };
    }

    /// <summary>Shared hover/mouse/touch lookup; half-open right/bottom edges.</summary>
    public Element? HitTest(float x, float y)
    {
        foreach (var element in Elements)
            if (element.Bounds.Contains(x, y)) return element;
        return null;
    }

    /// <summary>
    /// Fit at the actual draw scale using TeamPaneRenderer's existing ellipsis
    /// helper. Pass the chosen font's MeasureString(text).X as measureWidth;
    /// no font or graphics device is required to compute or test the layout.
    /// </summary>
    public static string FitText(Element element, string text, Func<string, float> measureWidth, float textScale = 1f)
    {
        if (!float.IsFinite(textScale) || textScale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(textScale));
        float width = element.TextBounds.Width / textScale;
        if (width <= 0f) return "";
        if (measureWidth(text) <= width) return text;
        if (measureWidth("...") > width) return "";
        return TeamPaneRenderer.Fit(text, measureWidth, width);
    }

    private static TouchRect Inset(TouchRect bounds, float pad)
    {
        float x = MathF.Min(pad, bounds.Width / 2f), y = MathF.Min(pad, bounds.Height / 2f);
        return new(bounds.X + x, bounds.Y + y, MathF.Max(0f, bounds.Width - 2f * x),
            MathF.Max(0f, bounds.Height - 2f * y));
    }
}
