using System;
using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Allegiance.Core.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P545 — draws projectile hull impacts the native way. Native
/// <c>ThingGeo::AddHullHit</c> (effect/thinggeo.cpp:1322-1340) does <b>two</b>
/// things, both now reproduced (replacing the old two additive white soft
/// blobs — the "distracting white thing on asteroid hits"):
/// <list type="bullet">
/// <item><b>Surface flash</b> — <c>HullHitGeoImpl</c> (efgeo.cpp:655-755)
/// draws a <b>fx11bmp</b> normal decal + <b>f103bmp</b> tangent decal
/// (<c>AWF_EFFECT_NORMAL</c>/<c>AWF_EFFECT_TANGENT</c>, efart.h:16-17),
/// white-tinted, oriented by the hit's normal/forward/right tangent frame,
/// grown <c>scale = 0.1 + 10·age</c> up to <c>maxSize = 2.0</c>
/// (efgeo.cpp:718-725), then removed (~0.19&#8209;s life). Additive decals
/// (DrawDecal default <c>BlendModeAdd</c>).</item>
/// <item><b>Two debris bits</b> — <c>BitsGeo</c> draws small <b>utl10</b>
/// geometry (<c>AWF_EFFECT_BIT</c>, efgeo.cpp:795) per bit, scaled to
/// <c>m_size = radius/2</c>, lit by the scene light, flying at constant
/// velocity <c>0.4·size</c> for 2&#8209;s — real 3D chunks, <b>not glowing
/// sprites</b>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Blend.</b> The flash decals are additive (native default). The
/// debris bits are lit opaque geometry (BasicEffect default lighting, as
/// <see cref="ProbeRenderer"/>) — no blend change.</para>
/// </remarks>
public sealed class HullHitSparkRenderer : IDisposable
{
    /// <summary>Native flash max size and growth (efgeo.cpp:716-724).</summary>
    private const float FlashMaxSize = 2.0f;
    /// <summary>Native flash growth rate <c>maxSize·5 = 10</c>/s.</summary>
    private const float FlashGrowthRate = 10.0f;

    private readonly NativeEffectTextures _native;
    private readonly ParticleBillboardBatcher _batcher;

    // The shared utl10 debris-bit mesh (AWF_EFFECT_BIT), built once.
    private readonly VertexBuffer _bitVertices;
    private readonly IndexBuffer _bitIndices;
    private readonly int _bitTriangleCount;
    private readonly BasicEffect _bitEffect;
    private readonly RasterizerState _bitRasterizer;

    public HullHitSparkRenderer(GraphicsDevice gd, NativeEffectTextures native, IAssetSource assets)
    {
        _native = native;
        _batcher = new ParticleBillboardBatcher(gd);

        // P545 — the real utl10 debris geometry (AWF_EFFECT_BIT, efart.h:18),
        // parsed from the artwork tree and built into shared buffers (the
        // ProbeRenderer pattern, P290). Falls back to a unit cube if missing.
        var mesh = LoadBitMesh(gd, assets);
        _bitVertices = mesh.Vertices;
        _bitIndices = mesh.Indices;
        _bitTriangleCount = mesh.TriangleCount;

        _bitEffect = new BasicEffect(gd)
        {
            AmbientLightColor = new Vector3(0.32f, 0.34f, 0.36f),
            TextureEnabled = false,
        };
        _bitRasterizer = new RasterizerState { CullMode = CullMode.CullCounterClockwiseFace };
    }

    /// <summary>
    /// Draw every live surface flash (fx11/f103, additive, growing) and every
    /// live debris bit (utl10, lit). <paramref name="cameraEye"/> is subtracted
    /// so vertex positions stay small (the P207 lesson).
    /// </summary>
    public void Draw(IReadOnlyList<HullHitSparkView> bits, IReadOnlyList<HullHitFlashView> flashes,
        Vector3 cameraEye, Matrix view, Matrix projection)
    {
        DrawFlashes(flashes, cameraEye, view, projection);
        DrawBits(bits, cameraEye, view, projection);
    }

    // ── Surface flash (native HullHitGeoImpl::Render, efgeo.cpp:711-748) ──
    private void DrawFlashes(IReadOnlyList<HullHitFlashView> flashes, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (flashes.Count == 0) return;

        var tangent = new List<ParticleBillboardBatcher.AxisQuad>();
        var normal = new List<ParticleBillboardBatcher.AxisQuad>();

        foreach (var f in flashes)
        {
            float scale = Math.Min(FlashMaxSize, FlashGrowthRate * f.AgeSec + 0.1f);
            if (scale <= 0.1f) continue;
            var centre = new XnaVec(f.Position.X, f.Position.Y, f.Position.Z) - cameraEye;
            var n = new XnaVec(f.Normal.X, f.Normal.Y, f.Normal.Z);
            var fw = new XnaVec(f.Forward.X, f.Forward.Y, f.Forward.Z);
            var rt = new XnaVec(f.Right.X, f.Right.Y, f.Right.Z);
            var white = new Color(1f, 1f, 1f, 1f);

            // Tangent decal (f103): a quad in the tangent plane at the hit,
            // spanned by forward·scale and right·scale (efgeo.cpp:733-739).
            tangent.Add(new ParticleBillboardBatcher.AxisQuad(centre, fw * scale, rt * scale, white));

            // Normal decal (fx11): a quad centred at pos + normal·scale, one
            // edge along the normal·scale, the other along an orthogonal·scale
            // (efgeo.cpp:741-747 — native passes right = 0, so it derives the
            // orthogonal).
            var orth = Orthogonal(n);
            normal.Add(new ParticleBillboardBatcher.AxisQuad(
                centre + n * scale, n * scale, orth * scale, white));
        }

        _batcher.DrawAxisQuads(tangent, _native.TextureFor(NativeEffectTextures.HullHitTangentStem) ?? _native.SoftFallback, view, projection);
        _batcher.DrawAxisQuads(normal, _native.TextureFor(NativeEffectTextures.HullHitNormalStem) ?? _native.SoftFallback, view, projection);
    }

