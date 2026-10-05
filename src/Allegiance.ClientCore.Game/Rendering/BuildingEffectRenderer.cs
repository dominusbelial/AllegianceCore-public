using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.Core.Rendering;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws the in-progress "station under construction" visual — the same
/// procedural <see cref="StationMeshFactory"/> hub+ring mesh <see cref="StationRenderer"/>
/// uses, but deliberately <b>not</b> identical to a finished station: it is
/// rendered semi-transparent under alpha blending, in native's hardcoded
/// building-effect violet, and (via the caller's world matrix) scaled by a
/// progress fraction so it visibly <i>grows</i> toward the finished station.
/// This is P294's visual for <c>FM_S_EXPORT(26)/OT_buildingEffect</c>.
/// </summary>
/// <remarks>
/// <para><b>Reuses the station geometry, not a new shape.</b> The construction
/// is the target station being built, so the same procedural mesh that
/// <see cref="StationRenderer"/> draws for the finished base is the honest
/// under-construction stand-in — the task's explicit direction (no new
/// geometry). The mesh is generated once and shared; only the draw state
/// differs from <see cref="StationRenderer"/> (translucent blend + distinct
/// tint + progress scale).</para>
/// <para><b>Look:</b> the tint is native's own building-effect colour
/// <c>Color(50/255, 28/255, 146/255)</c> (<c>src/Igc/buildingeffectigc.h:93</c> —
/// <c>LoadEffect</c> deliberately hardcodes it, ignoring the side colour via the
/// commented-out <c>//*pcolor</c>), and <c>Alpha</c> is driven by the caller's
/// <c>alpha</c> parameter (the reveal). Rendering swaps
/// <see cref="GraphicsDevice.BlendState"/> to <see cref="BlendState.AlphaBlend"/>
/// around the draw (restoring <see cref="BlendState.Opaque"/> after), exactly the
/// translucent-over-opaque ordering the scene needs; the caller draws this after
/// the opaque asteroids/stations so the blend lands correctly.</para>
/// </remarks>
public sealed class BuildingEffectRenderer : IDisposable
{
    private readonly VertexBuffer _vertices;
    private readonly IndexBuffer _indices;
    private readonly int _triangleCount;
    private readonly BasicEffect _effect;

    /// <summary>The under-construction tint — native's hardcoded building-effect
    /// violet <c>Color(50/255, 28/255, 146/255)</c> (<c>buildingeffectigc.h:93</c>),
    /// deliberately distinct from <see cref="StationRenderer.FriendlyColor"/>/<see cref="StationRenderer.EnemyColor"/>
    /// so a construction never reads as a finished base.</summary>
    public static readonly Color ConstructionColor =
        new(50f / 255f, 28f / 255f, 146f / 255f);

    /// <summary>Alpha floor at progress 0 (still faintly visible from the very
    /// start) and ceiling at progress 1 (still translucent — never identical to
    /// the opaque finished station).</summary>
    public const float MinAlpha = 0.35f;
    public const float MaxAlpha = 0.72f;

    /// <param name="gd">The graphics device the buffers are created on (no
    /// content-pipeline asset — the station mesh is generated, not loaded).</param>
    /// <param name="mesh">The shared mesh from <see cref="StationMeshFactory"/>.
    /// Null to build the default procedural station (same default as
    /// <see cref="StationRenderer"/>).</param>
    public BuildingEffectRenderer(GraphicsDevice gd, StationMesh? mesh = null)
    {
        mesh ??= StationMeshFactory.Generate();

        var verts = new VertexPositionNormalTexture[mesh.VertexCount];
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            verts[i] = new VertexPositionNormalTexture(
                new Vector3(mesh.Positions[i * 3], mesh.Positions[i * 3 + 1], mesh.Positions[i * 3 + 2]),
                new Vector3(mesh.Normals[i * 3], mesh.Normals[i * 3 + 1], mesh.Normals[i * 3 + 2]),
                new Vector2(mesh.Uvs[i * 2], mesh.Uvs[i * 2 + 1]));
        }

        _vertices = new VertexBuffer(gd, typeof(VertexPositionNormalTexture), mesh.VertexCount, BufferUsage.WriteOnly);
        _vertices.SetData(verts);

        _indices = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, mesh.Indices.Length, BufferUsage.WriteOnly);
        _indices.SetData(mesh.Indices);

        _triangleCount = mesh.TriangleCount;

        _effect = new BasicEffect(gd)
        {
            AmbientLightColor = new Vector3(0.32f, 0.34f, 0.36f),
            TextureEnabled = false,
        };
    }

    /// <summary>
    /// Draw the construction at the given world transform (the caller bakes the
    /// progress-scaled radius into <paramref name="world"/>, exactly as Game1
    /// does for stations) under the provided view/projection pair, blended at
    /// <paramref name="alpha"/> (the reveal, typically from
    /// <see cref="State.BuildingEffectEntity.ProgressFraction"/> mapped into
    /// <see cref="MinAlpha"/>..<see cref="MaxAlpha"/>). P530 —
    /// <paramref name="lighting"/> is the current sector's native bidirectional
    /// lighting; when supplied the shared <see cref="SectorLightingEffect"/>
    /// replaces the stock rig (key + blue back-light + 0.25 ambient, specular
    /// zero, third light off). When null the legacy stock rig is kept
    /// (back-compat / non-scene callers). <see cref="BlendState.AlphaBlend"/>
    /// is applied for this draw and restored to <see cref="BlendState.Opaque"/>
    /// after.
    /// </summary>
    public void Draw(Matrix world, Matrix view, Matrix projection, float alpha, SectorLighting? lighting = null)
    {
        var gd = _effect.GraphicsDevice;

        _effect.DiffuseColor = ConstructionColor.ToVector3();
        _effect.Alpha = alpha;
        _effect.World = world;
        _effect.View = view;
        _effect.Projection = projection;
        if (lighting is { } lp)
            SectorLightingEffect.Apply(_effect, lp);
        else
            _effect.EnableDefaultLighting();
        _effect.CurrentTechnique.Passes[0].Apply();

        // P530 — explicit render state for the building-effect pass: pin
        // depth/rasterizer/sampler (P529 discipline) and alpha-blend for the
        // translucent construction, restoring Opaque after so the opaque passes
        // that follow aren't affected.
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
        gd.SamplerStates[0] = SamplerState.LinearClamp;
        gd.BlendState = BlendState.AlphaBlend;
        gd.SetVertexBuffer(_vertices);
        gd.Indices = _indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _triangleCount);
        gd.BlendState = BlendState.Opaque;
    }

    public void Dispose()
    {
        _vertices.Dispose();
        _indices.Dispose();
        _effect.Dispose();
    }
}
