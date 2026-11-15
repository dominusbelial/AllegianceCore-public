using System;
using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>P1015 — which part of the Loadout V2 screen the keyboard/pad focus is in.</summary>
public enum LoadoutV2Column
{
    Hulls,
    Slots,
    /// <summary>The Default / Revert / Buy / Launch button row.</summary>
    Actions,
}

/// <summary>
/// P1015 — what the router asks <c>Game1</c> to do. Buy, Launch and leaving the
/// screen stay on the paths the old loadout uses (no second purchase rule):
/// native <c>OnButtonBuy</c> / <c>OnButtonLaunch</c> / <c>OnButtonBack</c>
/// (<c>WinTrek/load.cpp:1511-1531,1498-1509,1533-1538</c>).
/// </summary>
[Flags]
public enum LoadoutV2Request
{
    None = 0,
    Buy = 1,
    Launch = 2,
    /// <summary>Back to the hangar (native <c>SetViewMode(vmHangar)</c>).</summary>
    Leave = 4,
    /// <summary>Leave with the actual ship, through the existing docked action.</summary>
    Undock = 8,
}

/// <summary>
/// P1015 — the held state of every V2 verb this frame, already resolved from the
/// rebindable key table and the pad by the caller. The router does the press
/// edges itself, so a key held across a gate never becomes a fresh press.
/// <see cref="Left"/>/<see cref="Right"/> are the arrow pair (and the D-pad);
/// <see cref="PreviousHull"/>/<see cref="NextHull"/> are the dedicated hull
/// verbs (Q/E, pad shoulders).
/// </summary>
public readonly record struct LoadoutV2Keys(
    bool Up = false, bool Down = false, bool Left = false, bool Right = false,
    bool PreviousHull = false, bool NextHull = false,
    bool Activate = false, bool Back = false,
    bool FocusHulls = false, bool FocusSlots = false, bool CycleFocus = false,
    bool Default = false, bool Launch = false);

/// <summary>P1015 — the pad buttons the docked screens read (Game1.PadNav).</summary>
public readonly record struct LoadoutV2Pad(
    bool Up = false, bool Down = false, bool Left = false, bool Right = false,
    bool A = false, bool B = false, bool X = false, bool Y = false,
    bool LeftShoulder = false, bool RightShoulder = false);

/// <summary>P1015 — the pointer this frame: position, left button and the
/// absolute wheel value (MonoGame <c>ScrollWheelValue</c>, 120 per notch,
/// growing when the wheel is rolled up).</summary>
public readonly record struct LoadoutV2Pointer(float X, float Y, bool Pressed, int Wheel);

/// <summary>P1015 — one step's outcome. <see cref="HoverCue"/> asks for native's
/// <c>mouseoverSound</c> (slot <c>MouseEnter</c>, <c>WinTrek/load.cpp:233-237</c>).</summary>
public readonly record struct LoadoutV2Result(LoadoutV2Request Request, bool HoverCue);

/// <summary>
/// P1015 — the Loadout V2 input router: mouse, keyboard and pad events in,
/// <see cref="LoadoutV2Model"/> actions out. Pure (no device, no
/// <c>GraphicsDevice</c>); it owns only view state — the focused column and
/// rows, the three scroll offsets and the hover — and never a buy/mount rule:
/// every change goes through the model (hence the one <see cref="ShopModel"/>).
///
/// <para><b>Native behaviour kept</b>: a slot click opens that slot's part menu
/// (<c>Slot::Button</c> → <c>SlotClicked</c> → <c>BuyPartMenu</c>,
/// <c>WinTrek/load.cpp:244-252,1380-1384,1680-1751</c>); a menu pick mounts the
/// part or empties the slot and closes the menu (<c>OnMenuCommand</c>,
/// <c>:1863-1903</c>); Next/Previous walk <c>GetNextHullType</c>
/// (<c>:1568-1637</c>); Default is <c>OnButtonDefault</c> (<c>:1639-1648</c>).
/// Keyboard and pad navigation, the focus columns, the wheel and Revert are
/// presentation: native's screen is mouse-only apart from the custom-loadout
/// digits (<c>:2219-2250</c>).</para>
/// </summary>
public sealed class LoadoutV2Input
{
    private static readonly LoadoutV2Layout.Kind[] ActionKinds =
    [
        LoadoutV2Layout.Kind.Default, LoadoutV2Layout.Kind.Revert,
        LoadoutV2Layout.Kind.Buy, LoadoutV2Layout.Kind.Launch,
    ];

