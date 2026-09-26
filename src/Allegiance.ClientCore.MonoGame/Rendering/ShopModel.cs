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

    /// <summary>The ship's currently-mounted hull id (from SHIP_STATUS).</summary>
    public short CurrentHullId { get; }

    /// <summary>The ship's currently-mounted parts (the BUY_LOADOUT_ACK tail),
    /// used only to display "current ship"; not modified by the shop.</summary>
    public IReadOnlyList<ExpandedPartData> CurrentParts { get; }

    /// <summary>Index into the flattened hull-then-part list; clamped on move.</summary>
    public int Cursor { get; private set; }

    /// <summary>The hull the player picked (null = keep the current hull).</summary>
    public short? ChosenHullId { get; private set; }

    /// <summary>The parts the player has toggled on, to send as the loadout.</summary>
    public IReadOnlyList<ExpandedPartData> ChosenParts => _chosenParts;

    /// <summary>Number of flat shop rows (hulls + parts).</summary>
    public int RowCount => _cat.Hulls.Count + _cat.Parts.Count;

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
    /// server won't mount. A chosen part not resolvable in the catalogue (or an
    /// unknown effective hull) is conservatively NOT flagged — an unknown fit is
    /// not asserted to be a misfit.
    /// </summary>
    public IReadOnlyList<ExpandedPartData> ChosenPartsThatDontFitEffectiveHull
    {
        get
        {
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
        return null;
    }


    public ShopModel(ShopCatalog catalog, short currentHullId, IReadOnlyList<ExpandedPartData> currentParts,
        IReadOnlyList<DataStationTypeIGC>? builderTargets = null)
    {
        _cat = catalog;
        CurrentHullId = currentHullId;
        CurrentParts = currentParts ?? Array.Empty<ExpandedPartData>();
        _builderTargets = builderTargets?.ToList() ?? new List<DataStationTypeIGC>();
    }

    public ShopCatalog Catalog => _cat;

    // P285 — the Builder-target chooser state (which station type a purchased
    // Builder/Constructor hull should construct). Populated by the caller with
    // the tech-gated constructible station list (ShopCatalog.BuilderTargetsFor);
    // the player cycles through it with the ','/'.' keys while a Builder hull is
    // the effective (to-be-launched) hull.
    private readonly List<DataStationTypeIGC> _builderTargets;
    private int _builderTargetIndex = -1;

    /// <summary>The tech-gated station types offered for a purchased Builder
    /// hull (P285), in display order.</summary>
    public IReadOnlyList<DataStationTypeIGC> BuilderTargets => _builderTargets;

    /// <summary>The player's chosen Builder target station type id, or null
    /// until one is cycled to. Deliberately null-by-default (not a preset) so
    /// the launched Builder's <c>BuilderStationType</c> is really the player's
    /// explicit choice — never a silently-imposed default.</summary>
    public short? BuilderStationTypeId { get; private set; }

    /// <summary>The currently selected <see cref="BuilderTargets"/> entry, or
    /// null when none is chosen.</summary>
    public DataStationTypeIGC? CurrentBuilderTarget
    {
        get
        {
            if (BuilderStationTypeId is not { } id) return null;
            // Hand-rolled, not FirstOrDefault: see the EffectiveHull doc
            // comment above for why FirstOrDefault on a value-type sequence
            // silently returns a non-null "blank struct" on no-match.
            foreach (var t in _builderTargets)
                if (t.StationTypeId == id) return t;
            return null;
        }
    }

    /// <summary>P285 — whether the effective (to-be-launched) hull is a
    /// Builder/Constructor-class hull (<see cref="ShopCatalog.IsBuilderHull"/>).
    /// This gates showing the builder-target chooser and sending the chosen
    /// target on launch — a non-Builder hull carries no target.</summary>
    public bool IsBuilderActive => EffectiveHull is { } h && ShopCatalog.IsBuilderHull(h);

    /// <summary>P285 — cycle the chosen Builder target through the offered
    /// constructible list, wrapping at both ends. No-op when the effective hull
    /// is not a Builder or no targets are offered.</summary>
    public void CycleBuilderTarget(int delta)
    {
        if (!IsBuilderActive || _builderTargets.Count == 0) return;
        if (_builderTargetIndex < 0) _builderTargetIndex = 0;
        _builderTargetIndex = (_builderTargetIndex + delta) % _builderTargets.Count;
        if (_builderTargetIndex < 0) _builderTargetIndex += _builderTargets.Count;
        BuilderStationTypeId = _builderTargets[_builderTargetIndex].StationTypeId;
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

    /// <summary>SPACE/Enter: pick a hull (sets the chosen hull) or toggle a part
    /// in/out of the chosen loadout. Chosen parts are ordered by mount id.</summary>
    public void Activate()
    {
        if (CursorOnHull)
        {
            if (_cat.Hulls[Cursor].HullId == CurrentHullId) ChosenHullId = null; // de-pick the current
            else ChosenHullId = _cat.Hulls[Cursor].HullId;
            return;
        }

        var part = CursorPart;
        if (part is null) return;
        int existing = _chosenParts.FindIndex(p => p.PartId == part.Value.PartId);
        if (existing >= 0)
        {
            _chosenParts.RemoveAt(existing);
            RenumberMounts();
        }
        else
        {
            _chosenParts.Add(new ExpandedPartData(
                part.Value.PartId,
                DefaultAmount(part.Value),
                (byte)_chosenParts.Count,
                ExpandedPartData.FullyMounted));
        }
    }

    private void RenumberMounts()
    {
        for (int i = 0; i < _chosenParts.Count; i++)
            _chosenParts[i] = _chosenParts[i] with { MountId = (byte)i };
    }

    /// <summary>A sensible stock amount for a newly toggled part: ammunition-like
    /// equipment (weapons) get a real ammo reserve; one-off mounts get 1. This is
    /// a client display default; the server grants whatever actually mounts.</summary>
    public static short DefaultAmount(in DataPartTypeIGC part) =>
        part.EquipmentType == EquipmentType.Weapon ? (short)20 : (short)1;
}
