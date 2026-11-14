using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>The docked screen's two views: hulls and parts.</summary>
public enum ShopTab
{
    /// <summary>Pick / cycle the hull (native's HullMenu + next/prev buttons).</summary>
    Hulls,
    /// <summary>Slot-driven equipment: pick a slot, then a compatible part to fill it.</summary>
    Parts,
}

/// <summary>
/// P519 — one equipment slot of the effective hull, the slot-driven analogue of
/// native <c>LoadoutImpl::Slot</c> (load.cpp:58-83): a single
/// (<see cref="EquipmentType"/>, mount) pair. A weapon slot is one hardpoint
/// (mount = hardpoint index); every other equipment type has one slot (mount 0)
/// when the hull has a socket for it (<c>PmEquipment[et] != 0</c>, native
/// <c>SlotValidForHull</c> else-branch, load.cpp:112-122).
/// </summary>
public readonly record struct SlotDef(EquipmentType Type, byte Mount, bool IsTurret)
{
    public bool IsWeapon => Type == EquipmentType.Weapon;

    /// <summary>A short display label: weapon hardpoint number / turret / the type name.</summary>
    public string Label
    {
        get
        {
            if (Type == EquipmentType.Weapon)
                return IsTurret ? $"TURRET {Mount + 1}" : $"WEAPON {Mount + 1}";
            return Type.ToString().ToUpperInvariant();
        }
    }
}

/// <summary>
/// P519 — the redesigned docked loadout interaction model. Two tabs
/// (<see cref="ShopTab.Hulls"/> / <see cref="ShopTab.Parts"/>); the PARTS tab is
/// slot-driven: it lists the effective hull's real slots and, once a slot is
/// opened, only the parts that fit that slot+mount (native BuyPartMenu,
/// load.cpp:1680-1751: group/CanBuy/!IsObsolete, then equipment-type equality +
/// <c>CanMount(part, slotMount)</c>). Hulls are cycled next/previous through the
/// station's buyable, non-obsolete, GroupID≥0 hulls (native GetNextHullType,
/// load.cpp:1568-1616).
///
/// <para><b>Bug 3 (highest priority): BUY_LOADOUT always carries the full
/// intended loadout.</b> <see cref="ChosenParts"/> is seeded from the ship's
/// real mounted parts (the P519 dock-sync BUY_LOADOUT_ACK tail) and every edit
/// (fill a slot / clear a slot / swap hull) mutates that same pending list — so
/// a launch sends N &gt; 0 parts equal to the ship's intended loadout, never 0.
/// On a hull change the parts that don't fit the new hull are dropped (native
/// ReplaceLoadout/RefreshLoadout, load.cpp:1453-1474, mounts only what fits), so
/// launching after a hull change sends that hull's mountable loadout.</para>
///
/// <para>Model, not view: MonoGame-free and headless-testable (the
/// <see cref="ScoreboardLayout"/> discipline). The <em>current page</em> (hull
/// list / slot list / open-slot part list) is rendered as rows by
/// <see cref="ShopLayout"/>, navigated with <see cref="MoveCursor"/> /
/// <see cref="Activate"/> / <see cref="Back"/>, and detailed by
/// <see cref="PartInfoLayout"/> via <see cref="FocusHull"/>/<see cref="FocusPart"/>.</para>
/// </summary>
public sealed class ShopModel
{
    private ShopCatalog _cat;
    private readonly List<ExpandedPartData> _chosenParts = new();

    /// <summary>
    /// P759 — the in-memory custom-loadout slot 0 (native
    /// <c>m_customLoadouts[0]</c>, <c>clintlib.h:1155</c> + <c>SaveCustomLoadout</c>,
    /// <c>clintlib.h:1425-1428</c>), keyed by hull id. Native persists this list
    /// to disk (<c>SaveCustomLoadoutFile</c>, clintlib.h:1944-1956); the port keeps
    /// the session slot and uses it for the hull-swap refresh
    /// (<c>BuyHull</c> → <c>RefreshLoadout(customLoadouts[0], phullType)</c>,
    /// load.cpp:1478-1486), which is the behaviour this row pins.
    /// </summary>
    private readonly Dictionary<(short HullId, int Slot), List<ExpandedPartData>> _customLoadouts = new();

    /// <summary>P434/P435 — the real launcher/dispenser offers (missile + mine +
    /// chaff + probe), normalized to empty so a null Launchers is safe.</summary>
    private IReadOnlyList<LauncherOffer> _launchers;

    /// <summary>The ship's currently-mounted hull id (from SHIP_STATUS).</summary>
    public short CurrentHullId { get; }

    /// <summary>The ship's currently-mounted parts (the P519 dock-sync
    /// BUY_LOADOUT_ACK tail). Kept as a separate read-only snapshot; the shop
    /// never mutates it.</summary>
    public IReadOnlyList<ExpandedPartData> CurrentParts { get; }

    /// <summary>The hull the player picked (null = keep the current hull).</summary>
    public short? ChosenHullId { get; private set; }

    /// <summary>The parts to send as the full intended loadout on launch.</summary>
    public IReadOnlyList<ExpandedPartData> ChosenParts => _chosenParts;

    /// <summary>The active tab (HULLS or PARTS).</summary>
    public ShopTab Tab { get; private set; } = ShopTab.Hulls;

    /// <summary>P519 — the active page of the current tab: the hull list, the
    /// slot list, or the parts of an open slot. Drives rows + row navigation.</summary>
    public ShopPage Page { get; private set; } = ShopPage.HullList;

    /// <summary>P519 — whether the PARTS tab is drilling into a slot (showing
    /// its compatible parts) rather than the slot list.</summary>
    public bool SlotOpen => Page == ShopPage.SlotParts;

    /// <summary>P519 — the slot currently open (drilled into), when
    /// <see cref="SlotOpen"/>.</summary>
    public SlotDef? OpenSlot { get; private set; }

    // ── navigation cursors (indices into the current page's row list) ───────
    private int _hullCursor;
    private int _slotCursor;
    private int _partCursor;

    /// <summary>P519 — the hull id the screen will request: the chosen one,
    /// else the current.</summary>
    public short EffectiveHullId => ChosenHullId ?? CurrentHullId;

    /// <summary>P519 — the catalogue record for <see cref="EffectiveHullId"/>,
    /// or null if that hull isn't in the catalogue.</summary>
    public DataHullTypeIGC? EffectiveHull
    {
        get
        {
            foreach (var h in _cat.Hulls)
                if (h.HullId == EffectiveHullId) return h;
            return null;
        }
    }

