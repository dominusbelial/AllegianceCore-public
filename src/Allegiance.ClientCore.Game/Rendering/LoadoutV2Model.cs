using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.StaticCoreText;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>P1010 — whether a Loadout V2 hull row can be bought right now.</summary>
public enum HullRowStatus
{
    /// <summary>The station offers it: native <c>GroupID &gt;= 0 &amp;&amp;
    /// pstation-&gt;CanBuy(pht) &amp;&amp; !pstation-&gt;IsObsolete(pht)</c>
    /// (<c>WinTrek/load.cpp:1603-1606</c>).</summary>
    Buyable,

    /// <summary>Not offered yet, but inside the side's reachable tech tree (a
    /// future unlock). <see cref="LoadoutV2HullRow.LockReason"/> says why.</summary>
    Locked,
}

/// <summary>
/// P1010 — one row of the Loadout V2 hull column. Everything is display data
/// from real core records (name, class, price), never a raw id.
/// </summary>
/// <param name="HullId">The core hull id (wiring/selection only — never drawn).</param>
/// <param name="Name">The hull's core name (e.g. <c>Enh Fighter</c>).</param>
/// <param name="Class">The hull's armour class name from the core's defence
/// constant table — <c>ConstantsDamageComment(defenseType, -1)</c>, the port's
/// transcription of native <c>ConstantsDamageComment</c>
/// (<c>Igc/missionigc.cpp:4246-4299</c>): <c>Light Ship</c> / <c>Medium Ship</c>
/// / <c>Heavy Ship</c> / <c>Super Heavy Ship</c> / <c>Utility Ship</c>. Native's
/// hangar rows show name + price only (<c>GetBuyableName</c>,
/// <c>load.cpp:18-37</c>); the class is the V2 column's grouping, taken from
/// the hull's real <c>defenseType</c> (<c>Igc/igc.h:1795</c>) rather than
/// invented from the name.</param>
/// <param name="Price">The hull price (<c>IbuyableIGC::GetPrice</c>, the same
/// value the station charges — <c>load.cpp:18-34</c>).</param>
/// <param name="Status">Buyable / Locked (native's hull-list predicate).</param>
/// <param name="LockReason">Human reason for <see cref="HullRowStatus.Locked"/>,
/// null when buyable.</param>
/// <param name="IsCurrent">True for the hull the docked ship really has
/// (<c>ShopModel.CurrentHullId</c>).</param>
/// <param name="IsChosen">True for the effective hull the screen will send
/// (<c>ShopModel.EffectiveHullId</c>).</param>
/// <param name="Affordable">False when the switch to this hull costs more than
/// the wallet — <c>ShopModel.HullRowGreyed</c>, the port's row grey-out
/// (<c>load.cpp:2139-2182</c> money gate; native hull rows are not tech-gated
/// twice).</param>
public readonly record struct LoadoutV2HullRow(
    short HullId,
    string Name,
    string Class,
    int Price,
    HullRowStatus Status,
    string? LockReason,
    bool IsCurrent,
    bool IsChosen,
    bool Affordable)
{
    public bool IsBuyable => Status == HullRowStatus.Buyable;
}

/// <summary>
/// P1010 — one slot row of the Loadout V2 ship column for the effective hull:
/// one row per mount the hull really has (native <c>Slot::SlotValidForHull</c>,
/// <c>WinTrek/load.cpp:112-122</c>, applied to the authored slot table at
/// <c>:2023-2044</c>).
/// </summary>
/// <param name="Slot">The slot (equipment type + hardpoint/mount + turret flag)
/// exactly as <see cref="ShopModel"/> addresses it, so every mutation goes
/// through the one model.</param>
/// <param name="Label">Display label. Fixed weapons are <c>WEAPON n</c>
/// (mount + 1); turrets are numbered among turrets (<c>TURRET n</c>,
/// <c>n = mount - MaxFixedWeapons + 1</c> — native's turret mount is
/// <c>m_mount + GetMaxFixedWeapons()</c>, <c>load.cpp:104-110</c>); other
/// sockets show their equipment-type name.</param>
/// <param name="IsTurret">True for a gunner-manned turret hardpoint (native
/// <c>ET_Turret</c> slots, <c>load.cpp:118-119</c>).</param>
/// <param name="Mount">The hardpoint index = native <c>Slot::GetMount()</c>
/// (<c>load.cpp:104-110</c>) for weapons/turrets, 0 for single sockets.</param>
/// <param name="ChosenPartId">The part in the pending loadout for this slot, or
/// null when empty.</param>
/// <param name="ChosenPartName">That part's core name.</param>
/// <param name="Amount">The mounted amount (native draws the expendable count
/// in the slot, <c>load.cpp:208-222</c>).</param>
/// <param name="CurrentPartName">The part the docked ship really has in this
/// slot (<c>ShopModel.CurrentParts</c>), or null.</param>
/// <param name="GunnerName">Turret rows: the seated gunner's name, or null when
/// the turret is open. Native draws the gunner of that turret, else
/// <c>"&lt;open&gt;"</c> (<c>load.cpp:176-205</c>).</param>
public readonly record struct LoadoutV2SlotRow(
    SlotDef Slot,
    string Label,
    bool IsTurret,
    byte Mount,
    short? ChosenPartId,
    string? ChosenPartName,
    short? Amount,
    string? CurrentPartName,
    string? GunnerName)
{
    public bool Filled => ChosenPartId is not null;
}

