using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame;

public partial class Game1
{
    // Native console.cpp:1450-1504 issues CHAT commands to individual/group
    // recipients. ORDER_CHANGE acknowledges one's own command slot; it does
    // not deliver a queued order to another player.
    internal readonly record struct CommandOrder(short CommandId, IgcObjectType ObjectType, short ObjectId);

    /// <summary>The target half of the generic target-to-order decision: the
    /// target's kind/id plus the side-relationship and the capability data the
    /// native table reads. Optional fields are null when the client cannot
    /// resolve them; the resolver then falls back to the default branch, and
    /// the authoritative server rechecks everything anyway.</summary>
    internal readonly record struct CommandTargetProfile(
        TargetKind Kind,
        short Id,
        bool? Friendly,          // null = no side (neutral); true = same side or allied
        short? TargetHullAbm,    // OT_ship: the target hull's HabmCapabilities
        short? StationSabm,      // OT_station: the station type's SabmCapabilities
        short? ProbeEabm);       // OT_probe: the probe type's EabmCapabilities

    internal static class CommandOrderResolver
    {
        public static CommandOrder? Resolve(short commandeeRoleBits, TargetKind commandeeKind, TargetKind targetKind, short targetId)
        {
            if (commandeeKind != TargetKind.Ship) return null;   // only ships/drones are commanded
            bool isBuilder = (commandeeRoleBits & DataHullTypeIGC.HabmBuilder) != 0;
            if (isBuilder && targetKind == TargetKind.Asteroid)
                return new CommandOrder(OrderChange.CidBuild, IgcObjectType.Asteroid, targetId);
            return null;
        }

