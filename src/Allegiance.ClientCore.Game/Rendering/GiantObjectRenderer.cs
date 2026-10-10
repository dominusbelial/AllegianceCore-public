using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P592 — the <b>GIANT sky object</b>: one face-on galaxy drawn as a single
/// curved patch of the sky sphere (~90° across), so our own Milky Way can
/// loom over Terra and Andromeda over Alderan — "a larger object that spans
/// across half the sky but still respects the black background."
///
/// Unlike the small P589 distant objects (flat camera-facing billboards), a
/// ~90° object is a <b>tessellated spherical cap</b> (<see cref="GiantObjectMath"/>)
/// whose vertices lie ON the sky sphere, so it curves seamlessly with the sky
/// as the camera pans — no edge, stretch, seam or banding (the P573/P576b/P582b
/// lessons: no full dome, no wrap, camera-relative at infinity, depth off,
/// never far-clipped, render states pinned, no mid-frame render-target
/// switches).
///
/// Which sectors get a giant — and its image, angle, direction, rotation and
/// brightness — come from <c>giant-objects.json</c> (loaded from the artwork
/// root). The json direction is the nominal FIXED/seeded placement;
/// <see cref="GiantObjectConfig.ResolveDirection"/> enforces the two physical
/// rules (clear of the sector's sun so the lens flare doesn't wash it out,
/// clear of the planet). Toggle: <c>--giant-objects off</c> (ON by default).
/// </summary>
public sealed class GiantObjectRenderer : IDisposable
{
    private const string Dir = "giantobj";
    private const string ConfigFile = "giant-objects.json";

    /// <summary>How fine the patch mesh is: cells per side. 40 → 41×41 = 1681
    /// vertices, 3200 triangles — plenty smooth for a 90° cap at 1440p.</summary>
    private const int Segments = 40;

    private readonly GraphicsDevice _gd;
    private readonly IAssetSource _assets;
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

    private GiantObjectConfig _config = GiantObjectConfig.Empty();
    private bool _configLoaded;

    // The active (resolved) instance for the current sector.
    private Texture2D? _activeTex;
    private Vector3 _activeDir;
    private float _activeAngleDeg = GiantObjectConfig.DefaultSpanDeg;
    private float _activeRotationRad;
    private float _activeBrightness = 0.85f;
    private VertexBuffer? _vb;
    private int _vertCount;

    public GiantObjectRenderer(GraphicsDevice gd, IAssetSource assets)
    {
        _gd = gd;
        _assets = assets;
    }

    /// <summary>Switch the giant to a cluster's data (called on sector change):
    /// resolve the configured direction (clear of sun/planet), load the texture
    /// and rebuild the patch mesh.</summary>
    public void SetCluster(DataClusterIGC cluster)
    {
        _config = EnsureConfig();
        _activeTex = null;
        _vb = null;

        if (!_config.ByCluster.TryGetValue(cluster.ClusterId, out var spec))
            return; // this sector has no giant

        var toward = TowardLight(cluster);
        NativeSkyMath.Vector3Unit? towardUnit = toward is { } tv
            ? new NativeSkyMath.Vector3Unit(tv.X, tv.Y, tv.Z) : null;
        var p = NativeSkyMath.PlanetDirection(cluster.PlanetSinLatitude, cluster.PlanetLongitude);
        NativeSkyMath.Vector3Unit? planetDir =
            (p.X * p.X + p.Y * p.Y + p.Z * p.Z) < 1e-6f ? null : p;

        var dir = GiantObjectConfig.ResolveDirection(spec, towardUnit, planetDir);
        _activeDir = new Vector3(dir.X, dir.Y, dir.Z);
        _activeAngleDeg = spec.AngleDeg;
        _activeRotationRad = MathHelperDeg2Rad(spec.RotationDeg);
        _activeBrightness = spec.Brightness;
        _activeTex = LoadPremultipliedPng($"{Dir}/{spec.Image}");

        if (_activeTex is not null)
            BuildMesh();
    }

    /// <summary>Draw the active giant on the sky shell (premultiplied, on a
    /// curved sphere patch). Render states are pinned by the caller
    /// (BlackSkyRenderer); this only sets premultiplied alpha + Clamp so the
    /// feathered edge blends without fringes and the patch never wraps.</summary>
    public void Draw(Matrix view, Matrix projection, float shellDist)
    {
        if (_activeTex is null || _vb is null) return;

        var prevBlend = _gd.BlendState;
        var prevSampler = _gd.SamplerStates[0];
        _gd.BlendState = BlendState.AlphaBlend;      // premultiplied texture
        _gd.SamplerStates[0] = SamplerState.LinearClamp; // no wrap at the patch edge

        byte b = (byte)Math.Clamp(_activeBrightness * 255f, 0f, 255f);
        using var effect = new BasicEffect(_gd)
        {
            TextureEnabled = true,
            Texture = _activeTex,
            VertexColorEnabled = true,
            DiffuseColor = new Vector3(b / 255f),
            LightingEnabled = false,
            View = view,
            Projection = projection,
            World = Matrix.Identity,
        };
        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _gd.SetVertexBuffer(_vb);
            _gd.DrawPrimitives(PrimitiveType.TriangleList, 0, _vertCount / 3);
        }