/// <summary>
/// P1010 — one part offered for a slot in the Loadout V2 inline picker: a part
/// that fits the slot (native <c>LoadoutImpl::CanMount</c> +
/// <c>BuyPartMenu</c>'s per-mount mask, <c>WinTrek/load.cpp:1656-1678,
/// 1714-1743</c>) and that the station's tech-filtered catalogue offers
/// (<c>pstation-&gt;CanBuy</c>, <c>Igc/stationIGC.h:383-391</c>), with its price
/// (<c>GetBuyableName</c>'s <c>$(price)</c>, <c>load.cpp:18-34</c>).
/// <para><b>No stats here</b> — P1011 owns the stat sheet; <see cref="Part"/>
/// carries the whole core record so that model can derive them.</para>
/// </summary>
/// <param name="Part">The real core part record (type, masks, stats).</param>
/// <param name="Name">The part's core name.</param>
/// <param name="Price">The part price.</param>
/// <param name="Amount">The amount mounted when chosen (a launcher carries its
/// real reserve, <see cref="SlotPartOffer.Amount"/>).</param>
/// <param name="IsLauncher">True for a real launcher/dispenser offer built from
/// the core's launcher table.</param>
/// <param name="AlreadyMounted">True when the DOCKED ship already carries this
/// part in this slot (nothing is spent on it).</param>
/// <param name="IsChosen">True when the pending loadout has it in this slot.</param>
public readonly record struct LoadoutV2Offer(
    DataPartTypeIGC Part,
    string Name,
    int Price,
    short Amount,
    bool IsLauncher,
    bool AlreadyMounted,
    bool IsChosen)
{
    public short PartId => Part.PartId;
}

