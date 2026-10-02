using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws the shared procedural station mesh once per station instance. One
/// <see cref="VertexBuffer"/>/<see cref="IndexBuffer"/> is built from
/// <see cref="StationMeshFactory"/>'s output in the constructor and re-used for
/// every station — the "generated, not loaded" model (no content-pipeline
/// asset), exactly like <see cref="AsteroidRenderer"/>/<see cref="WarpRenderer"/>.
/// </summary>
/// <remarks>
/// <para><b>Look:</b> a solid, lit structure (a station is physical, unlike a
/// self-lit warp), shaded by <see cref="BasicEffect"/>'s default lighting from
/// the factory's analytic outward normals. The per-instance friendly/enemy
/// side tint is applied as the flat material (<c>DiffuseColor</c>) under
/// lighting, so a friendly station reads in the HUD's own teal accent and an
/// enemy/neutral one in a contrasting warm tone — the same side-colour
/// convention the rest of this client's overlays use. No texture: the flat
/// tint is deliberately enough for this temporary placeholder.</para>
/// <para><b>Solid geometry:</b> the hub+ring mesh is closed with outward
/// analytic normals (see <see cref="StationMeshFactory"/>), so the default
/// back-face culling is correct — the solid-rock argument
/// <see cref="AsteroidRenderer"/> makes, not the hollow CullNone a warp needs.
/// No per-instance allocation beyond the (cheap, reused) world matrix.</para>
/// </remarks>
public sealed class StationRenderer : IDisposable
{
    private readonly VertexBuffer _vertices;
    private readonly IndexBuffer _indices;
    private readonly int _triangleCount;
    private readonly BasicEffect _effect;

    /// <summary>Friendly-station tint — the HUD's own teal accent
    /// (<see cref="HudColor.TealAccent"/>), so your own side's stations read as
    /// yours (the same colour family the scoreboard/HUD uses for the local
    /// craft).</summary>
    public static readonly Color FriendlyColor =
        new(HudColor.TealAccent.R, HudColor.TealAccent.G, HudColor.TealAccent.B);

    /// <summary>Enemy/neutral-station tint — a warm amber-orange, deliberately
    /// distinct from the friendly teal so hostile or neutral infrastructure is
    /// told apart at a glance (matches the warmer tones this client already
    /// uses for threats/infrastructure, e.g. the radar's gold station icon).</summary>
    public static readonly Color EnemyColor = new(1.00f, 0.56f, 0.22f);

    /// <summary>Fallback visual radius (world units) used only when a station's
    /// type can't be resolved from the static core (a missing/unknown
    /// <c>DataStationTypeIGC</c>). Calibrated to the real most-common value in
    /// PCore001.igc: Garrisons/Supremacy/Expansion bases all carry
    /// <c>Radius = 302.5</c> (measured from the corpus), so a station with an
    /// unresolvable type still renders at a sensible ~3x-ship size instead of
    /// vanishing or exploding to an arbitrary size.</summary>
    public const float DefaultFallbackRadius = 302.5f;

    /// <param name="gd">The graphics device the buffers are created on (no
    /// content-pipeline asset — the station is generated, not loaded).</param>
    /// <param name="mesh">The shared mesh from <see cref="StationMeshFactory"/>.
    /// Null to build the default procedural station.</param>
    public StationRenderer(GraphicsDevice gd, StationMesh? mesh = null)
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
            // Flat material: the per-instance side tint is applied as
            // DiffuseColor in Draw. Ambient tuned so dark surfaces (the side
            // turned away from the key light) still read against the starfield.
            AmbientLightColor = new Vector3(0.32f, 0.34f, 0.36f),
            TextureEnabled = false,
        };
    }

    /// <summary>
    /// Draw the shared station mesh at the given world transform under the
    /// provided view/projection pair, tinted <paramref name="tint"/> (the
    /// friendly/enemy side colour). The caller bakes the real per-instance
    /// radius scale into <paramref name="world"/> (via
    /// <c>Matrix.CreateScale(stationTypeRadius)</c>) exactly as
    /// <see cref="Game1"/> does for asteroids/warps — so a station's visual
    /// radius equals its real <c>DataStationTypeIGC.Radius</c> in world units.
    /// </summary>
    public void Draw(Matrix world, Matrix view, Matrix projection, Color tint)
    {
        _effect.DiffuseColor = tint.ToVector3();
        _effect.World = world;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.EnableDefaultLighting();
        _effect.CurrentTechnique.Passes[0].Apply();

        var gd = _effect.GraphicsDevice;
        gd.SetVertexBuffer(_vertices);
        gd.Indices = _indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _triangleCount);
    }

    public void Dispose()
    {
        _vertices.Dispose();
        _indices.Dispose();
        _effect.Dispose();
    }
}