    private LoadoutV2Model? _model;
    private LoadoutV2Keys _held;
    private bool _pointerDown;
    private int? _wheel;
    private float _pointerX, _pointerY;
    private (LoadoutV2Layout.Kind Kind, int Index)? _hoverKey;

    public LoadoutV2Column Column { get; private set; }
    /// <summary>Focused hull row (index into <see cref="LoadoutV2Model.HullRows"/>).</summary>
    public int HullFocus { get; private set; }
    /// <summary>Focused slot row (index into <see cref="LoadoutV2Model.SlotRows"/>).</summary>
    public int SlotFocus { get; private set; }
    /// <summary>Focused picker row: 0 is the "empty slot" row, i ≥ 1 is offer i − 1.</summary>
    public int OfferFocus { get; private set; }
    /// <summary>Focused button, an index into Default / Revert / Buy / Launch.</summary>
    public int ActionFocus { get; private set; }
    /// <summary>The focused button while <see cref="Column"/> is the button row.</summary>
    public LoadoutV2Layout.Kind? FocusedAction =>
        Column == LoadoutV2Column.Actions ? ActionKinds[ActionFocus] : null;

    /// <summary>Requested scroll offsets in rows; <see cref="Arrange"/> replaces
    /// them with the values the layout clamped (P1013 contract).</summary>
    public int HullScroll { get; private set; }
    public int SlotScroll { get; private set; }
    public int OfferScroll { get; private set; }

    /// <summary>The interactive element under the pointer (null while gated).</summary>
    public LoadoutV2Layout.Element? Hover { get; private set; }

    /// <summary>The hover re-resolved against the layout being drawn (a scroll or
    /// an opened picker moves rows after the step); null while gated.</summary>
    public LoadoutV2Layout.Element? HoverIn(LoadoutV2Layout layout) =>
        Hover is null ? null : Interactive(layout.HitTest(_pointerX, _pointerY));

    /// <summary>
    /// Lay the screen out at this router's scroll offsets and keep the clamped
    /// offsets, so a wheel past the end never accumulates hidden travel.
    /// </summary>
    public LoadoutV2Layout Arrange(float width, float height, float uiScale,
        int hullRows, int slotRows, int offerRows, int openSlot, int statCount)
    {
        var layout = LoadoutV2Layout.Compute(width, height, uiScale, hullRows, slotRows, offerRows,
            openSlot, HullScroll, SlotScroll, OfferScroll, statCount);
        HullScroll = layout.HullScroll;
        SlotScroll = layout.SlotScroll;
        OfferScroll = layout.OfferScroll;
        return layout;
    }