/// <summary>
/// P1010 — the pure view-model the Loadout V2 screen renders
/// (<c>visuals.hangar.loadoutV2</c>, P1014). One screen: hulls | ship | slots.
///
/// <para><b>It wraps <see cref="ShopModel"/> and never re-implements a rule.</b>
/// Every mutation — select a hull, mount a part, clear a slot, apply the native
/// default, revert — calls the same <see cref="ShopModel"/> member the old
/// hull/slot/parts screen calls, and every total (price, affordability,
/// <see cref="ShopModel.BlockReason"/>, buy shortfall) is read straight off
/// that model, so the two screens can never disagree about money or legality.
/// The model adds only what the V2 presentation needs and the old screen has no
/// use for: a slot's offers without opening it
/// (<see cref="ShopModel.SlotOffers"/>), a hull picked by id
/// (<see cref="ShopModel.SelectHull"/>), an explicit-slot mount
/// (<see cref="ShopModel.MountInSlot"/>) and the docked-loadout revert
/// (<see cref="ShopModel.RevertToCurrentLoadout"/>).</para>
///
/// <para><b>Native rules, cited</b>
/// <list type="bullet">
/// <item>Hull list order = the station's buyable hulls, exactly what
/// <see cref="ShopModel.CycleHull"/> cycles (native <c>GetNextHullType</c>,
/// <c>WinTrek/load.cpp:1568-1636</c>; predicate <c>:1603-1606</c>).</item>
/// <item>Locked hulls = the native predicate's missing half: <c>GroupID &gt;= 0</c>,
/// not obsolete (<c>pstation-&gt;IsObsolete</c>, <c>Igc/stationIGC.h:393-397</c>),
/// not buyable by the current <c>ttbmLocal | side techs</c>
/// (<c>Igc/stationIGC.h:383-391</c>), yet inside the side's reachable tech tree
/// (<c>Igc/sideigc.cpp:119-206</c>, the port's
/// <see cref="BucketCatalog.ReachableTechs"/>) — the hulls the side could unlock. A
/// hull another civilization owns never appears: its required bits are outside
/// this side's tree.</item>
/// <item>Slot rows per mount: native <c>Slot::SlotValidForHull</c>
/// (<c>load.cpp:112-122</c>) over the authored slot table
/// (<c>:2023-2044</c>; the retail table is <c>defaultloadout.mdl</c>/<c>loadout.mdl:31-54</c>:
/// weapons 0-3, turrets 0-3, magazine/afterburner/shield/cloak/dispenser/chaff).</item>
/// <item>Offers: <see cref="ShopModel.SlotOffers"/> (native <c>CanMount</c> +
/// <c>BuyPartMenu</c>, <c>load.cpp:1656-1678,1714-1743</c>).</item>
/// <item>Default: native <c>OnButtonDefault</c> (<c>load.cpp:1639-1648</c>),
/// already ported by <see cref="ShopModel.ApplyDefaultLoadout"/>.</item>
/// <item>Revert: back to the docked ship's loadout, the state the shop opens in
/// (<see cref="ShopModel.RevertToCurrentLoadout"/>).</item>
/// </list></para>
///
/// <para>Presentation-only additions (not native rules): the hull <c>Class</c>
/// column, the per-row "already mounted" marker and the turret gunner name the
/// renderer may show. No <c>GraphicsDevice</c> here — pure and headless-testable
/// like <see cref="ScoreboardLayout"/>.</para>
/// </summary>
public sealed class LoadoutV2Model
{
    /// <summary>The equipment sockets a hull can have, in native
    /// <see cref="EquipmentType"/> order (<c>Igc/igc.h:530-538</c>), minus
    /// <see cref="EquipmentType.Weapon"/> (weapons are per hardpoint). Each is a
    /// single mount-0 socket, valid only when the hull has a non-zero
    /// <c>PmEquipment[type]</c> mask (native <c>SlotValidForHull</c> else-branch,
    /// <c>load.cpp:120-121</c>).</summary>
    private static readonly EquipmentType[] SocketTypes =
    [
        EquipmentType.ChaffLauncher,
        EquipmentType.Magazine,
        EquipmentType.Dispenser,
        EquipmentType.Shield,
        EquipmentType.Cloak,
        EquipmentType.Pack,
        EquipmentType.Afterburner,
    ];

    private readonly ShopModel _shop;
    private readonly IReadOnlyList<DataHullTypeIGC> _hullUniverse;
    private readonly byte[]? _stationLocal;
    private readonly TechTreeBitMask _sideTechs;
    private readonly TechTreeBitMask? _unlockableTechs;
    private readonly Func<int, string?>? _turretGunner;

    private SlotDef? _openSlot;
    private string? _statusReason;
    private List<DataHullTypeIGC>? _lockedHulls;

    /// <param name="shop">The one shop model this screen drives (never copied).</param>
    /// <param name="hullUniverse">Every hull record the screen may consider for
    /// locked rows (normally the core's whole hull table). Null = the shop's own
    /// (already tech-filtered) catalogue, i.e. no locked rows can be discovered.</param>
    /// <param name="unlockableTechs">The side's reachable tech tree
    /// (<see cref="BucketCatalog.ReachableTechs"/> over the core + the side's current
    /// development techs). Null = no locked rows (the caller has no tech-tree
    /// knowledge; the hull column then shows only what the station offers).</param>
    /// <param name="stationLocal">The docked station type's <c>TtbmLocal</c>.</param>
    /// <param name="sideTechs">The local side's live development+building techs.</param>
    /// <param name="turretGunner">Optional resolver: hardpoint mount index → the
    /// seated gunner's name (null when open). Native draws
    /// <c>GetGunner(GetMount())</c> for turret slots (<c>load.cpp:176-205</c>);
    /// the caller supplies the live seating (e.g. from the ship entities'
    /// <c>ParentId</c>/<c>BoardTurretId</c>, see
    /// <c>HudInventoryPaneBuilder.SourceShipOccupants</c>).</param>
    public LoadoutV2Model(
        ShopModel shop,
        IReadOnlyList<DataHullTypeIGC>? hullUniverse = null,
        TechTreeBitMask? unlockableTechs = null,
        byte[]? stationLocal = null,
        TechTreeBitMask? sideTechs = null,
        Func<int, string?>? turretGunner = null)
    {
        _shop = shop ?? throw new ArgumentNullException(nameof(shop));
        _hullUniverse = hullUniverse ?? shop.Catalog.Hulls;
        _unlockableTechs = unlockableTechs;
        _stationLocal = stationLocal;
        _sideTechs = sideTechs ?? TechTreeBitMask.Empty;
        _turretGunner = turretGunner;
    }

