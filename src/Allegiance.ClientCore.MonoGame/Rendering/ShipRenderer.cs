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
    private readonly GraphicsDevice _device;

    /// <summary>
    /// Live-playtest-discovered fix (post-P209): <c>mesh.ParentBone.Transform</c>
    /// (the Blender/FBX export's baked axis-swap+scale) correctly converts
    /// Blender's Z-up-meters convention into the engine's Y-up-units
    /// convention, but leaves the mesh's actual geometric nose pointing along
    /// what the sim considers its local <b>right</b> axis, not
    /// <b>forward</b> (backward negated). Confirmed empirically: at
    /// identity sim orientation the nose rendered pointing to screen-left
    /// instead of away from the (behind-the-ship) camera; a pure ROLL
    /// (rotation about the sim's forward axis, which the mesh's nose is NOT
    /// on) swung the nose up/down instead of banking it, exactly matching
    /// what was reported live ("A/D pulls the nose up/down"). A +90°
    /// yaw candidate (rotation about the sim's up axis) looked correct in a
    /// static offline screenshot, but a foreshortened head-on silhouette
    /// turned out to be ambiguous — live testing showed it pointed the nose
    /// AT the camera instead of away, so the sign is -90°, confirmed live.
    /// This is a one-time static correction to the mesh's resting
    /// alignment — applied in the same "sim-local" space slot as
    /// <c>ParentBone.Transform</c> itself, before the ship's real per-frame
    /// orientation is applied — so every subsequent roll/pitch/yaw command
    /// now rotates around axes that actually match the visible geometry.
    /// </summary>
    private static readonly Matrix NoseAlignmentCorrection = Matrix.CreateRotationY(MathHelper.ToRadians(-90f));

    public ShipRenderer(Model model, GraphicsDevice device)
    {
        _model = model;
        _device = device;
    }

    /// <summary>Draw the ship model at the given world transform under the
    /// provided view/projection pair. Culling is disabled (CullNone) because
    /// the Meshy/Blender FBX winding is back-facing to MonoGame's default
    /// counter-clockwise-front convention — with default culling every triangle
    /// is rejected and the model vanishes. Disabling it guarantees the mesh
    /// rasterizes for this POC.
    /// <para>P270 — <paramref name="opacity"/> in (0,1] alpha-blends the whole
    /// model (a cloaked ship renders translucent so a teammate can still tell a
    /// cloaked ally is there). Below 1.0 the pass runs under
    /// <see cref="BlendState.AlphaBlend"/> with <c>BasicEffect.Alpha</c> set;
    /// the prior blend state is restored afterwards so the opaque passes that
    /// follow aren't affected. The silhouette stays recognisable (it is a true
    /// partial transparency of the real mesh, not a sticker), and any shimmer
    /// is applied by the caller modulating this value over time.</para></summary>
    public void Draw(Matrix world, Matrix view, Matrix projection, float opacity = 1f)
    {
        bool translucent = opacity < 0.999f;
        if (translucent)
            _device.BlendState = BlendState.AlphaBlend;

        foreach (var mesh in _model.Meshes)
        {
            foreach (var effect in mesh.Effects)
            {
                if (effect is not BasicEffect basic)
                    continue;
                basic.World = mesh.ParentBone.Transform * NoseAlignmentCorrection * world;
                basic.View = view;
                basic.Projection = projection;
                basic.EnableDefaultLighting();
                basic.Alpha = translucent ? opacity : 1f;
                basic.GraphicsDevice.RasterizerState = RasterizerState.CullNone;
            }
            mesh.Draw();
        }

        if (translucent)
            _device.BlendState = BlendState.Opaque;
    }
}