    /// <summary>
    /// One frame. <paramref name="live"/> false (command screen, chat entry, a
    /// popup, a pending launch, or the V2 screen not shown) still observes the
    /// held keys, the button and the wheel but acts on nothing and drops the hover.
    /// </summary>
    /// <param name="layout">The layout last drawn (its elements are the only
    /// hit targets); null before the first frame is laid out.</param>
    public LoadoutV2Result Step(LoadoutV2Model? model, LoadoutV2Layout? layout, bool live,
        in LoadoutV2Keys keys, LoadoutV2Pointer? pointer)
    {
        var was = _held;
        _held = keys;
        bool click = pointer is { Pressed: true } && !_pointerDown;
        _pointerDown = pointer?.Pressed ?? false;
        int wheelDelta = pointer is { } p0 && _wheel is { } before ? p0.Wheel - before : 0;
        _wheel = pointer?.Wheel ?? _wheel;

        if (!live || model is null)
        {
            Hover = null;
            _hoverKey = null;
            return default;
        }
        Sync(model);

        var request = LoadoutV2Request.None;
        bool cue = false;

        // ── pointer: hover, wheel, click ────────────────────────────────────
        if (pointer is { } p && layout is not null)
        {
            (_pointerX, _pointerY) = (p.X, p.Y);
            var hit = Interactive(layout.HitTest(p.X, p.Y));
            var key = hit is { } h ? (h.Kind, h.Index) : ((LoadoutV2Layout.Kind, int)?)null;
            cue = key is not null && key != _hoverKey;
            _hoverKey = key;
            Hover = hit;
            if (wheelDelta != 0) Wheel(model, layout, p.X, p.Y, wheelDelta);
            if (click && hit is { } target) request |= Activate(model, layout, target);
        }
        else
        {
            Hover = null;
            _hoverKey = null;
        }

        // ── keys / pad ──────────────────────────────────────────────────────
        bool Edge(bool now, bool then) => now && !then;
        if (Edge(keys.FocusHulls, was.FocusHulls)) Focus(model, LoadoutV2Column.Hulls);
        if (Edge(keys.FocusSlots, was.FocusSlots)) Focus(model, LoadoutV2Column.Slots);
        if (Edge(keys.CycleFocus, was.CycleFocus))
            Focus(model, (LoadoutV2Column)(((int)Column + 1) % 3));

        int vertical = (Edge(keys.Down, was.Down) ? 1 : 0) - (Edge(keys.Up, was.Up) ? 1 : 0);
        int horizontal = (Edge(keys.Right, was.Right) ? 1 : 0) - (Edge(keys.Left, was.Left) ? 1 : 0);
        int hull = (Edge(keys.NextHull, was.NextHull) ? 1 : 0) - (Edge(keys.PreviousHull, was.PreviousHull) ? 1 : 0);
        bool picker = model.OpenSlot is not null;
        if (Column == LoadoutV2Column.Actions && !picker)
        {
            // The button row is horizontal: both arrow pairs walk it. Q/E and
            // the shoulders still change the hull.
            int step = vertical + horizontal;
            if (step != 0) ActionFocus = Math.Clamp(ActionFocus + Math.Sign(step), 0, ActionKinds.Length - 1);
        }
        else
        {
            hull += horizontal;
            if (vertical != 0) Move(model, layout, vertical);
        }
        if (hull != 0)
        {
            // Native OnButtonNext / OnButtonPrevious (load.cpp:1627-1637).
            if (hull > 0) model.NextHull(); else model.PreviousHull();
            HullChanged(model, layout);
        }

        if (Edge(keys.Activate, was.Activate)) request |= ActivateFocus(model, layout);
        if (Edge(keys.Default, was.Default))
        {
            model.ApplyDefault();
            HullChanged(model, layout);
        }
        if (Edge(keys.Launch, was.Launch)) request |= LoadoutV2Request.Launch;
        if (Edge(keys.Back, was.Back) && !ClosePicker(model)) request |= LoadoutV2Request.Leave;

        Sync(model);
        return new(request, cue);
    }

    /// <summary>A touch tap: the same targets and actions as a mouse click
    /// (<see cref="DockedTouchZones.LoadoutV2HitTest"/>).</summary>
    public LoadoutV2Request Tap(LoadoutV2Model model, LoadoutV2Layout layout, float x, float y)
    {
        Sync(model);
        var request = Interactive(DockedTouchZones.LoadoutV2HitTest(layout, x, y)) is { } target
            ? Activate(model, layout, target) : LoadoutV2Request.None;
        Sync(model);
        return request;
    }

    /// <summary>Discard screen-local state when leaving V2, retaining device
    /// latches so a held button cannot fire when the screen returns.</summary>
    public void Suspend(LoadoutV2Model? model)
    {
        model?.CloseSlot();
        _model = null;
        Hover = null;
        _hoverKey = null;
        Column = LoadoutV2Column.Hulls;
        HullFocus = SlotFocus = OfferFocus = ActionFocus = 0;
        HullScroll = SlotScroll = OfferScroll = 0;
    }

    /// <summary>Escape's first step: close the open picker. False when none was
    /// open, so the caller leaves the screen as it does today.</summary>
    public bool ClosePicker(LoadoutV2Model model)
    {
        if (model.OpenSlot is null) return false;
        model.CloseSlot();
        OfferFocus = OfferScroll = 0;
        return true;
    }

    /// <summary>The key-table actions the V2 screen reads, in hint order.</summary>
    public const string FocusHullsAction = "LoadoutFocusHulls", FocusSlotsAction = "LoadoutFocusSlots",
        DefaultAction = "LoadoutDefault";

