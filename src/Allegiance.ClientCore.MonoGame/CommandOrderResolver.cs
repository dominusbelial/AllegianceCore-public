using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame;

public partial class Game1
{
    /// <summary>
    /// P425 — the pure, headlessly-testable ORDER-CHANGE payload for a
    /// commandee+target pair. The MonoGame client now has a "commandee" selection
    /// (the same-side ship/drone the player is commanding, distinct from the
    /// target reticle — see <c>SelectCommandee</c>) and an issue-order key
    /// (<c>TryIssueOrderToCommandee</c>). This resolver maps the commandee's
    /// ROLE (its hull's <c>HabmCapabilities</c>) and the target's KIND to the
    /// real <c>FM_CS_ORDER_CHANGE(93)</c> payload (<c>ShipID</c> = commandee,
    /// <c>CommandId</c>, <c>ObjectType</c>, <c>ObjectID</c>) the client sends.
    ///
    /// Kept general on purpose: the order follows from the commandee's role + the
    /// target's kind, so a new order is a new case HERE rather than a special-case
    /// in the key handler. Only BUILD is implemented (a Builder-role commandee at
    /// an asteroid target → <c>c_cidBuild</c> at that rock) because the server's
    /// direct-drone command path (P425, <c>Server.HandleDirectNpcBuildOrder</c>)
    /// currently handles only Build; a Miner/Goto order would fall out of the same
    /// plumbing at near-zero extra cost once the server grows a matching direct-NPC
    /// arm (documented P425 scope boundary, not silently dropped).
    ///
    /// Builder-role = the commandee's hull carries <c>c_habmBuilder</c> (0x8000,
    /// igc.h:741) — the same ability-bit the server's legality gate reads
    /// (<c>DataHullTypeIGC.HabmBuilder</c>), so the client pre-check agrees with
    /// the server's authoritative refusal.
    /// </summary>
    internal readonly record struct CommandOrder(short CommandId, IgcObjectType ObjectType, short ObjectId);

    internal static class CommandOrderResolver
    {
        /// <summary>Command a same-side Builder-role ship at an asteroid target:
        /// <c>c_cidBuild</c> at that rock (native <c>OfflineCommandToDrone</c>
        /// resolves a Builder drone's default order to <c>c_cidBuild</c> against a
        /// compatible asteroid, clintlib.cpp:2212). Any other pair yields null — no
        /// applicable order (a non-Builder commandee, or a Builder ordered at
        /// something other than an asteroid).</summary>
        public static CommandOrder? Resolve(short commandeeRoleBits, TargetKind commandeeKind, TargetKind targetKind, short targetId)
        {
            if (commandeeKind != TargetKind.Ship) return null;   // only ships/drones are commanded
            bool isBuilder = (commandeeRoleBits & DataHullTypeIGC.HabmBuilder) != 0;
            if (isBuilder && targetKind == TargetKind.Asteroid)
                return new CommandOrder(OrderChange.CidBuild, IgcObjectType.Asteroid, targetId);
            return null;
        }
    }
}
