using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P818 — the compose's individual/group recipient selection, the port of
/// native <c>ConsoleDataImpl</c>'s subject handling (WinTrek/console.cpp:66-455)
/// plus the two places it is consumed: the wire (<c>SendChatInternal</c>,
/// console.cpp:1448-1507) and the radar's <c>c_maskSubject</c> brackets
/// (WinTrek/radarimage.cpp:570-574,651).
///
/// <para><b>How native picks and shows them</b></para>
/// <list type="bullet">
/// <item>A LEFT click on a ship (console.cpp:2155-2185) makes it the
/// <c>CHAT_INDIVIDUAL</c> recipient: <c>PickShip</c> starts the
/// <c>MouseCommand</c> compose when none is open (console.cpp:1799-1812) and
/// <c>SetRecipient(CHAT_INDIVIDUAL, oid, pship)</c> puts it in the subject
/// list (console.cpp:299-309).</item>
/// <item>SHIFT + left click toggles that ship in/out of the list
/// (<c>ToggleSubject</c>, console.cpp:2167-2170 → :123-158); one survivor is
/// <c>CHAT_INDIVIDUAL</c>, two or more <c>CHAT_GROUP</c>, and the last ship
/// can never be toggled out.</item>
/// <item>A left DRAG over friendly ships selects them all into
/// <c>CHAT_GROUP</c> (<c>SetRecipient(CHAT_GROUP, NA, NULL, &amp;ships)</c>,
/// console.cpp:2130-2147); an empty rectangle quits composing.</item>
/// <item>The compose panel's recipient box shows the picked ship's name for an
/// individual and the literal "Group" for a group (console.cpp:1345-1346,
/// 1381-1386) — the port's compose label mirrors that
/// (<see cref="ChatTargetLogic.ResolvedLabel"/>).</item>
/// <item>The radar brackets every ship in the list while a compose is open and
/// only in the command camera (radarimage.cpp:570-574,651;
/// <see cref="RadarOverlayRules.SubjectsApply"/>/<see cref="RadarOverlayRules.IsSubject"/>).</item>
/// </list>
///
/// <para><b>What they send</b> — the existing chat wire, no new fields:
/// Individual is one <c>CHATMESSAGE</c> addressed to the ship
/// (<c>oidRecipient</c>); Group is one message per subject, the first
/// <c>CHAT_GROUP</c> and the rest <c>CHAT_GROUP_NOECHO</c>
/// (console.cpp:1470-1491), which the server already rewrites to Individual
/// (FedSrv.CPP:3531-3536). <see cref="ChatSendPlan"/> is that loop, pure.</para>
///
/// <para><b>Which list the radar sees</b> — native's <c>GetSubjects()</c>
/// returns <c>m_pchsCurrent</c>'s list (console.cpp:2327-2333), so the CHAT
/// composes read <c>m_chsChat</c>'s list and the MOUSE-COMMAND/COMMAND composes
/// read <c>m_chsCommand</c>'s list. The port mirrors that: the chat states read
/// <see cref="_chatRecipient"/>, the command states read the command selection
/// the port already keeps (<c>_commandee</c>/<c>_commandGroup</c>, P784).</para>
/// </summary>
public sealed partial class Game1
{
    /// <summary>Native <c>GetMission() != NULL</c> (<c>shipIGC.h</c>): the port
    /// only knows a ship while the world roster still holds it. Call under the
    /// session gate.</summary>
    private static bool ShipInMission(ClientWorld world, short shipId) =>
        world.Ships.TryGetValue(shipId, out var ship) && !ship.Unknown;

    /// <summary>Native <c>GetPilotType() &gt;= c_ptPlayer</c> (Igc/igc.h:685):
    /// the port learns player identity from <c>PLAYERINFO(10)</c> only, so a
    /// ship without one is a server-spawned NPC (drone/miner/builder/layer) —
    /// exactly the case native converts to a Team chat (console.cpp:255-266).
    /// Call under the session gate.</summary>
    private static bool ShipPlayerPiloted(ClientWorld world, short shipId) =>
        world.Ships.TryGetValue(shipId, out var ship)
        && RadarOverlayRules.PilotTypeOf(ship.Player) >= RadarOverlayRules.PilotTypePlayer;

    /// <summary>Native <c>StartComposing(c_csComposeChat, &amp;m_chsChat)</c>'s
    /// <c>Reset(cs == c_csComposeChat)</c> (console.cpp:1656): on every compose
    /// START, drop ships that left the mission and convert an Individual
    /// recipient that is yourself or a drone into Team (console.cpp:255-266).</summary>
    private void ResetChatSubjectOnStart()
    {
        lock (_session.Gate)
        {
            var world = _session.World;
            _chatRecipient.Reset(starting: true, world.MyShipId ?? -1,
                id => ShipInMission(world, id), id => ShipPlayerPiloted(world, id));
        }
    }

