using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P651WeaponReadinessTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Unready_part_shows_not_ready_even_with_full_ammo(bool consumesAmmo)
    {
        var tracker = new WeaponReloadTracker();
        var slot = new WeaponSlotDescriptor(0, "weapon", consumesAmmo, 10, IsReady: false);
        Assert.Equal(WeaponStatus.NotReady, Assert.Single(tracker.Update(0, 1, false, [slot])).Status);
        Assert.Equal(WeaponStatus.Ready, Assert.Single(tracker.Update(10, 1, false, [slot with { IsReady = true }])).Status);
    }
}
