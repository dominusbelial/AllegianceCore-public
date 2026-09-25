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

    /// <summary>True once a KILL_SHIP(105) names this ship as the victim. The
    /// entity stays in the table after death — querying "did ship X die" must
    /// remain answerable — but this flag latches the death. Set by
    /// <c>ClientWorld.Apply(KillShip)</c> alongside the event-log record.</summary>
    public bool IsDead { get; internal set; }

    /// <summary>True once the server tells us this ship is no longer in our
    /// view — it docked at a station (SHIP_DELETE <c>SDR_DOCKED</c>) or warped
    /// to a DIFFERENT sector (SHIP_DELETE <c>SDR_LEFTSECTOR</c>). Unlike death
    /// this is <b>transient</b>: a fresh pose/identity update for the ship (it
    /// undocked or warped back into view, or a new EXPORT re-establishes it)
    /// clears the flag, so the render/target loops resume the ship exactly when
    /// the server resumes its telemetry for it. Set by
    /// <c>ClientWorld.Apply(ShipDelete)</c>; cleared by every Apply that
    /// re-establishes the ship's presence (SetCluster / SingleShipUpdate /
    /// HeavyShipsUpdate / ExportShip).</summary>
    public bool IsGone { get; internal set; }

    /// <summary>The render/target exclusion predicate: this ship is not
    /// currently visible to the local peer — it died (<see cref="IsDead"/>) OR
    /// is out of view (<see cref="IsGone"/>). Every place that would otherwise
    /// draw/target a stale last-known pose (the P344/P370 ghost-ship exclusion)
    /// gates on this single property so a docked/warped ship is hidden exactly
    /// where a dead ship already is.</summary>
    public bool IsNotVisible => IsDead || IsGone;

    /// <summary>P373 — transient latch recording that the one-shot
    /// "new contact" sound cue has already been played for this ship for its
    /// current visibility episode. It starts false, is set true by
    /// <c>ClientWorld.AnnounceNewContactIfNew</c> when the cue is first queued,
    /// and is reset to false when the ship leaves view (<c>ShipDelete</c> /
    /// <c>KillShip</c> — a docked/warped-out ship that later returns counts as
    /// a FRESH sighting, mirroring native <c>UpdateSideVisibility</c>'s
    /// not-visible→visible edge firing once per real transition). Guards every
    /// presence-apply (ExportShip / HeavyShipsUpdate) so an already-announced,
    /// still-visible ship never re-cues on routine pose refreshes.</summary>
    public bool NewContactSounded { get; internal set; }

    /// <summary>P373 — true once a real-id <c>EXPORT(26)</c> has established
    /// this ship's identity (<see cref="SideId"/>/<see cref="HullId"/>) on the
    /// client. Identity flows ONLY from EXPORT (never from the 176 heavy
    /// telemetry, which carries no SideId/HullId — see
    /// <c>ClientWorld.Apply(HeavyShipsUpdate)</c>), so this flag gates the
    /// P373 first-sighting cue on the telemetry path: only a ship whose side is
    /// genuinely known is classified enemy/friendly, avoiding a wrong cue from
    /// the C# default SideId 0 during the brief pre-identity window.</summary>
    public bool HasRealIdentity { get; internal set; }

    /// <summary>P362 — how many <c>RELOAD(129)</c> relays have been received for
    /// this ship. On a successful P361 auto-reload the server broadcasts the
    /// applied entries sector-wide (<c>Server.Combat.BroadcastAutoReload</c>);
    /// the client counts them so the weapon-status tracker can detect the
    /// successful reload even though the wire ammo fraction never shows 0 (the
    /// server refills the pool synchronously in the same tick). Incremented
    /// under the world gate by <c>ClientWorld.Apply(Reload)</c>; the MonoGame
    /// client compares it across frames to know "a reload arrived for me".</summary>
    public long ReloadCount { get; internal set; }

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

    /// <summary>The mounted-part tail from the most recent BUY_LOADOUT_ACK(5),
    /// in mount order — the part <em>granted</em> loadout, distinct from the
    /// <c>ExpandedPartData</c> representation (<see cref="ExpandedPartData"/> is
    /// already a full 6-byte struct carrying <c>PartId/Amount/MountId/
    /// FractionMounted</c>, so keeping the list verbatim costs no per-part
    /// flattening). Null until the first ack targets this ship; an empty list
    /// means a hull-only loadout was bought. A BuyLoadoutAck always concerns
    /// the requesting peer's own ship (it is routed only to that peer), so this
    /// is practically set on <see cref="IsMine"/> only.</summary>
    public IReadOnlyList<ExpandedPartData>? Loadout { get; internal set; }

    // ── teammate-visibility fields, attached by EXPORT(26)/PLAYERINFO(10) ──

    /// <summary>The exporting ship's side id (ExportShip.SideId). A teammate
    /// ship enters the table this way; compared against others to know who is
    /// on my side.</summary>
    public short SideId { get; internal set; }

    /// <summary>The exporting ship's CharacterName (ExportShip / PlayerInfo).
    /// Empty until an EXPORT or PLAYERINFO names this ship.</summary>
    public string CharacterName { get; internal set; } = "";

    /// <summary>The PlayerInfo(10) attached to this ship (may arrive after the
    /// ship itself, so it is upserted independently). Null until one arrives.</summary>
    public PlayerInfo? Player { get; internal set; }
}
