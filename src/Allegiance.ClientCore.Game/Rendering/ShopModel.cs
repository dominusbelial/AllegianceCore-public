using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P268 — the pure interaction model of the minimal docked/hangar loadout
/// shop: a flattened list of buyable hulls then parts, a movable cursor, and a
/// real pending loadout (a chosen hull + toggled-on parts) that the player
/// sends as a real <c>FM_C_BUY_LOADOUT(4)</c> to launch.
///
/// <para>Kept MonoGame-free and headless-testable (the <see cref="ScoreboardLayout"/>
/// discipline) so the shop logic — cursor movement, hull choice, part toggling,
/// the effective loadout — is unit-testable without a graphics device.</para>
///
/// <para><b>Model, not view:</b> this class is deliberately dumb about layout.
/// The flattened row order is <c>[0..Hulls-1]</c> hulls then <c>[Hulls..]</c>
/// parts; <see cref="ShopLayout"/> turns the same model into display rows.</para>
///
/// <para><b>Real hull swap (P334/P335) — not cosmetic anymore.</b> The server
/// (<c>ApplyLoadout</c>) now performs a genuine in-place hull change
/// (<c>Ship.ChangeHull</c>) whenever the requested hull differs from the one
/// flown, and the resulting BUY_LOADOUT_ACK carries the ship's REAL hull id and
/// the REAL mounted part tail. That tail honours a hardpoint-compatibility
/// policy: a requested part the new hull can't mount is DROPPED server-side
/// (native <c>CanMount</c>, see <see cref="DataHullTypeIGC.CanMountPart"/>), so
/// the ACK may be shorter than the requested list and <c>FBoughtEverything</c>
/// is honestly false. The client applies that authoritative ACK to its
/// <c>ShipEntity</c> (ClientWorld.Apply), so after launch the rendered model,
/// flight feel and HUD all reflect the real new hull. This screen's
/// <see cref="ChosenHullId"/>/<see cref="EffectiveHullId"/> drive that
/// launch-time swap; the fit helpers below show which chosen parts the server
/// will drop for the effective hull, so the UI never promises parts the server
/// won't mount.</para>
/// </summary>
public sealed class ShopModel
{
    private readonly ShopCatalog _cat;
    private readonly List<ExpandedPartData> _chosenParts = new();
    // P434/P435 — the real launcher/dispenser offers (built via the P433/P435
    // shared From<Kind>Launcher bridge — missile + mine + chaff + probe),
    // normalized to empty so a null Launchers (e.g. ShopCatalog.Empty) is safe.
    private readonly IReadOnlyList<LauncherOffer> _launchers;

    /// <summary>The ship's currently-mounted hull id (from SHIP_STATUS).</summary>
    public short CurrentHullId { get; }

    /// <summary>The ship's currently-mounted parts (the BUY_LOADOUT_ACK tail).
    /// Seeded into <see cref="_chosenParts"/> on construction so the shop opens
    /// with the player's already-equipped loadout toggled in and relaunching
    /// without touching anything re-sends it (P394). Kept as a separate
    /// read-only snapshot for the "current ship" display; the shop never
    /// mutates it.</summary>
    public IReadOnlyList<ExpandedPartData> CurrentParts { get; }

    /// <summary>Index into the flattened hull-then-part list; clamped on move.</summary>
    public int Cursor { get; private set; }

    /// <summary>The hull the player picked (null = keep the current hull).</summary>
    public short? ChosenHullId { get; private set; }

    /// <summary>The parts the player has toggled on, to send as the loadout.</summary>
    public IReadOnlyList<ExpandedPartData> ChosenParts => _chosenParts;

    /// <summary>Number of flat shop rows (hulls + parts + launchers).</summary>
    public int RowCount => _cat.Hulls.Count + _cat.Parts.Count + _launchers.Count;

    /// <summary>The hull this screen will request: the chosen one, else the current.</summary>
    public short EffectiveHullId => ChosenHullId ?? CurrentHullId;

