using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P626 — the client world's handling of <c>FM_S_BALLOT(17)</c>: the decoded
/// ballot becomes the <see cref="ClientWorld.ActiveBallot"/> (the source the
/// VoteYes/VoteNo keys read) and surfaces as a system chat line. Exactly the
/// dispatch path <c>Session.ConnectionSession</c> uses: <c>Ballot.Decode</c>
/// then <c>ClientWorld.Apply</c>.
/// </summary>
public class ClientWorldBallotTests
{
    [Fact]
    public void Decoded_Ballot_sets_active_ballot_and_posts_system_line()
    {
        var world = new ClientWorld();

        var msg = new Ballot
        {
            BallotText = "Pilot A has proposed to mutiny.  ",
            OtInitiator = (short)Allegiance.Core.IgcObjectType.Ship,
            OidInitiator = 7,
            BallotId = 42,
            TimeExpiration = 99_000u,
            HideToLeader = true,
        };
        var decoded = Ballot.Decode(msg.Encode());
        world.Apply(decoded);

        Assert.NotNull(world.ActiveBallot);
        Assert.Equal((short)42, world.ActiveBallot!.BallotId);
        Assert.Equal("Pilot A has proposed to mutiny.", world.ActiveBallot.BallotText);

        // A system chat line carries the proposal so the pilot sees the vote.
        Assert.Contains(world.Events.Chats, c => c.Message.Contains("Pilot A has proposed to mutiny"));
    }

    [Fact]
    public void New_ballot_queues_behind_the_previous_active_ballot()
    {
        var world = new ClientWorld();
        world.Apply(Ballot.Decode(new Ballot { BallotId = 1, BallotText = "one" }.Encode()));
        world.Apply(Ballot.Decode(new Ballot { BallotId = 2, BallotText = "two" }.Encode()));

        Assert.Equal((short)1, world.ActiveBallot!.BallotId);
        Assert.Equal("one", world.ActiveBallot.BallotText);
    }
}
