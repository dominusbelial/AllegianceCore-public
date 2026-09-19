namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// P270 — client-side cloak presentation. This is an ORIGINAL UX effect: native
/// has no distinct 3D-viewport render for a cloaked ship (the only cloak
/// client data is the loadout/console pane, see <c>WinTrek/consoledata.cpp</c>
/// <c>ModelData::GetCloaking()/IsCloaked()</c>; the model renderer has no
/// opacity/shimmer path), so the effect is designed here, clearly labelled as
/// such — the same honest "no native visual existed" stance P245 took for the
/// warp flash.
/// <para>
/// Pure float math, no MonoGame types, so it is unit-testable headless. The
/// wire carries only the <c>CloakActive</c> state bit (P269), not the sim's
/// exact visible-fraction ramp, so the client renders a cloaked ship at a
/// representative translucent opacity with a slow shimmer — a teammate can
/// still clearly tell the ally ship is there even though an undetectable enemy
/// receives no data about it at all (the server gates that in
/// <see cref="Allegiance.Server.Server"/>.BroadcastShipsUpdate, P270).
/// </para>
/// </summary>
public static class CloakVisual
{
    /// <summary>Nominal opacity of a fully-cloaked ship — clearly translucent
    /// (the real mesh shows through partly) yet recognisably a ship.</summary>
    public const float BaseOpacity = 0.32f;

    /// <summary>Fractional shimmer amplitude about <see cref="BaseOpacity"/>
    /// — the slow wobble that reads as a living cloak rather than a static
    /// glass shell.</summary>
    public const float ShimmerAmplitude = 0.14f;

    /// <summary>Angular frequency of the shimmer, radians per second (≈ 0.8 Hz
    /// — a calm ripple, not a strobe).</summary>
    public const float ShimmerRadiansPerSecond = 5.0f;

    /// <summary>Hard floor so the shimmer can never blink the ship fully out
    /// (a teammate must always be able to tell the ally is there).</summary>
    public const float MinOpacity = 0.06f;

    /// <summary>Ceiling so a cloaked ship never animates back to fully solid.</summary>
    public const float MaxOpacity = 0.8f;

    /// <summary>Clamped shimmering opacity for a cloaked ship at wall-clock
    /// <paramref name="elapsedSeconds"/>. Non-cloaked callers pass 1.0
    /// directly — this is only the cloaked value.</summary>
    public static float Opacity(double elapsedSeconds)
    {
        float ripple = (float)Math.Sin(ShimmerRadiansPerSecond * elapsedSeconds);
        float v = BaseOpacity * (1f + ShimmerAmplitude * ripple);
        if (v < MinOpacity) return MinOpacity;
        if (v > MaxOpacity) return MaxOpacity;
        return v;
    }

    /// <summary>Whether a ship's compact <c>StateM</c> word carries the cloak
    /// bit (P269's <c>cloakActiveIGC = 0x800</c>) — the single wire surface the
    /// client has for \"this ship is cloaked\".</summary>
    public static bool IsCloaked(int stateM) =>
        (stateM & Allegiance.Core.Sim.ShipControlState.CloakActive) != 0;
}
