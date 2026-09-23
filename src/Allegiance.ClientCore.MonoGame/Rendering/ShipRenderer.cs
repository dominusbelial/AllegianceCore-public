using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws a ship model once per visible ship. P288: the model is chosen per
/// ship instance by the caller (via <see cref="HullModelResolver"/>), so
/// different factions/classes actually render different geometry instead of
/// every ship sharing one hardcoded mesh. Each instance supplies its own
/// world transform plus the model to draw. Lighting is the built-in
/// BasicEffect default (directionless-ish default light) — good enough for a
/// POC, no bespoke shaders.
/// </summary>
public sealed class ShipRenderer
{
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

    /// <summary>The same baked-axis correction applied by the ship mesh, exposed
    /// so the P288 imported missile content models (same Blender FBX pipeline)
    /// use the identical alignment.</summary>
    public static readonly Matrix SharedNoseAlignment = NoseAlignmentCorrection;

    /// <summary>P337 — small ambient term so a ship's unlit hull faces still
    /// read real texture detail against space instead of crushing to black.
    /// Matches the ambient <c>StationRenderer</c>/<c>BuildingEffectRenderer</c>/
    /// <c>ProbeRenderer</c> already use (0.32, 0.34, 0.36).</summary>
    public static readonly Vector3 ShipAmbient = new(0.32f, 0.34f, 0.36f);

    /// <summary>P355 — camera-relative "headlight" diffuse strength. BasicEffect
    /// only exposes 3 directional lights; the 3 defaults are FIXED in world
    /// space, so a ship rotated so none of its camera-facing faces catch one of
    /// them falls back to flat ambient alone — which for a genuinely dark hull
    /// can sit visually on top of the black space backdrop. Repurposing the
    /// weakest default (the back/rim light) as a light that always points from
    /// the camera into the scene guarantees the face the camera is actually
    /// looking at always receives direct illumination regardless of the ship's
    /// world orientation. Deliberately moderate so well-lit ships are not
    /// flattened/washed out (see P352/P353: genuinely dark GT/IC liveries must
    /// still read as recognizably dark).</summary>
    public static readonly Vector3 HeadlightColor = new(0.62f, 0.64f, 0.68f);

    public ShipRenderer(GraphicsDevice device)
    {
        _device = device;
    }

    /// <summary>Draw the ship <paramref name="model"/> at the given world
    /// transform under the provided view/projection pair. Culling is disabled
    /// (CullNone) because the Meshy/Blender FBX winding is back-facing to
    /// MonoGame's default counter-clockwise-front convention — with default
    /// culling every triangle is rejected and the model vanishes. Disabling it
    /// guarantees the mesh rasterizes for this POC.
    /// <paramref name="opacity"/> in (0,1] alpha-blends the whole
    /// model (a cloaked ship renders translucent so a teammate can still tell a
    /// cloaked ally is there). Below 1.0 the pass runs under
    /// <see cref="BlendState.AlphaBlend"/> with <c>BasicEffect.Alpha</c> set;
    /// the prior blend state is restored afterwards so the opaque passes that
    /// follow aren't affected. The silhouette stays recognisable (it is a true
    /// partial transparency of the real mesh, not a sticker), and any shimmer
    /// is applied by the caller modulating this value over time.</para>
    /// <para>P333 — <paramref name="worldScale"/> is a PURE VISUAL uniform scale
    /// folded into the model's local space before the world transform (uniform
    /// scale commutes with the rotations, so placement here is equivalent to
    /// scaling the assembled world matrix). The caller supplies it from
    /// <see cref="ShipRenderScale.ScaleForLength"/> to normalize each ship's raw
    /// model size to the real hull size; default 1 keeps every existing call
    /// (roster-capture, etc.) byte-for-byte unchanged. It touches rendering
    /// only — the server's collision radius is never involved.</para>
    /// <para>P337 — <paramref name="texture"/> is the real Meshy baseColor for
    /// this asset, bound onto the preserved UVs (BasicEffect diffuse) so a ship
    /// shows its faction livery/panel lines instead of flat matte gray. When
    /// null the effect renders untextured (all-white BasicEffect default),
    /// which keeps existing untextured callers intact. The ambient term is set
    /// to <see cref="ShipAmbient"/> (matching <c>StationRenderer</c>) so the
    /// hull's unlit faces still read real texture detail against space rather
    /// than crushing to black the moment a texture lands.</para></summary>
    public void Draw(Model model, Matrix world, Matrix view, Matrix projection, float opacity = 1f,
        float worldScale = 1f, Texture2D? texture = null)
    {
        bool translucent = opacity < 0.999f;
        if (translucent)
            _device.BlendState = BlendState.AlphaBlend;

        var localScale = Matrix.CreateScale(worldScale);
        // P355 — camera-relative headlight: the 3 BasicEffect default lights are
        // fixed in world space, so repurpose the weakest (back/rim, slot 2) into
        // a light pointing from the camera into the scene (light.Direction is the
        // propagation direction, so it must equal the camera's world forward).
        var viewInverse = Matrix.Invert(view);
        // headlight propagation direction = from camera into the scene =
        // the world-space direction that projects to view -Z. Faces the camera
        // sees face back toward the camera, so this lights exactly the visible
        // side regardless of ship world orientation.
        var headlightDir = Vector3.Normalize(Vector3.TransformNormal(Vector3.Forward, viewInverse));
        foreach (var mesh in model.Meshes)
        {
            foreach (var effect in mesh.Effects)
            {
                if (effect is not BasicEffect basic)
                    continue;
                basic.World = mesh.ParentBone.Transform * NoseAlignmentCorrection * localScale * world;
                basic.View = view;
                basic.Projection = projection;
                basic.EnableDefaultLighting();
                basic.AmbientLightColor = ShipAmbient;   // P337 — keep unlit faces readable
                // P355 — override the default back-light with the camera-relative
                // headlight so the face the camera sees is always illuminated.
                basic.DirectionalLight2.Direction = headlightDir;
                basic.DirectionalLight2.DiffuseColor = HeadlightColor;
                basic.DirectionalLight2.SpecularColor = Vector3.Zero;
                basic.DirectionalLight2.Enabled = true;
                basic.Texture = texture;                 // P337 — real Meshy livery
                basic.TextureEnabled = texture is not null;
                basic.Alpha = translucent ? opacity : 1f;
                basic.GraphicsDevice.RasterizerState = RasterizerState.CullNone;
            }
            mesh.Draw();
        }

        if (translucent)
            _device.BlendState = BlendState.Opaque;
    }
}