    /// <summary>The shop model this view-model wraps (the single rule set).</summary>
    public ShopModel Shop => _shop;

    // ── hull column ──────────────────────────────────────────────────────────

    /// <summary>
    /// P1010 — the hull column: the station's buyable hulls first, in native
    /// <c>GetNextHullType</c> order (<c>WinTrek/load.cpp:1568-1636</c>, the order
    /// <see cref="ShopModel.CycleHull"/> walks), then the hulls the side could
    /// unlock, ordered by hull id for a stable list. The current hull is marked.
    /// </summary>
    public IReadOnlyList<LoadoutV2HullRow> HullRows
    {
        get
        {
            var rows = new List<LoadoutV2HullRow>();
            foreach (var hull in _shop.BuyableHulls)
                rows.Add(Row(hull, HullRowStatus.Buyable, reason: null));
            foreach (var hull in LockedHulls)
                rows.Add(Row(hull, HullRowStatus.Locked, LockReasonFor(hull)));
            return rows;
        }
    }

    /// <summary>P1010 — the not-yet-buyable hulls inside the side's reachable tech
    /// tree (cached; <see cref="Refresh"/> recomputes it).</summary>
    public IReadOnlyList<DataHullTypeIGC> LockedHulls => _lockedHulls ??= ComputeLockedHulls();

    /// <summary>P1010 — recompute the hull column after a live tech/money change
    /// (the docked P519 refresh path, native <c>load.cpp:1554-1565,1603-1606</c>).</summary>
    public void Refresh() => _lockedHulls = null;

    private List<DataHullTypeIGC> ComputeLockedHulls()
    {
        var list = new List<DataHullTypeIGC>();
        if (_unlockableTechs is not { } reachable) return list;
        var core = _shop.Catalog.Core;
        foreach (var hull in _hullUniverse)
        {
            if (hull.GroupId < 0) continue;                       // drone/utility hull: native never lists it (load.cpp:1604)
            if (_shop.BuyableHulls.Any(h => h.HullId == hull.HullId)) continue;
            if (TechTreeBitMask.RequiredTechsBuyable(hull.TtbmRequired, _stationLocal, _sideTechs))
                continue;                                          // already buyable — it is an offered row
            if (core is not null && SuccessorUpgrade.HullIsObsolete(core, hull, _stationLocal, _sideTechs))
                continue;                                          // out-teched: native hides it (load.cpp:1606)
            if (!TechTreeBitMask.RequiredTechsBuyable(hull.TtbmRequired, null, reachable))
                continue;                                          // outside this side's tree — it could never unlock it
            list.Add(hull);
        }
        list.Sort((a, b) => a.HullId.CompareTo(b.HullId));
        return list;
    }

    private LoadoutV2HullRow Row(DataHullTypeIGC hull, HullRowStatus status, string? reason) =>
        new(
            HullId: hull.HullId,
            Name: hull.Name,
            Class: HullClass(hull),
            Price: hull.Price,
            Status: status,
            LockReason: reason,
            IsCurrent: hull.HullId == _shop.CurrentHullId,
            IsChosen: hull.HullId == _shop.EffectiveHullId,
            Affordable: !_shop.HullRowGreyed(hull));

    /// <summary>
    /// P1010 — the hull's armour class name from the core's defence constants
    /// (native <c>ConstantsDamageComment</c>, <c>Igc/missionigc.cpp:4246-4299</c>,
    /// ported once in <see cref="CoreTable"/>): <c>Light Ship</c>,
    /// <c>Medium Ship</c>, <c>Heavy Ship</c>, <c>Super Heavy Ship</c>,
    /// <c>Utility Ship</c>.
    /// </summary>
    public static string HullClass(in DataHullTypeIGC hull) =>
        CoreTable.ConstantsDamageComment(hull.DefenseType, -1);

