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
    public IReadOnlyList<SlotPartOffer> OpenSlotParts
    {
        get
        {
            if (OpenSlot is not { } slot) return Array.Empty<SlotPartOffer>();
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
            ClearSlot(slot);
            SlotPartChosen?.Invoke(false);   // native unmountSound, load.cpp:1889
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
    public void ClearSlot(SlotDef slot)
    {
        if (slot.IsWeapon)
            _chosenParts.RemoveAll(p => (LookupPart(p.PartId) is { } pt && pt.EquipmentType == EquipmentType.Weapon) && p.MountId == slot.Mount);
        else
            _chosenParts.RemoveAll(p => LookupPart(p.PartId) is { } pt && pt.EquipmentType == slot.Type);
        NormalizeWeaponMounts();
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
        foreach (var p in DefaultLoadoutFor(eff))
            _chosenParts.Add(p);
        NormalizeWeaponMounts();
        // Keep the chosen slots consistent with the new hull (a filled slot that
        // vanished is just empty).
        SlotCursor = 0;
    }

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
