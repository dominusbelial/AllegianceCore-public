namespace Allegiance.Core.Model;

/// <summary>
/// The four ballot types native recognizes (P187). Native declares these as
/// bare <c>#define</c>s (<c>fsmission.h:79-83</c>):
/// <c>BALLOT_RESIGN=1, BALLOT_OFFERDRAW=2, BALLOT_ACCEPTDRAW=3,
/// BALLOT_MUTINY=4</c>. Used only to enforce one in-flight ballot of each
/// type at a time (<c>CFSMission::HasBallots</c>, fsmission.cpp:1225-1235).
/// </summary>
public enum BallotType
{
    Resign = 1,
    OfferDraw = 2,
    AcceptDraw = 3,
    Mutiny = 4,
}

/// <summary>
/// P187 — the port of native <c>Ballot</c> (<c>fsmission.h:85-153</c>) as a
/// pure server-side data + tally object: one pending vote with its per-side
/// tallies, eligible-voter list, scope and expiration. The tally rules
/// (<see cref="HasPassed"/>, <see cref="AllVotesAreIn"/>,
/// <see cref="TryCastVote"/>) are transcribed verbatim from native
/// <c>Ballot::HasPassed</c>/<c>AllVotesAreIn</c>/<c>CastVote</c>
/// (<c>fsmission.cpp:5836-5879</c>); the wire I/O (broadcasting BALLOT,
/// applying an <c>OnPassed</c> effect) lives on the Server, which drives this
/// object's lifecycle via the per-mission ballot list. Construction (which
/// ships are eligible, the initiator's auto-yes vote) is likewise the
/// Server's job because only it can enumerate seated players; this type only
/// tallies what it is handed.
/// </summary>
public sealed class PendingBallot
{
    /// <summary>Native <c>c_cSidesMax</c> (<c>igc.h:507</c>) — the fixed width
    /// of the per-side tally arrays.</summary>
    public const int SideSlots = 6;

    /// <summary>The unique ballot id (the port of native
    /// <c>Ballot::s_ballotIDNext</c> — a server-wide counter).</summary>
    public uint Id;

    /// <summary>Which of the four proposal kinds this is.</summary>
    public BallotType Type;

    /// <summary>The ship that proposed the vote (native
    /// <c>MutinyBallot::m_idInitiatorShip</c>, fsmission.h:180). Meaningful for
    /// player-initiated ballots; for the mission-wide AcceptDraw the initiator
    /// is a side, not a ship.</summary>
    public short InitiatorShipId;

    /// <summary>The single side that votes on this ballot, or <c>-1</c> for a
    /// whole-mission ballot (native distinguishes a player's side group from
    /// <c>GetGroupRealSides()</c>, fsmission.cpp:5882-5980). Player-initiated
    /// ballots (mutiny/resign/offer-draw) are voted by the proposer's own
    /// side; the AcceptDraw ballot is voted by every OTHER side.</summary>
    public short VotingSideId = -1;

    /// <summary>Whether the scope is the whole mission rather than one side
    /// (only true for the AcceptDraw ballot, whose BALLOT is broadcast to
    /// every real side — native <c>GetGroupRealSides()</c>).</summary>
    public bool IsMissionWide => VotingSideId < 0;

    /// <summary>For the mission-wide AcceptDraw ballot, the OFFERING side —
    /// the side whose players are NOT eligible to vote (they already agreed
    /// to the offer). Ignored for player-initiated ballots.</summary>
    public short ExcludedSideId = -1;

    /// <summary>The proposal name broadcast in the pass/fail announcement
    /// (native <c>m_strProposal</c>), e.g. "Alice's proposal to mutiny".</summary>
    public string Proposal = "";

    /// <summary>The ballot text carried on the wire BALLOT message.</summary>
    public string BallotText = "";

    /// <summary>Expiration as this port's sim-seconds clock (native
    /// <c>m_timeExpiration = Time::Now() + 30s</c>, fsmission.cpp:5900).</summary>
    public double Expiration;

    /// <summary>Whether the ballot was cancelled (native <c>m_bCanceled</c>);
    /// a cancelled ballot is discarded on the next resolution pass.</summary>
    public bool Canceled;

    // Per-side tallies (indexed by SideId), transcribed from native
    // fsmission.h:138-140.
    public readonly int[] InFavor = new int[SideSlots];
    public readonly int[] Opposed = new int[SideSlots];
    public readonly int[] Abstaining = new int[SideSlots];

    /// <summary>The ship ids that can still vote (native <c>m_vShips</c>); a
    /// ship is removed once it votes.</summary>
    public readonly List<short> EligibleShips = new();

    /// <summary>Add an eligible voter on <paramref name="sideId"/>, mirroring
    /// native <c>Ballot::Init</c>'s tally seeding
    /// (<c>++m_cAbstaining[sideID]; m_vShips.PushEnd(shipID)</c>,
    /// fsmission.cpp:5912-5916 / 5956-5960).</summary>
    public void AddEligibleVoter(short shipId, short sideId)
    {
        if (sideId < 0 || sideId >= SideSlots) return;
        Abstaining[sideId]++;
        EligibleShips.Add(shipId);
    }

    /// <summary>Record a vote from <paramref name="shipId"/> on
    /// <paramref name="sideId"/>. Returns <c>false</c> (and changes nothing)
    /// if the ship is not an eligible voter — the port of native
    /// <c>Ballot::CastVote</c>'s <c>if (m_vShips.Remove(...) != -1)</c> guard
    /// (fsmission.cpp:5847-5865). An ineligible/unknown voter is refused and
    /// ignored.</summary>
    public bool TryCastVote(short shipId, short sideId, bool agree)
    {
        if (!EligibleShips.Remove(shipId)) return false;
        if (sideId < 0 || sideId >= SideSlots) return false;
        if (Abstaining[sideId] <= 0) return false;

        Abstaining[sideId]--;
        if (agree) InFavor[sideId]++;
        else Opposed[sideId]++;
        return true;
    }

    /// <summary>True iff every eligible voter has voted — the port of native
    /// <c>Ballot::AllVotesAreIn</c> (<c>all m_cAbstaining == 0</c>,
    /// fsmission.cpp:5867-5875).</summary>
    public bool AllVotesAreIn() => Abstaining.All(n => n == 0);

    /// <summary>Whether the vote passes — the port of native
    /// <c>Ballot::HasPassed</c> (fsmission.cpp:5836-5854): for every side that
    /// saw any vote, the measure fails if that side's in-favor tally does not
    /// STRICTLY exceed its opposed tally (a plain majority on every side that
    /// participated).</summary>
    public bool HasPassed()
    {
        for (int side = 0; side < SideSlots; side++)
        {
            if (Abstaining[side] + InFavor[side] + Opposed[side] == 0)
                continue;                                  // no votes on this side
            if (InFavor[side] <= Opposed[side])
                return false;                              // failed to get a majority
        }
        return true;
    }

    /// <summary>Whether the ballot should be resolved now — the port of native
    /// <c>Ballot::Update</c>'s trigger (fsmission.cpp:5817-5834): resolution
    /// happens when the expiration passes OR every vote is in (whichever
    /// first), unless it was cancelled.</summary>
    public bool ShouldResolve(double now) =>
        !Canceled && (now >= Expiration || AllVotesAreIn());
}
