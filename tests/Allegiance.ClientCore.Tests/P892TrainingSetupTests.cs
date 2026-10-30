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

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Mission4_set_signature_writes_a_real_override_on_the_ships_effective_signature()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.EnemyEngagement);

        // The Core-level override itself: SetSignature(x) → EffectiveSignature = x · Cloaking
        // (shipIGC.h:268-270), replacing the computed base sum.
        host.PlayerShip.SetSignature(500f);
        Assert.Equal(500f * host.PlayerShip.Cloaking, host.PlayerShip.EffectiveSignature, 3);

        // The production site path (mission4.cs:442 — SetSignatureAction(Ship, id, 1.0e6)):
        // routed through the host's real ship, not the old logged no-op.
        int miner = host.CreateDrone("enemy miner", -1, 486, 1, Ship.PilotTypePlayer);
        using var conn = new Allegiance.ClientCore.Session.ConnectionSession();
        var site = new Allegiance.ClientCore.MonoGame.ClientTrainingSite(
            new Allegiance.ClientCore.MonoGame.ClientTrainingFacilities
            {
                World = () => conn.World, Session = conn, Host = host,
            });
        // FAILS BEFORE THE FIX: ClientTrainingSite.SetSignature was a logged no-op, so the miner's
        // EffectiveSignature stayed its base hull signature instead of 1.0e6.
        site.SetSignature(ObjectType.Ship, miner, 1.0e6f);
        Assert.True(host.Drones.TryGetValue(miner, out var drone), "drone must resolve");
        Assert.Equal(1.0e6f * drone.Cloaking, drone.EffectiveSignature, 3);
        // A non-ship object has no host model and stays the harmless logged seam.
        site.SetSignature(ObjectType.Station, 1010, 5f);
    }
}