    /// <summary>P335 — the catalogue record for <see cref="EffectiveHullId"/>
    /// (the hull the shop will actually launch), or null if that hull isn't in
    /// the catalogue (e.g. filtered out by tech). Drives fit predictions and
    /// labels against the real to-be-launched hull.
    /// <para>Hand-rolled instead of <c>FirstOrDefault</c>: <see cref="DataHullTypeIGC"/>
    /// is a value type, so <c>FirstOrDefault</c> on a no-match returns a real,
    /// non-null <c>default(DataHullTypeIGC)</c> — a blank struct whose array
    /// fields (e.g. <c>PmEquipment</c>) are null — silently wrapped into a
    /// "has a value" <see cref="Nullable{T}"/> rather than an actual null.
    /// A caller pattern-matching on <c>is {} hull</c> would then call a real
    /// method (<see cref="DataHullTypeIGC.CanMountPart"/>) on that blank
    /// struct and NRE. This loop returns a genuine null on no-match.</para>
    /// </summary>
    public DataHullTypeIGC? EffectiveHull
    {
        get
        {
            foreach (var h in _cat.Hulls)
                if (h.HullId == EffectiveHullId) return h;
            return null;
        }
    }

    /// <summary>P335 — true when a genuinely different hull is chosen (a real
    /// hull swap will occur on launch). This is exactly the condition under
    /// which the server (P334) applies its hardpoint-compatibility drop policy —
    /// a same-hull loadout is left to free-mount and nothing is dropped.</summary>
    public bool WillSwapHull => ChosenHullId is { } c && c != CurrentHullId;

    /// <summary>
    /// P335 — the chosen parts the server will DROP on launch because they have
    /// no compatible hardpoint on <see cref="EffectiveHull"/>, mirroring P334's
    /// server-side policy (a requested part the new hull can't mount is dropped;
    /// native <c>CanMount</c> via <see cref="DataHullTypeIGC.CanMountPart"/>).
    /// The UI warns about these so the pending loadout never promises parts the
    /// server won't mount.
    ///
    /// <para><b>Gated on <see cref="WillSwapHull"/> (P394).</b> P334 applies its
    /// CanMount drop policy ONLY on the hull-change path
    /// (<c>hullChanged</c>, Server.ShipCommands.cs:1516-1527); a same-hull
    /// loadout is left to the port's existing free-mount behaviour and nothing
    /// is dropped. So a part only risks being dropped when a REAL hull swap is
    /// pending — returning <c>[]</c> for a no-swap loadout matches the server's
    /// actual policy AND the UI's own gating (ShopLayout.cs:111-123). This
    /// matters now that the shop pre-seeds the ship's currently-mounted parts
    /// (P394): a legitimately free-mounted part (one mounted through a same-hull
    /// buy) must not be falsely flagged "DROPPED on launch" when the server will
    /// actually keep it. A pre-seeded part that does not fit a DIFFERENT chosen
    /// hull still IS flagged (WillSwapHull is true then), exactly as P335
    /// handles a freshly-chosen incompatible part.</para>
    ///
    /// <para>A chosen part not resolvable in the catalogue (or an unknown
    /// effective hull) is conservatively NOT flagged — an unknown fit is
    /// not asserted to be a misfit.</para>
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

    private DataPartTypeIGC? LookupPart(short partId)
    {
        // Hand-rolled, not FirstOrDefault: see the EffectiveHull doc comment
        // above for why FirstOrDefault on a value-type sequence silently
        // returns a non-null "blank struct" on no-match instead of null.
        foreach (var p in _cat.Parts)
            if (p.PartId == partId) return p;
        // P434 — a mounted/chosen launcher resolves through the real launcher
        // table (a launcher PartId is a real part id, distinct from core.Parts),
        // so the fit machinery (ChosenPartsThatDontFitEffectiveHull -> CanMountPart)
        // treats a bought launcher exactly like any other part.
        foreach (var l in _launchers)
            if (l.Part.PartId == partId) return l.Part;
        return null;
    }