    /// <summary>
    /// The one input map of the V2 screen: key-table actions (so every key is
    /// rebindable; <paramref name="down"/> is <c>ActionMap.IsDownMostSpecific</c>)
    /// merged with the pad the way the old docked screen merges it — D-pad =
    /// arrows, A = select, B = back, Y = launch (Game1.Panes.cs, P499/P519).
    /// The shoulders, which switched the old HULLS/PARTS tab, are previous/next
    /// hull here and X steps the focus (hulls → slots → buttons).
    /// <paramref name="control"/> held leaves the plain focus/default keys alone:
    /// Ctrl+digit is native's custom-loadout save (<c>WinTrek/load.cpp:2228-2235</c>).
    /// </summary>
    public static LoadoutV2Keys Keys(Func<string, bool> down, bool control, LoadoutV2Pad pad = default) => new(
        Up: down("MenuUp") || pad.Up,
        Down: down("MenuDown") || pad.Down,
        Left: down("MenuLeft") || pad.Left,
        Right: down("MenuRight") || pad.Right,
        PreviousHull: down("ShopCycleHullPrev") || pad.LeftShoulder,
        NextHull: down("ShopCycleHullNext") || pad.RightShoulder,
        Activate: down("MenuSelect") || down("MenuConfirm") || pad.A,
        Back: down("MenuBack") || pad.B,
        FocusHulls: !control && down(FocusHullsAction),
        FocusSlots: !control && down(FocusSlotsAction),
        CycleFocus: down("ShopTabSwitch") || pad.X,
        Default: !control && down(DefaultAction),
        Launch: down("ShopLaunch") || pad.Y);

    /// <summary>
    /// The footer hint, from the live key table (<paramref name="label"/> is
    /// <c>ActionMap.LabelOf</c>), so a rebind shows at once. An unbound verb is
    /// left out.
    /// </summary>
    public static string Hint(Func<string, string> label)
    {
        var parts = new System.Collections.Generic.List<string>();
        void Add(string text, params string[] actions)
        {
            var keys = new System.Collections.Generic.List<string>();
            foreach (var action in actions)
                if (label(action) is { Length: > 0 } key) keys.Add(key);
            if (keys.Count > 0) parts.Add(string.Join("/", keys) + " " + text);
        }
        Add("hulls/slots", FocusHullsAction, FocusSlotsAction);
        Add("next", "ShopTabSwitch");
        Add("move", "MenuUp", "MenuDown");
        Add("hull", "MenuLeft", "MenuRight");
        Add("choose", "MenuSelect");
        Add("default", DefaultAction);
        Add("back", "MenuBack");
        Add("launch", "ShopLaunch");
        return string.Join("  |  ", parts);
    }

    // ── internals ───────────────────────────────────────────────────────────

    private static LoadoutV2Layout.Element? Interactive(LoadoutV2Layout.Element? element) =>
        element is { Kind: LoadoutV2Layout.Kind.Hull or LoadoutV2Layout.Kind.Slot or LoadoutV2Layout.Kind.Offer
            or LoadoutV2Layout.Kind.Default or LoadoutV2Layout.Kind.Revert
            or LoadoutV2Layout.Kind.Buy or LoadoutV2Layout.Kind.Launch
            or LoadoutV2Layout.Kind.Back or LoadoutV2Layout.Kind.Undock } ? element : null;

    /// <summary>Clamp the view state to the model's current rows; a new model
    /// (new dock, or the same shop after a tech change) starts on the chosen hull
    /// and keeps an open picker open.</summary>
    private void Sync(LoadoutV2Model model)
    {
        var hulls = model.HullRows;
        if (!ReferenceEquals(model, _model))
        {
            bool sameShop = _model is not null && ReferenceEquals(_model.Shop, model.Shop);
            if (sameShop && _model!.OpenSlot is { } open && model.OpenSlot is null) model.OpenSlotFor(open);
            if (!sameShop)
            {
                Column = LoadoutV2Column.Hulls;
                HullFocus = ChosenHull(model);
                SlotFocus = ActionFocus = OfferFocus = 0;
                // Arrange clamps this to the last page, so the chosen hull is in view.
                HullScroll = HullFocus;
                SlotScroll = OfferScroll = 0;
            }
            _model = model;
        }
        HullFocus = Math.Clamp(HullFocus, 0, Math.Max(0, hulls.Count - 1));
        SlotFocus = Math.Clamp(SlotFocus, 0, Math.Max(0, model.SlotRows.Count - 1));
        if (model.OpenSlot is null) OfferFocus = OfferScroll = 0;
        else OfferFocus = Math.Clamp(OfferFocus, 0, model.Offers.Count);
    }

