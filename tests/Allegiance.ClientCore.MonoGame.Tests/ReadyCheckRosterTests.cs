using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-domain tests for <see cref="ReadyCheckRoster.Gather"/> — how the
/// READY-CHECK screen reduces the live <see cref="ClientWorld"/> into its
/// display inputs: the own side's name, the same-side player roster (each
/// player's ready flag from the real PLAYER_READY relays, plus names from
/// teammate ship entities), and the per-side TEAM_READY aggregate. No sockets:
/// messages are fed straight through <see cref="ClientWorld.Apply"/>.
/// </summary>
public class ReadyCheckRosterTests
{
    private const short MySide = 7;
    private static readonly object Gate = new();   // stand-in for the session's world-read lock

    private static ClientWorld World()
    {
        var w = new ClientWorld();
        // Own-ship EXPORT (ShipId = NA dummy) is the wire source of MySideId.
        w.Apply(new ExportShip { ShipId = -1, SideId = MySide });
        // A same-side teammate ship entity (name + side).
        w.Apply(new ExportShip { ShipId = 101, SideId = MySide, CharacterName = "Alpha" });
        // A cross-side ship — must NOT appear on the roster.
        w.Apply(new ExportShip { ShipId = 202, SideId = 8, CharacterName = "Enemy" });
        return w;
    }

    [Fact]
    public void Gather_lists_same_side_players_with_real_ready_flags_and_excludes_enemies()
    {
        var w = World();
        w.Apply(new PlayerReady { ShipId = 101, FReady = true });

        var (sideName, players, agg) = ReadyCheckRoster.Gather(w, Gate, ownReady: false);

        Assert.Equal("SIDE 7", sideName);            // no SIDE_ROSTER yet → fallback name
        Assert.Null(agg);                             // no TEAM_READY yet

        // Self row is always present, driven by the local toggle.
        var self = players.Single(p => p.IsSelf);
        Assert.Equal("you", self.Name);
        Assert.False(self.Ready);

        // The teammate appears, ready true (from the real PLAYER_READY relay).
        var alpha = players.Single(p => p.Name == "Alpha");
        Assert.True(alpha.Ready);
        Assert.False(alpha.IsSelf);

        // Cross-side "Enemy" is excluded (same-side-only roster).
        Assert.DoesNotContain(players, p => p.Name == "Enemy");
    }

    [Fact]
    public void Gather_reflects_own_ready_toggle_and_team_ready_aggregate()
    {
        var w = World();
        w.Apply(new PlayerReady { ShipId = 101, FReady = false });
        w.Apply(new TeamReady { SideId = MySide, FReady = false });

        var (_, players, agg) = ReadyCheckRoster.Gather(w, Gate, ownReady: true);

        Assert.True(players.Single(p => p.IsSelf).Ready);   // own toggle true
        Assert.False(players.Single(p => p.Name == "Alpha").Ready);
        Assert.Equal(false, agg);                           // TEAM_READY(46) received
    }

    [Fact]
    public void Gather_adds_roster_only_player_with_no_entity_by_ship_id()
    {
        var w = World();
        w.Apply(new PlayerReady { ShipId = 303, FReady = true });   // toggled but no export yet

        var (_, players, _) = ReadyCheckRoster.Gather(w, Gate, ownReady: false);

        Assert.Contains(players, p => p.Name == "sid303" && p.Ready);
    }

    [Fact]
    public void Gather_excludes_known_foreign_ready_flags_after_side_change()
    {
        var w = World();
        w.Apply(new PlayerReady { ShipId = 202, FReady = true });
        w.Apply(new PlayerReady { ShipId = 101, FReady = true });
        w.Apply(new JoinSide { ShipId = 101, SideId = 8 });
        w.Apply(new PlayerReady { ShipId = 303, FReady = false });
        var (_, players, _) = ReadyCheckRoster.Gather(w, Gate, false);
        Assert.Equal(2, players.Count); // self and the genuinely unknown relay
        Assert.Contains(players, p => p.Name == "sid303" && !p.Ready);
        Assert.DoesNotContain(players, p => p.Name is "sid202" or "sid101" or "Alpha" or "Enemy");
        w.Apply(new JoinSide { ShipId = 101, SideId = MySide });
        Assert.Contains(ReadyCheckRoster.Gather(w, Gate, false).Players, p => p.Name == "Alpha" && p.Ready);
    }

    [Fact]
    public void Gather_uses_side_roster_display_name_when_available()
    {
        var w = World();
        w.Apply(new SideRoster
        {
            Sides =
            [
                new SideRoster.SideRecord(MySide, CivId: 1, PlayerCount: 2, "Bios"),
                new SideRoster.SideRecord(8, CivId: 3, PlayerCount: 1, "IronCo"),
            ],
        });

        var (sideName, _, _) = ReadyCheckRoster.Gather(w, Gate, ownReady: false);

        Assert.Equal("Bios", sideName);
    }
}