    /// <summary>
    /// P1010 — why a hull the side could unlock is locked right now: the tech
    /// bits of its <c>TtbmRequired</c> that neither the docked station's local
    /// techs nor the side's techs provide, each named by the real source that
    /// grants it — a researchable development, a station type whose LOCAL techs
    /// carry the bit (native <c>CanBuy</c> compares against
    /// <c>GetLocalTechs()</c>, so a different station sells it), a station that
    /// must be built (native's BUILDING techs are the built stations' effect
    /// techs, <c>Igc/sideigc.h:140-158</c>), or a part that carries the tech in
    /// its effect mask; the plain bit index when the core has no such record.
    /// The containment test itself is native <c>CanBuy</c>
    /// (<c>Igc/stationIGC.h:383-391</c>); the naming follows the
    /// <c>BitsToTechsList</c> idea of <c>Igc/missionigc.cpp:377-413</c>.
    /// <para>Money is deliberately not a lock: native's hull list ignores the
    /// wallet and the buy button grants the shortfall
    /// (<c>WinTrek/load.cpp:1513-1520,2139-2182</c>) — affordability is the row's
    /// <see cref="LoadoutV2HullRow.Affordable"/> flag instead.</para>
    /// </summary>
    private string? LockReasonFor(DataHullTypeIGC hull)
    {
        var missing = MissingTechBits(hull.TtbmRequired);
        if (missing.Count == 0) return "not offered at this station";
        var phrases = new List<string>();
        foreach (int bit in missing) phrases.Add(MissingPhrase(bit));
        return "missing " + string.Join(", ", phrases);
    }

    private List<int> MissingTechBits(byte[] required)
    {
        var list = new List<int>();
        var side = _sideTechs.AsSpan();
        for (int bit = 0; bit < required.Length * 8; bit++)
        {
            int by = bit >> 3;
            int mask = 0x80 >> (bit & 7);
            if ((required[by] & mask) == 0) continue;
            bool have = (by < side.Length && (side[by] & mask) != 0)
                        || (_stationLocal is { } local && by < local.Length && (local[by] & mask) != 0);
            if (!have) list.Add(bit);
        }
        return list;
    }

    /// <summary>The source that grants a missing tech bit, as a human phrase:
    /// <c>research &lt;development&gt;</c> (preferring the step the side can buy
    /// next), else <c>a &lt;station&gt; station</c> when the bit is a station
    /// type's LOCAL tech (native <c>CanBuy</c> compares against
    /// <c>GetLocalTechs()</c>, <c>Igc/stationIGC.h:383-391</c> — this station does
    /// not sell it, that one does), else <c>building &lt;station type&gt;</c>
    /// (native building techs, <c>Igc/sideigc.h:140-158</c>), else
    /// <c>part &lt;part&gt;</c> (a part's effect techs), else the raw bit index.</summary>
    private string MissingPhrase(int bit)
    {
        var core = _shop.Catalog.Core;
        int by = bit >> 3;
        int mask = 0x80 >> (bit & 7);
        if (core is not null)
        {
            DataDevelopmentIGC? fallbackDev = null;
            foreach (var kv in core.Developments.OrderBy(kv => kv.Key))
            {
                var dev = kv.Value;
                if (by >= dev.TtbmEffects.Length || (dev.TtbmEffects[by] & mask) == 0) continue;
                if (!string.IsNullOrEmpty(dev.Name)
                    && TechTreeBitMask.RequiredTechsBuyable(dev.TtbmRequired, _stationLocal, _sideTechs))
                    return $"research {dev.Name}";
                fallbackDev ??= dev;
            }

            DataStationTypeIGC? localStation = null;
            DataStationTypeIGC? effectStation = null;
            foreach (var kv in core.StationTypes.OrderBy(kv => kv.Key))
            {
                var st = kv.Value;
                if (localStation is null && by < st.TtbmLocal.Length && (st.TtbmLocal[by] & mask) != 0)
                    localStation = st;
                if (effectStation is null && by < st.TtbmEffects.Length && (st.TtbmEffects[by] & mask) != 0)
                    effectStation = st;
            }
            if (fallbackDev is { } f && !string.IsNullOrEmpty(f.Name)) return $"research {f.Name}";
            if (localStation is { } ls && !string.IsNullOrEmpty(ls.Name)) return $"a {ls.Name} station";
            if (effectStation is { } es && !string.IsNullOrEmpty(es.Name)) return $"building {es.Name}";

            foreach (var p in core.PartTypes.OrderBy(kv => kv.Key).Select(kv => kv.Value))
                if (by < p.TtbmEffects.Length && (p.TtbmEffects[by] & mask) != 0
                    && !string.IsNullOrEmpty(p.Name)) return $"part {p.Name}";
        }
        return $"technology #{bit}";
    }

    // ── ship column: the effective hull's slots ──────────────────────────────

