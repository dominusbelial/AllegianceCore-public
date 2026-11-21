#if BROWSER
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>One render-thread scratch set per WebGL device. Sky painters draw
/// sequentially, including destination postcards; disposal follows the device.</summary>
internal sealed class BrowserSkyBuffers
{
    private static readonly ConditionalWeakTable<GraphicsDevice, BrowserSkyBuffers> Devices = new();
    public static BrowserSkyBuffers For(GraphicsDevice device) => Devices.GetValue(device, static gd => new(gd));
    public readonly VertexPositionColorTexture[] Quad = new VertexPositionColorTexture[4];
    public readonly short[] Indices = [0, 1, 2, 2, 1, 3];
    private readonly BasicEffect _effect;
    private BrowserSkyBuffers(GraphicsDevice device)
    {
        _effect = new(device);
        device.Disposing += Dispose;
    }
    private void Dispose(object? sender, EventArgs e) => _effect.Dispose();
    public BasicEffect Prepare(Texture2D? texture, Vector3 diffuse, Matrix view, Matrix projection)
    {
        _effect.TextureEnabled = true; _effect.Texture = texture;
        _effect.VertexColorEnabled = true; _effect.DiffuseColor = diffuse;
        _effect.LightingEnabled = false; _effect.Alpha = 1; _effect.FogEnabled = false;
        _effect.View = view; _effect.Projection = projection; _effect.World = Matrix.Identity;
        return _effect;
    }
}
#endif
