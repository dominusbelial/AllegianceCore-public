using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

/// <summary>
/// One ship known to the client, accumulated incrementally from real wire
/// traffic. Ship data arrives piecemeal across three messages — SET_CLUSTER(7)
/// assigns the ship and its spawn state, SINGLE_SHIP_UPDATE(78) refines the
/// position/velocity each sim tick it moves, and SHIP_STATUS(90) attaches the
/// sector/station/parent/state fields — so this is the aggregation point that
/// the flat per-message lists in AllegianceCore's own <c>Bot.cs</c> never had:
/// keying by <see cref="ShipId"/> lets the same logical ship be re-targeted by
/// different messages without ever creating separate entities.
/// </summary>
public sealed class ShipEntity
{
    /// <summary>The ship's object id, from the wire (embedding id in SET_CLUSTER /
    /// <c>OidTarget</c> in SINGLE_SHIP_UPDATE / <c>ShipID</c> in SHIP_STATUS).</summary>
    public short ShipId { get; init; }

    /// <summary>True iff this is the local player's own ship (learned from the
    /// SET_CLUSTER assignment).</summary>
    public bool IsMine { get; internal set; }

    /// <summary>The most recent <c>ServerShipUpdate</c> seen for this ship,
    /// stored verbatim (never re-flattened into local copies — the codec is the
    /// single source of truth for the 47-byte payload). Null until the first
    /// SET_CLUSTER or SINGLE_SHIP_UPDATE arrives.</summary>
    public ServerShipUpdate? Latest { get; internal set; }

    /// <summary>The sim tick (the uint <c>Time</c> slot) of the most recent
    /// <see cref="Latest"/> payload — monotonic proof a ticking server is
    /// updating this entity, not a canned fixture.</summary>
    public uint LastUpdateTick { get; internal set; }

    // ── SHIP_STATUS(90) fields, attached incrementally by that message ──────

    /// <summary>Status.GetHullID().</summary>
    public short HullId { get; internal set; }

    /// <summary>Status.GetSectorID() — the sector the ship is currently in.</summary>
    public short SectorId { get; internal set; }

    /// <summary>Status.GetStationID() (NA/-1 when flying free).</summary>
    public short StationId { get; internal set; }

    /// <summary>Status.GetParentID() (NA/-1 when not docked/turreted).</summary>
    public short ParentId { get; internal set; }

    /// <summary>The ordered ShipState enum value (bits 0-5).</summary>
    public byte State { get; internal set; }

    /// <summary>bit6 — no scanning system, so always false in practice.</summary>
    public bool Unknown { get; internal set; }

    /// <summary>bit7 — always true in practice (no visibility system).</summary>
    public bool Detected { get; internal set; }

    /// <summary>Status.GetStateTime() — the server's sim clock at the status.</summary>
    public uint StateTime { get; internal set; }
}
