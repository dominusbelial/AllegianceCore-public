using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P871 / SIM-0179 — the force-feedback producer gates, direction math and the
/// stop-on-expiry/focus-loss/disconnect rules.
/// <para>Native producer gates: the seam's <c>PlayFFEffect</c> model gate
/// (<c>Igc/igc.h:4833</c>, <c>WinTrek/trekigc.cpp:4985-4990</c>), weapon fire
/// (<c>Igc/weaponIGC.cpp:174</c>), collisions
/// (<c>Igc/shipIGC.cpp:835,1005,1190-1194</c>) and positive credited damage with a
/// direction (<c>WinTrek/trekigc.cpp:4296-4302</c>). The settings toggle defaults
/// on (<c>WinTrek/WinTrek.cpp:3049</c>).</para>
/// </summary>
public class P871ForceFeedbackTests
{
    private sealed class FakeDevice : IForceFeedbackDevice
    {
        public bool IsAvailable { get; set; } = true;
        public List<(short Effect, int Direction)> Plays { get; } = new();
        public int Stops { get; private set; }
        public void Play(short effectId, int direction) => Plays.Add((effectId, direction));
        public void Stop() => Stops++;
    }

    [Fact]
    public void The_settings_toggle_defaults_on_and_off_stops_the_device()
    {
        var device = new FakeDevice();
        var controller = new ForceFeedbackController(device);
        Assert.True(controller.Enabled);   // native LoadPreference("EnableFeedback", 1)
        Assert.True(controller.PlayCollision(10, ownShipId: 10, now: 0));
        Assert.Single(device.Plays);
        controller.SetEnabled(false);
        Assert.True(device.Stops >= 1);
        Assert.False(controller.PlayCollision(10, ownShipId: 10, now: 1));
        Assert.Single(device.Plays);
    }

    [Fact]
    public void The_seam_gate_only_lets_the_local_ship_models_through()
    {
        Assert.True(ForceFeedbackRules.AllowsModel(modelId: null, ownShipId: 10));   // (model == NULL) passes
        Assert.True(ForceFeedbackRules.AllowsModel(modelId: 10, ownShipId: 10));
        Assert.False(ForceFeedbackRules.AllowsModel(modelId: 11, ownShipId: 10));
    }

    [Fact]
    public void Weapon_fire_is_credited_to_the_gunner_ship_when_one_is_boarded()
    {
        // weaponIGC.cpp:174 — effectFire with (m_pshipGunner ? m_pshipGunner : m_ship).
        Assert.Equal((short)11, ForceFeedbackRules.FireModel(firingShipId: 10, gunnerShipId: 11));
        Assert.Equal((short)10, ForceFeedbackRules.FireModel(firingShipId: 10, gunnerShipId: null));

        var device = new FakeDevice();
        var controller = new ForceFeedbackController(device);
        // Own pilot firing.
        Assert.True(controller.PlayWeaponFire(firingShipId: 10, gunnerShipId: null, ownShipId: 10, now: 0));
        // Local gunner firing the carrier's turret.
        Assert.True(controller.PlayWeaponFire(firingShipId: 10, gunnerShipId: 11, ownShipId: 11, now: 1));
        // Somebody else's shot never rumbles.
        Assert.False(controller.PlayWeaponFire(firingShipId: 12, gunnerShipId: null, ownShipId: 10, now: 2));
        Assert.Equal(new[] { (ForceEffect.Fire, 0), (ForceEffect.Fire, 0) }, device.Plays);
    }

    [Fact]
    public void Only_own_ship_collisions_rumble()
    {
        var device = new FakeDevice();
        var controller = new ForceFeedbackController(device);
        Assert.True(controller.PlayCollision(partyId: 10, ownShipId: 10, now: 0));
        Assert.False(controller.PlayCollision(partyId: 11, ownShipId: 10, now: 1));
        Assert.Equal(ForceEffect.Bounce, Assert.Single(device.Plays).Effect);
    }