    private static int ChosenHull(LoadoutV2Model model)
    {
        var hulls = model.HullRows;
        for (int i = 0; i < hulls.Count; i++) if (hulls[i].IsChosen) return i;
        return 0;
    }

    private void Focus(LoadoutV2Model model, LoadoutV2Column column)
    {
        ClosePicker(model);
        Column = column;
    }

    private void Move(LoadoutV2Model model, LoadoutV2Layout? layout, int delta)
    {
        if (model.OpenSlot is not null)
        {
            OfferFocus = Math.Clamp(OfferFocus + delta, 0, model.Offers.Count);
            OfferScroll = Reveal(layout?.OfferScroll ?? OfferScroll, OfferFocus, layout?.OfferCapacity ?? 0);
        }
        else if (Column == LoadoutV2Column.Hulls)
        {
            HullFocus = Math.Clamp(HullFocus + delta, 0, Math.Max(0, model.HullRows.Count - 1));
            HullScroll = Reveal(layout?.HullScroll ?? HullScroll, HullFocus, layout?.HullCapacity ?? 0);
        }
        else if (Column == LoadoutV2Column.Slots)
        {
            SlotFocus = Math.Clamp(SlotFocus + delta, 0, Math.Max(0, model.SlotRows.Count - 1));
            SlotScroll = Reveal(layout?.SlotScroll ?? SlotScroll, SlotFocus, layout?.SlotCapacity ?? 0);
        }
    }

    /// <summary>The smallest scroll change that shows <paramref name="focus"/>.</summary>
    private static int Reveal(int scroll, int focus, int capacity) =>
        capacity <= 0 ? scroll
        : focus < scroll ? focus
        : focus >= scroll + capacity ? focus - capacity + 1
        : scroll;

    private void HullChanged(LoadoutV2Model model, LoadoutV2Layout? layout)
    {
        HullFocus = ChosenHull(model);
        HullScroll = Reveal(layout?.HullScroll ?? HullScroll, HullFocus, layout?.HullCapacity ?? 0);
        SlotFocus = 0;
        SlotScroll = OfferScroll = OfferFocus = 0;
    }

    private LoadoutV2Request ActivateFocus(LoadoutV2Model model, LoadoutV2Layout? layout)
    {
        if (model.OpenSlot is { } open)
        {
            ChooseRow(model, open, OfferFocus);
            return LoadoutV2Request.None;
        }
        switch (Column)
        {
            case LoadoutV2Column.Hulls:
                SelectHull(model, layout, HullFocus);
                return LoadoutV2Request.None;
            case LoadoutV2Column.Slots:
                ToggleSlot(model, layout, SlotFocus);
                return LoadoutV2Request.None;
            default:
                return Press(model, layout, ActionKinds[ActionFocus]);
        }
    }

    private LoadoutV2Request Activate(LoadoutV2Model model, LoadoutV2Layout layout, LoadoutV2Layout.Element target)
    {
        switch (target.Kind)
        {
            case LoadoutV2Layout.Kind.Back:
                return ClosePicker(model) ? LoadoutV2Request.None : LoadoutV2Request.Leave;
            case LoadoutV2Layout.Kind.Undock:
                return LoadoutV2Request.Undock;
            case LoadoutV2Layout.Kind.Hull:
                ClosePicker(model);
                Column = LoadoutV2Column.Hulls;
                SelectHull(model, layout, target.Index);
                return LoadoutV2Request.None;
            case LoadoutV2Layout.Kind.Slot:
                Column = LoadoutV2Column.Slots;
                ToggleSlot(model, layout, target.Index);
                return LoadoutV2Request.None;
            case LoadoutV2Layout.Kind.Offer:
                if (model.OpenSlot is { } open) ChooseRow(model, open, target.Index);
                return LoadoutV2Request.None;
            default:
                ClosePicker(model);
                Column = LoadoutV2Column.Actions;
                ActionFocus = Math.Max(0, Array.IndexOf(ActionKinds, target.Kind));
                return Press(model, layout, target.Kind);
        }
    }

