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

    public short WingId { get; internal set; }

    /// <summary>GAIN_FLAG carrier state; -1 clears it, -2 is an artifact.
    /// Native clintlib/appmsg.cpp:3136.</summary>
    public short CarriedFlagSideId { get; internal set; } = -1;

    /// <summary>True iff this is the local player's own ship (learned from the
    /// JOINED_MISSION identity or SET_CLUSTER assignment).</summary>
    public bool IsMine { get; internal set; }

    /// <summary>True once a KILL_SHIP(105) names this ship as the victim. The
    /// entity stays in the table after death — querying "did ship X die" must
    /// remain answerable — but this flag latches the death. Set by
    /// <c>ClientWorld.Apply(KillShip)</c> (and by SHIP_DELETE SDR_KILLED)
    /// alongside the event-log record. It is <b>not</b> permanent: P521 clears
    /// it at the real respawn / re-presence signal, so a ship that dies and then
    /// genuinely returns (the OWN ship's SET_CLUSTER at launch/undock/respawn;
    /// a REMOTE ship's in-cluster HeavyShipsUpdate / identity ExportShip, or a
    /// SHIP_RESET) renders and resumes live updates again. It is deliberately
    /// NOT cleared by SingleShipUpdate (that message also reaches out-of-view
    /// same-side teammates, so a corpse must not re-present from it — the P370
    /// protection).</summary>
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

    /// <summary>The sector confirmed by in-view telemetry, separate from the
    /// last-known strategic SHIP_STATUS sector. Identity/loadout refreshes and
    /// cross-sector teammate poses cannot establish cluster membership.</summary>
    public short? PoseSectorId { get; internal set; }

    /// <summary>Shared hull/effect gather gate. Native caches departed ships in
    /// the null cluster (clintlib/appmsg.cpp:475-488) and renders only current
    /// scanner visibility (WinTrek/trekigc.cpp:2167-2169).</summary>
    public bool CanDrawInSector(short sectorId) =>
        !IsNotVisible && !Unknown && State != ShipStatusMessage.StateDocked
        && ParentId < 0 && PoseSectorId == sectorId && SectorId == sectorId
        && Latest is not null;

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

    /// <summary>P508 — the per-player <em>ship</em> cookie this client must echo
    /// on every native <c>FM_C_SHIP_UPDATE(24)</c>. The server drops any native
    /// update whose cookie doesn't match its current one for this player
    /// (<c>FedSrv.CPP:4854</c>; ported in <c>Server.Dispatch.cs</c>). Delivered in
    /// <c>SET_CLUSTER(7)</c> at join and on warp, and renewed by each
    /// <c>SHIP_RESET(183)</c> the server sends on rejecting this ship's pose. Set
    /// by <c>ClientWorld.Apply(SetCluster)</c> and <c>Apply(ShipReset)</c>;
    /// consumed by the P508 own-ship send path.</summary>
    public byte Cookie { get; internal set; }

    /// <summary>P508 — set by <c>ClientWorld.Apply(ShipReset)</c> for the OWN ship
    /// when the server rejects its reported pose and sends an authoritative reset
    /// (<c>SHIP_RESET</c> carries the server pose + a <b>new</b> cookie). The P508
    /// own-ship host snaps its local simulation to <see cref="Latest"/> and adopts
    /// <see cref="Cookie"/> once, then clears this flag (single-consumer). Only the
    /// owning peer receives a <c>SHIP_RESET</c>, so this is effectively own-ship-only.
    /// </summary>
    public bool HasPendingReset { get; internal set; }

    /// <summary>P540 — set by <c>ClientWorld.Apply(Eject)</c> for the OWN ship
    /// when the server broadcast its ejection into a drifting Lifepod hull
    /// (<c>FM_S_EJECT</c>, appmsg.cpp:230-237). The single consumer is the P540
    /// own-ship host in Game1: it re-seeds the local simulation to the pod pose
    /// (see <see cref="ShipEntity.Latest"/>), engages the rescue autopilot
    /// toward the nearest friendly station/probe, plays the engage sound, then
    /// clears this flag via <c>ClientWorld.ClearOwnEjectFlag</c>. Only the
    /// ejecting peer sees itself as <c>IsMine</c>, so this is own-ship-only.
    /// </summary>
    public bool HasPendingEject { get; internal set; }

    /// <summary>P448b — the LOCAL wall-clock arrival time (seconds since boot,
    /// <c>Environment.TickCount64 / 1000.0</c>) of the most recent
    /// <see cref="Latest"/> payload. This is purely LOCAL client meta, not wire
    /// truth — the wire's own <c>Time</c> field is the server tick index and is
    /// kept separately in <see cref="LastUpdateTick"/>. It anchors the P448b
    /// per-remote-ship dead-reckoner (<c>Flight.ShipDeadReckoner</c>) at the
    /// moment the update was received, so the replay's "now" is measured in the
    /// same monotonic clock as the seed. Stamped in <c>ClientWorld</c> at each
    /// of the three pose-arrival sites (SetCluster / SingleShipUpdate /
    /// HeavyShipsUpdate).</summary>
    public double LatestArrivalTime { get; internal set; }

    // ── SHIP_STATUS(90) fields, attached incrementally by that message ──────

    /// <summary>P761b — the ship's <c>c_cmdAccepted</c> command slot as relayed by
    /// <c>ORDER_CHANGE(93)</c>: the action id (<c>CommandID</c>, native
    /// <c>m_commandIDs[c_cmdAccepted]</c>) and the target model it acts on. Native
    /// applies ORDER_CHANGE to ANY ship in the client core
    /// (<c>clintlib/appmsg.cpp:1831-1855</c>), and the server forwards every
    /// accepted-command change to the whole side
    /// (<c>FedSrv/FedSrv.CPP:13695-13732</c>), which is how the team pane's
    /// OBJECTIVE column sorts by command text (<c>CurrentCommandText</c>,
    /// <c>WinTrek/teampane.cpp:1660-1676</c>).</summary>
    public short AcceptedCommandId { get; internal set; } = OrderChange.CidNone;

    /// <summary>The <see cref="AcceptedCommandId"/> target's
    /// <c>ObjectType</c> (<c>IgcObjectType</c>), or null when the slot has no
    /// target (<c>OT_invalid</c>/<c>NA</c> on the wire).</summary>
    public short? AcceptedTargetType { get; internal set; }

    /// <summary>The <see cref="AcceptedCommandId"/> target's object id
    /// (native <c>m_commandTargets[c_cmdAccepted]</c>'s id).</summary>
    public short AcceptedTargetId { get; internal set; } = -1;

    /// <summary>Status.GetHullID().</summary>
    public short HullId { get; internal set; }

    /// <summary>Status.GetSectorID() — the sector the ship is currently in.</summary>
    public short SectorId { get; internal set; }

    /// <summary>HUD-18 — true once <see cref="SectorId"/> came from real sector
    /// telemetry (<c>SHIP_STATUS</c>'s <c>Status.GetSectorID()</c>,
    /// <c>SET_CLUSTER</c>, an in-cluster <c>HEAVY_SHIPS_UPDATE</c>, a relaunch at
    /// its carrier or a promoted turret seat). A brand-new <see cref="ShipEntity"/>
    /// keeps the C# default <c>SectorId = 0</c> — and 0 is a real cluster id in
    /// some missions — so a ship known only from <c>EXPORT(26)</c> must not be
    /// reported as being in sector 0. Native's <c>PlayerInfo::LastSeenSector()</c>
    /// equivalent (<c>WinTrek/consoledata.cpp:76-85</c>) only exists after a
    /// status, and this flag is the port's "a status has landed" marker.</summary>
    public bool StatusSectorKnown { get; internal set; }

    /// <summary>Status.GetStationID() (NA/-1 when flying free).</summary>
    public short StationId { get; internal set; }

    /// <summary>Status.GetParentID() (NA/-1 when not docked/turreted).</summary>
    // Native starts unparented (Igc/shipIGC.cpp:37); ship 0 is a real id.
    public short ParentId { get; internal set; } = -1;

    /// <summary>The ordered ShipState enum value (bits 0-5).</summary>
    public byte State { get; internal set; }

    /// <summary>bit6 — true once the server hides this ship from our side
    /// (a cross-side SHIP_STATUS(90) hidden transition, P493): we no longer
    /// know where it is. Cleared again by the next spotted status.</summary>
    public bool Unknown { get; internal set; }

    /// <summary>bit7 — the ship's own side was detected by an enemy (the
    /// <c>SetDetected</c> side of native ShipStatusSpotted/Hidden). Not meaningfully
    /// populated for cross-side copies (the seeing side's status carries it false);
    /// retained for wire parity.</summary>
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

    /// <summary>P626 — the ship's cargo hold entries (unmounted spare parts,
    /// each with a <em>negative</em> mount -1..-5 encoded as a high-bit byte),
    /// from the most recent BUY_LOADOUT_ACK. Null until the first ack carries
    /// cargo; empty when the hold is empty. Split out of the ack's part tail
    /// (native mixes mounted + cargo into one tail, so this port separates them
    /// by mount sign: non-negative → <see cref="Loadout"/>, high-bit → here).
    /// Drives the in-flight NextCargo/PrevPart/NextPart/DropCargo/Reload keys.</summary>
    public IReadOnlyList<ExpandedPartData>? Cargo { get; internal set; }

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
