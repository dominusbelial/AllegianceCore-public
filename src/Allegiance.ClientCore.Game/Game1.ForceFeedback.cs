using Allegiance.ClientCore.MonoGame.Audio;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P871 / SIM-0179 — the force-feedback slice of <see cref="Game1"/> (partial
/// class). The native producers are the sim seam's <c>PlayFFEffect</c> calls
/// (<c>Igc/shipIGC.cpp:835,1005,1190-1194</c> collisions;
/// <c>Igc/weaponIGC.cpp:174</c> weapon fire) and the client's credited-damage
/// branch (<c>WinTrek/trekigc.cpp:4296-4302</c>). The port's client does not run
/// the sim, so the same triggers are reconstructed from the events it already
/// receives: COLLISION_EVENT(216), PROJECTILE_HULL_HIT(213) and the local
/// weapon-fire detection in <see cref="Game1.UpdateProjectileSounds"/>.
/// </summary>
public sealed partial class Game1
{
    private MonoGameForceFeedback? _forceFeedbackDevice;
    private ForceFeedbackController? _forceFeedback;

    /// <summary>The live controller (created on first use so headless/proof
    /// runs never touch a gamepad).</summary>
    private ForceFeedbackController ForceFeedback =>
        _forceFeedback ??= new ForceFeedbackController(
            _forceFeedbackDevice ??= new MonoGameForceFeedback(),
            IntroPreferences.Load().EnableForceFeedback);

    /// <summary>Native <c>m_bEnableFeedback</c> gate + expiry/focus/device-poll,
    /// run once per frame.</summary>
    private void UpdateForceFeedback()
    {
        if (_forceFeedback is null && _forceFeedbackDevice is null && !IsActive) return;
        var controller = ForceFeedback;
        if (_forceFeedbackDevice is { } device && device.PollConnectionChanged())
            controller.OnDeviceChanged();
        controller.OnFocusChanged(IsActive);
        controller.Update(_nowSeconds);
    }

    /// <summary>Own collision party → <c>effectBounce</c>
    /// (<c>Igc/shipIGC.cpp:835,1005,1190-1194</c> via
    /// <c>WinTrek/trekigc.cpp:4985-4990</c>).</summary>
    private void ForceFeedbackCollision(short partyId, short? myShipId)
    {
        if (_forceFeedback is null && _forceFeedbackDevice is null) return;
        ForceFeedback.PlayCollision(partyId, myShipId, _nowSeconds);
    }

    /// <summary>Local weapon fire → <c>effectFire</c>
    /// (<c>Igc/weaponIGC.cpp:174</c>).</summary>
    private void ForceFeedbackWeaponFire(short firingShipId, short? gunnerShipId, short? myShipId)
    {
        if (_forceFeedback is null && _forceFeedbackDevice is null) return;
        ForceFeedback.PlayWeaponFire(firingShipId, gunnerShipId, myShipId, _nowSeconds);
    }

    /// <summary>Positive credited damage to the local source ship →
    /// <c>effectBounce</c> with the native impact direction
    /// (<c>WinTrek/trekigc.cpp:4296-4302</c>).</summary>
    private void ForceFeedbackCreditedDamage(short? creditedModelId, short damagedShipId,
        short? mySourceShipId, float amount, int direction)
    {
        if (_forceFeedback is null && _forceFeedbackDevice is null) return;
        ForceFeedback.PlayCreditedDamage(creditedModelId, damagedShipId, mySourceShipId,
            amount, direction, _nowSeconds);
    }
}