    /// <summary>P519 — true when a genuinely different hull is chosen (a real
    /// hull swap will occur on launch), the server's P334 CanMount-drop scope.</summary>
    public bool WillSwapHull => ChosenHullId is { } c && c != CurrentHullId;

    /// <summary>
    /// P519 — the chosen parts the server will DROP on launch because the
    /// effective hull can't mount them. Under the new slot-driven model a hull
    /// change already drops non-fitting parts eagerly (native ReplaceLoadout),
    /// so this is normally empty; it stays as the honest projection for any
    /// remaining part that slipped through (e.g. a free-mounted same-type part).
    /// Gated on <see cref="WillSwapHull"/> like P394/P335.
    /// </summary>
    public IReadOnlyList<ExpandedPartData> ChosenPartsThatDontFitEffectiveHull
    {
        get
        {
            if (!WillSwapHull) return [];
            if (EffectiveHull is not { } eff) return [];
            var bad = new List<ExpandedPartData>();
            foreach (var p in _chosenParts)
            {
                var pt = LookupPart(p.PartId);
                if (pt is { } part && !eff.CanMountPart(part)) bad.Add(p);
            }
            return bad;
        }
    }

    // ── pricing (native GetValue: hull price + Σ part prices) ───────────────

    /// <summary>P468 — the local player's wallet (P442), or null until the first
    /// SET_MONEY. When null the shop can't judge affordability.</summary>
    public int? MyMoney { get; private set; }

    public bool AffordabilityKnown => MyMoney is not null;

    public long ValueOf(short hullId, IReadOnlyList<ExpandedPartData> parts)
    {
        long v = 0;
        foreach (var h in _cat.Hulls)
            if (h.HullId == hullId) { v += h.Price; break; }
        foreach (var p in parts)
            if (LookupPart(p.PartId) is { } pc) v += pc.Price;
        return v;
    }

    public long CurrentShipValue => ValueOf(CurrentHullId, CurrentParts);

    public long NewLoadoutValue => ValueOf(EffectiveHullId, _chosenParts);

    public long Cost => NewLoadoutValue - CurrentShipValue;

    public bool CanAffordTotal => AffordabilityKnown && Cost <= MyMoney!.Value;

    /// <summary>
    /// P759 — the native <c>OnButtonBuy</c> shortfall (<c>load.cpp:1513-1520</c>):
    /// <c>cost = new - (current + money)</c>, i.e. the amount the server must
    /// grant via <c>REQUEST_MONEY</c> before the buy can complete. 0 when the
    /// player can already afford the loadout (or money is unknown — then the
    /// server stays authoritative and the client does not fabricate a request).
    /// </summary>
    public long BuyShortfall =>
        AffordabilityKnown ? Math.Max(0L, Cost - MyMoney!.Value) : 0L;

    public string? BlockReason { get; private set; }

    public bool TryLaunch()
    {
        if (AffordabilityKnown && Cost > MyMoney!.Value)
        {
            BlockReason = $"cannot afford: loadout cost ${Cost} > money ${MyMoney.Value}";
            return false;
        }
        BlockReason = null;
        return true;
    }

    public bool HullRowGreyed(DataHullTypeIGC hull)
    {
        if (!AffordabilityKnown || hull.HullId == EffectiveHullId) return false;
        long inc = EffectiveHull is { } c ? (long)hull.Price - c.Price : hull.Price;
        return inc > MyMoney!.Value;
    }

    public bool PartRowGreyed(short partId, int price)
    {
        if (!AffordabilityKnown) return false;
        if (IsPartChosen(partId)) return false;
        return price > MyMoney!.Value;
    }

    // ── P519 hulls: the buyable hull list + cycling ─────────────────────────

    /// <summary>The station's buyable hulls (GroupID ≥ 0 ∧ CanBuy ∧ !IsObsolete,
    /// via the already-filtered catalogue). This is the HULLS tab's list.</summary>
    public IReadOnlyList<DataHullTypeIGC> BuyableHulls => _cat.Hulls;

    public int HullCursor
    {
        get => _hullCursor;
        private set => _hullCursor = Math.Clamp(value, 0, Math.Max(0, BuyableHulls.Count - 1));
    }

    /// <summary>P519 — cycle to the next/previous buyable hull (native
    /// OnButtonNext/OnButtonPrevious → GetNextHullType, load.cpp:1568-1636),
    /// wrapping around the buyable list. Sets the chosen hull and rebases the
    /// pending loadout onto the new hull (dropping non-fitting parts).</summary>
    public void CycleHull(bool forward)
    {
        if (BuyableHulls.Count == 0) return;
        int idx = IndexOfHull(EffectiveHullId);
        idx = (idx + (forward ? 1 : -1) + BuyableHulls.Count) % BuyableHulls.Count;
        var next = BuyableHulls[idx];
        ChosenHullId = next.HullId;
        HullCursor = idx;
        RebaseForEffectiveHull();
        BlockReason = null;
    }

    /// <summary>P519 — select the hull under the HULLS-list cursor (the native
    /// hull-menu click). Picking the current hull de-picks (keep current);
    /// picking any other sets it as the chosen hull and rebases the loadout.</summary>
    public void SelectHullAtCursor()
    {
        if (BuyableHulls.Count == 0) return;
        var hull = BuyableHulls[HullCursor];
        bool picked = hull.HullId != CurrentHullId;   // re-picking de-picks (keep current)
        ChosenHullId = picked ? hull.HullId : null;
        RebaseForEffectiveHull();
        BlockReason = null;
        if (picked)
            HullChosen?.Invoke();            // native mountSound on hull pick, load.cpp:1480-1487
    }

    /// <summary>The hull row the HULLS cursor is on (for the detail panel).</summary>
    public DataHullTypeIGC? HullUnderCursor =>
        BuyableHulls.Count > 0 && _hullCursor < BuyableHulls.Count ? BuyableHulls[_hullCursor] : null;

