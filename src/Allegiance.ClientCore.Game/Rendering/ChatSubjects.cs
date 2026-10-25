using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P818 — the port of native's <c>ChatSubject</c> (WinTrek/console.cpp:66-267):
/// the compose's recipient PLUS the ship list it addresses. Native keeps two
/// instances — <c>m_chsChat(CHAT_TEAM)</c> for a chat compose (console.cpp:644)
/// and <c>m_chsCommand</c> for a command compose — and both survive
/// <c>QuitComposing</c>, which is why a recipient picked once is still the
/// recipient the next time the composer opens.
///
/// <para>The ship list (<c>m_shipsSubject</c>, console.cpp:73) is what native
/// hands to the radar: <c>GetSubjects()</c> returns it and
/// <c>RadarImage::RenderBlips</c> sets <c>c_maskSubject</c> for every model it
/// contains (radarimage.cpp:572-574,651). That is the whole reason this type
/// exists — the port's composer had no individual/group recipient list, so
/// those brackets never drew (parity row "Radar command subjects").</para>
///
/// <para>Recipient derivation is native's, verbatim:</para>
/// <list type="bullet">
/// <item><b>SetRecipient(CHAT_GROUP, …)</b> rebuilds the list from the passed
/// ship list; ONE live ship decays to <see cref="ChatTarget.Individual"/>, NONE
/// decays to <see cref="ChatTarget.Team"/> (console.cpp:278-298).</item>
/// <item><b>SetRecipient(CHAT_INDIVIDUAL, …)</b> adds the single ship, or
/// decays to Team when it has no mission (console.cpp:299-309).</item>
/// <item><b>ToggleSubject</b> (shift-click, console.cpp:123-158) adds/removes
/// one ship and re-derives Individual (1) vs Group (n&gt;1); the LAST ship can
/// never be toggled out (console.cpp:128-133).</item>
/// <item><b>AddSubject</b> always puts the player's OWN ship first
/// (console.cpp:104-109) so the local echo of a group chat sorts first.</item>
/// <item><b>Reset</b> (console.cpp:227-267) drops ships that left the mission,
/// re-derives the target, and — on a compose START — converts an Individual
/// recipient that is yourself or a drone into Team ("Chats to a drone or myself
/// convert to the team", console.cpp:255-266).</item>
/// </list>
///
/// <para>Pure (no MonoGame types, no world access): every rule is
/// unit-testable, the <see cref="ChatTargetLogic"/> discipline. The caller
/// passes the world predicates (<c>GetMission() != NULL</c> ⇒
/// <c>inMission</c>; <c>GetPilotType() &lt; c_ptPlayer</c> ⇒ not
/// <c>playerPiloted</c>) so the type stays headless.</para>
/// </summary>
public sealed class ChatSubjects
{
    private readonly List<short> _ships = [];

    /// <summary>The recipient the compose will send to — native
    /// <c>m_ctRecipient</c> (console.cpp:68). A fresh instance is
    /// <see cref="ChatTargetLogic.DefaultTarget"/> = Team (console.cpp:644).</summary>
    public ChatTarget Target { get; private set; } = ChatTargetLogic.DefaultTarget;

    /// <summary>The compose's ship list — native <c>m_shipsSubject</c>
    /// (console.cpp:73), own ship first. This is what the radar's
    /// <c>c_maskSubject</c> test reads (radarimage.cpp:651).</summary>
    public IReadOnlyList<short> Ships => _ships;

    /// <summary>Native <c>m_shipsSubject.n()</c>.</summary>
    public int Count => _ships.Count;

    /// <summary>Native <c>m_shipsSubject.find(pship) != NULL</c>
    /// (radarimage.cpp:651).</summary>
    public bool Contains(short shipId) => _ships.Contains(shipId);

    /// <summary>The single Individual recipient (native
    /// <c>m_oidRecipient</c>, console.cpp:70), or null for any other target.</summary>
    public short? IndividualShipId =>
        Target == ChatTarget.Individual && _ships.Count == 1 ? _ships[0] : null;

    // ── primitives (console.cpp:88-121) ─────────────────────────────────────

    /// <summary>Native <c>PurgeSubjects</c> (console.cpp:88-96).</summary>
    public void PurgeSubjects() => _ships.Clear();

    /// <summary>Native <c>AddSubject</c> (console.cpp:98-111): ignore a
    /// duplicate; put the player's own ship at the head of the list, everyone
    /// else at the tail.</summary>
    private void AddSubject(short shipId, short selfId)
    {
        if (_ships.Contains(shipId)) return;
        if (selfId >= 0 && shipId == selfId) _ships.Insert(0, shipId);
        else _ships.Add(shipId);
    }

    // ── SetRecipient (console.cpp:269-455) ─────────────────────────────────

