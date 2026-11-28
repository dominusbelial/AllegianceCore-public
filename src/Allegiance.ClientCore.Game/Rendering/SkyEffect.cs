using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>A sky painter's device-local effect. Inactive tiers stay cold.
/// The same painter can be used for a destination postcard and then the flight camera.</summary>
internal sealed class SkyEffect(GraphicsDevice device) : IDisposable
{
    private BasicEffect? _effect;

    public BasicEffect Prepare(Texture2D? texture, Vector3 tint, Matrix view, Matrix projection, Matrix world)
    {
        var effect = _effect ??= new BasicEffect(device) { VertexColorEnabled = true, LightingEnabled = false };
        effect.TextureEnabled = texture is not null;
        effect.Texture = texture;
        effect.DiffuseColor = tint;
        effect.View = view;
        effect.Projection = projection;
        effect.World = world;
        return effect;
    }

    public void Dispose() => _effect?.Dispose();
}