    /// <summary>
    /// P1010 — pick a hull BY ID (the Loadout V2 hull column's click), the
    /// targeted form of <see cref="SelectHullAtCursor"/>. Only a hull the
    /// station offers is selectable (the gate is native <c>GetNextHullType</c>'s
    /// <c>GroupID &gt;= 0 &amp;&amp; pstation-&gt;CanBuy(pht) &amp;&amp;
    /// !pstation-&gt;IsObsolete(pht)</c>, <c>WinTrek/load.cpp:1603-1606</c>, which
    /// <see cref="BuyableHulls"/> already encodes). Selecting the docked hull
    /// de-picks it (keep the current hull, no loadout refresh) exactly as native
    /// <c>NextHull</c> does when <c>pht == phtCurrent</c> (<c>load.cpp:1620-1625</c>,
    /// hull menu <c>:1896-1898</c>); selecting another hull rebases the pending
    /// loadout onto that hull's saved custom slot 0 / native default and raises
    /// <see cref="HullChosen"/> for the hull-menu <c>mountSound</c>
    /// (<c>load.cpp:1480-1487</c>).
    /// </summary>
    /// <returns>False when the hull is not offered at this station.</returns>
    public bool SelectHull(short hullId)
    {
        if (!InHullCatalog(hullId)) return false;
        HullCursor = IndexOfHull(hullId);
        bool picked = hullId != CurrentHullId;   // re-picking the docked hull de-picks (keep current)
        ChosenHullId = picked ? hullId : null;
        RebaseForEffectiveHull();
        BlockReason = null;
        if (picked) HullChosen?.Invoke();
        return true;
    }

    // ── P519 slots (the PARTS tab) ─────────────────────────────────────────

    /// <summary>P519 — the effective hull's real equipment slots, in display
    /// order: weapons per hardpoint (turrets labelled), then the single-mount
    /// equipment sockets the hull actually has (<c>PmEquipment[et] != 0</c>).</summary>
    public IReadOnlyList<SlotDef> Slots
    {
        get
        {
            var hull = EffectiveHull;
            if (hull is null) return Array.Empty<SlotDef>();
            var slots = new List<SlotDef>();
            // Weapons: one slot per hardpoint.
            for (int mount = 0; mount < hull.Value.Hardpoints.Length; mount++)
            {
                bool fixed_ = mount < hull.Value.MaxFixedWeapons;
                slots.Add(new SlotDef(EquipmentType.Weapon, (byte)mount, IsTurret: !fixed_));
            }
            // Single-mount equipment sockets the hull actually supports.
            foreach (var et in new[]
            {
                EquipmentType.Magazine, EquipmentType.Dispenser,
                EquipmentType.ChaffLauncher, EquipmentType.Shield,
                EquipmentType.Cloak, EquipmentType.Afterburner,
            })
            {
                int idx = (int)et;
                if (idx < hull.Value.PmEquipment.Length && hull.Value.PmEquipment[idx] != 0)
                    slots.Add(new SlotDef(et, 0, IsTurret: false));
            }
            return slots;
        }
    }

    public int SlotCursor
    {
        get => _slotCursor;
        private set => _slotCursor = Math.Clamp(value, 0, Math.Max(0, Slots.Count - 1));
    }

    public SlotDef? SlotUnderCursor => Slots.Count > 0 && _slotCursor < Slots.Count ? Slots[_slotCursor] : null;

