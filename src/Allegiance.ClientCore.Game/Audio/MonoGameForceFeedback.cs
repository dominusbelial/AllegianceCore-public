using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P871 / SIM-0179 — the MonoGame force-feedback adapter: native's DirectInput
/// joystick effects (<c>engine/dinput.cpp:1392-1445</c>) expressed with
/// <see cref="GamePad.SetVibration(PlayerIndex, float, float)"/>, the only
/// rumble actuator MonoGame exposes.
/// <para>
/// <b>Device status</b>: native gates on
/// <c>m_pinputEngine-&gt;GetJoystick(0)-&gt;HasForceFeedback()</c>
/// (<c>WinTrek/trekinput.cpp:946-952</c>); the equivalent here is a connected
/// pad that reports a vibration motor.
/// </para>
/// <para>
/// <b>Effect mapping</b> (a device adaptation, not a gameplay rule): native's
/// <c>effectFire</c> is a constant rumble, <c>effectBounce</c> a directional
/// (polar) impulse, <c>effectExplode</c> a stronger constant. Fire drives both
/// motors equally; a bounce pans between them by the native impact direction
/// (<c>WinTrek/trekigc.cpp:4297-4301</c>); explode is the strongest, but no live
/// producer emits it in this checkout.
/// </para>
/// </summary>
public sealed class MonoGameForceFeedback : IForceFeedbackDevice
{
    private readonly PlayerIndex _player;
    private bool _lastConnected;

    public MonoGameForceFeedback(PlayerIndex player = PlayerIndex.One) => _player = player;

    /// <summary>Native <c>HasForceFeedback()</c>.</summary>
    public bool IsAvailable
    {
        get
        {
            var caps = GamePad.GetCapabilities(_player);
            return caps.IsConnected
                   && (caps.HasLeftVibrationMotor || caps.HasRightVibrationMotor);
        }
    }

    /// <summary>True when the pad's connected state changed since the previous
    /// poll (the caller turns that into <see cref="ForceFeedbackController.OnDeviceChanged"/>).</summary>
    public bool PollConnectionChanged()
    {
        bool connected = GamePad.GetCapabilities(_player).IsConnected;
        bool changed = connected != _lastConnected;
        _lastConnected = connected;
        return changed;
    }

    public void Play(short effectId, int direction)
    {
        if (!IsAvailable) return;
        var (left, right) = Profile(effectId, direction);
        GamePad.SetVibration(_player, left, right);
    }

    public void Stop()
    {
        // Unconditional: a pad that vanished between Play and Stop must still be
        // told to stop, and a disconnected SetVibration is a no-op.
        GamePad.SetVibration(_player, 0f, 0f);
    }

    /// <summary>The motor profile for one effect (pure, unit-testable).</summary>
    public static (float Left, float Right) Profile(short effectId, int direction)
    {
        const float fire = 0.28f;
        const float bounce = 0.45f;
        const float explode = 0.65f;
        return effectId switch
        {
            ForceEffect.Fire => (fire, fire),
            ForceEffect.Bounce => Pan(bounce, direction),
            ForceEffect.Explode => (explode, explode),
            _ => (0f, 0f),
        };
    }

    /// <summary>Pan a directional impulse between the two motors: direction 0°
    /// (ship right) leans right, 180° leans left, front/back split evenly —
    /// the crude two-motor reduction of native's polar <c>lDirection</c>.</summary>
    private static (float Left, float Right) Pan(float strength, int direction)
    {
        double radians = direction * Math.PI / 180.0;
        float pan = (float)Math.Cos(radians);          // +1 = right, -1 = left
        float right = strength * (0.5f + 0.5f * pan);
        float left = strength * (0.5f - 0.5f * pan);
        return (Math.Clamp(left, 0f, 1f), Math.Clamp(right, 0f, 1f));
    }
}