    public ShopModel(ShopCatalog catalog, short currentHullId, IReadOnlyList<ExpandedPartData> currentParts,
        int? money = null)
    {
        _cat = catalog;
        _launchers = catalog.Launchers ?? Array.Empty<LauncherOffer>();
        CurrentHullId = currentHullId;
        CurrentParts = currentParts ?? Array.Empty<ExpandedPartData>();
        // P468 — the local player's wallet (P442, world.MyMoney), or null until
        // the first SET_MONEY for the local ship. Drives the shop's price
        // summary (cost vs money) and the affordability grey-out / confirm gate.
        MyMoney = money;

        // P394 — seed the pending loadout from the ship's currently-mounted
        // parts (the BUY_LOADOUT_ACK tail). Without this, _chosenParts started
        // empty on every fresh ShopModel (one is built on every dock,
        // Game1.Panes.cs) and relaunching with L — without manually re-toggling
        // every part you already have — silently dropped them all: ApplyLoadout
        // clears the whole loadout then re-mounts only the requested parts
        // (Server.ShipCommands.cs:1436-1498), and weapons were spared only by
        // MountCombatWeapon's separate join-spawn auto-arm (Server.cs:639).
        //
        // Verbatim copy, NOT re-derived DefaultAmount/FullyMounted: each seeded
        // entry keeps its real mounted Amount (so a partly-used weapon/ammo-pool
        // isn't topped up to 20 on every relaunch) and FractionMounted. The
        // existing remove-if-present toggle (Activate) and P335's
        // ChosenPartsThatDontFitEffectiveHull then operate uniformly on these
        // seeded entries — a pre-seeded part toggles OFF on the first press and
        // an incompatible-for-the-chosen-hull seeded part is flagged/dropped
        // exactly like a freshly-chosen one. Mount ids are renumbered to the
        // shop's per-equipment-type convention (RenumberMounts — P437: every
        // non-Weapon type occupies the single mount 0, Weapons number 0..N
        // within the Weapon type), the same invariant Activate enforces, so the
        // pending loadout stays internally consistent regardless of the ACK
        // tail's wire mount ids and every type lands at the mount its server
        // consumer reads (a seeded launcher at mount 0 can lock + fire).
        //
        // Weapons ARE included in the seed (see the P394 writeup §weapon-
        // seeding for the full judgement): the shop should reflect "here's what
        // you have" including the guns, sending them is idempotent with
        // MountCombatWeapon, and it future-proofs the relaunch against that
        // auto-arm ever being narrowed.
        foreach (var p in CurrentParts)
            _chosenParts.Add(p);
        RenumberMounts();
    }

    public ShopCatalog Catalog => _cat;

    // ── P468 — pricing (native GetValue: hull price + Σ part prices) ─────────

    /// <summary>P468 — the local player's wallet (P442), or null until the
    /// first SET_MONEY for the local ship. When null the shop cannot judge
    /// affordability (no grey-out, no total gate), because "money unknown" is
    /// deliberately distinct from zero.</summary>
    public int? MyMoney { get; }

    /// <summary>P468 — true when a real wallet is known, so affordability is
    /// computable (the price summary can compare cost vs money).</summary>
    public bool AffordabilityKnown => MyMoney is not null;

    /// <summary>P468 — the value of a loadout the native way:
    /// <c>hull.Price + Σ part prices</c> (native <c>CshipIGC::GetValue</c>,
    /// <c>shipIGC.h:396-406</c>; the same helper <c>Server.Docking.cs:1313</c>
    /// uses to price a purchase). An unresolvable part contributes 0 (defensive —
    /// every real loadout part, incl. a bought launcher via <see cref="LookupPart"/>,
    /// resolves).</summary>
    public long ValueOf(short hullId, IReadOnlyList<ExpandedPartData> parts)
    {
        long v = 0;
        foreach (var h in _cat.Hulls)
            if (h.HullId == hullId) { v += h.Price; break; }
        foreach (var p in parts)
            if (LookupPart(p.PartId) is { } pc) v += pc.Price;
        return v;
    }

    /// <summary>P468 — value of the CURRENT ship (the hull you fly + the parts
    /// currently mounted).</summary>
    public long CurrentShipValue => ValueOf(CurrentHullId, CurrentParts);

    /// <summary>P468 — value of the NEW (pending) loadout the shop would launch:
    /// the effective (chosen-or-current) hull + the chosen parts.</summary>
    public long NewLoadoutValue => ValueOf(EffectiveHullId, _chosenParts);

    /// <summary>P468 — the purchase cost: <c>NewLoadoutValue − CurrentShipValue</c>.
    /// Negative = a refund (a cheaper loadout pays the player back). This is the
    /// exact formula the server (P441) charges (<c>newShip.GetValue() −
    /// oldShip.GetValue()</c>).</summary>
    public long Cost => NewLoadoutValue - CurrentShipValue;

    /// <summary>P468 — whether the pending loadout's total <see cref="Cost"/> is
    /// affordable: <c>Cost ≤ MyMoney</c>. The exact boundary (<c>Cost ==
    /// MyMoney</c>) is affordable. False when no wallet is known (can't prove
    /// affordability) — the confirm gate only BLOCKS when the wallet IS known
    /// and the cost provably exceeds it (see <see cref="TryLaunch"/>).</summary>
    public bool CanAffordTotal => AffordabilityKnown && Cost <= MyMoney!.Value;

    /// <summary>P468 — the visible reason a launch is currently refused
    /// client-side, or null when launch is allowed. Set by <see cref="TryLaunch"/>,
    /// cleared whenever the pending loadout changes (<see cref="Activate"/>) so a
    /// stale refusal never lingers. The docked layout renders it as a prominent
    /// no-fit line.</summary>
    public string? BlockReason { get; private set; }

