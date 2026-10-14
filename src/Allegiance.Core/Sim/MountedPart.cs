using Allegiance.Core.Wire;

namespace Allegiance.Core.Sim;

/// <summary>
/// A part mounted on a <see cref="Ship"/> — the generalized successor to the
/// old weapons-only <c>_weapons</c> list. Captures everything the server needs
/// to both remember <em>and round-trip</em> a requested loadout: the resolved
/// static-core record (<see cref="Type"/>), the wire quantities the client
/// asked for (partID/amount/mountID/fractionMounted), and — for <c>Weapon</c>
/// subtypes only — the realized in-sim <see cref="Weapon"/> that actually
/// fires in the authoritative tick.
/// </summary>
public sealed class MountedPart : IPartIgc
{
    /// <summary>The wire <c>partID</c> — copied from <see cref="Type"/>.PartId at mount time.</summary>
    public required short PartId { get; init; }

    /// <summary>The wire <c>amount</c> the owner requested for this part (copied from the request). For a realized weapon this is bookkeeping only; the live ammo lives on <see cref="Ship.Ammo"/>.</summary>
    public required short Amount { get; init; }

    /// <summary>The wire <c>mountID</c> (slot/hardpoint index) this part occupies. <c>Mount = char</c> (<c>igc.h:841</c>), hence byte.</summary>
    public required byte MountId { get; init; }

    private float _mountedFraction;

    /// <summary>The raw <c>fractionMounted</c> byte (<c>ExpandedPartData.fractionMounted</c>, a <c>BytePercentage</c> stored as <c>value·Divisor</c>, <c>_Utility/Utility.h:1311-1327</c>).</summary>
    public required byte FractionMounted
    {
        get => (byte)Math.Clamp((int)(_mountedFraction * 255f), 0, 255);
        init => _mountedFraction = value / 255f;
    }

    /// <summary>Native per-part mount accumulator; retain sub-byte progress.</summary>
    public float MountedFraction { get => _mountedFraction; internal set => _mountedFraction = value; }

    /// <summary>Magazine load deadline (magazineigc.h:73,138-140).</summary>
    public double TimeLoaded { get; internal set; }

    /// <summary>The resolved static-core part-type record (used to distinguish the subtype).</summary>
    public required DataPartTypeIGC Type { get; init; }

    /// <summary>The realized in-sim weapon, present only when <see cref="Type"/>'s subtype is <c>Weapon</c> and the projectile could be resolved from the mission core. Null for every other subtype and for an unrealizable weapon (a requested part whose projectile is absent is still remembered for round-trip, just inert).</summary>
    public Weapon? FunctionalWeapon { get; set; }

    /// <summary>The part subtype, hoisted for convenience (pattern-match <see cref="Type.Type"/> for subtype-specific fields).</summary>
    public EquipmentType EquipmentType => Type.EquipmentType;

    /// <summary>This part as its wire <c>ExpandedPartData</c> entry, for echoing it back in a <c>BUY_LOADOUT_ACK</c>.</summary>
    public ExpandedPartData ToExpandedPartData() =>
        new(PartId, Amount, MountId, FractionMounted);
}