        /// <summary>
        /// P757 — the full generic target-to-order table, faithful to native
        /// <c>IshipIGC::GetDefaultOrder</c> (Igc/shipIGC.h:2071-2161): the
        /// command a drone/wingman issues when the player targets something
        /// without naming a verb (native <c>SetTarget(pmodel, c_cidDefault)</c>,
        /// WinTrek.cpp:8830, and the compose chat path
        /// <c>pshipRecipient-&gt;GetDefaultOrder(pmodel)</c>, trekigc.cpp:4716-4725).
        /// The commandee's hull role bits are its <c>HabmCapabilities</c> (the
        /// client-side mirror of native's pilot-type switch — this port's
        /// EXPORT_SHIP only carries player ships, so the drone's role is read
        /// from its hull like the P425 Build gate); <paramref name="commandeeIsLayer"/>
        /// is the caller's DroneTypes lookup (hull → PilotType c_ptLayer). The
        /// target profile carries the side relationship plus the target's
        /// hull/station/probe capabilities when the client can resolve them.
        /// Capability and target legality are rechecked by the authoritative
        /// server.
        /// </summary>
        public static CommandOrder? ResolveDefaultOrder(
            short commandeeRoles,
            bool commandeeIsLayer,
            bool commandeeHasParent,
            in CommandTargetProfile target)
        {
            if (target.Kind == TargetKind.Cluster) return null;   // not a model target
            var type = ObjectTypeOf(target.Kind);
            if (type is null) return null;

            short cid = OrderChange.CidGoto;   // native's starting default (shipIGC.h:2076)

            bool miner = (commandeeRoles & DataHullTypeIGC.HabmMiner) != 0;
            bool builder = (commandeeRoles & DataHullTypeIGC.HabmBuilder) != 0;

            if (miner || builder)
            {
                // Miner/Builder: an asteroid carrying the commandee's orders ABM
                // becomes Mine/Build (shipIGC.h:2080-2090). The client cannot see
                // the per-drone orders ABM, so it applies the hull role directly:
                // a Miner commands Mine at an asteroid, a Builder Build (the same
                // split native's pshipTo->GetOrdersABM() produces for the stock
                // core's miner/builder hulls). Anything else stays Goto.
                if (target.Kind == TargetKind.Asteroid)
                    cid = miner ? OrderChange.CidMine : OrderChange.CidBuild;
            }
            else if (commandeeIsLayer)
            {
                // Layer: a buoy or a warp becomes Build (shipIGC.h:2092-2097).
                // The port has no buoys to click; the warp half is live.
                if (target.Kind == TargetKind.Warp)
                    cid = OrderChange.CidBuild;
            }
            else
            {
                // Wingman/Player/CheatPlayer (shipIGC.h:2099-2161).
                if (target.Friendly == true)
                {
                    cid = OrderChange.CidDefend;
                    if (!commandeeHasParent)
                    {
                        if (target.Kind == TargetKind.Ship)
                        {
                            // Lifepod/rescue pairing → Pickup; carrier → Goto
                            // (shipIGC.h:2108-2119).
                            if (target.TargetHullAbm is { } habm)
                            {
                                bool targetIsLifepod = (habm & DataHullTypeIGC.HabmLifepod) != 0;
                                bool targetIsRescue = (habm & DataHullTypeIGC.HabmRescue) != 0;
                                bool mineIsRescue = (commandeeRoles & DataHullTypeIGC.HabmRescue) != 0;
                                bool mineIsLifepod = (commandeeRoles & DataHullTypeIGC.HabmLifepod) != 0;
                                if ((targetIsLifepod && mineIsRescue) || (targetIsRescue && mineIsLifepod))
                                    cid = OrderChange.CidPickup;
                                else if ((habm & DataHullTypeIGC.HabmCarrier) != 0
                                         && (commandeeRoles & DataHullTypeIGC.HabmLandOnCarrier) != 0)
                                    cid = OrderChange.CidGoto;
                            }
                        }
                        else if (target.Kind == TargetKind.Station)
                        {
                            // Station capability ladder (shipIGC.h:2121-2136):
                            // non-fighter checks ONLY CapLand (shipIGC.h:2124-2128);
                            // a fighter checks Rescue|Land when it is a lifepod and
                            // Land otherwise (shipIGC.h:2129-2135). The branches are
                            // exclusive — a non-fighter with Land but no CapLand
                            // stays Defend rather than falling through to the Land
                            // branch. Anything else stays Defend.
                            if (target.StationSabm is { } sabm)
                            {
                                bool fighter = (commandeeRoles & DataHullTypeIGC.HabmFighter) != 0;
                                bool lifepod = (commandeeRoles & DataHullTypeIGC.HabmLifepod) != 0;
                                if (!fighter)
                                {
                                    if ((sabm & DataStationTypeIGC.SabmCapLand) != 0)
                                        cid = OrderChange.CidGoto;
                                }
                                else if (lifepod)
                                {
                                    if ((sabm & (DataStationTypeIGC.SabmRescue | DataStationTypeIGC.SabmLand)) != 0)
                                        cid = OrderChange.CidGoto;
                                }
                                else if ((sabm & DataStationTypeIGC.SabmLand) != 0)
                                    cid = OrderChange.CidGoto;
                            }
                        }
                        else if (target.Kind == TargetKind.Probe)
                        {
                            // Own lifepod + rescue probe → Goto (shipIGC.h:2137-2146).
                            if (target.ProbeEabm is { } eabm
                                && (commandeeRoles & DataHullTypeIGC.HabmLifepod) != 0
                                && (eabm & (DataStationTypeIGC.SabmRescue | DataStationTypeIGC.SabmRescueAny)) != 0)
                                cid = OrderChange.CidGoto;
                        }
                    }
                }
                else
                {
                    // Enemy/neutral side (shipIGC.h:2147-2161): treasure → Pickup;
                    // probe → Attack (Goto for a lifepod against RescueAny); any
                    // sided model → Attack; sideless (asteroid/warp) stays Goto.
                    if (target.Kind == TargetKind.Treasure)
                        cid = OrderChange.CidPickup;
                    else if (target.Kind == TargetKind.Probe)
                    {
                        cid = OrderChange.CidAttack;
                        if (target.ProbeEabm is { } eabm
                            && (commandeeRoles & DataHullTypeIGC.HabmLifepod) != 0
                            && (eabm & DataStationTypeIGC.SabmRescueAny) != 0)
                            cid = OrderChange.CidGoto;
                    }
                    else if (target.Friendly == false)
                        cid = OrderChange.CidAttack;
                }
            }

            return new CommandOrder(cid, type.Value, target.Id);
        }

        private static IgcObjectType? ObjectTypeOf(TargetKind kind) => kind switch
        {
            TargetKind.Ship => IgcObjectType.Ship,
            TargetKind.Station => IgcObjectType.Station,
            TargetKind.Asteroid => IgcObjectType.Asteroid,
            TargetKind.Warp => IgcObjectType.Warp,
            TargetKind.Probe => IgcObjectType.Probe,
            TargetKind.Treasure => IgcObjectType.Treasure,
            _ => null,
        };
    }
}