    /// <summary>P468 — try to permit a launch of the current pending loadout.
    /// Returns true when the loadout is affordable, or when no wallet is known
    /// (we can't prove it unaffordable, so we don't block). Returns false — and
    /// records <see cref="BlockReason"/> — when the wallet is known and the total
    /// <see cref="Cost"/> exceeds it, so the client never sends a BUY_LOADOUT
    /// the server (P441) would silently refuse.</summary>
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

    /// <summary>P468 — grey the given hull row for lack of funds? Grey when a
    /// real wallet is known and the net cost to swap TO this hull (this hull's
    /// price − the effective hull's price) exceeds MyMoney. The effective hull
    /// itself (net 0) is never greyed. Cosmetic; the authoritative gate is the
    /// total <see cref="TryLaunch"/> check + the server's own funds gate.</summary>
    public bool HullRowGreyed(DataHullTypeIGC hull)
    {
        if (!AffordabilityKnown || hull.HullId == EffectiveHullId) return false;
        long inc = EffectiveHull is { } c ? (long)hull.Price - c.Price : hull.Price;
        return inc > MyMoney!.Value;
    }

    /// <summary>P468 — grey the given part/launcher row for lack of funds? Grey
    /// when a real wallet is known, the part is not already in the pending
    /// loadout (an already-chosen part is effectively free to keep), and its
    /// price exceeds MyMoney. Cosmetic; the authoritative gate is the total
    /// <see cref="TryLaunch"/> check + the server's own funds gate.</summary>
    public bool PartRowGreyed(short partId, int price)
    {
        if (!AffordabilityKnown) return false;
        if (IsPartChosen(partId)) return false;
        return price > MyMoney!.Value;
    }

    private bool IsPartChosen(short partId)
    {
        foreach (var p in _chosenParts)
            if (p.PartId == partId) return true;
        return false;
    }

    /// <summary>Move the cursor by <paramref name="delta"/> rows, clamped to bounds.</summary>
    public void MoveCursor(int delta)
    {
        Cursor = Math.Clamp(Cursor + delta, 0, Math.Max(0, RowCount - 1));
    }

    /// <summary>Whether the cursor currently rests on a hull row (vs a part row).</summary>
    public bool CursorOnHull => _cat.Hulls.Count > 0 && Cursor < _cat.Hulls.Count;

    /// <summary>The hull row under the cursor, if the cursor is on a hull.</summary>
    public DataHullTypeIGC? CursorHull => CursorOnHull ? _cat.Hulls[Cursor] : null;

    /// <summary>The part row under the cursor, if the cursor is on a part.</summary>
    public DataPartTypeIGC? CursorPart =>
        !CursorOnHull && Cursor - _cat.Hulls.Count < _cat.Parts.Count
            ? _cat.Parts[Cursor - _cat.Hulls.Count]
            : null;

    /// <summary>P434/P435 — whether the cursor rests on a real launcher/dispenser
    /// row (the flat list is hulls, then parts, then launchers — missile + mine +
    /// chaff + probe all live here).</summary>
    public bool CursorOnLauncher =>
        _launchers.Count > 0
        && Cursor - _cat.Hulls.Count - _cat.Parts.Count is var li
        && li >= 0 && li < _launchers.Count;

    /// <summary>P434/P435 — the launcher offer under the cursor, if on a launcher row.</summary>
    public LauncherOffer? CursorLauncher =>
        CursorOnLauncher ? _launchers[Cursor - _cat.Hulls.Count - _cat.Parts.Count] : null;

    /// <summary>SPACE/Enter: pick a hull (sets the chosen hull) or toggle a part
    /// (or, P434/P435, a real launcher/dispenser — missile/mine/chaff/probe) in/out
    /// of the chosen loadout. Chosen parts are ordered by mount id.</summary>
    public void Activate()
    {
        // P468 — the pending loadout changed, so a prior launch refusal (a
        // "cannot afford" block) no longer applies until the next TryLaunch.
        BlockReason = null;
        if (CursorOnHull)
        {
            if (_cat.Hulls[Cursor].HullId == CurrentHullId) ChosenHullId = null; // de-pick the current
            else ChosenHullId = _cat.Hulls[Cursor].HullId;
            return;
        }

        if (CursorOnLauncher)
        {
            // P434/P435 — a launcher is a buyable/equippable row exactly like any
            // other part: toggled into ChosenParts as a real part id with the
            // launcher's REAL ammo reserve (DataLauncherTypeIGC.Amount) — the
            // exact ExpandedPartData shape a P433/P435-equipped bot's launcher
            // entry carries on BUY_LOADOUT, so the normal launch flow needs no
            // special-casing for "came from the human shop click".
            var lo = _launchers[Cursor - _cat.Hulls.Count - _cat.Parts.Count];
            TogglePart(lo.Part.PartId, lo.Ammo);
            return;
        }

        var part = CursorPart;
        if (part is null) return;
        TogglePart(part.Value.PartId, DefaultAmount(part.Value));
    }

