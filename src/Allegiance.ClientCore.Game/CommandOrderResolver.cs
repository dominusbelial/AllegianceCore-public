using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame;

public partial class Game1
{
    // Native console.cpp:1450-1504 issues CHAT commands to individual/group
    // recipients. ORDER_CHANGE acknowledges one's own command slot; it does
    // not deliver a queued order to another player.
    internal readonly record struct CommandOrder(short CommandId, IgcObjectType ObjectType, short ObjectId);

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

        // Default order selection: Igc/shipIGC.h:2071-2161. Capability and
        // target legality are rechecked by the authoritative server.
        public static CommandOrder? ResolveChat(short roles, TargetKind recipient, TargetKind target, short id, bool friendly)
        {
            if (recipient != TargetKind.Ship) return null;
            var special = Resolve(roles, recipient, target, id);
            if (special is not null) return special;
            if ((roles & DataHullTypeIGC.HabmMiner) != 0 && target == TargetKind.Asteroid)
                return new(OrderChange.CidMine, IgcObjectType.Asteroid, id);
            IgcObjectType? type = target switch
            {
                TargetKind.Ship => IgcObjectType.Ship,
                TargetKind.Station => IgcObjectType.Station,
                TargetKind.Asteroid => IgcObjectType.Asteroid,
                TargetKind.Warp => IgcObjectType.Warp,
                _ => null
            };
            if (type is null) return null;
            short command = target is TargetKind.Ship or TargetKind.Station
                ? friendly ? target == TargetKind.Ship ? OrderChange.CidDefend : OrderChange.CidGoto : OrderChange.CidAttack
                : OrderChange.CidGoto;
            if ((roles & (DataHullTypeIGC.HabmBuilder | DataHullTypeIGC.HabmMiner)) != 0) command = OrderChange.CidGoto;
            return new(command, type.Value, id);
        }
    }
}
