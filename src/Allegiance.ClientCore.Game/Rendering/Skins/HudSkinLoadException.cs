namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>Thrown when a skin definition fails validation. A failed definition
/// is rejected in full; the prior skin stays active.</summary>
public sealed class HudSkinLoadException : Exception
{
    public HudSkinLoadException(string message) : base(message) { }
    public HudSkinLoadException(string message, Exception inner) : base(message, inner) { }
}