    /// <summary>Native <c>SendChatInternal</c>'s <c>m_pchsCurrent-&gt;Reset()</c>
    /// (console.cpp:1453): revalidate the subject list just before the send.
    /// Call under the session gate.</summary>
    private void ResetChatSubjectBeforeSend(ClientWorld world) =>
        _chatRecipient.Reset(starting: false, world.MyShipId ?? -1,
            id => ShipInMission(world, id), id => ShipPlayerPiloted(world, id));

    /// <summary>P818 — LEFT pick on a FRIENDLY ship is native's <c>PickShip</c>
    /// (console.cpp:2155-2185,1799-1812): the pick becomes the Individual
    /// recipient, SHIFT toggles it in/out of the group, and a pick when no
    /// compose is open starts the <c>MouseCommand</c> compose. Call under the
    /// session gate (it reads the world roster).</summary>
    private void ApplyLeftPickSubjects(AutopilotTarget picked, bool shiftAdd, bool wasComposing)
    {
        var world = _session.World;
        short self = world.MyShipId ?? -1;
        if (wasComposing && shiftAdd)
            _chatRecipient.ToggleSubject(picked.Id, self);            // console.cpp:2167-2170
        else
            _chatRecipient.SetIndividual(picked.Id, self,
                id => ShipInMission(world, id));                      // console.cpp:2172-2175
    }

    /// <summary>P818 — native's drag rectangle sets <c>CHAT_GROUP</c> over every
    /// friendly ship it caught (console.cpp:2130-2147). Call under the session
    /// gate.</summary>
    private void ApplyGroupSubjects(IReadOnlyList<AutopilotTarget> ships)
    {
        var world = _session.World;
        short self = world.MyShipId ?? -1;
        var ids = new short[ships.Count];
        for (int i = 0; i < ships.Count; i++) ids[i] = ships[i].Id;
        _chatRecipient.SetGroup(ids, self, id => ShipInMission(world, id));
    }

    /// <summary>P818 — native <c>OnRecipientButton</c>'s <c>c_iRecipientMe</c>
    /// column is <c>PickShip(trekClient.GetShip())</c> + <c>SetRecipient(
    /// CHAT_INDIVIDUAL, NA, ownShip)</c> (console.cpp:1144-1149): a chat to
    /// yourself. Every other column is a channel
    /// (<c>SetRecipient(ct, NA, NULL)</c>, console.cpp:1152-1187).</summary>
    private void ApplyRecipientButton(ChatTarget target)
    {
        if (target != ChatTarget.Individual) { _chatRecipient.SetChannel(target); return; }
        lock (_session.Gate)
        {
            var world = _session.World;
            short self = world.MyShipId ?? -1;
            if (self >= 0) _chatRecipient.SetIndividual(self, self, id => ShipInMission(world, id));
            else _chatRecipient.SetChannel(ChatTarget.Team);
        }
    }

    /// <summary>The compose's ship list as the radar sees it — native
    /// <c>GetSubjects()</c> → <c>m_pchsCurrent-&gt;m_shipsSubject</c>
    /// (console.cpp:2327-2333). Between the chat and command composes this is
    /// <c>m_chsChat</c> vs <c>m_chsCommand</c>, hence the two sources; the
    /// buffer is reused so the radar pass allocates nothing per frame.</summary>
    private readonly List<short> _composeSubjectIds = [];

    private IReadOnlyList<short> ComposeSubjectIds(NativeComposeState state)
    {
        _composeSubjectIds.Clear();
        if (state is NativeComposeState.Command or NativeComposeState.MouseCommand)
        {
            // m_chsCommand's list: the command compose's recipients (P784's
            // commandee/group selection, set by the same pick/rectangle paths).
            foreach (var target in _commandGroup) _composeSubjectIds.Add(target.Id);
            if (_commandGroup.Count == 0 && _commandee is { } commandee) _composeSubjectIds.Add(commandee.Id);
        }
        else
        {
            foreach (short id in _chatRecipient.Ships) _composeSubjectIds.Add(id);
        }
        return _composeSubjectIds;
    }

    /// <summary>The recipient the compose's subject list belongs to. Native
    /// derives CHAT_INDIVIDUAL from one subject and CHAT_GROUP from several
    /// (console.cpp:144-157,286-297), and a wing selected on the command bar
    /// sweeps the wing's ships (console.cpp:398-406).</summary>
    private ChatTarget ComposeSubjectTarget(NativeComposeState state, int subjectCount)
    {
        if (state is NativeComposeState.Command or NativeComposeState.MouseCommand)
        {
            // A wing column (c_iRecipientWing0 = 4, console.cpp:13-18) addresses
            // every same-side ship in that wing; otherwise the picked subjects.
            if (_commandRecipientColumn >= 4) return ChatTarget.Wing;
            return subjectCount >= 2 ? ChatTarget.Group : ChatTarget.Individual;
        }
        return _chatTarget;
    }
}