    private LoadoutV2Request Press(LoadoutV2Model model, LoadoutV2Layout? layout, LoadoutV2Layout.Kind kind)
    {
        switch (kind)
        {
            case LoadoutV2Layout.Kind.Default:
                model.ApplyDefault();
                HullChanged(model, layout);
                return LoadoutV2Request.None;
            case LoadoutV2Layout.Kind.Revert:
                model.Revert();
                HullChanged(model, layout);
                return LoadoutV2Request.None;
            case LoadoutV2Layout.Kind.Buy: return LoadoutV2Request.Buy;
            case LoadoutV2Layout.Kind.Launch: return LoadoutV2Request.Launch;
            default: return LoadoutV2Request.None;
        }
    }

    private void SelectHull(LoadoutV2Model model, LoadoutV2Layout? layout, int index)
    {
        var hulls = model.HullRows;
        if (index < 0 || index >= hulls.Count) return;
        HullFocus = index;
        short before = model.Shop.EffectiveHullId;
        // A locked hull is refused by the model, which keeps the reason.
        if (model.SelectHull(hulls[index].HullId) && model.Shop.EffectiveHullId != before)
        {
            SlotFocus = 0;
            SlotScroll = OfferScroll = OfferFocus = 0;
        }
        HullScroll = Reveal(layout?.HullScroll ?? HullScroll, HullFocus, layout?.HullCapacity ?? 0);
    }

    /// <summary>Open the slot's inline picker, or close it when it is the open
    /// one. The picker starts on the part the slot holds.</summary>
    private void ToggleSlot(LoadoutV2Model model, LoadoutV2Layout? layout, int index)
    {
        var slots = model.SlotRows;
        if (index < 0 || index >= slots.Count) return;
        SlotFocus = index;
        var slot = slots[index].Slot;
        if (model.OpenSlot == slot)
        {
            ClosePicker(model);
            return;
        }
        if (!model.OpenSlotFor(slot)) return;
        OfferFocus = 0;
        var offers = model.Offers;
        for (int i = 0; i < offers.Count; i++)
            if (offers[i].IsChosen) { OfferFocus = i + 1; break; }
        // The layout gives the picker at most half the slot viewport (P1013).
        int capacity = Math.Min(offers.Count + 1, Math.Max(1, (layout?.SlotCapacity ?? 0) / 2));
        OfferScroll = Reveal(0, OfferFocus, capacity);
    }

    /// <summary>Picker row 0 empties the slot (native "-Make Slot Empty-",
    /// <c>load.cpp:1704</c>); row i ≥ 1 mounts offer i − 1. Either closes the
    /// picker, as native's <c>OnMenuCommand</c> does (<c>:1863-1893</c>).</summary>
    private void ChooseRow(LoadoutV2Model model, SlotDef slot, int row)
    {
        var offers = model.Offers;
        if (row <= 0) model.ClearSlot(slot);
        else if (row <= offers.Count) model.ChooseOffer(offers[row - 1].PartId);
        else return;
        if (model.OpenSlot is null) OfferFocus = OfferScroll = 0;
    }

    /// <summary>
    /// The wheel scrolls the column under the pointer by rows. The slot column
    /// cannot scroll its open picker's owner out of view (P1013 keeps owner and
    /// picker visible): going past that range closes the picker first, but only
    /// when the closed list has somewhere to scroll to.
    /// </summary>
    private void Wheel(LoadoutV2Model model, LoadoutV2Layout layout, float x, float y, int delta)
    {
        int rows = -Math.Sign(delta) * Math.Max(1, Math.Abs(delta) / 120);
        if (layout.OfferCapacity > 0 && layout.OfferViewport.Contains(x, y))
            OfferScroll = Math.Clamp(layout.OfferScroll + rows, 0, layout.MaxOfferScroll);
        else if (layout.HullList.Contains(x, y))
            HullScroll = Math.Clamp(layout.HullScroll + rows, 0, layout.MaxHullScroll);
        else if (layout.SlotList.Contains(x, y))
        {
            int want = layout.SlotScroll + rows;
            if (model.OpenSlot is null || (want >= layout.MinSlotScroll && want <= layout.MaxSlotScroll))
            {
                SlotScroll = Math.Clamp(want, layout.MinSlotScroll, layout.MaxSlotScroll);
                return;
            }
            int closedMax = Math.Max(0, model.SlotRows.Count - layout.SlotCapacity);
            bool room = rows > 0 ? layout.SlotScroll < closedMax : layout.SlotScroll > 0;
            if (!room) return;
            ClosePicker(model);
            SlotScroll = Math.Clamp(want, 0, closedMax);
        }
    }
}
