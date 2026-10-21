using Allegiance.Core;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P774 — the in-mission roster is DERIVED, as native, from the per-ship
/// <see cref="PlayerInfo"/> objects: PLAYERINFO(10) seeds them
/// (<c>FedSrv/FedSrv.CPP:2056-2135</c>) and KILL_SHIP(105) updates the
/// counters (<c>clintlib/appmsg.cpp:2766-2773,2805-2808</c>). No 209/219
/// frames exist. One row per HUMAN ship on a real side (native IsHuman(),
/// clintlib.h:343; drones stay out; a team-lobby side of -2 stays out).
/// Pure <see cref="ClientWorld.Apply"/> + <see cref="ClientWorld.MarkApplied"/>
/// (the end-of-batch rebuild the receive thread performs) — no sockets.
/// </summary>
public sealed class P774DerivedScoreRosterTests
{
    private static PlayerInfo Seed(short shipId, short side, string name,
        short kills = 0, short deaths = 0, short ejections = 0, short rank = 0, short dtid = PlayerInfo.NA) => new()
    {
        ShipId = shipId,
        ISide = side,
        CharacterName = name,
        NKills = kills,
        NDeaths = deaths,
        NEjections = ejections,
        DtidDrone = dtid,
        PersistScores = [new PlayerInfo.PersistPlayerScore(0f, 0f, rank, PlayerInfo.NA)],
    };

    [Fact]
    public void Roster_is_built_from_PLAYERINFO_humans_only()
    {
        var w = new ClientWorld();
        w.Apply(Seed(10, 0, "Ace", kills: 2, deaths: 1, ejections: 1, rank: 3));
        w.Apply(Seed(20, 1, "Bee"));
        w.Apply(Seed(30, 1, "drone", dtid: 4));    // a drone — never a row
        w.Apply(Seed(40, -2, "Lobby"));            // team lobby — not a real side
        w.MarkApplied();                           // end of the received batch: one rebuild

        var roster = w.ScoreRoster;
        Assert.Equal(2, roster.Count);             // ordered by ship id
        var ace = roster[0];
        Assert.Equal("Ace", ace.CharacterName);
        Assert.Equal((short)0, ace.SideId);
        Assert.Equal((short)2, ace.Kills);
        Assert.Equal((short)1, ace.Deaths);
        Assert.Equal((short)1, ace.Ejections);
        Assert.Equal((short)3, ace.Rank);
        // The seven fields with no native in-mission source read 0 until P774b.
        Assert.Equal(0f, ace.Score);
        Assert.Equal(0f, ace.CombatRating);
        Assert.Equal((short)0, ace.Rescues);
        Assert.Equal((short)0, ace.BaseKills);
        Assert.Equal((short)0, ace.BaseCaptures);
        Assert.Equal((short)0, ace.Artifacts);
        Assert.Equal((short)0, ace.Flags);
        Assert.Equal("Bee", roster[1].CharacterName);
        Assert.Equal((short)1, roster[1].SideId);
    }

    [Fact]
    public void Kill_ship_updates_kills_deaths_and_ejections()
    {
        var w = new ClientWorld();
        w.Apply(Seed(10, 0, "Ace"));
        w.Apply(Seed(20, 1, "Bee"));

        // Ace kills Bee — a real death (bDeathCredit = true): AddDeath for the
        // victim, AddKill for the credited killer (appmsg.cpp:2766-2767,2805-2808).
        w.Apply(new KillShip { ShipId = 20, TypeCredit = (short)IgcObjectType.Ship, IdCredit = 10, BKillCredit = true, BDeathCredit = true });
        w.MarkApplied();
        Assert.Equal((short)1, w.ScoreRoster.Single(e => e.CharacterName == "Ace").Kills);
        Assert.Equal((short)1, w.ScoreRoster.Single(e => e.CharacterName == "Bee").Deaths);

        // Bee ejects (bDeathCredit = false): AddEjection for the victim
        // (appmsg.cpp:2770-2771), and the kill credit still counts for Ace.
        w.Apply(new KillShip { ShipId = 20, TypeCredit = (short)IgcObjectType.Ship, IdCredit = 10, BKillCredit = true, BDeathCredit = false });
        w.MarkApplied();
        Assert.Equal((short)2, w.ScoreRoster.Single(e => e.CharacterName == "Ace").Kills);
        Assert.Equal((short)1, w.ScoreRoster.Single(e => e.CharacterName == "Bee").Ejections);
        Assert.Equal((short)1, w.ScoreRoster.Single(e => e.CharacterName == "Bee").Deaths);   // unchanged
    }

