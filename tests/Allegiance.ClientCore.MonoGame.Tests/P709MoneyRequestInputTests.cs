using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P709MoneyRequestInputTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Accept_or_execute_donates_once_without_selecting_or_engaging_a_flight_target(bool execute)
    {
        var world = World(); var map = Map(); var input = new CommandActionInput();
        world.GetMoneyRequest(11, 100, 1);
        var key = new KeyboardState(execute ? Keys.F16 : Keys.Home);
        var result = input.Step(key, false, map, world);
        Assert.Equal(80, result.Donation!.MoneyGiven); Assert.Equal(11, result.Donation.ShipID);
        Assert.Null(result.Request); Assert.False(result.ChangeSelection); Assert.False(result.Engage);
        Assert.Null(input.Step(key, false, map, world).Donation);
    }

    [Fact]
    public void Composing_suppresses_approval_and_reject_clears_only_the_money_request()
    {
        var world = World(); var map = Map(); var input = new CommandActionInput();
        world.GetMoneyRequest(11, 50, 1);
        Assert.Null(input.Step(new(Keys.Home), true, map, world).Donation);
        Assert.Null(input.Step(new(Keys.Home), false, map, world).Donation);
        Assert.NotNull(world.PendingMoneyRequest);
        input.Step(new(), false, map, world);
        input.Step(new(Keys.Delete), false, map, world);
        Assert.Null(world.PendingMoneyRequest); Assert.Equal(80, world.MyMoney);
    }

    private static ActionMap Map()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        map.SetBinding("AcceptCommand", new(Keys.Home, InputModifiers.None), force: true);
        map.SetBinding("ExecuteCommand", new(Keys.F16, InputModifiers.None), force: true);
        map.SetBinding("RejectCommand", new(Keys.Delete, InputModifiers.None), force: true);
        return map;
    }

    private static ClientWorld World()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0,
            Vector3.Zero, Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        world.Apply(new ExportShip { ShipId = 11, SideId = 0, CharacterName = "Requester" });
        world.Apply(new SetMoney { ShipId = 10, Money = 80 });
        return world;
    }
}