        _gd.BlendState = prevBlend;
        _gd.SamplerStates[0] = prevSampler;
    }

    /// <summary>Build the XNA patch mesh. Positions are placed at a large fixed
    /// radius on the sky sphere so the patch sits camera-relative at infinity
    /// and never far-clips under the sky's own projection (far = 80000,
    /// <see cref="NativeSkyPlacement.SkyProjection"/>).</summary>
    private void BuildMesh()
    {
        const float radius = 6600f;
        float halfAng = MathHelperDeg2Rad(_activeAngleDeg * 0.5f);
        var center = new NativeSkyMath.Vector3Unit(_activeDir.X, _activeDir.Y, _activeDir.Z);
        var nodes = GiantObjectMath.BuildPatch(center, halfAng, Segments, _activeRotationRad);

        int n = Segments + 1;
        var verts = new VertexPositionColorTexture[n * n];
        for (int i = 0; i < verts.Length; i++)
        {
            var nd = nodes[i].Dir;
            verts[i] = new VertexPositionColorTexture(
                new Vector3(nd.X, nd.Y, nd.Z) * radius, Color.White,
                new Vector2(nodes[i].U, nodes[i].V));
        }

        // Triangles in index order, then expanded to a plain triangle list so a
        // single non-indexed DrawPrimitives(TriangleList) call draws the cap.
        int cells = Segments * Segments;
        int[] idx = new int[cells * 6];
        int w = 0;
        for (int i = 0; i < Segments; i++)
        {
            for (int j = 0; j < Segments; j++)
            {
                int tl = i * n + j;
                int tr = tl + 1;
                int bl = tl + n;
                int br = bl + 1;
                idx[w++] = tl; idx[w++] = bl; idx[w++] = tr;
                idx[w++] = tr; idx[w++] = bl; idx[w++] = br;
            }
        }
        var triList = new VertexPositionColorTexture[idx.Length];
        for (int t = 0; t < idx.Length; t++) triList[t] = verts[idx[t]];

        _vertCount = triList.Length;
        _vb?.Dispose();
        _vb = new VertexBuffer(_gd, VertexPositionColorTexture.VertexDeclaration, _vertCount, BufferUsage.WriteOnly);
        _vb.SetData(triList);
    }

    private static float MathHelperDeg2Rad(float deg) => deg * MathF.PI / 180f;

    private GiantObjectConfig EnsureConfig()
    {
        if (_configLoaded) return _config;
        _configLoaded = true;
        try
        {
            using var s = _assets.OpenRead(ConfigFile);
            using var reader = new StreamReader(s);
            _config = GiantObjectConfig.Parse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] giant-objects.json unavailable: {ex.Message} — no giant objects");
        }
        return _config;
    }

    private Texture2D? LoadPremultipliedPng(string rel)
    {
        if (_textures.TryGetValue(rel, out var t)) return t;
        if (_missing.Contains(rel)) return null;
        try
        {
            using var s = _assets.OpenRead(rel);
            var tex = Texture2D.FromStream(_gd, s);
            var data = new Color[tex.Width * tex.Height];
            tex.GetData(data);
            for (int i = 0; i < data.Length; i++)
            {
                float a = data[i].A / 255f;
                data[i] = new Color(
                    (byte)(data[i].R * a),
                    (byte)(data[i].G * a),
                    (byte)(data[i].B * a),
                    data[i].A);
            }
            tex.SetData(data);
            _textures[rel] = tex;
            return tex;
        }
        catch (Exception ex)
        {
            _missing.Add(rel);
            Console.WriteLine($"[monogame] giant-object sprite '{rel}' failed: {ex.Message}");
            return null;
        }
    }

    private static Vector3? TowardLight(DataClusterIGC cluster)
    {
        var ld = cluster.LightDirection;
        if (ld is { Length: 3 })
        {
            float len = MathF.Sqrt(ld[0] * ld[0] + ld[1] * ld[1] + ld[2] * ld[2]);
            if (len >= 1e-6f) return new Vector3(ld[0] / len, ld[1] / len, ld[2] / len);
        }
        return null;
    }

    public void Dispose()
    {
        _vb?.Dispose();
        foreach (var t in _textures.Values) t.Dispose();
        _textures.Clear();
    }
}