    /// <summary>SetRecipient for a channel whose ship list the port derives at
    /// radar time (Team/Allies/Everyone/Wing/FriendlySector/AllSector/Admin/
    /// Leaders): purge the ship list, keep the target. Native rebuilds the list
    /// with the same sweep the radar test re-runs (console.cpp:329-451), so
    /// nothing is lost by not materializing it here.</summary>
    public void SetChannel(ChatTarget target)
    {
        PurgeSubjects();
        Target = target;
    }

    /// <summary>SetRecipient(CHAT_INDIVIDUAL, oid, pship) — console.cpp:299-309.
    /// A ship that is no longer in the mission decays to Team, exactly like
    /// native's <c>pbaseRecipient-&gt;GetMission() == NULL</c> branch.</summary>
    /// <param name="inMission">Native <c>GetMission() != NULL</c>.</param>
    public void SetIndividual(short shipId, short selfId, Func<short, bool>? inMission = null)
    {
        PurgeSubjects();
        if (inMission is not null && !inMission(shipId)) { Target = ChatTarget.Team; return; }
        AddSubject(shipId, selfId);
        Target = ChatTarget.Individual;
    }

    /// <summary>SetRecipient(CHAT_GROUP, NA, NULL, &amp;pships) — the drag
    /// rectangle (console.cpp:2144-2147) and the group decay rules
    /// (console.cpp:278-298): only ships still in the mission are added, one
    /// survivor becomes Individual, none becomes Team.</summary>
    /// <param name="inMission">Native <c>GetMission() != NULL</c>.</param>
    public void SetGroup(IReadOnlyList<short> shipIds, short selfId, Func<short, bool>? inMission = null)
    {
        PurgeSubjects();
        foreach (short id in shipIds)
            if (inMission is null || inMission(id))
                AddSubject(id, selfId);

        if (_ships.Count == 1) Target = ChatTarget.Individual;
        else if (_ships.Count == 0) Target = ChatTarget.Team;
        else Target = ChatTarget.Group;
    }

    /// <summary>Native <c>ToggleSubject</c> (console.cpp:123-158): shift-click
    /// add/remove, with the last ship protected, then re-derive the recipient.</summary>
    public void ToggleSubject(short shipId, short selfId)
    {
        int index = _ships.IndexOf(shipId);
        if (index >= 0)
        {
            //Do not allow the last person to be toggled out of the group
            if (_ships.Count > 1) _ships.RemoveAt(index);
        }
        else
        {
            AddSubject(shipId, selfId);
        }

        Target = _ships.Count == 1 ? ChatTarget.Individual : ChatTarget.Group;
    }

    /// <summary>Native <c>Reset(bStart)</c> (console.cpp:227-267), called when a
    /// compose starts (<c>StartComposing</c> → <c>Reset(cs == c_csComposeChat)</c>,
    /// console.cpp:1656) and before every send (<c>SendChatInternal</c>,
    /// console.cpp:1453). Drops ships that left the mission, re-derives the
    /// Individual/Group/Team decay, and on a compose start converts an
    /// Individual recipient that is yourself or a drone into Team
    /// (console.cpp:255-266: "Chats to a drone or myself convert to the team").</summary>
    /// <param name="starting">Native <c>bStart</c>.</param>
    /// <param name="inMission">Native <c>GetMission() != NULL</c>.</param>
    /// <param name="playerPiloted">Native <c>GetPilotType() &gt;= c_ptPlayer</c>;
    /// null treats every ship as player-piloted (no drone conversion).</param>
    public void Reset(bool starting, short selfId,
        Func<short, bool>? inMission = null, Func<short, bool>? playerPiloted = null)
    {
        if (Target == ChatTarget.Group)
        {
            if (inMission is not null) _ships.RemoveAll(id => !inMission(id));
            if (_ships.Count == 1) Target = ChatTarget.Individual;
            else if (_ships.Count == 0) Target = ChatTarget.Team;
        }
        else if (Target == ChatTarget.Individual)
        {
            // SetRecipient(m_ctRecipient, …) — the individual ship is revalidated.
            if (_ships.Count != 1 || (inMission is not null && !inMission(_ships[0])))
            {
                PurgeSubjects();
                Target = ChatTarget.Team;
            }
        }
        else
        {
            // Channel targets rebuild their list in native; the port derives it
            // at radar time, so the stored list stays empty.
            PurgeSubjects();
        }

        if (starting && Target == ChatTarget.Individual && _ships.Count == 1)
        {
            short shipId = _ships[0];
            bool mine = selfId >= 0 && shipId == selfId;
            bool drone = playerPiloted is not null && !playerPiloted(shipId);
            if (mine || drone)
            {
                //Chats to a drone or myself convert to the team
                PurgeSubjects();
                Target = ChatTarget.Team;
            }
        }
    }
}
