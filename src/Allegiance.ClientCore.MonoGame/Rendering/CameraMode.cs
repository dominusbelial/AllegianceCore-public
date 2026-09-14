namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// The active view mode for the local pilot. <see cref="Chase"/> is the
/// original third-person view (and remains the default at startup, so anyone
/// not pressing the new toggle key sees exactly the pre-P211 behaviour);
/// <see cref="Cockpit"/> is the first-person view added in P211, where you sit
/// inside the ship looking along its real forward direction and your own hull
/// is (correctly) not drawn. Toggled with <c>V</c> in <see cref="Game1"/>.
/// </summary>
public enum CameraMode
{
    Chase,
    Cockpit,
}