    /// <summary>The effective hull the slot column describes (chosen, else the
    /// docked one).</summary>
    public DataHullTypeIGC? EffectiveHull => _shop.EffectiveHull;

    /// <summary>
    /// P1010 — one row per mount the effective hull really has: fixed weapon
    /// hardpoints (mount &lt; <c>MaxFixedWeapons</c>), gunner-manned turret
    /// hardpoints (mount &lt; <c>MaxWeapons</c>), then the single-mount equipment
    /// sockets whose <c>PmEquipment[type]</c> mask is non-zero — exactly native
    /// <c>Slot::SlotValidForHull</c> (<c>WinTrek/load.cpp:112-122</c>) applied to
    /// the authored slot table (<c>:2023-2044</c>).
    /// </summary>
    public IReadOnlyList<LoadoutV2SlotRow> SlotRows
    {
        get
        {
            if (_shop.EffectiveHull is not { } h) return Array.Empty<LoadoutV2SlotRow>();
            var rows = new List<LoadoutV2SlotRow>();
            int maxFixed = Math.Min(h.MaxFixedWeapons, h.Hardpoints.Length);
            int maxWeapons = Math.Min(h.MaxWeapons, h.Hardpoints.Length);
            for (int mount = 0; mount < maxFixed; mount++)
                rows.Add(SlotRow(h, new SlotDef(EquipmentType.Weapon, (byte)mount, IsTurret: false)));
            for (int mount = maxFixed; mount < maxWeapons; mount++)
                rows.Add(SlotRow(h, new SlotDef(EquipmentType.Weapon, (byte)mount, IsTurret: true)));
            foreach (var et in SocketTypes)
            {
                int idx = (int)et;
                if (idx < h.PmEquipment.Length && h.PmEquipment[idx] != 0)
                    rows.Add(SlotRow(h, new SlotDef(et, 0, IsTurret: false)));
            }
            return rows;
        }
    }

    private LoadoutV2SlotRow SlotRow(in DataHullTypeIGC hull, SlotDef slot)
    {
        var chosen = _shop.PartInSlot(slot);
        var chosenType = chosen is { } c ? _shop.PartById(c.PartId) : null;
        DataPartTypeIGC? currentType = null;
        foreach (var p in _shop.CurrentParts)
        {
            var type = _shop.PartById(p.PartId);
            if (!CurrentSlotHolds(slot, p, type)) continue;
            currentType = type;
            break;
        }
        return new LoadoutV2SlotRow(
            Slot: slot,
            Label: SlotLabel(hull, slot),
            IsTurret: slot.IsTurret,
            Mount: slot.Mount,
            ChosenPartId: chosen?.PartId,
            ChosenPartName: chosenType?.Name,
            Amount: chosen?.Amount,
            CurrentPartName: currentType?.Name,
            GunnerName: slot.IsTurret ? _turretGunner?.Invoke(slot.Mount) : null);
    }

    /// <summary>
    /// P1010 — the row label. Fixed weapons are numbered by hardpoint
    /// (<c>WEAPON 1..</c>); turrets are numbered among turrets
    /// (<c>TURRET 1..</c>), which is native's separate turret mount space
    /// (<c>Slot::GetMount</c> = <c>m_mount + GetMaxFixedWeapons()</c>,
    /// <c>WinTrek/load.cpp:104-110</c>); equipment sockets show their native
    /// equipment-type name.
    /// </summary>
    public static string SlotLabel(in DataHullTypeIGC hull, SlotDef slot)
    {
        if (slot.Type == EquipmentType.Weapon)
        {
            if (!slot.IsTurret) return $"WEAPON {slot.Mount + 1}";
            int turret = slot.Mount - Math.Min(hull.MaxFixedWeapons, hull.Hardpoints.Length) + 1;
            return $"TURRET {Math.Max(1, turret)}";
        }
        return slot.Type.ToString().ToUpperInvariant();
    }

    private static bool CurrentSlotHolds(SlotDef slot, in ExpandedPartData part, DataPartTypeIGC? type)
    {
        if (type is not { } t) return false;
        if (slot.Type == EquipmentType.Weapon)
            return t.EquipmentType == EquipmentType.Weapon && part.MountId == slot.Mount;
        return t.EquipmentType == slot.Type && part.MountId == 0;
    }

    // ── the inline part picker ───────────────────────────────────────────────

    /// <summary>The slot whose part picker is open, or null.</summary>
    public SlotDef? OpenSlot => _openSlot;

