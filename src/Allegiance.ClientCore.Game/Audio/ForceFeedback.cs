using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// Native <c>ForceEffectID</c> values (<c>Igc/igc.h:216-218</c>). The numbers are
/// the ids the DirectInput backend switches on (<c>engine/dinput.cpp:1392-1445</c>).
/// </summary>
public static class ForceEffect
{
    public const short Fire = 0;
    public const short Bounce = 1;
    public const short Explode = 2;
}

/// <summary>
/// P871 / SIM-0179 — the producer gates native applies before a force-feedback
/// effect reaches the device, plus the direction reconstruction.
///
/// <para><b>The seam gate</b> (<c>Igc/igc.h:4833</c> → <c>WinTrek/trekigc.cpp:4985-4990</c>):
/// <c>WinTrekClient::PlayFFEffect(effectID, model, direction)</c> only forwards
/// when <c>model == NULL || model == GetShip()</c>. Every shipped producer passes
/// a model, so in practice only the locally controlled ship vibrates.</para>
///
/// <para><b>Producers</b>:
/// <list type="bullet">
/// <item><b>Weapon fire</b> — <c>Igc/weaponIGC.cpp:174</c> fires
/// <c>effectFire</c> with <c>m_pshipGunner ? m_pshipGunner : m_ship</c>: the
/// gunner's own (child) ship when a player mans the turret, otherwise the firing
/// ship. It passes the seam gate only for the local pilot or the local gunner.</item>
/// <item><b>Collision</b> — <c>Igc/shipIGC.cpp:835,1005,1190-1194</c> fire
/// <c>effectBounce</c> with the ship party, so a collision the local ship is not
/// part of never vibrates.</item>
/// <item><b>Credited damage</b> — <c>WinTrek/trekigc.cpp:4296-4302</c> (client
/// <c>DamageShipEvent</c>): when the damaged ship is the local ship's SOURCE ship
/// (the carrier for a boarded gunner), the credited model exists and the amount
/// is positive, it fires <c>effectBounce</c> with the impact direction:
/// <c>direction = degrees(atan2(backward·(p2−p1), right·(p2−p1))) + 90</c>.</item>
/// </list>
/// <c>effectExplode</c> has no live producer in this checkout (the DirectInput
/// backend defines the effect, but nothing calls it), so the adapter keeps it for
/// completeness and never emits it.</para>
///
/// <para><b>Deviation, stated:</b> native's DirectInput switch
/// (<c>engine/dinput.cpp:1392-1445</c>) has no <c>break</c> after the bounce case,
/// so a bounce also falls through and starts the explode effect when the
/// file-based effects are disabled. That is a device-backend bug, not a gameplay
/// rule; the MonoGame vibration adapter maps each effect to one rumble profile.</para>
/// </summary>
public static class ForceFeedbackRules
{
    /// <summary><c>IIgcSite::PlayFFEffect</c>'s model gate (a null model — the
    /// unpositioned form — always passes).</summary>
    public static bool AllowsModel(short? modelId, short? ownShipId) => modelId is null || modelId == ownShipId;

    /// <summary>The model a weapon shot is credited to (<c>weaponIGC.cpp:174</c>).</summary>
    public static short FireModel(short firingShipId, short? gunnerShipId) => gunnerShipId ?? firingShipId;

    /// <summary><c>effectFire</c> fires only for the local pilot's or local
    /// gunner's own weapon.</summary>
    public static bool AllowsFire(short firingShipId, short? gunnerShipId, short? ownShipId)
        => AllowsModel(FireModel(firingShipId, gunnerShipId), ownShipId);

    /// <summary><c>effectBounce</c> on a collision fires only when the local ship
    /// is a party (the model passed is that party).</summary>
    public static bool AllowsCollision(short partyId, short? ownShipId) => AllowsModel(partyId, ownShipId);

    /// <summary>The client <c>DamageShipEvent</c> branch: positive credited
    /// damage to the local SOURCE ship.</summary>
    public static bool AllowsCreditedDamage(short? creditedModelId, short? damagedShipId,
        short? ownSourceShipId, float amount)
        => creditedModelId is not null
           && amount > 0f
           && damagedShipId is { } damaged
           && ownSourceShipId is { } own
           && damaged == own;

    /// <summary>
    /// Native's impact direction in degrees (<c>WinTrek/trekigc.cpp:4297-4301</c>):
    /// <c>((long)DegreesFromRadians(atan2(backward·delta, right·delta)) + 90)</c>,
    /// expressed as <c>0..359</c>. <paramref name="delta"/> is <c>p2 − p1</c>; the
    /// client reconstructs it from the wire's hit normal
    /// (<c>normalize(position1 − position2)</c>, <c>projectileIGC.cpp:224-225</c>),
    /// so the sign is inverted exactly once here.
    /// </summary>
    public static int ImpactDirection(Orientation shipOrientation, Vector3 delta)
    {
        Vector3 right = shipOrientation.GetRight();
        Vector3 backward = shipOrientation.GetBackward();
        double dotRight = right.X * delta.X + right.Y * delta.Y + right.Z * delta.Z;
        double dotBackward = backward.X * delta.X + backward.Y * delta.Y + backward.Z * delta.Z;
        long direction = (long)(Math.Atan2(dotBackward, dotRight) * (180.0 / Math.PI)) + 90;
        return (int)((direction % 360 + 360) % 360);
    }

