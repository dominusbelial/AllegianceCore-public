using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws the Bios Scout model once per visible ship. The model is loaded once
/// and re-used; each ship instance supplies its own world transform. Lighting
/// is the built-in BasicEffect default (directionless-ish default light) —
/// good enough for a POC, no bespoke shaders.
/// </summary>
public sealed class ShipRenderer
{
    private readonly Model _model;

    public ShipRenderer(Model model) => _model = model;

    /// <summary>Draw the ship model at the given world transform under the
    /// provided view/projection pair. Culling is disabled (CullNone) because
    /// the Meshy/Blender FBX winding is back-facing to MonoGame's default
    /// counter-clockwise-front convention — with default culling every triangle
    /// is rejected and the model vanishes. Disabling it guarantees the mesh
    /// rasterizes for this POC.</summary>
    public void Draw(Matrix world, Matrix view, Matrix projection)
    {
        foreach (var mesh in _model.Meshes)
        {
            foreach (var effect in mesh.Effects)
            {
                if (effect is not BasicEffect basic)
                    continue;
                basic.World = mesh.ParentBone.Transform * world;
                basic.View = view;
                basic.Projection = projection;
                basic.EnableDefaultLighting();
                basic.GraphicsDevice.RasterizerState = RasterizerState.CullNone;
            }
            mesh.Draw();
        }
    }
}