    [Fact]
    public void Credited_damage_requires_a_positive_amount_on_the_local_source_ship()
    {
        Assert.True(ForceFeedbackRules.AllowsCreditedDamage(creditedModelId: 12, damagedShipId: 10,
            ownSourceShipId: 10, amount: 1f));
        // No credited model.
        Assert.False(ForceFeedbackRules.AllowsCreditedDamage(null, 10, 10, 1f));
        // Zero/negative amount.
        Assert.False(ForceFeedbackRules.AllowsCreditedDamage(12, 10, 10, 0f));
        Assert.False(ForceFeedbackRules.AllowsCreditedDamage(12, 10, 10, -3f));
        // Somebody else's ship (or a remote gunner's carrier).
        Assert.False(ForceFeedbackRules.AllowsCreditedDamage(12, 11, 10, 1f));
        // A boarded gunner feels damage to the CARRIER (GetSourceShip).
        Assert.True(ForceFeedbackRules.AllowsCreditedDamage(12, 10, 10, 1f));
    }

    [Fact]
    public void Credited_damage_sends_the_native_direction()
    {
        // WinTrek/trekigc.cpp:4297-4301:
        // direction = degrees(atan2(backward·delta, right·delta)) + 90.
        var identity = new Orientation();   // right = +X, backward = +Z
        Assert.Equal(90, ForceFeedbackRules.ImpactDirection(identity, new Vector3(1, 0, 0)));
        Assert.Equal(180, ForceFeedbackRules.ImpactDirection(identity, new Vector3(0, 0, 1)));
        Assert.Equal(0, ForceFeedbackRules.ImpactDirection(identity, new Vector3(0, 0, -1)));
        Assert.Equal(270, ForceFeedbackRules.ImpactDirection(identity, new Vector3(-1, 0, 0)));
        // The wire normal is normalize(position1 - position2) = -(p2 - p1)
        // (projectileIGC.cpp:224-225), so the helper inverts it exactly once.
        Assert.Equal(180, ForceFeedbackRules.ImpactDirectionFromNormal(identity, new Vector3(0, 0, -1)));
    }

    [Fact]
    public void Expiry_focus_loss_and_disconnect_stop_the_device()
    {
        var device = new FakeDevice();
        var controller = new ForceFeedbackController(device, enabled: true, effectSeconds: 0.1);
        Assert.True(controller.PlayCollision(10, 10, now: 0));
        Assert.True(controller.IsActive);
        controller.Update(now: 0.05);
        Assert.True(controller.IsActive);
        controller.Update(now: 0.11);   // native's effects are one-shot starts
        Assert.False(controller.IsActive);
        Assert.Equal(1, device.Stops);

        Assert.True(controller.PlayCollision(10, 10, now: 1));
        controller.OnFocusChanged(false);   // window deactivated
        Assert.False(controller.IsActive);
        Assert.Equal(2, device.Stops);
        Assert.False(controller.PlayCollision(10, 10, now: 2));   // no feedback without focus
        controller.OnFocusChanged(true);

        Assert.True(controller.PlayCollision(10, 10, now: 3));
        device.IsAvailable = false;         // unplugged
        controller.OnDeviceChanged();
        Assert.False(controller.IsActive);
        Assert.Equal(3, device.Stops);
        Assert.False(controller.PlayCollision(10, 10, now: 4));
    }

    [Fact]
    public void The_device_profiles_map_each_effect_to_a_rumble()
    {
        Assert.Equal((0.28f, 0.28f), MonoGameForceFeedback.Profile(ForceEffect.Fire, 0));
        // Direction pans between the motors: 0 deg leans right, 180 deg left,
        // front/back split evenly.
        var right = MonoGameForceFeedback.Profile(ForceEffect.Bounce, 0);
        var left = MonoGameForceFeedback.Profile(ForceEffect.Bounce, 180);
        var front = MonoGameForceFeedback.Profile(ForceEffect.Bounce, 90);
        Assert.True(right.Right > right.Left);
        Assert.True(left.Left > left.Right);
        Assert.Equal(front.Left, front.Right, 3);
        Assert.True(MonoGameForceFeedback.Profile(ForceEffect.Explode, 0).Left > right.Left);
    }
}