    /// <summary>Same as <see cref="ImpactDirection"/> but from the wire hit
    /// normal (which is <c>normalize(position1 − position2)</c>, i.e. the negated
    /// <c>p2 − p1</c>).</summary>
    public static int ImpactDirectionFromNormal(Orientation shipOrientation, Vector3 hitNormal)
        => ImpactDirection(shipOrientation, new Vector3(-hitNormal.X, -hitNormal.Y, -hitNormal.Z));
}

/// <summary>
/// A force-feedback device the rules can drive. Kept device-agnostic so the
/// producer gates, expiry and focus handling are unit-testable without a
/// controller (production: <see cref="MonoGameForceFeedback"/>).
/// </summary>
public interface IForceFeedbackDevice
{
    /// <summary>True when a force-feedback-capable device is present
    /// (native <c>HasForceFeedback()</c>, <c>WinTrek/trekinput.cpp:946-952</c>).</summary>
    bool IsAvailable { get; }
    /// <summary>Start one effect (native <c>PlayFFEffect(effectID, direction)</c>).</summary>
    void Play(short effectId, int direction);
    /// <summary>Stop all vibration (effect expiry, focus loss, device loss,
    /// settings off).</summary>
    void Stop();
}

/// <summary>
/// P871 / SIM-0179 — the live feedback controller: applies the native producer
/// gates, expires each effect after a short window (native's DirectInput effects
/// are one-shot <c>Start(1, 0)</c> starts, <c>engine/dinput.cpp:1399-1443</c>), and
/// stops the device immediately on focus loss, device disconnect or the settings
/// toggle. The enable flag defaults to ON, like native's
/// <c>LoadPreference("EnableFeedback", 1)</c> (<c>WinTrek/WinTrek.cpp:3049</c>).
/// </summary>
public sealed class ForceFeedbackController
{
    /// <summary>Default effect window (seconds). Native's own fire/bounce effects
    /// are a few tens of milliseconds; this is the device-adapter's one-shot
    /// duration, not a gameplay rule.</summary>
    public const double DefaultEffectSeconds = 0.12;

    private readonly IForceFeedbackDevice? _device;
    private readonly double _effectSeconds;
    private double _activeUntil = double.NegativeInfinity;
    private short? _activeEffect;
    private int _activeDirection;

    public ForceFeedbackController(IForceFeedbackDevice? device, bool enabled = true,
        double effectSeconds = DefaultEffectSeconds)
    {
        _device = device;
        Enabled = enabled;
        _effectSeconds = effectSeconds;
    }

    /// <summary>Native <c>m_bEnableFeedback</c> (settings default on).</summary>
    public bool Enabled { get; private set; }

    /// <summary>Whether a feedback effect is currently running.</summary>
    public bool IsActive => _activeEffect is not null;

    /// <summary>The last effect/direction actually sent to the device (the
    /// observable command surface for tests and the live proof log).</summary>
    public (short Effect, int Direction)? LastCommand { get; private set; }

    public bool HasFocus { get; private set; } = true;
    public bool IsConnected => _device?.IsAvailable == true;

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (!enabled) Stop();
    }

    /// <summary>Window activation (native stops feeding an inactive window).</summary>
    public void OnFocusChanged(bool focused)
    {
        HasFocus = focused;
        if (!focused) Stop();
    }

    /// <summary>Device unplugged / failed (<c>HasForceFeedback()</c> turns false).</summary>
    public void OnDeviceChanged()
    {
        if (!IsConnected) Stop();
    }

    /// <summary>Collision producer: <c>Igc/shipIGC.cpp:835,1005,1190-1194</c>.</summary>
    public bool PlayCollision(short partyId, short? ownShipId, double now)
        => ForceFeedbackRules.AllowsCollision(partyId, ownShipId)
           && Emit(ForceEffect.Bounce, 0, now);

    /// <summary>Weapon-fire producer: <c>Igc/weaponIGC.cpp:174</c>.</summary>
    public bool PlayWeaponFire(short firingShipId, short? gunnerShipId, short? ownShipId, double now)
        => ForceFeedbackRules.AllowsFire(firingShipId, gunnerShipId, ownShipId)
           && Emit(ForceEffect.Fire, 0, now);

    /// <summary>Credited-damage producer: <c>WinTrek/trekigc.cpp:4296-4302</c>.</summary>
    public bool PlayCreditedDamage(short? creditedModelId, short damagedShipId, short? ownSourceShipId,
        float amount, int direction, double now)
        => ForceFeedbackRules.AllowsCreditedDamage(creditedModelId, damagedShipId, ownSourceShipId, amount)
           && Emit(ForceEffect.Bounce, direction, now);

    /// <summary>Per-frame expiry (a one-shot effect stops by itself).</summary>
    public void Update(double now)
    {
        if (_activeEffect is not null && now >= _activeUntil) Stop();
    }

    private bool Emit(short effect, int direction, double now)
    {
        if (!Enabled || !HasFocus || !IsConnected) return false;
        _device!.Play(effect, direction);
        _activeEffect = effect;
        _activeDirection = direction;
        _activeUntil = now + _effectSeconds;
        LastCommand = (effect, direction);
        return true;
    }

    /// <summary>Stop everything (expiry, focus loss, disconnect, toggle off,
    /// teardown). Safe to call repeatedly.</summary>
    public void Stop()
    {
        if (_activeEffect is not null) _device?.Stop();
        _activeEffect = null;
        _activeDirection = 0;
        _activeUntil = double.NegativeInfinity;
    }
}
