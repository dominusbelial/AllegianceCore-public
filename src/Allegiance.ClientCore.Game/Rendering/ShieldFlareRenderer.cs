using System;
using System.Collections.Generic;
using System.IO;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P569 — draws the shield-hit flare the native way: the REAL curved
/// <b>lens30</b> spherical-cap geometry (from <c>lens30.x</c>, the DirectX
/// export of native's <c>GetNameSpace(AWF_SHIELD_FLARE)-&gt;FindMember("lens30")</c>,
/// trekigc.cpp:2517-2523) wrapped onto the struck ship's shield ellipsoid at
/// the hit, textured with the animated <b>fx09bmp</b> lens, additively blended
/// over the native 0.75&#8209;s life. Replaces P545's flat tangent quad (which
/// Domingo flagged as "actually flat" side-on).
/// </summary>
/// <remarks>
/// <para><b>Native (WinTrek/trekigc.cpp:1624-1645; effect/thinggeo.cpp:1243-1305).</b>
/// A shield-absorbed hit calls <c>AddFlare</c> (id 0 = lens30), which wraps the
/// cap <c>m_pgeoFlares[id]</c> in a <c>TextureGeo</c> + <c>AnimatedImage</c>
/// (<c>m_pimageFlare</c> = the animated <c>fx09bmp</c>), orients and scales it
/// onto the shield ellipsoid via <c>ThingGeo::AddFlare</c>'s <c>orient.Set +
/// orient.Scale(ellipse/m_radius) + Matrix(orient, 0, m_radiusMesh)</c>
/// (thinggeo.cpp:1264-1289 — see <see cref="ShieldFlareCapMath"/>), wraps it in a
/// <c>TimedGeo</c> over <b>0.75&#8209;s</b> (:1290-1301) and blends it
/// <c>BlendModeAdd</c> (:1297). The pool
/// (<see cref="ShieldFlareParticles"/>) caps admission at
/// <c>GetFlareCount() &lt; 4</c> (trekigc.cpp:1626).</para>
/// <para><b>Render-state hygiene (P568 lesson).</b> This pass pins its own
/// sampler (<c>LinearClamp</c> — the cap UVs live in [0,1]), blend
/// (<c>BlendStateAdd</c> SRCALPHA/ONE), depth (test, no write) and rasterizer
/// (cull-none — the cap shell is thin and double-sided) and restores the
/// device's prior states afterwards, so nothing leaks into the passes that
/// follow (P339 B2).</para>
/// </remarks>
public sealed class ShieldFlareRenderer : IDisposable
{
    /// <summary>The native cap id native's shield path uses (lens30); the other
    /// caps (lens60/lens90) exist for the id selector but are not used by
    /// <c>shieldIGC.cpp:171</c> (always id 0).</summary>
    private const int ShieldCapId = 0;

    private const string CapArt = "lens30";
    private const string CapExtension = ".x";

    private readonly GraphicsDevice _gd;
    private readonly NativeEffectTextures _native;
    private readonly IAssetSource _assets;
    private readonly BasicEffect _effect;
    private readonly BlendState _additive;
    private readonly DepthStencilState _depthRead;
    private readonly RasterizerState _cullNone;

    // The unit cap geometry (MdlMesh) parsed from lens30.x.
    private readonly float[]? _capPositions;
    private readonly float[]? _capUvs;
    private readonly ushort[]? _capIndices;
    private int _capVertexCount;
    private int _capTriangleCount;

    // Reusable dynamic geometry arrays (worst case: 64 pooled flares × 37 verts).
    private VertexPositionColorTexture[] _verts = Array.Empty<VertexPositionColorTexture>();
    private short[] _indices = Array.Empty<short>();

    public ShieldFlareRenderer(GraphicsDevice gd, NativeEffectTextures native, IAssetSource assets)
    {
        _gd = gd;
        _native = native;
        _assets = assets;
        _effect = new BasicEffect(gd) { TextureEnabled = true, VertexColorEnabled = true };
        _additive = new BlendState
        {
            ColorSourceBlend = Blend.SourceAlpha,
            AlphaSourceBlend = Blend.SourceAlpha,
            ColorDestinationBlend = Blend.One,
            AlphaDestinationBlend = Blend.One,
            ColorBlendFunction = BlendFunction.Add,
            AlphaBlendFunction = BlendFunction.Add,
        };
        _depthRead = new DepthStencilState { DepthBufferEnable = false, DepthBufferWriteEnable = false };
        _cullNone = new RasterizerState { CullMode = CullMode.None };

        (_capPositions, _capUvs, _capIndices, _capVertexCount, _capTriangleCount) = LoadCap();
    }

