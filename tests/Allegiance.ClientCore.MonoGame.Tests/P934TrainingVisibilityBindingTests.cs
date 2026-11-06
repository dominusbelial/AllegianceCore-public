using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using V = System.Numerics.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("Training")]
public sealed class P934TrainingVisibilityBindingTests
{
    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Shared_enemy_eye_reaches_the_real_client_own_status_and_decays(HudStyle style)
    {
        Require.Artwork();
        using var c = new P909TrainingBindingsTests.Client(style, TrainingHost.BasicFlight);
        int id = c.Host.CreateDrone("Visibility regression", 9342, 315, 1, Ship.PilotTypePlayer);
        var position = c.Site.PlayerShip.Position + new V(0, 50, 0);
        c.Host.PlaceDrone(id, c.Host.StartSectorId, position, V.UnitX, V.UnitZ, null, 0);
        c.Host.Paused = false;
        Assert.True(SpinWait.SpinUntil(() => c.Host.PlayerShip.GetCurrentEye(2) &&
            c.Connection.World.Ships[(short)c.Host.PlayerShipId].Detected, 5000));
        c.Host.Paused = true;
        Assert.True(c.Host.PlayerShip.SeenBySide(99));
        c.Host.DestroyDrone(id);
        c.Host.Paused = false;
        Assert.True(SpinWait.SpinUntil(() => !c.Host.PlayerShip.GetCurrentEye(2) &&
            !c.Connection.World.Ships[(short)c.Host.PlayerShipId].Detected, 5000));
        c.Host.Paused = true;
    }
}
