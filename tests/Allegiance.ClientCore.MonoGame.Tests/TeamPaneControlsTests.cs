using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P752 — the team pane's selected-player controls (native teampane.cpp:1104-1367):
/// roster selection, the donate/stop/auto verbs acting on the SELECTED player,
/// take-me-to's sector gating, and the leader's join-request accept. Pure
/// decision logic — <see cref="TeamPaneControls.Step"/> returns the wire
/// intents and Game1 performs the sends, so the native rules are pinned
/// without a graphics device.
/// </summary>
public class TeamPaneControlsTests
{
    private static ClientWorld World()
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0, Vector3.Zero, Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        // Own ship 10 (side 0, sector 1) + teammate 11 (side 0, sector 1) +
        // enemy 12 (side 1, sector 2). All seated on the score roster.
        foreach (short id in new short[] { 10, 11, 12 })
            w.Apply(new ExportShip { ShipId = id, SideId = id == 12 ? (short)1 : (short)0, CharacterName = $"p{id}" });
        // P774 — PLAYERINFO(10) seeds the roster (the retired 209 is gone).
        w.Apply(new PlayerInfo { ShipId = 10, ISide = 0, CharacterName = "p10" });
        w.Apply(new PlayerInfo { ShipId = 11, ISide = 0, CharacterName = "p11" });
        w.Apply(new PlayerInfo { ShipId = 12, ISide = 1, CharacterName = "p12" });
        w.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        w.Apply(new SetMoney { ShipId = 10, Money = 250 });
        // Sector positions for the take-me-to gate.
        w.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = -1, ParentId = -1, State = ShipStatusMessage.StateFlying });
        w.Apply(new ShipStatusMessage { ShipId = 11, SectorId = 1, StationId = -1, ParentId = -1, State = ShipStatusMessage.StateFlying });
        w.Apply(new ShipStatusMessage { ShipId = 12, SectorId = 2, StationId = -1, ParentId = -1, State = ShipStatusMessage.StateFlying });
        w.MarkApplied();   // P774 — end of the received batch: the derived roster rebuilds here
        return w;
    }

    private static TeamPaneControls.Result Step(ClientWorld w, TeamPaneControls c, Keys key, bool active = true, bool ballot = false)
    {
        var r = c.Step(new KeyboardState(key), new ActionMap(InputActionCatalog.Definitions), w, active, ballot);
        c.Step(default, new ActionMap(InputActionCatalog.Definitions), w, false, false);   // release the edge
        return r;
    }

    [Fact]
    public void Roster_ListsEverySideThenOwnJoinRequestsForTheLeader()
    {
        var w = World();
        var rows = new TeamPaneControls().BuildRoster(w);
        // All sides' players, side id then name order (the pane's display order).
        Assert.Equal(["p10", "p11", "p12"], rows.Select(r => r.Name));
        Assert.False(rows[0].IsJoinRequest);

        // A pending join request on the OWN side appears for the leader only
        // (native ConcatinatedList(members, requests), teampane.cpp:987-995).
        Assert.DoesNotContain(new TeamPaneControls().BuildRoster(w), r => r.IsJoinRequest);
        w.Apply(new SetTeamLeader { SideId = 0, ShipId = 10 });
        w.Apply(new PositionReqBroadcast { SideId = 0, ShipId = 55 });
        w.Apply(new ExportShip { ShipId = 55, SideId = -1, CharacterName = "p55" });
        rows = new TeamPaneControls().BuildRoster(w);
        var req = Assert.Single(rows, r => r.IsJoinRequest);
        Assert.Equal("p55", req.Name);
        Assert.Equal(55, req.ShipId);
    }

    [Fact]
    public void PageDown_CyclesTheSelection_StartingAtTheFirstRow()
    {
        var w = World();
        var c = new TeamPaneControls();
        Assert.Equal(-1, c.Selection);
        var r = Step(w, c, Keys.PageDown);
        Assert.True(r.SelectionChanged);
        Assert.Equal(0, c.Selection);
        Step(w, c, Keys.PageDown);
        Assert.Equal(1, c.Selection);
        Step(w, c, Keys.PageDown);
        Step(w, c, Keys.PageDown);
        Assert.Equal(0, c.Selection);   // wraps
    }

    [Fact]
    public void Donate_ActsOnTheSelectedPlayer_WithNativeAmountsAndNoSelfGift()
    {
        var w = World();
        var c = new TeamPaneControls();
        Step(w, c, Keys.PageDown);           // select p10 (self)
        Assert.Null(Step(w, c, Keys.D).Gift);   // no self-gift (teampane.cpp:1153)
        Step(w, c, Keys.PageDown);           // select p11
        var small = Step(w, c, Keys.D).Gift!;
        Assert.Equal(100, small.MoneyGiven);
        Assert.Equal((short)11, small.ShipID);
        var large = Step(w, c, Keys.E).Gift!;
        Assert.Equal(250, large.MoneyGiven);    // capped at the wallet
        Assert.Equal(500 > 250 ? 250 : large.MoneyGiven, large.MoneyGiven);
        // Wallet drained: no further gifts.
        w.Apply(new SetMoney { ShipId = 10, Money = 0 });
        Assert.Null(Step(w, c, Keys.D).Gift);
    }

    [Fact]
    public void CrossSideGiftLine_ShowsMoneyMinusTheGift_NeverNegative()
    {
        // Native teampane.cpp:1152-1158, 1181-1188: "You now have $" is the
        // post-gift balance (money − given), not the old balance.
        Assert.Equal("You gave p11 $100. You now have $150.",
            TeamPaneControls.CrossSideGiftLine("p11", 250, 100));
        Assert.Equal("You gave p11 $500. You now have $0.",
            TeamPaneControls.CrossSideGiftLine("p11", 250, 500));   // gift capped at the wallet
        Assert.Equal("You gave p11 $100. You now have $0.",
            TeamPaneControls.CrossSideGiftLine("p11", 100, 100));
    }

    [Fact]
    public void StopToggle_IsLeaderOnly_AutoDonateGivesTheWholeWallet()
    {
        var w = World();
        var c = new TeamPaneControls();
        Step(w, c, Keys.PageDown);
        Step(w, c, Keys.PageDown);           // select p11
        Assert.Null(Step(w, c, Keys.X).Auto);   // not the leader
        w.Apply(new SetTeamLeader { SideId = 0, ShipId = 10 });
        var toggle = Step(w, c, Keys.X).Auto!;
        Assert.Equal((short)11, toggle.SidDonateTo);
        Assert.Equal(-1, toggle.Amount);        // leader stop-donating toggle (KGJV #110)
        var auto = Step(w, c, Keys.A).Auto!;
        Assert.Equal((short)11, auto.SidDonateTo);
        Assert.Equal(250, auto.Amount);         // whole wallet
    }

    [Fact]
    public void TakeMeTo_AlwaysFiresForAPlayerRow_AcceptOnJoinRequests_BallotWins()
    {
        var w = World();
        var c = new TeamPaneControls();
        Step(w, c, Keys.PageDown);
        Step(w, c, Keys.PageDown);           // p11 — my sector: take-me-to fires
        var r = Step(w, c, Keys.Y);
        Assert.Equal((short?)11, r.TakeMeToShipId);
        Assert.Equal((short?)1, r.TakeMeToSectorId);
        Step(w, c, Keys.PageDown);           // p12 — enemy in ANOTHER sector, not docked
        // Native OnButtonTakeMeTo still closes the overlay + PickShip
        // (teampane.cpp:1288-1330); the sector (ship-status) rides along so
        // TakeMeToSelected can skip only the camera jump.
        r = Step(w, c, Keys.Y);
        Assert.Equal((short?)12, r.TakeMeToShipId);
        Assert.Equal((short?)2, r.TakeMeToSectorId);
        // Docked: a different sector is now reachable (native requests the
        // player's sector through the view-cluster stream).
        w.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = 4, ParentId = -1, State = ShipStatusMessage.StateDocked });
        r = Step(w, c, Keys.Y);
        Assert.Equal((short?)12, r.TakeMeToShipId);
        Assert.Equal((short?)2, r.TakeMeToSectorId);
        // A pending ballot keeps Y for the vote.
        Assert.Equal(default(TeamPaneControls.Result), Step(w, c, Keys.Y, ballot: true));
        // A join-request row accepts instead.
        w.Apply(new PositionReqBroadcast { SideId = 0, ShipId = 55 });
        w.Apply(new ExportShip { ShipId = 55, SideId = -1, CharacterName = "p55" });
        w.Apply(new SetTeamLeader { SideId = 0, ShipId = 10 });
        Step(w, c, Keys.PageDown);   // p12 (index 2) -> the request row (index 3)
        r = Step(w, c, Keys.Y);
        Assert.Equal((short?)55, r.AcceptShipId);
        Assert.Null(r.TakeMeToShipId);
    }

    [Fact]
    public void InactiveSteps_ObserveEdgesButFireNothing()
    {
        var w = World();
        var c = new TeamPaneControls();
        var map = new ActionMap(InputActionCatalog.Definitions);
        // Hold D while inactive, then activate: the stale edge must not fire.
        c.Step(new KeyboardState(Keys.D), map, w, false, false);
        Assert.Null(c.Step(new KeyboardState(Keys.D), map, w, true, false).Gift);
        Step(w, c, Keys.PageDown);
        Step(w, c, Keys.PageDown);
        Assert.NotNull(Step(w, c, Keys.D).Gift);
    }
}