    private void TogglePart(short partId, short amount)
    {
        int existing = _chosenParts.FindIndex(p => p.PartId == partId);
        if (existing >= 0)
        {
            _chosenParts.RemoveAt(existing);
            RenumberMounts();
            return;
        }

        // P437 — a non-Weapon part occupies the ONE slot native gives its
        // equipment type (mount 0), so choosing a new part of that type REPLACES
        // the previously chosen one instead of stacking a second (inert) copy.
        // This is the real native reality (see the RenumberMounts doc) and it
        // fixes the old behaviour where buying weapon→afterburner→cloak→shield
        // then a launcher silently accumulated five magazines at mounts 4-10
        // that lock/fire could never reach (both gates read ET_Magazine@0). A
        // Weapon stacks (per-hardpoint): the hull carries up to MaxWeapons guns.
        var et = EquipmentTypeOf(partId);
        if (et is { } type && type != EquipmentType.Weapon)
        {
            int sameType = _chosenParts.FindIndex(p => EquipmentTypeOf(p.PartId) == type);
            if (sameType >= 0)
                _chosenParts.RemoveAt(sameType);
        }

        _chosenParts.Add(new ExpandedPartData(
            partId,
            amount,
            0,
            ExpandedPartData.FullyMounted));
        RenumberMounts();
    }

    /// <summary>
    /// P437 — renumber the pending loadout's <c>MountId</c>s to the REAL
    /// per-equipment-type hardpoint scheme, not a global sequential counter.
    /// The mount contract everywhere in the sim/server is
    /// <c>(EquipmentType, MountId)</c>: <see cref="Ship.ResolvePart"/>,
    /// <c>TrySwapPart</c> and every consumer resolve a non-Weapon type at
    /// <see cref="EquipmentType.Magazine"/>/<c>Cloak</c>/<c>Afterburner</c>/
    /// <c>Dispenser</c>/<c>ChaffLauncher</c> at <c>MountId == 0</c>
    /// (<c>Ship.UpdateMountedMissileLock</c>/<c>TryFireMissile</c>,
    /// <c>Server.HandleFireMissile</c>/<c>HandleFireExpendable</c>, the P269
    /// cloak slot, the P412 afterburner slot). <see cref="DataHullTypeIGC.CanMountPart"/>
    /// confirms the native reality that motivates it: only <c>Weapon</c> is
    /// per-hardpoint (mount 0..<c>MaxWeapons</c>-1); every other equipment type
    /// has a single mount (mount 0) whose mask is the hull's <c>pmEquipment[et]</c>
    /// entry. So a <c>Magazine</c> the player buys after any number of weapons /
    /// other equipment must land at mount 0, never at "whatever came before".
    /// </summary>
    private void RenumberMounts()
    {
        int weaponMount = 0;
        for (int i = 0; i < _chosenParts.Count; i++)
        {
            var p = _chosenParts[i];
            bool isWeapon = EquipmentTypeOf(p.PartId) == EquipmentType.Weapon;
            byte mount = isWeapon ? (byte)(weaponMount++) : (byte)0;
            _chosenParts[i] = p with { MountId = mount };
        }
    }

    /// <summary>The <see cref="EquipmentType"/> of a chosen part, resolved the
    /// same way the fit machinery resolves one (a real core part or a real
    /// launcher/dispenser; P434/P435). Null when the part isn't resolvable in
    /// this catalogue (defensive — every part a real loadout/toggle produces
    /// resolves).</summary>
    private EquipmentType? EquipmentTypeOf(short partId) =>
        LookupPart(partId) is { } p ? p.EquipmentType : null;

    /// <summary>A sensible stock amount for a newly toggled part: ammunition-like
    /// equipment (weapons) get a real ammo reserve; one-off mounts get 1. This is
    /// a client display default; the server grants whatever actually mounts.</summary>
    public static short DefaultAmount(in DataPartTypeIGC part) =>
        part.EquipmentType == EquipmentType.Weapon ? (short)20 : (short)1;
}