    /// <summary>
    /// P1010 — open the inline picker for a slot of the effective hull (native
    /// opens the part menu on the slot, <c>WinTrek/load.cpp:1680-1751</c>).
    /// </summary>
    public bool OpenSlotFor(SlotDef slot)
    {
        foreach (var row in SlotRows)
            if (row.Slot == slot) { _openSlot = slot; _statusReason = null; return true; }
        return false;
    }

    /// <summary>P1010 — close the inline picker (native closes the popup,
    /// <c>load.cpp:1684-1688,1887-1895</c>).</summary>
    public void CloseSlot()
    {
        _openSlot = null;
        _statusReason = null;
    }

    /// <summary>P1010 — the offers of the open slot (empty when none is open).</summary>
    public IReadOnlyList<LoadoutV2Offer> Offers =>
        _openSlot is { } slot ? OffersFor(slot) : Array.Empty<LoadoutV2Offer>();

    /// <summary>
    /// P1010 — the parts the effective hull can mount in <paramref name="slot"/>
    /// and the side can buy: <see cref="ShopModel.SlotOffers"/> (native
    /// <c>CanMount</c> + <c>BuyPartMenu</c>, <c>load.cpp:1656-1678,1714-1743</c>,
    /// over the station's tech-filtered catalogue = <c>pstation-&gt;CanBuy</c>,
    /// <c>Igc/stationIGC.h:383-391</c>), with each offer's price and the
    /// "already mounted on the docked ship" marker.
    /// </summary>
    public IReadOnlyList<LoadoutV2Offer> OffersFor(SlotDef slot)
    {
        var list = new List<LoadoutV2Offer>();
        var chosenId = _shop.PartInSlot(slot)?.PartId;
        foreach (var offer in _shop.SlotOffers(slot))
        {
            bool alreadyMounted = false;
            foreach (var p in _shop.CurrentParts)
            {
                if (p.PartId != offer.Part.PartId) continue;
                bool inSlot = slot.Type == EquipmentType.Weapon ? p.MountId == slot.Mount : p.MountId == 0;
                if (inSlot) { alreadyMounted = true; break; }
            }
            list.Add(new LoadoutV2Offer(
                Part: offer.Part,
                Name: offer.Part.Name,
                Price: offer.Part.Price,
                Amount: offer.Amount,
                IsLauncher: offer.IsLauncher,
                AlreadyMounted: alreadyMounted,
                IsChosen: chosenId == offer.Part.PartId));
        }
        return list;
    }

    // ── actions (all through ShopModel) ──────────────────────────────────────

    /// <summary>
    /// P1010 — select a hull row. Refused (with a status reason) for a locked or
    /// unknown hull; otherwise <see cref="ShopModel.SelectHull"/> applies the
    /// native hull-pick rules.
    /// </summary>
    public bool SelectHull(short hullId)
    {
        var row = FindHullRow(hullId);
        if (row is not { } r)
        {
            _statusReason = "that hull is not in this station's hull list";
            return false;
        }
        if (r.Status == HullRowStatus.Locked)
        {
            _statusReason = $"{r.Name} is locked — {r.LockReason}";
            return false;
        }
        if (!_shop.SelectHull(hullId))
        {
            _statusReason = $"{r.Name} cannot be bought here";
            return false;
        }
        _statusReason = null;
        _openSlot = null;   // a hull change rebuilds the slot column
        return true;
    }

    /// <summary>P1010 — native <c>OnButtonNext</c> → <c>GetNextHullType(true)</c>
    /// (<c>load.cpp:1627-1631</c>) through <see cref="ShopModel.CycleHull"/>.</summary>
    public void NextHull()
    {
        _shop.CycleHull(true);
        _openSlot = null;
        _statusReason = null;
    }

    /// <summary>P1010 — native <c>OnButtonPrevious</c> → <c>GetNextHullType(false)</c>
    /// (<c>load.cpp:1633-1637</c>) through <see cref="ShopModel.CycleHull"/>.</summary>
    public void PreviousHull()
    {
        _shop.CycleHull(false);
        _openSlot = null;
        _statusReason = null;
    }