    private static XnaVec Orthogonal(XnaVec v)
    {
        var up = Math.Abs(v.Y) < 0.985f ? XnaVec.Up : XnaVec.UnitX;
        return XnaVec.Normalize(XnaVec.Cross(up, v));
    }

    // ── Debris bits (native BitsGeo, efgeo.cpp:795-846; utl10 geometry) ─────
    private void DrawBits(IReadOnlyList<HullHitSparkView> bits, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (bits.Count == 0 || _bitTriangleCount == 0) return;

        foreach (var b in bits)
        {
            var pos = new XnaVec(b.Position.X, b.Position.Y, b.Position.Z) - cameraEye;
            float size = b.Size;
            // Native scales the bit geo to m_size and spins it about a random
            // axis (efgeo.cpp:813,841); the port scales uniformly and rolls by a
            // deterministic seed + a continuous spin over the 2-s life.
            float rem = MathHelper.Clamp(b.RemainingFrac, 0f, 1f);
            float spin = ParticleBillboardBatcher.RotationSeed(pos) + (1f - rem) * MathHelper.TwoPi;
            var world = Matrix.CreateScale(size)
                        * Matrix.CreateRotationY(spin)
                        * Matrix.CreateRotationX(spin * 0.618034f)
                        * Matrix.CreateTranslation(pos);

            _bitEffect.DiffuseColor = new Vector3(1f, 1f, 1f);
            _bitEffect.World = world;
            _bitEffect.View = view;
            _bitEffect.Projection = projection;
            _bitEffect.EnableDefaultLighting();
            _bitEffect.CurrentTechnique.Passes[0].Apply();

            var gd = _bitEffect.GraphicsDevice;
            gd.RasterizerState = _bitRasterizer;
            gd.SetVertexBuffer(_bitVertices);
            gd.Indices = _bitIndices;
            gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _bitTriangleCount);
        }
        // Restore the rasterizer so nothing leaks into later passes.
        _bitEffect.GraphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    private static (VertexBuffer Vertices, IndexBuffer Indices, int TriangleCount) LoadBitMesh(GraphicsDevice gd, IAssetSource assets)
    {
        IReadOnlyList<MdlMesh> meshes;
        try
        {
            meshes = MdlFile.Parse(AssetSources.ReadAllBytes(assets, "utl10.mdl")).Meshes;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] hull-hit bit mesh 'utl10.mdl' unavailable ({ex.Message}) — unit-cube fallback");
            return BuildCube(gd);
        }

        int vertexTotal = 0, indexTotal = 0;
        foreach (var m in meshes) { vertexTotal += m.VertexCount; indexTotal += m.Indices.Length; }
        if (vertexTotal == 0 || indexTotal == 0)
        {
            Console.WriteLine("[monogame] hull-hit bit mesh 'utl10.mdl' has no geometry — unit-cube fallback");
            return BuildCube(gd);
        }

        var verts = new VertexPositionNormalTexture[vertexTotal];
        var indices = new uint[indexTotal];
        int vCursor = 0, iCursor = 0;
        foreach (var m in meshes)
        {
            int vertBase = vCursor;
            for (int v = 0; v < m.VertexCount; v++)
                verts[vCursor++] = new VertexPositionNormalTexture(
                    new Vector3(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]),
                    new Vector3(m.Normals[3 * v], m.Normals[3 * v + 1], m.Normals[3 * v + 2]),
                    new Vector2(m.TexCoords[2 * v], m.TexCoords[2 * v + 1]));
            for (int i = 0; i < m.Indices.Length; i++)
                indices[iCursor++] = (uint)(m.Indices[i] + vertBase);
        }
        var vb = new VertexBuffer(gd, typeof(VertexPositionNormalTexture), verts.Length, BufferUsage.WriteOnly);
        vb.SetData(verts);
        var ib = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
        ib.SetData(indices);
        return (vb, ib, indices.Length / 3);
    }

    /// <summary>A small unit cube fallback when utl10 is missing (so bits never
    /// disappear entirely) — solid lit, size scaled per bit.</summary>
    private static (VertexBuffer, IndexBuffer, int) BuildCube(GraphicsDevice gd)
    {
        var verts = new[]
        {
            new VertexPositionNormalTexture(new Vector3(-1,-1,-1), new Vector3(0,0,-1), Vector2.Zero),
            new VertexPositionNormalTexture(new Vector3( 1,-1,-1), new Vector3(0,0,-1), Vector2.Zero),
            new VertexPositionNormalTexture(new Vector3( 1, 1,-1), new Vector3(0,0,-1), Vector2.Zero),
            new VertexPositionNormalTexture(new Vector3(-1, 1,-1), new Vector3(0,0,-1), Vector2.Zero),
        };
        uint[] idx = { 0,1,2, 0,2,3 };
        var vb = new VertexBuffer(gd, typeof(VertexPositionNormalTexture), verts.Length, BufferUsage.WriteOnly);
        vb.SetData(verts);
        var ib = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, idx.Length, BufferUsage.WriteOnly);
        ib.SetData(idx);
        return (vb, ib, idx.Length / 3);
    }

    public void Dispose()
    {
        _batcher.Dispose();
        _bitVertices.Dispose();
        _bitIndices.Dispose();
        _bitEffect.Dispose();
        _bitRasterizer.Dispose();
    }
}
