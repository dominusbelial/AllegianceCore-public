namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Client presentation rules shared by both HUD styles.</summary>
public static class ShipEffectRules
{
    /// <summary>
    /// P915 visual adjustment to native's fixed 1.5–2.5-unit vent puffs
    /// (effect/thinggeo.cpp:739). Only the own hull in external views is scaled.
    /// Full size at length >= 12 and camera distance >= 24 world units;
    /// smaller/closer hulls use the smaller ratio. Unknown dimensions retain
    /// native size. Thresholds, vent counts and lifetime are independent.
    /// </summary>
    public static float DamageSmokeScale(bool ownShip, bool externalView, float hullLength, float cameraDistance)
    {
        if (!ownShip || !externalView || !float.IsFinite(hullLength) || hullLength <= 0
            || !float.IsFinite(cameraDistance) || cameraDistance <= 0) return 1f;
        return Math.Min(1f, Math.Min(hullLength / 12f, cameraDistance / 24f));
    }

    /// <summary>
    /// Native WinTrek.cpp:7999-8006 requires a mounted afterburner. Only the
    /// owner's granted loadout is known on this client. Remote lifepods are
    /// excluded; other remote hulls retain the button-based approximation.
    /// </summary>
    public static bool HasAfterburner(bool ownShip, bool ownMounted, bool lifepod)
        => !lifepod && (!ownShip || ownMounted);

    /// <summary>No residual ramp survives removal of the part (e.g. ejection).
    /// Native sets thrust power directly to zero when absent (WinTrek.cpp:8005-8006).
    /// The existing client ramp for mounted parts is preserved.</summary>
    public static float ThrustPower(bool mounted, bool button, float previous, float dt, float onRate, float offRate)
        => !mounted ? 0f : button ? Math.Min(1f, previous + onRate * dt) : Math.Max(0f, previous - offRate * dt);

    /// <summary>Native keeps ordinary throttle glow without an afterburner;
    /// only the boost override depends on the part (thinggeo.cpp:442-455).</summary>
    public static float NozzleGlow(bool mounted, bool button, float throttle)
        => mounted && button ? 1f : Math.Clamp(0.5f * (throttle + 1f), 0f, 1f);
}