    /// <summary>Parse the native <c>lens30.x</c> cap into the unit spherical-cap
    /// geometry. Returns empty arrays (counts 0) when the artwork is missing —
    /// the caller degrades to the flat-billboard fallback, never invisible.</summary>
    private (float[]? pos, float[]? uv, ushort[]? idx, int verts, int tris) LoadCap()
    {
        try
        {
            if (!_assets.Exists(CapArt + CapExtension)) return (null, null, null, 0, 0);
            var text = AssetSources.ReadAllText(_assets, CapArt + CapExtension);
            var mesh = DirectXMeshFile.Parse(text);
            if (mesh.VertexCount == 0 || mesh.TriangleCount == 0) return (null, null, null, 0, 0);
            return (mesh.Positions, mesh.TexCoords, mesh.Indices, mesh.VertexCount, mesh.TriangleCount);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] shield-flare cap '{CapArt}{CapExtension}' failed to parse ({ex.Message}) — flat-billboard fallback");
            return (null, null, null, 0, 0);
        }
    }

    /// <summary>
    /// Draw every live flare as the native curved <b>lens30</b> cap wrapped onto
    /// the struck ship's shield ellipsoid (<see cref="ShieldFlareCapMath"/>),
    /// animated through <c>fx09bmp</c>'s frames over the 0.75&#8209;s life with a
    /// white tint fading by remaining life (the native <c>TimedGeo</c> fade-off).
    /// Vertices are baked camera-relative (world − eye, the P207
    /// small-coordinates convention) and drawn with the frame's view/projection.
    /// </summary>
    public void Draw(IReadOnlyList<ShieldFlareView> flares, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (flares.Count == 0) return;

        if (System.Environment.GetEnvironmentVariable("ALLEGIANCE_DEBUG_FLARE") == "1")
        {
            var f0 = flares[0];
            Console.WriteLine($"[flare] draw {flares.Count} flare(s); first center=({f0.Center.X:F1},{f0.Center.Y:F1},{f0.Center.Z:F1}) "
                + $"ellipse=({f0.Ellipse.X:F1},{f0.Ellipse.Y:F1},{f0.Ellipse.Z:F1}) r={f0.Radius:F1} mr={f0.MeshRadius:F1} rem={f0.RemainingFrac:F2} "
                + $"eyes=({cameraEye.X:F1},{cameraEye.Y:F1},{cameraEye.Z:F1})");
            var ndc = Vector3.Transform(new Vector3(f0.Center.X - cameraEye.X, f0.Center.Y - cameraEye.Y, f0.Center.Z - cameraEye.Z), view * projection);
            Console.WriteLine($"[flare]   shipCenter NDC=({ndc.X:F2},{ndc.Y:F2},{ndc.Z:F2})");
            var apex = ShieldFlareCapMath.TransformCapPosition(
                new SimVec(0, 0, 1), f0.ShipOrientation,
                new SimVec(f0.Center.X, f0.Center.Y, f0.Center.Z),
                new SimVec(f0.HitWorldPos.X, f0.HitWorldPos.Y, f0.HitWorldPos.Z),
                new SimVec(f0.Ellipse.X, f0.Ellipse.Y, f0.Ellipse.Z), f0.Radius, f0.MeshRadius);
            var ndcApex = Vector3.Transform(new Vector3(apex.X - cameraEye.X, apex.Y - cameraEye.Y, apex.Z - cameraEye.Z), view * projection);
            Console.WriteLine($"[flare]   apex NDC=({ndcApex.X:F2},{ndcApex.Y:F2},{ndcApex.Z:F2})");
        }

        var frames = _native.FramesFor(NativeEffectTextures.ShieldFlareStem);
        if (System.Environment.GetEnvironmentVariable("ALLEGIANCE_DEBUG_FLARE") == "1" && frames is { Length: > 0 })
            Console.WriteLine($"[flare]   fx09 frames={frames.Length} frame0=({frames[0].Width}x{frames[0].Height})");
        if (frames is not { Length: > 0 })
            frames = new[] { _native.SoftFallback };

        // Fallback: cap art unavailable → draw the old flat tangent patch so the
        // flare never disappears (graceful degradation, logged once in LoadCap).
        if (_capVertexCount == 0)
        {
            DrawFlatFallback(flares, frames, cameraEye, view, projection);
            return;
        }

        var eye = new Vector3(cameraEye.X, cameraEye.Y, cameraEye.Z);

        // Group by animation frame so each frame's caps draw in one batch
        // (native AnimatedImage advances the lens over the 0.75-s life).
        var byFrame = new Dictionary<int, List<(ShieldFlareView f, Color col)>>();
        foreach (var f in flares)
        {
            float rem = MathHelper.Clamp(f.RemainingFrac, 0f, 1f);
            int frame = Math.Min(frames.Length - 1, (int)((1f - rem) * frames.Length));
            // White tint fading by remaining life (TimedGeo fade-off); the
            // texture carries the ice-blue-white lens colour.
            var c = new Color(1f, 1f, 1f, rem);
            if (!byFrame.TryGetValue(frame, out var list))
                byFrame[frame] = list = new List<(ShieldFlareView, Color)>();
            list.Add((f, c));
        }

        foreach (var (frame, items) in byFrame)
            DrawCapBatch(items, frames[frame], eye, view, projection);
    }

    private void DrawCapBatch(List<(ShieldFlareView f, Color col)> items, Texture2D tex,
        Vector3 eye, Matrix view, Matrix projection)
    {
        int stride = _capVertexCount;
        int totalVerts = items.Count * stride;
        if (_verts.Length < totalVerts)
        {
            _verts = new VertexPositionColorTexture[totalVerts];
            _indices = new short[items.Count * _capIndices!.Length];
        }

        var capPos = _capPositions!;
        var capUv = _capUvs!;

        int vi = 0;
        for (int k = 0; k < items.Count; k++)
        {
            var (f, c) = items[k];
            var center = new SimVec(f.Center.X, f.Center.Y, f.Center.Z);
            var hit = new SimVec(f.HitWorldPos.X, f.HitWorldPos.Y, f.HitWorldPos.Z);
            var ellipse = new SimVec(f.Ellipse.X, f.Ellipse.Y, f.Ellipse.Z);
            for (int v = 0; v < _capVertexCount; v++, vi++)
            {
                var capLocal = new SimVec(capPos[3 * v], capPos[3 * v + 1], capPos[3 * v + 2]);
                var w = ShieldFlareCapMath.TransformCapPosition(
                    capLocal, f.ShipOrientation, center, hit, ellipse, f.Radius, f.MeshRadius);
                _verts[vi] = new VertexPositionColorTexture(
                    new Vector3(w.X - eye.X, w.Y - eye.Y, w.Z - eye.Z),
                    c,
                    new Vector2(capUv[2 * v], capUv[2 * v + 1]));
            }
        }

        var capIdx = _capIndices!;
        int triCount = _capTriangleCount * items.Count;
        for (int k = 0; k < items.Count; k++)
        {
            int baseV = k * stride;
            int baseI = k * capIdx.Length;
            for (int i = 0; i < capIdx.Length; i++)
                _indices[baseI + i] = (short)(capIdx[i] + baseV);
        }

        _effect.Texture = tex;
        _effect.World = Matrix.Identity;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.CurrentTechnique.Passes[0].Apply();

        var gd = _gd;
        var prevBlend = gd.BlendState;
        var prevDepth = gd.DepthStencilState;
        var prevRaster = gd.RasterizerState;
        var prevSample = gd.SamplerStates[0];

        gd.BlendState = _additive;
        gd.DepthStencilState = _depthRead;
        gd.RasterizerState = _cullNone;
        gd.SamplerStates[0] = SamplerState.LinearClamp;
        gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, _verts, 0, totalVerts, _indices, 0, triCount);

        // Restore (P568 / P339 B2 — don't leak render state).
        gd.BlendState = prevBlend;
        gd.DepthStencilState = prevDepth;
        gd.RasterizerState = prevRaster;
        gd.SamplerStates[0] = prevSample;
    }

    /// <summary>Graceful degradation when the native cap art is unavailable:
    /// draw the P545-style flat tangent patch (never an invisible flare).</summary>
    private void DrawFlatFallback(IReadOnlyList<ShieldFlareView> flares, Texture2D[] frames,
        Vector3 cameraEye, Matrix view, Matrix projection)
    {
        var batcher = new ParticleBillboardBatcher(_gd);
        try
        {
            var byFrame = new Dictionary<int, List<ParticlePatch>>();
            foreach (var f in flares)
            {
                float rem = MathHelper.Clamp(f.RemainingFrac, 0f, 1f);
                int frame = Math.Min(frames.Length - 1, (int)((1f - rem) * frames.Length));
                var centre = new Vector3(f.Center.X, f.Center.Y, f.Center.Z) - cameraEye;
                var normal = new Vector3(f.Direction.X, f.Direction.Y, f.Direction.Z);
                var col = new Color(1f, 1f, 1f, rem);
                if (!byFrame.TryGetValue(frame, out var list))
                    byFrame[frame] = list = new List<ParticlePatch>();
                list.Add(new ParticlePatch(centre, normal, f.MeshRadius * 0.4f, col, ParticleBillboardBatcher.RotationSeed(centre)));
            }
            foreach (var (frame, patches) in byFrame)
                batcher.DrawPatches(patches, frames[frame], view, projection);
        }
        finally
        {
            batcher.Dispose();
        }
    }

    public void Dispose()
    {
        _effect.Dispose();
        _additive.Dispose();
        _depthRead.Dispose();
        _cullNone.Dispose();
    }
}
