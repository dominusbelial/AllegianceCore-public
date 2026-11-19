using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Assets;

/// <summary>Optional presentation-only textures already prepared by the platform.
/// This lookup never performs IO or decoding. True with null means render the
/// existing solid/empty fallback while the platform prepares that visual.</summary>
public interface IResidentTextureSource
{
    long TextureRevision { get; }
    bool TryGetTexture(string stem, bool colorKey, out Texture2D? texture);
}
