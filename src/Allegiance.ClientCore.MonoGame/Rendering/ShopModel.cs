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
/// <para><b>Honest limits (Phase 2):</b> the server does not yet hot-swap a
/// ship's physical hull (ApplyLoadout grants parts and ACK-echoes the requested
/// hull id; the hull itself is fixed at spawn — Server.cs:9470-9477). Choosing
/// a hull here therefore sets what we <em>request</em>, and the ACK carries the
/// granted hull id; the definitive real effect is the mounted <em>parts</em>
/// (Server.cs:9497). That hull-swap is a named follow-up.</para>
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
    public DataStationTypeIGC? CurrentBuilderTarget =>
        BuilderStationTypeId is { } id
            ? _builderTargets.FirstOrDefault(t => t.StationTypeId == id)
            : null;

    /// <summary>P285 — whether the effective (to-be-launched) hull is a
    /// Builder/Constructor-class hull (<see cref="ShopCatalog.IsBuilderHull"/>).
    /// This gates showing the builder-target chooser and sending the chosen
    /// target on launch — a non-Builder hull carries no target.</summary>
    public bool IsBuilderActive
    {
        get
        {
            var hull = _cat.Hulls.FirstOrDefault(h => h.HullId == EffectiveHullId);
            return hull is { } h && ShopCatalog.IsBuilderHull(h);
        }
    }

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