    /// <summary>P519 — the part currently mounted in a slot (from the pending
    /// loadout), or null if the slot is empty.</summary>
    public ExpandedPartData? PartInSlot(SlotDef slot)
    {
        foreach (var p in _chosenParts)
        {
            var pt = LookupPart(p.PartId);
            if (pt is null) continue;
            if (slot.IsWeapon)
            {
                if (pt.Value.EquipmentType == EquipmentType.Weapon && p.MountId == slot.Mount) return p;
            }
            else if (pt.Value.EquipmentType == slot.Type && p.MountId == 0)
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>P519 — whether a slot is filled in the pending loadout.</summary>
    public bool SlotFilled(SlotDef slot) => PartInSlot(slot) is not null;

    /// <summary>P519 — open a slot in the PARTS tab to list its compatible parts.</summary>
    public void OpenSlotAtCursor()
    {
        var slot = SlotUnderCursor;
        if (slot is null) return;
        OpenSlot = slot;
        Page = ShopPage.SlotParts;
        _partCursor = 0;
    }

    /// <summary>P519 — the compatible parts for the open slot, in a stable order.
    /// Only parts that fit this slot's type AND mount are ever listed (native
    /// BuyPartMenu: equipment-type equality + <c>CanMount(part, slotMount)</c>).</summary>
    public IReadOnlyList<SlotPartOffer> OpenSlotParts =>
        OpenSlot is { } slot ? SlotOffers(slot) : Array.Empty<SlotPartOffer>();

    /// <summary>
    /// P1010 — the compatible parts for ANY slot of the effective hull, without
    /// opening it in the tab/page machinery (<see cref="OpenSlotParts"/> is this
    /// same list for the opened slot). One predicate, so the Loadout V2 inline
    /// pickers and the old screen's open-slot menu can never disagree: the part
    /// must be the slot's equipment type and pass the per-mount mask test
    /// <see cref="PartFitsSlot"/> (native <c>IhullTypeIGC::CanMount(part, mount)</c>,
    /// <c>src/Igc/hullTypeIGC.h:163-176,199-213</c>, which is the term
    /// <c>LoadoutImpl::BuyPartMenu</c> applies at <c>WinTrek/load.cpp:1734-1735</c>).
    /// The catalogue is already the station's tech-filtered one
    /// (<see cref="ShopCatalog.FilteredFor"/> = native <c>pstation-&gt;CanBuy</c>,
    /// <c>Igc/stationIGC.h:383-391</c>), so every offer is something the side can
    /// buy. Order is stable: price, then name, then id.
    /// </summary>
    public IReadOnlyList<SlotPartOffer> SlotOffers(SlotDef slot)
    {
        var hull = EffectiveHull;
        if (hull is null) return Array.Empty<SlotPartOffer>();
        var list = new List<SlotPartOffer>();
        foreach (var p in _cat.Parts)
            if (PartFitsSlot(hull.Value, slot, p))
                list.Add(new SlotPartOffer(p, DefaultAmount(p), false));
        if (_launchers is { } ls)
            foreach (var l in ls)
                if (PartFitsSlot(hull.Value, slot, l.Part))
                    list.Add(new SlotPartOffer(l.Part, l.Ammo, true));
        // Stable, deterministic order: by price then name then id.
        list.Sort((a, b) =>
        {
            int c = a.Part.Price.CompareTo(b.Part.Price);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Part.Name, b.Part.Name);
            if (c != 0) return c;
            return a.Part.PartId.CompareTo(b.Part.PartId);
        });
        return list;
    }

    public int OpenSlotCursor
    {
        get => _partCursor;
        private set => _partCursor = Math.Clamp(value, 0, OpenSlotParts.Count); // +1 reserved for the "(empty)" row
    }

    /// <summary>P755 — a slot part choice was made from the open-slot menu:
    /// <c>true</c> = a part was mounted, <c>false</c> = the slot was emptied
    /// (the native loadout menu plays mountSound / unmountSound here,
    /// load.cpp:1887/1889). Game1 maps these to SoundManager.PlayMountPart /
    /// PlayUnmountPart.</summary>
    public event Action<bool>? SlotPartChosen;

    /// <summary>P755 — a different hull was picked from the HULLS list, so the
    /// pending loadout was rebased onto its default loadout (the native hull-menu
    /// selection plays mountSound here, load.cpp:1480-1487). Game1 maps it to
    /// SoundManager.PlayMountPart.</summary>
    public event Action? HullChosen;

    /// <summary>P519 — choose the part under the open-slot cursor, filling the
    /// open slot in the pending loadout, then return to the slot list.</summary>
    public void ChoosePartInOpenSlot()
    {
        if (OpenSlot is not { } slot) return;
        // Last row is the "(make slot empty)" clear action (native "-Make Slot Empty-").
        if (_partCursor >= OpenSlotParts.Count)
        {
            ClearSlot(slot, notify: true);   // native unmountSound, load.cpp:1889
        }
        else
        {
            var offer = OpenSlotParts[_partCursor];
            FillSlot(slot, offer);
            SlotPartChosen?.Invoke(true);    // native mountSound, load.cpp:1887
        }
        CloseSlot();
        BlockReason = null;
    }

    /// <summary>P519 — fill a slot with a part in the pending loadout.</summary>
    private void FillSlot(SlotDef slot, SlotPartOffer offer)
    {
        if (slot.IsWeapon)
            _chosenParts.RemoveAll(p => (LookupPart(p.PartId) is { } pt && pt.EquipmentType == EquipmentType.Weapon) && p.MountId == slot.Mount);
        else
            _chosenParts.RemoveAll(p => LookupPart(p.PartId) is { } pt && pt.EquipmentType == slot.Type);
        _chosenParts.Add(new ExpandedPartData(offer.Part.PartId, offer.Amount, slot.Mount, ExpandedPartData.FullyMounted));
        NormalizeWeaponMounts();
    }

    /// <summary>P519 — empty a slot (remove its part from the pending loadout).</summary>
    /// <param name="notify">P1010 — raise <see cref="SlotPartChosen"/>(false) for the
    /// native <c>unmountSound</c> (<c>WinTrek/load.cpp:1889</c>). The old screen's
    /// open-slot menu passes <c>true</c> through
    /// <see cref="ChoosePartInOpenSlot"/>; the Loadout V2 clear action does the
    /// same. Default <c>false</c> keeps the pre-P1010 silent overload for every
    /// existing caller.</param>
    public void ClearSlot(SlotDef slot, bool notify = false)
    {
        if (slot.IsWeapon)
            _chosenParts.RemoveAll(p => (LookupPart(p.PartId) is { } pt && pt.EquipmentType == EquipmentType.Weapon) && p.MountId == slot.Mount);
        else
            _chosenParts.RemoveAll(p => LookupPart(p.PartId) is { } pt && pt.EquipmentType == slot.Type);
        NormalizeWeaponMounts();
        if (notify) SlotPartChosen?.Invoke(false);
    }

    /// <summary>
    /// P1010 — mount an offered part into an explicit slot (the Loadout V2
    /// inline picker), instead of the tab/cursor walk of
    /// <see cref="ChoosePartInOpenSlot"/>. Same rule and same effect: only an
    /// offer from <see cref="SlotOffers"/>(<paramref name="slot"/>) is accepted
    /// (native <c>BuyPartMenu</c>: equipment-type equality + <c>CanMount(part,
    /// slotMount)</c>, <c>WinTrek/load.cpp:1734-1743</c>), the slot's existing
    /// part is replaced, non-weapons are pinned to mount 0 (P437), and
    /// <see cref="SlotPartChosen"/>(true) fires for the native
    /// <c>mountSound</c> (<c>WinTrek/load.cpp:1887</c>).
    /// </summary>
    /// <returns>True when the part was mounted.</returns>
    public bool MountInSlot(SlotDef slot, SlotPartOffer offer)
    {
        bool fits = false;
        foreach (var o in SlotOffers(slot))
            if (o.Part.PartId == offer.Part.PartId) { fits = true; break; }
        if (!fits) return false;
        FillSlot(slot, offer);
        BlockReason = null;
        SlotPartChosen?.Invoke(true);
        return true;
    }

    /// <summary>P519 — leave the open-slot view, back to the slot list.</summary>
    public void CloseSlot()
    {
        OpenSlot = null;
        Page = ShopPage.SlotList;
    }

    /// <summary>P519 — back out of the current page: slot parts → slot list;
    /// slot list → hull list (PARTS tab head); hull list stays (consumed).</summary>
    public void Back()
    {
        if (Page == ShopPage.SlotParts) CloseSlot();
        else if (Page == ShopPage.SlotList) { SwitchTab(ShopTab.Hulls); }
    }

    /// <summary>P519 — switch to the given tab (HULLS or PARTS), resetting to its
    /// head page.</summary>
    public void SwitchTab(ShopTab tab)
    {
        Tab = tab;
        if (tab == ShopTab.Hulls)
        {
            Page = ShopPage.HullList;
            OpenSlot = null;
        }
        else
        {
            Page = ShopPage.SlotList;
            OpenSlot = null;
            SlotCursor = 0;
        }
    }

    public void ToggleTab() => SwitchTab(Tab == ShopTab.Hulls ? ShopTab.Parts : ShopTab.Hulls);

    // ── unified row navigation (rendered as rows; touch maps taps to MoveCursor
    //    then Activate, the same contract the old flat model had) ─────────────

    /// <summary>P519 — the page currently displayed (which set of rows).</summary>
    public enum ShopPage
    {
        /// <summary>HULLS tab: the buyable hull list.</summary>
        HullList,
        /// <summary>PARTS tab: the effective hull's slot list.</summary>
        SlotList,
        /// <summary>PARTS tab: the parts that fit the open slot (last row = make empty).</summary>
        SlotParts,
    }

    /// <summary>P519 — number of rows in the current page (for touch hit-testing).</summary>
    public int RowCount => Page switch
    {
        ShopPage.HullList => BuyableHulls.Count,
        ShopPage.SlotList => Slots.Count,
        ShopPage.SlotParts => OpenSlotParts.Count + 1, // +1 "(empty)"
        _ => 0,
    };

    /// <summary>P519 — the row cursor position in the current page.</summary>
    public int Cursor => Page switch
    {
        ShopPage.HullList => HullCursor,
        ShopPage.SlotList => SlotCursor,
        ShopPage.SlotParts => OpenSlotCursor,
        _ => 0,
    };

    /// <summary>P519 — move the current page's cursor by <paramref name="delta"/>.</summary>
    public void MoveCursor(int delta)
    {
        switch (Page)
        {
            case ShopPage.HullList: HullCursor += delta; break;
            case ShopPage.SlotList: SlotCursor += delta; break;
            case ShopPage.SlotParts: OpenSlotCursor += delta; break;
        }
    }

    /// <summary>P519 — activate the current page's cursor row (pick hull / open
    /// slot / choose part), the unified analogue of the old flat <c>Activate()</c>.</summary>
    public void Activate()
    {
        switch (Page)
        {
            case ShopPage.HullList: SelectHullAtCursor(); break;
            case ShopPage.SlotList: OpenSlotAtCursor(); break;
            case ShopPage.SlotParts: ChoosePartInOpenSlot(); break;
        }
    }

    // ── detail-panel focus (consumed by PartInfoLayout) ─────────────────────

    /// <summary>P519 — the hull the detail panel describes: the cursor hull on
    /// the HULLS list, else the effective hull.</summary>
    public DataHullTypeIGC? FocusHull => Page == ShopPage.HullList ? HullUnderCursor : EffectiveHull;

    /// <summary>P519 — the part the detail panel describes: the cursor part on an
    /// open-slot list; else the part mounted in the cursor slot (PARTS slot list).</summary>
    public DataPartTypeIGC? FocusPart
    {
        get
        {
            if (Page == ShopPage.SlotParts)
            {
                if (_partCursor < OpenSlotParts.Count) return OpenSlotParts[_partCursor].Part;
                return null; // the "(empty)" clear row
            }
            if (Page == ShopPage.SlotList && SlotUnderCursor is { } slot && PartInSlot(slot) is { } mounted)
                return LookupPart(mounted.PartId);
            return null;
        }
    }

    // ── construction + helpers ───────────────────────────────────────────────

    public ShopModel(ShopCatalog catalog, short currentHullId, IReadOnlyList<ExpandedPartData> currentParts,
        int? money = null)
    {
        _cat = catalog;
        _launchers = catalog.Launchers ?? Array.Empty<LauncherOffer>();
        CurrentHullId = currentHullId;
        CurrentParts = currentParts ?? Array.Empty<ExpandedPartData>();
        MyMoney = money;

        // P394 + P519 Bug 3 — seed the pending loadout verbatim from the ship's
        // real mounted parts (the P519 dock-sync ACK tail), preserving each
        // part's real Amount + FractionMounted + wire MountId, so relaunching
        // without touching the shop re-sends the FULL intended loadout (never 0,
        // which previously stripped the ship). Weapons included (judgement: the
        // shop reflects "here's what you have"; idempotent with MountCombatWeapon).
        foreach (var p in CurrentParts)
            _chosenParts.Add(p);
        NormalizeWeaponMounts();

        // Put the HULLS cursor on the current hull if it's in the buyable list.
        int idx = IndexOfHull(EffectiveHullId);
        if (idx >= 0) HullCursor = idx;
    }

    public ShopCatalog Catalog => _cat;

    /// <summary>
    /// P996 — the native client's <c>RestoreLoadout(station)</c> for a hangar
    /// opened while the local ship is BERTHED IN A LIFEPOD
    /// (<c>clintlib.h:1876-1886</c>). Native's handler chain is
    /// <c>appmsg.cpp:2022-2023</c> (`pod hull &amp;&amp; GetStation()` →
    /// <c>RestoreLoadout(station)</c>) → <c>RestoreLoadout(m_ship, pship, station,
    /// m_ship-&gt;GetValue() + GetMoney())</c>, whose pod/no-matching-loadout branch
    /// is `ReplaceLoadout(sink, pstation, NULL, budget, m_loadouts)`
    /// (<c>clintlib.h:1631-1633</c>): take the head of the cached loadout list —
    /// the ship the player last flew — upgrade the hull to
    /// <c>pstation-&gt;GetSuccessor(hull)</c>, clone every part the new hull can mount
    /// (<c>clintlib.h:1806-1848</c>), and — when the hull was upgraded — fill the
    /// remaining slots with its default parts (<c>TryToBuyParts</c>,
    /// <c>clintlib.h:1341-1368</c>). If the saved hull is unaffordable/unbuyable
    /// native falls back to the first hull in core order that the station can buy
    /// within budget, with its default loadout (<c>clintlib.h:1850-1868</c>).
    /// <para>Native then SENDS that restored loadout to the server with
    /// <c>BuyLoadout(pship, false)</c> (<c>clintlib.h:1883</c>), so the docked ship
    /// really becomes the real hull — the port reaches the same end state because
    /// the shop's Launch/Buy both send <see cref="EffectiveHullId"/> +
    /// <see cref="ChosenParts"/> (<c>Game1.PurchaseHangarLoadout</c>), and the
    /// server's own P488 successor upgrade then applies to hull and parts.</para>
    /// The port's shop carries the mounted loadout only (cargo is the existing
    /// shop gap, not a P996 decision); cargo parts are therefore not replayed.
    /// </summary>
    /// <param name="savedHullId">The last real hull the local player flew
    /// (<see cref="Allegiance.ClientCore.State.ClientWorld.OwnSavedLoadout"/>).</param>
    /// <param name="savedParts">That hull's mounted parts.</param>
    /// <returns>True when a real (non-pod) hull is now the selection.</returns>
    public bool RestoreSavedLoadout(short savedHullId, IReadOnlyList<ExpandedPartData> savedParts)
    {
        // Budget = m_ship->GetValue() + GetMoney() (clintlib.h:1880). A berthed pod
        // is worth nothing (price 0), so the wallet is the budget; when the wallet
        // is not known yet, stay permissive and let the server's funds gate decide
        // (native never has that uncertainty — it always has GetMoney()).
        long budget = MyMoney ?? long.MaxValue;

        short? hullId = BuyableHullOrDefault(savedHullId);
        if (hullId is { } candidate && HullPrice(candidate) <= budget)
        {
            SelectRestoredHull(candidate, savedParts, upgradedFrom: candidate != savedHullId);
            return true;
        }

        // Native fallback (clintlib.h:1850-1868): the first hull in core order the
        // station can buy within budget, then BuyDefaultLoadout.
        foreach (var hull in BuyableHulls)
        {
            if (hull.Price > budget) continue;
            SelectRestoredHull(hull.HullId, savedParts: null, upgradedFrom: true);
            return true;
        }
        return false;
    }

    /// <summary>P996 — apply one restored selection: the hull (if it differs from
    /// the berthed pod) plus either the saved parts that fit it or, when there is
    /// no saved list for that hull (fallback/default hull), its native default
    /// loadout. Native's clone loop mounts only the saved parts
    /// <c>phtSuccessor-&gt;CanMount(ppt, mount)</c> accepts
    /// (<c>clintlib.h:1810-1812</c>) and then fills the remaining slots when the
    /// hull was upgraded (<c>:1620-1624</c>).</summary>
    private void SelectRestoredHull(short hullId, IReadOnlyList<ExpandedPartData>? savedParts, bool upgradedFrom)
    {
        ChosenHullId = hullId == CurrentHullId ? null : hullId;
        HullCursor = IndexOfHull(hullId);
        _chosenParts.Clear();
        if (EffectiveHull is not { } eff)
        {
            NormalizeWeaponMounts();
            return;
        }
        if (savedParts is null)
        {
            foreach (var p in DefaultLoadoutFor(eff)) _chosenParts.Add(p);
        }
        else
        {
            foreach (var p in savedParts)
            {
                if (LookupPart(p.PartId) is not { } part) continue;      // not offered here
                if (!eff.CanMountPart(part)) continue;                   // native CanMount clone gate
                _chosenParts.Add(p);
            }
            // Native TryToBuyParts runs only when the hull was upgraded
            // (`if (pcl->pht != pht)`, clintlib.h:1620): keep the cloned parts and
            // fill the slots the NEW hull leaves empty.
            if (upgradedFrom) FillEmptySlotsWithDefaults(eff);
        }
        NormalizeWeaponMounts();
        SlotCursor = 0;
        BlockReason = null;
    }

    /// <summary>P996 — append <see cref="DefaultLoadoutFor"/> parts for every slot
    /// the pending loadout still leaves empty (native <c>TryToBuyParts</c>,
    /// <c>clintlib.h:1341-1368</c>: it only mounts where
    /// <c>GetMountedPart(et, i) == NULL</c>).</summary>
    private void FillEmptySlotsWithDefaults(in DataHullTypeIGC hull)
    {
        foreach (var d in DefaultLoadoutFor(hull))
        {
            if (LookupPart(d.PartId) is not { } part) continue;
            bool occupied = _chosenParts.Any(p =>
                LookupPart(p.PartId) is { } have &&
                (part.EquipmentType == EquipmentType.Weapon
                    ? have.EquipmentType == EquipmentType.Weapon && p.MountId == d.MountId
                    : have.EquipmentType == part.EquipmentType && p.MountId == 0));
            if (!occupied) _chosenParts.Add(d);
        }
    }

    /// <summary>P996 — walk a saved hull's successor chain to the furthest hull the
    /// docked station actually offers, native <c>pstation-&gt;GetSuccessor(pht)</c>
    /// (<c>stationIGC.h:399-440</c>: <c>pSuccessor = pht</c>, then follow
    /// <c>GetSuccessorHullType()</c> while <c>CanBuy(pNext)</c>). The catalogue's
    /// <see cref="BuyableHulls"/> is already the CanBuy∧!IsObsolete set, so
    /// membership is the CanBuy test. Null when neither the hull nor any successor
    /// is offered.</summary>
    private short? BuyableHullOrDefault(short rawHullId)
    {
        short? best = InHullCatalog(rawHullId) ? rawHullId : null;
        short cur = rawHullId;
        for (int guard = 0; guard < 64; guard++)
        {
            if (!TryCoreHull(cur, out var hull) || hull.SuccessorHullId < 0 || hull.SuccessorHullId == cur) break;
            cur = hull.SuccessorHullId;
            if (InHullCatalog(cur)) best = cur;
        }
        return best;
    }

    /// <summary>P996 — whether the docked station offers this hull (it is in the
    /// already tech-filtered catalogue the shop shows).</summary>
    private bool InHullCatalog(short hullId)
    {
        foreach (var h in _cat.Hulls) if (h.HullId == hullId) return true;
        return false;
    }

    /// <summary>P996 — a hull's core record for successor-chain stepping (the shop's
    /// filtered list cannot link an un-offered hull to its successor).</summary>
    private bool TryCoreHull(short hullId, out DataHullTypeIGC hull)
    {
        if (_cat.Core?.HullTypes is { } hulls && hulls.TryGetValue(hullId, out hull)) return true;
        hull = default;
        return false;
    }

    private long HullPrice(short hullId)
    {
        if (TryCoreHull(hullId, out var hull)) return hull.Price;
        foreach (var h in _cat.Hulls) if (h.HullId == hullId) return h.Price;
        return 0;
    }

    // Native load.cpp:1554-1565,1603-1606 refreshes offers on tech changes while the
    // player stays docked. Keep the pending loadout and navigation intact.
    public void RefreshOffers(ShopCatalog catalog, int? money)
    {
        _cat = catalog;
        _launchers = catalog.Launchers ?? Array.Empty<LauncherOffer>();
        MyMoney = money;
        HullCursor = _hullCursor;
        SlotCursor = _slotCursor;
    }

    public void RefreshMoney(int? money) => MyMoney = money;

    private void NormalizeWeaponMounts()
    {
        // P437 — per-equipment-type mounts: weapons keep their (hardpoint) mount
        // ids, every other type occupies mount 0. We preserve the wire/seeded
        // weapon mounts (they ARE hardpoint indices); non-weapons are pinned to 0.
        for (int i = 0; i < _chosenParts.Count; i++)
        {
            var p = _chosenParts[i];
            bool isWeapon = LookupPart(p.PartId) is { } pt && pt.EquipmentType == EquipmentType.Weapon;
            if (!isWeapon) _chosenParts[i] = p with { MountId = 0 };
        }
    }

    /// <summary>P571 — after a real hull swap, the pending loadout becomes the
    /// NEW hull's NATIVE default loadout — the preferred part types that mount
    /// (native <c>TryToBuyParts</c>, clintlib.h:1341-1368, which fills the
    /// empty slots with <c>GetPreferredPartTypes()</c>; <c>BuyHull</c> → <c>RefreshLoadout(customLoadouts[0],
    /// phullType)</c>, load.cpp:1478-1486). The player can still edit it in the
    /// shop. Keeping the old hull's parts verbatim (minus non-fitting ones)
    /// would ship a stripped hull whenever the docked ship had none — e.g. the
    /// respawned lifepod's bare chassis (the P571 "+0 parts" bug). Scope: only
    /// when a real swap is pending (P334).</summary>
    private void RebaseForEffectiveHull()
    {
        if (!WillSwapHull) return;
        if (EffectiveHull is not { } eff) return;
        _chosenParts.Clear();
        // P759 — native BuyHull refreshes the NEW hull from the saved custom
        // loadout slot 0 for that hull when one exists, else its native default
        // (RefreshLoadout(customLoadouts[0], phullType), load.cpp:1478-1486).
        IReadOnlyList<ExpandedPartData> seed =
            _customLoadouts.TryGetValue((eff.HullId, 0), out var saved)
                ? saved
                : DefaultLoadoutFor(eff);
        foreach (var p in seed)
            _chosenParts.Add(p);
        NormalizeWeaponMounts();
        // Keep the chosen slots consistent with the new hull (a filled slot that
        // vanished is just empty).
        SlotCursor = 0;
    }

    /// <summary>
    /// P759 — save the current pending loadout as the native custom-loadout slot 0
    /// for the effective hull (<c>SaveCustomLoadout(m_pship, 0)</c>, load.cpp:1521/
    /// 1646; <c>clintlib.h:1425-1428</c>). Idempotent; the next swap to this hull
    /// re-seeds from this list.
    /// </summary>
    public void SaveCustomLoadout()
    {
        SaveCustomLoadoutToSlot(0);
    }

    /// <summary>
    /// P764 — save the pending loadout into custom-loadout slot
    /// <paramref name="slot"/> for the effective hull (native
    /// <c>SaveCustomLoadout(m_pship, num)</c>, load.cpp:2232 +
    /// clintlib.h:1425-1432; <c>MAX_CUSTOM_LOADOUTS = 10</c>, clintlib.h:825).
    /// Native then persists the slot file (<c>SaveCustomLoadoutFile</c>) and
    /// cues only on success — the port's store is in-memory, so a save always
    /// succeeds and Game1 always cues (SIM-0325).
    /// </summary>
    public void SaveCustomLoadoutToSlot(int slot)
    {
        _customLoadouts[(EffectiveHullId, slot)] = new List<ExpandedPartData>(_chosenParts);
    }

    /// <summary>
    /// P764 — restore custom-loadout slot <paramref name="slot"/> as the
    /// pending loadout (native load.cpp:2236-2247:
    /// <c>RefreshLoadout(m_customLoadouts[num], GetBaseHullType())</c> +
    /// <c>UpdateHullType()</c>): the saved part list replaces the pending
    /// loadout wholesale.
    /// <b>P764b</b> — a slot that has never been saved restores the hull's
    /// DEFAULT loadout, not an empty hold: <c>RefreshLoadout</c> terminates
    /// every current part (load.cpp:1453-1466) and then hands the (possibly
    /// empty) cached list to <c>ReplaceLoadout</c>, which buys the hull's
    /// default loadout when there is no cached loadout link for that hull
    /// (clintlib.h:1796-1800 — <c>else BuyDefaultLoadout(...)</c>). Only a
    /// slot the player actually saved EMPTY restores empty. Game1 cues
    /// <c>mountSound</c> unconditionally afterwards (load.cpp:2247, SIM-0326).
    /// </summary>
    public void RestoreCustomLoadout(int slot)
    {
        _chosenParts.Clear();
        if (_customLoadouts.TryGetValue((EffectiveHullId, slot), out var saved))
            foreach (var p in saved)
                _chosenParts.Add(p);
        else if (EffectiveHull is { } eff)   // P764b — no cached loadout for the hull → the default (clintlib.h:1796-1800)
            foreach (var p in DefaultLoadoutFor(eff))
                _chosenParts.Add(p);
        NormalizeWeaponMounts();
        BlockReason = null;
    }

    /// <summary>
    /// P759 — the native <c>OnButtonDefault</c> action (<c>load.cpp:1639-1648</c>):
    /// replace the pending loadout with the hull's native default
    /// (<c>BuyDefaultLoadout</c>, clintlib.h:2110-2160) and save it back to the
    /// custom-loadout slot 0 (<c>SaveCustomLoadout(m_pship, 0)</c>).
    /// </summary>
    public void ApplyDefaultLoadout()
    {
        if (EffectiveHull is not { } eff) return;
        _chosenParts.Clear();
        foreach (var p in DefaultLoadoutFor(eff))
            _chosenParts.Add(p);
        NormalizeWeaponMounts();
        SaveCustomLoadout();
        BlockReason = null;
    }

    /// <summary>
    /// P1010 — the Loadout V2 "Revert" action: drop every pending edit and go
    /// back to the docked ship's real loadout. This is the state the shop opens
    /// in (<see cref="CurrentParts"/> seeded verbatim, the P394/P519 Bug-3 dock
    /// sync) with no hull swap pending, i.e. the native end state of
    /// <c>RestoreLoadout</c> for the ship's OWN cached loadout
    /// (<c>clintlib.h:1824-1844</c> clones exactly the parts the hull can mount
    /// from the cached list, and <c>BuyLoadout(pship, false)</c> re-sends it at
    /// <c>:1883</c>). No new rule: it is the constructor's seeding applied again
    /// after edits.
    /// </summary>
    public void RevertToCurrentLoadout()
    {
        ChosenHullId = null;
        _chosenParts.Clear();
        foreach (var p in CurrentParts)
            _chosenParts.Add(p);
        NormalizeWeaponMounts();
        HullCursor = IndexOfHull(EffectiveHullId);
        SwitchTab(Tab);          // keep the page consistent with the active tab
        BlockReason = null;
    }

    /// <summary>P1010 — a part's core record as the shop knows it (the filtered
    /// catalogue plus the real launcher table), or null when the station does not
    /// offer it. The Loadout V2 rows/totals resolve part names and types through
    /// this, never through a second catalogue copy.</summary>
    public DataPartTypeIGC? PartById(short partId) => LookupPart(partId);

    /// <summary>P571 — the hull's NATIVE default loadout as a pending part list:
    /// walk <see cref="DataHullTypeIGC.PreferredPartsTypes"/> in order and mount
    /// each part that fits (native <c>TryToBuyParts</c>, clintlib.h:1341-1368:
    /// <c>if (GetMountedPart(et,i)==NULL) &amp;&amp; pht-&gt;CanMount(ppt,i)</c> —
    /// a weapon is mounted into EVERY empty hardpoint that accepts it
    /// (<c>iMountMax = GetMaxWeapons()</c>), every other equipment type into its
    /// single socket (<c>iMountMax = 1</c>)). Preferred parts that don't mount on
    /// the hull (P555 masks — e.g. the gunship's Prox Mine) are skipped, exactly
    /// as native's <c>CanMount</c> does.</summary>
    private List<ExpandedPartData> DefaultLoadoutFor(in DataHullTypeIGC hull)
    {
        var list = new List<ExpandedPartData>();
        var taken = new HashSet<(EquipmentType, int)>();
        foreach (var rawPartId in hull.PreferredPartsTypes)
        {
            if (rawPartId == -1) break;
            // Resolve the preferred part to the actual part the shop would mount:
            // the raw preferred part when the shop offers it (the task's expected
            // defaults — Sm Shield 1 / PW Gat Gun 1 / MRM Seeker 1 / Counter 1),
            // else its highest OFFERED successor (native TryToBuyParts →
            // pstation->GetSuccessor(ppt), clintlib.h:1357 + stationIGC.h:399-435:
            // a preferred part the station has out-teched is upgraded to the
            // buyable successor). Without this, the allTech/testUnlock debug maps
            // cull every entry-tier preferred part as obsolete and the default is
            // spuriously EMPTY.
            if (BuyablePartOrDefault(rawPartId) is not { } partId) continue;
            if (LookupPart(partId) is not { } part) continue;
            if (!hull.CanMountPart(part)) continue;
            if (part.EquipmentType == EquipmentType.Weapon)
            {
                for (int m = 0; m < hull.Hardpoints.Length; m++)
                {
                    if ((part.PartMask & ~hull.Hardpoints[m].PartMask) != 0) continue;
                    if (!taken.Add((EquipmentType.Weapon, m))) continue;
                    list.Add(new ExpandedPartData(partId, DefaultAmmoFor(partId, part), (byte)m, ExpandedPartData.FullyMounted));
                }
            }
            else
            {
                if (!taken.Add((part.EquipmentType, 0))) continue;
                list.Add(new ExpandedPartData(partId, DefaultAmmoFor(partId, part), 0, ExpandedPartData.FullyMounted));
            }
        }
        return list;
    }

    /// <summary>P571 — resolve a preferred part id to the part the shop would
    /// mount for the default loadout: the raw preferred part when the station's
    /// (filtered) catalogue offers it, else the highest part in its successor
    /// chain that IS offered (native <c>pstation-&gt;GetSuccessor</c> walked to the
    /// highest buyable successor, stationIGC.h:399-435). Returns null when
    /// neither the preferred part nor any successor is offered.</summary>
    private short? BuyablePartOrDefault(short rawPartId)
    {
        if (InCatalog(rawPartId)) return rawPartId;
        short? best = null;
        short? cur = rawPartId;
        int guard = 0;
        while (cur is { } id && id >= 0 && ++guard <= 32768)
        {
            if (InCatalog(id)) best = id;
            if (!TryCorePart(id, out var part) || part.SuccessorPartId < 0 || part.SuccessorPartId == id) break;
            cur = part.SuccessorPartId;
        }
        return best;
    }

    /// <summary>Whether a part id is present in the shop's (filtered) catalogue —
    /// the station actually offers it (native's <c>CanBuy</c> for the shop view).</summary>
    private bool InCatalog(short partId)
    {
        foreach (var p in _cat.Parts) if (p.PartId == partId) return true;
        foreach (var l in _launchers) if (l.Part.PartId == partId) return true;
        return false;
    }

    /// <summary>Look up a part's core record (parts OR expendable launcher tables)
    /// for successor-chain stepping — the same dual-table resolve the core's
    /// <see cref="Allegiance.Core.Model.SuccessorUpgrade"/> uses.</summary>
    private bool TryCorePart(short id, out DataPartTypeIGC part)
    {
        if (_cat.Core?.PartTypes is { } pts && pts.TryGetValue(id, out part)) return true;
        part = default;
        return _cat.Core is not null && _cat.Core.TryBuildExpendableLauncher(id, out part);
    }

    /// <summary>P571 — the ammo a default-loadout part carries: a launcher's real
    /// reserve (the shop's <see cref="LauncherOffer.Ammo"/>), else the standard
    /// toggle amount (<see cref="DefaultAmount"/>: weapons get a real reserve,
    /// one-off mounts get 1).</summary>
    private short DefaultAmmoFor(short partId, in DataPartTypeIGC part)
    {
        if (_launchers is { } ls)
            foreach (var l in ls)
                if (l.Part.PartId == partId) return l.Ammo;
        return DefaultAmount(part);
    }

    private int IndexOfHull(short hullId)
    {
        for (int i = 0; i < BuyableHulls.Count; i++)
            if (BuyableHulls[i].HullId == hullId) return i;
        return 0;
    }

    private bool IsPartChosen(short partId)
    {
        foreach (var p in _chosenParts)
            if (p.PartId == partId) return true;
        return false;
    }

    private DataPartTypeIGC? LookupPart(short partId)
    {
        foreach (var p in _cat.Parts)
            if (p.PartId == partId) return p;
        foreach (var l in _launchers)
            if (l.Part.PartId == partId) return l.Part;
        return null;
    }

    /// <summary>P519 — whether <paramref name="part"/> fits <paramref name="slot"/>
    /// of <paramref name="hull"/>: equipment-type equality + mountability at that
    /// specific mount (a weapon must fit the hardpoint's part mask; an equipment
    /// part must fit the hull's <c>PmEquipment[type]</c> socket).</summary>
    private static bool PartFitsSlot(in DataHullTypeIGC hull, SlotDef slot, in DataPartTypeIGC part)
    {
        if (part.EquipmentType != slot.Type) return false;
        if (slot.IsWeapon)
        {
            if (slot.Mount >= hull.Hardpoints.Length) return false;
            return (part.PartMask & ~hull.Hardpoints[slot.Mount].PartMask) == 0;
        }
        int idx = (int)slot.Type;
        if (idx >= hull.PmEquipment.Length || hull.PmEquipment[idx] == 0) return false;
        return (part.PartMask & ~hull.PmEquipment[idx]) == 0;
    }

    /// <summary>A sensible stock amount for a newly toggled part (weapons get a
    /// real ammo reserve; one-off mounts get 1). Launchers carry their real ammo
    /// (from <see cref="LauncherOffer.Ammo"/>).</summary>
    public static short DefaultAmount(in DataPartTypeIGC part) =>
        part.EquipmentType == EquipmentType.Weapon ? (short)20 : (short)1;
}

/// <summary>P519 — one part offered in an open slot: the real part plus the ammo
/// to grant when it's mounted (the launcher's real reserve when <see cref="IsLauncher"/>).</summary>
public readonly record struct SlotPartOffer(DataPartTypeIGC Part, short Amount, bool IsLauncher);