    /// <summary>
    /// P1010 — choose an offer in the open slot (the inline picker's pick):
    /// <see cref="ShopModel.MountInSlot"/>, which refuses anything that is not an
    /// offer of that slot (native <c>BuyPartMenu</c>'s filter). The picker closes
    /// on success, as the native menu does.
    /// </summary>
    public bool ChooseOffer(short partId)
    {
        if (_openSlot is not { } slot)
        {
            _statusReason = "no slot is open";
            return false;
        }
        LoadoutV2Offer? pick = null;
        foreach (var o in OffersFor(slot))
            if (o.PartId == partId) { pick = o; break; }
        if (pick is not { } chosen)
        {
            _statusReason = $"that part does not fit {SlotName(slot)}";
            return false;
        }
        bool ok = _shop.MountInSlot(slot, new SlotPartOffer(chosen.Part, chosen.Amount, chosen.IsLauncher));
        _statusReason = ok ? null : $"{chosen.Name} does not fit {SlotName(slot)}";
        if (ok) _openSlot = null;
        return ok;
    }

    /// <summary>P1010 — choose a part into an explicit slot (the row-level pick,
    /// without opening the picker).</summary>
    public bool ChooseOffer(SlotDef slot, short partId)
    {
        foreach (var o in OffersFor(slot))
        {
            if (o.PartId != partId) continue;
            bool ok = _shop.MountInSlot(slot, new SlotPartOffer(o.Part, o.Amount, o.IsLauncher));
            _statusReason = ok ? null : $"{o.Name} does not fit {SlotName(slot)}";
            if (ok && _openSlot == slot) _openSlot = null;
            return ok;
        }
        _statusReason = $"that part does not fit {SlotName(slot)}";
        return false;
    }

    /// <summary>The slot's label for refusal text (hull-aware when the effective
    /// hull is known, else the equipment-type name).</summary>
    private string SlotName(SlotDef slot) =>
        _shop.EffectiveHull is { } hull ? SlotLabel(hull, slot) : slot.Type.ToString().ToUpperInvariant();

    /// <summary>P1010 — empty a slot (native "-Make Slot Empty-",
    /// <c>load.cpp:1704,1889</c>), raising the unmount cue through
    /// <see cref="ShopModel.ClearSlot"/>.</summary>
    public void ClearSlot(SlotDef slot)
    {
        _shop.ClearSlot(slot, notify: true);
        _statusReason = null;
        if (_openSlot == slot) _openSlot = null;
    }

    /// <summary>P1010 — the native Default button (<c>OnButtonDefault</c>,
    /// <c>load.cpp:1639-1648</c>): the hull's default loadout, through
    /// <see cref="ShopModel.ApplyDefaultLoadout"/>.</summary>
    public void ApplyDefault()
    {
        _shop.ApplyDefaultLoadout();
        _openSlot = null;
        _statusReason = null;
    }

    /// <summary>P1010 — back to the docked ship's loadout
    /// (<see cref="ShopModel.RevertToCurrentLoadout"/>).</summary>
    public void Revert()
    {
        _shop.RevertToCurrentLoadout();
        _openSlot = null;
        _statusReason = null;
    }

    // ── price box (read straight off ShopModel) ──────────────────────────────

    /// <summary>The pending loadout's value (hull + parts) — native
    /// <c>GetValue</c>, <see cref="ShopModel.NewLoadoutValue"/>.</summary>
    public long PriceTotal => _shop.NewLoadoutValue;

    /// <summary>The docked ship's value, <see cref="ShopModel.CurrentShipValue"/>.</summary>
    public long CurrentValue => _shop.CurrentShipValue;

    /// <summary>The money the buy would spend (new − current),
    /// <see cref="ShopModel.Cost"/> (<c>load.cpp:1513-1520</c>).</summary>
    public long Cost => _shop.Cost;

    public bool AffordabilityKnown => _shop.AffordabilityKnown;

    public bool CanAffordTotal => _shop.CanAffordTotal;

    /// <summary>The native <c>OnButtonBuy</c> shortfall (<c>load.cpp:1513-1520</c>)
    /// — the "Need $X" the V2 price box shows.</summary>
    public long MoneyNeeded => _shop.BuyShortfall;

    /// <summary>True when a real hull swap is pending
    /// (<see cref="ShopModel.WillSwapHull"/>).</summary>
    public bool WillSwapHull => _shop.WillSwapHull;

    /// <summary>
    /// The refusal text the screen shows: the V2 action's own refusal (locked hull,
    /// part that does not fit) when one fired, else the shop's
    /// <see cref="ShopModel.BlockReason"/> (affordability / launch block). Null when
    /// nothing blocks.
    /// </summary>
    public string? BlockReason => _statusReason ?? _shop.BlockReason;

    private LoadoutV2HullRow? FindHullRow(short hullId)
    {
        foreach (var row in HullRows)
            if (row.HullId == hullId) return row;
        return null;
    }
}
