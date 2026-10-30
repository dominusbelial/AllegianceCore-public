using Allegiance.ClientCore.Training;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P892 — server-side native training-universe setup (TRN-0217 Mission 3 starting equipment/energy,
/// TRN-0220 Mission 4 initial stealth setup). The native missions apply these in their
/// <c>CreateUniverse</c>; the port mirrors them on the embedded training host so the client and server
/// agree (mission3.cpp:168-190, mission4.cpp:110-137). Each assertion fails before the fix in
/// <c>TrainingHost</c> that applies the setup.
/// </summary>
[Collection("Training")]
public sealed class P892TrainingSetupTests
{
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Mission3_host_applies_starting_equipment_and_quarter_energy()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.BasicWeaponry);

        // mission3.cpp:176 — AddPartToShip(92,0,0): anti-shield gun mounted at mount 0.
        var core = host.Mission.Core;
        Assert.True(core.PartTypes.TryGetValue(92, out var antiShield), "part 92 (anti-shield gun) must exist");
        var mounted = host.PlayerShip.Parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Weapon && p.MountId == 0);
        Assert.NotNull(mounted);
        Assert.Equal(antiShield, mounted!.Type);   // the mounted mount-0 weapon IS part 92

        // mission3.cpp:177-180 — missile magazine removed.
        Assert.DoesNotContain(host.PlayerShip.Parts, p => p.EquipmentType == EquipmentType.Magazine);

        // mission3.cpp:181-182 — only the starting weapon selected, group fire OFF.
        Assert.Equal(0, host.PlayerShip.StateM & ShipControlState.SelectedWeaponMask);
        Assert.Equal(0, host.PlayerShip.StateM & ShipControlState.AllWeapons);

        // mission3.cpp:187-190 — side max energy cut to one quarter.
        Assert.Equal(0.25f, host.Mission.Sides[0].GlobalAttributes.Get(Allegiance.Core.Model.GlobalAttributeSet.MaxEnergy));
        Assert.Equal(host.PlayerShip.HullType.MaxEnergy * 0.25f, host.PlayerShip.EffectiveMaxEnergy(), 3);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Mission4_host_applies_initial_stealth_setup_and_tenfold_energy()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.EnemyEngagement);

        // mission4.cpp:126 — group fire ON (all starting weapons active).
        Assert.NotEqual(0, host.PlayerShip.StateM & ShipControlState.AllWeapons);

        // mission4.cpp:131-136 — missile magazine removed.
        Assert.DoesNotContain(host.PlayerShip.Parts, p => p.EquipmentType == EquipmentType.Magazine);

        // mission4.cpp:113-116 — side max energy multiplied by ten.
        Assert.Equal(10.0f, host.Mission.Sides[0].GlobalAttributes.Get(Allegiance.Core.Model.GlobalAttributeSet.MaxEnergy));
        Assert.Equal(host.PlayerShip.HullType.MaxEnergy * 10.0f, host.PlayerShip.EffectiveMaxEnergy(), 3);
    }
}
