using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    /// <summary>
    /// P784 — pure per-recipient order fan-out. Native's
    /// <c>ConsoleDataImpl::SendChatInternal</c> sends ONE chat per subject when
    /// the recipient is a group (the <c>CHAT_GROUP</c> subject loop,
    /// WinTrek/console.cpp:1546-1564: the first subject goes out as
    /// <c>CHAT_GROUP</c>, every subsequent one as <c>CHAT_GROUP_NOECHO</c>),
    /// and a wing recipient's subject list is EVERY same-side ship whose wing
    /// matches (<c>SetRecipient</c>'s <c>CHAT_WING</c> sweep,
    /// console.cpp:398-406). This port's server cannot deliver a drone order on
    /// a Wing/Group target (<c>TryCommandToDrone</c> routes Individual-family
    /// chats only), so the fan-out happens client-side: the order goes out as
    /// one existing <c>FM_CS_CHATMESSAGE(23)</c> Individual order per
    /// recipient — the same loop shape, one send per subject.
    /// </summary>
    internal static class CommandFanout
    {
        /// <summary>Resolve the ships ONE issued order goes to.
        /// <paramref name="group"/> is the drag/shift-click selection
        /// (console.cpp:2128-2140); when non-empty it wins. Otherwise a wing
        /// recipient selection (a command-bar column ≥ <c>c_iRecipientWing0</c>
        /// = 4, console.cpp:13-18) expands to every same-side ship in that
        /// wing — SELF INCLUDED, because native's CHAT_WING subject sweep adds
        /// every side ship with the matching wing (console.cpp:398-406), the
        /// player's own among them. With no group and no wing, the single
        /// <paramref name="commandee"/> is the only recipient. Call under the
        /// session gate.</summary>
        public static List<AutopilotTarget> Recipients(
            IReadOnlyList<AutopilotTarget> group, AutopilotTarget? commandee,
            bool wingSelected, short wingId, short self,
            IReadOnlyDictionary<short, ShipEntity> ships, short? mySide)
        {
            if (group.Count > 0)
                return group.ToList();

            if (wingSelected && mySide is { } side)
            {
                var wing = new List<AutopilotTarget>();
                foreach (var (id, ship) in ships)
                {
                    if (ship.SideId != side || ship.WingId != wingId) continue;
                    string name = string.IsNullOrEmpty(ship.CharacterName) ? $"ship {id}" : ship.CharacterName;
                    wing.Add(new AutopilotTarget(TargetKind.Ship, id, name));
                }
                if (wing.Count > 0)
                    return wing;
            }

            return commandee is { } cmd ? [cmd] : [];
        }
    }
}
