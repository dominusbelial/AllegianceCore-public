namespace Allegiance.Lobby.Squad;

/// <summary>
/// Lobby squad wire contracts (P820a). These mirror the native club squad
/// messages the client Squads screen consumes — <c>SQUAD_*</c>
/// (<c>Inc/ClubMessages.h:129-301</c>) — delivered over HTTP instead of the
/// native Club sockets. Field names/meaning match the native messages so the
/// port screens render the same data. Roles use native's position vocabulary
/// (<c>Zone/Member.h:37-43</c>: Member / ASL / Leader) plus the pending
/// join-request status (<c>Member.h:91-101</c>).
/// </summary>
public static class SquadContracts
{
    /// <summary>Native <c>c_cbNameDB</c> squad-name length (<c>src/_Utility/Utility.h:60</c>,
    /// applied SquadsScreen.cpp:558).</summary>
    public const int MaxName = 24;
    /// <summary>Native <c>c_cbSquadDescriptionDB</c> (<c>src/Zone/Zone.h:6</c>,
    /// applied SquadsScreen.cpp:565).</summary>
    public const int MaxDescription = 510;
    /// <summary>Native <c>c_cbURLDB</c> (<c>src/Zone/Zone.h:5</c>, applied SquadsScreen.cpp:572).</summary>
    public const int MaxUrl = 255;
    /// <summary>Short clan tag shown beside the name (a modern lobby convenience;
    /// native stored no tag — kept optional).</summary>
    public const int MaxTag = 8;

    /// <summary>Native <c>SQUAD_SORT_COLUMN</c> (<c>src/Zone/Squad.h:52-61</c>);
    /// ranking/score/wins/losses are squad-in-game stats (P820b) and stay 0 here,
    /// so the All-tab sort degrades to name in practice.</summary>
    public enum SquadSortColumn
    {
        Unknown = 0, Name = 1, Score = 2, Wins = 3, Losses = 4, Hours = 5,
    }

    /// <summary>Native member position + pending status (Member.h:37-43,91-101):
    /// a member is either pending (join request outstanding) or active in one of
    /// the three positions. Leader owns the squad.</summary>
    public enum SquadRole
    {
        Pending = 0,
        Member = 1,
        AssistantLeader = 2,
        Leader = 3,
    }

    /// <summary>One row in the squads list (native <c>SQUAD_INFO</c> /
    /// <c>SQUAD_INFO_DUDEX</c>, <c>Inc/ClubMessages.h:256-276</c>). Wins/losses/
    /// score are the native display fields; all 0 until P820b ties squads into
    /// games.</summary>
    public sealed record SquadSummary(
        string Id,
        string Name,
        string Tag,
        short Civ,
        int Wins,
        int Losses,
        int Score,
        int Ranking,        // position within the current sort, 1-based
        int MemberCount,
        SquadRole? ViewerRole = null,  // set for Mine / ByPlayer rows
        string? LeaderCallsign = null);

    /// <summary>The full details view (native <c>SQUAD_DETAILS</c> +
    /// <c>SQUAD_DETAILS_PLAYER</c>, <c>Inc/ClubMessages.h:278-292</c>): the
    /// squad header plus its membership with each member's role.</summary>
    public sealed record SquadDetails(
        string Id,
        string Name,
        string Tag,
        string Description,
        string Url,
        short Civ,
        long CreatedAt,
        string LeaderAccountId,
        IReadOnlyList<SquadMember> Members);

    public sealed record SquadMember(
        string AccountId,
        string Callsign,
        SquadRole Role,
        long? LastPlayedAt,
        long JoinedAt);

    /// <summary>One ownership/audit log line — native <c>SQUAD_LOG_INFO</c>
    /// (<c>Inc/ClubMessages.h:294-297</c>) lines are <c>"Name          date"</c>;
    /// the parser splits on <c>';'</c> into name + date pairs
    /// (<c>ZoneSquad.cpp:290-332</c>).</summary>
    public sealed record SquadLogEntry(long Seq, string Line);

    /// <summary>Create-squad body (native <c>SQUAD_CREATE</c>,
    /// <c>Inc/ClubMessages.h:181-187</c>).</summary>
    public sealed record CreateSquadRequest(string Name, string Tag, string Description, string Url, short Civ);

    /// <summary>Edit-squad body (native <c>SQUAD_EDIT</c>,
    /// <c>Inc/ClubMessages.h:195-200</c> — name is never transmitted on edit).</summary>
    public sealed record EditSquadRequest(string Description, string Url, short Civ);

    /// <summary>Outcome of a mutating squad action. <c>Ok</c> true on success;
    /// <c>Error</c> carries the human-readable native-style refusal message
    /// (native surfaces these as <c>SQUAD_TEXT_MESSAGE</c> popups,
    /// <c>Inc/ClubMessages.h:154-156</c>).</summary>
    public sealed record SquadActionResult(bool Ok, string? Error = null)
    {
        public static SquadActionResult Success => new(true);
        public static SquadActionResult Fail(string error) => new(false, error);
    }
}