    [Fact]
    public void Side_change_and_quit_mission_update_the_roster()
    {
        var w = new ClientWorld();
        w.Apply(Seed(10, 0, "Ace"));
        w.Apply(Seed(20, 1, "Bee"));

        // JOIN_SIDE moves Bee to side 0 (the port client already updates
        // PlayerInfo.ISide there — JOIN_SIDE/QUIT_SIDE keep the row's side
        // current, like native's AddPlayerToSide/RemovePlayerFromSide).
        w.Apply(new JoinSide { ShipId = 20, SideId = 0 });
        w.MarkApplied();
        Assert.Equal((short)0, w.ScoreRoster.Single(e => e.CharacterName == "Bee").SideId);

        // QUIT_MISSION removes the ship (and its row) entirely.
        w.Apply(new QuitMission { ShipId = 20, Reason = QuitSideReason.Quit });
        w.MarkApplied();
        Assert.DoesNotContain(w.ScoreRoster, e => e.CharacterName == "Bee");
        Assert.Single(w.ScoreRoster);
    }

    [Fact]
    public void Reset_and_next_mission_clear_the_roster()
    {
        var w = new ClientWorld();
        w.Apply(Seed(10, 0, "Ace"));
        w.MarkApplied();
        Assert.Single(w.ScoreRoster);
        w.Reset();
        Assert.Empty(w.ScoreRoster);
        w.Apply(Seed(10, 0, "Ace"));
        w.MarkApplied();
        Assert.Single(w.ScoreRoster);
        w.BeginNextMission();
        Assert.Empty(w.ScoreRoster);
    }

    [Fact]
    public void Unchanged_roster_reads_return_the_same_instance()
    {
        var w = new ClientWorld();
        w.Apply(Seed(10, 0, "Ace"));
        w.MarkApplied();
        Assert.Single(w.ScoreRoster);
        Assert.Same(w.ScoreRoster, w.ScoreRoster);   // no rebuild on read
    }

    [Fact]
    public void A_PLAYERINFO_burst_rebuilds_once_per_batch_not_per_message()
    {
        // The rotation burst delivers one PLAYERINFO per player to every world;
        // a per-Apply rebuild was O(N) per message (O(N^2) per world, and
        // ~O(N^3) across a bot process). Applies only mark the roster dirty;
        // the batch end (MarkApplied) rebuilds ONCE.
        var w = new ClientWorld();
        var before = w.ScoreRosterRebuilds;
        for (short i = 1; i <= 50; i++) w.Apply(Seed(i, (short)(i % 2), "P" + i));
        Assert.Equal(before, w.ScoreRosterRebuilds);      // nothing rebuilt mid-batch
        Assert.Empty(w.ScoreRoster);                       // and nothing rebuilt on read
        w.MarkApplied();
        Assert.Equal(before + 1, w.ScoreRosterRebuilds);
        Assert.Equal(50, w.ScoreRoster.Count);
        var first = w.ScoreRoster;
        w.MarkApplied();                                   // a batch with no roster input
        Assert.Equal(before + 1, w.ScoreRosterRebuilds);
        Assert.Same(first, w.ScoreRoster);
    }

    [Fact]
    public void An_observer_frame_that_clears_the_ship_table_yields_a_fresh_roster()
    {
        // ApplyLocalObservation replaces the whole ship table; the roster built
        // from the previous ship table must not survive it.
        var w = new ClientWorld();
        w.Apply(Seed(10, 0, "Ace"));
        w.MarkApplied();
        Assert.Single(w.ScoreRoster);
        w.ApplyLocalObservation([], []);
        Assert.Empty(w.ScoreRoster);
        w.ApplyLocalObservation([Seed(20, 1, "Bee")], []);
        Assert.Equal("Bee", Assert.Single(w.ScoreRoster).CharacterName);
    }
}
