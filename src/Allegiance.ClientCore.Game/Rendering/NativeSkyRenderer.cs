using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// The native per-sector sky (P576-A): the <b>seeded star field</b>, the
/// <b>environment dome</b> (globe sky-dome mesh + nebula texture) and the
/// <b>planet poster</b>, each per-cluster, drawn camera-centred at infinity —
/// depth-write off, behind everything, exactly as native composites them on a
/// cluster change (<c>WinTrek/WinTrek.cpp:3622-3640</c> →
/// <c>SetCluster</c>: environment <c>SetGeo</c>, stars <c>SetCount(seed,nStars)</c>,
/// posters via the cluster site).
///
/// Source rules ported (cited in docs/reports/P576_SECTOR_SKY_AND_ALEPH_GLIMPSE.md):
/// <list type="bullet">
/// <item>Stars: <c>StarImage::SetCount</c> (<c>effect/starimage.cpp:32-53</c>) +
/// <c>Vector::RandomDirection</c> (<c>zlib/vector.cpp:37-46</c>) + MSVC
/// <c>rand()</c> (so the field is byte-identical to native), drawn at infinity.</item>
/// <item>Environment dome: <c>clusterIGC.cpp:41-42</c> picks
/// <c>SetEnvironmentGeo(posterName)</c> = <c>globeN</c> sky dome; the dome is a
/// full sphere (radius 7500 for globe1-9, radius 5 for globe10) textured with
/// <c>environmentNbmp</c>, rendered <c>ShadeModeCopy</c> (<c>efgeo.cpp:1029-1052</c>)
/// i.e. unlit; the near-black surround is colour-keyed transparent so the
/// nebula's black reveals the stars beneath (colour-key, modeler.cpp:726 /
/// device3d colorkey path).</item>
/// <item>Planet: <c>clusterIGC.cpp:44-57</c> direction + radius via
/// <c>AddPoster</c> → <c>PosterImage::AddPoster</c> (<c>trekigc.cpp:243-248</c>,
/// <c>efimage.cpp:515-563</c>): a billboarded square at the planet's direction,
/// sized by <c>planetRadius</c>, textured with the planet texture.</item>
/// </list>
/// </summary>
public sealed class NativeSkyRenderer : IDisposable
{
    /// <summary>World radius the star points sit on — just inside the globe
    /// dome's 7500 shell so stars read behind the nebula (native draws stars
    /// before the environment).</summary>
    private const float StarRadius = 7000f;

    private readonly GraphicsDevice _gd;
    private readonly IAssetSource _assets;
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkyDome> _domes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

    private NativeStar[] _stars = [];
    private uint _starSeed;
    private short _nStars = -1;

    private SkyDome? _activeDome;
    private string? _activePoster;
    private Texture2D? _activeEnvTexture;

    private Texture2D? _planetTexture;
    private string? _activePlanet;
    private Vector3 _planetDir;
    private float _planetRadius;
    private bool _hasPlanet;

    public NativeSkyRenderer(GraphicsDevice gd, IAssetSource assets)
    {
        _gd = gd;
        _assets = assets;
    }

    /// <summary>
    /// A decoded globe sky dome: mesh (positions + texcoords + indices) rebuilt
    /// as a <c>VertexPositionColorTexture</c> user-primitive array (the same
    /// vertex/draw path the planet uses and that provably renders).
    /// </summary>
    private sealed class SkyDome
    {
        public required VertexPositionColorTexture[] Vertices;
        public required short[] Indices;
        public required int VertexCount;
        public required int PrimitiveCount;
        /// <summary>The native dome radius (globe1-9: 7500; globe10: 5).</summary>
        public required float Radius;
    }

    /// <summary>
    /// Switch the sky to a new cluster's data (called on sector change / warp
    /// transit). Asset decode is cached per poster/planet name; the star field
    /// is regenerated only when the (seed, nStars) pair changes.
    /// </summary>
    public void SetCluster(DataClusterIGC cluster)
    {
        if (cluster.StarSeed != _starSeed || cluster.Stars != _nStars)
        {
            _stars = NativeSkyMath.GenerateStars(cluster.StarSeed, cluster.Stars);
            _starSeed = cluster.StarSeed;
            _nStars = cluster.Stars;
        }

        // Environment dome: posterName -> globeN.mdl dome + environmentNbmp.mdl.
        var poster = cluster.PosterName;
        if (!string.IsNullOrEmpty(poster) && !string.Equals(poster, _activePoster, StringComparison.OrdinalIgnoreCase))
        {
            _activeDome = DomeFor(poster);
            _activeEnvTexture = EnvTextureFor(poster);
            _activePoster = poster;
        }

        // Planet poster.
        if (!string.IsNullOrEmpty(cluster.PlanetName)
            && !string.Equals(cluster.PlanetName, _activePlanet, StringComparison.OrdinalIgnoreCase))
        {
            _planetTexture = PlanetTextureFor(cluster.PlanetName);
            _planetDir = ToXna(NativeSkyMath.PlanetDirection(cluster.PlanetSinLatitude, cluster.PlanetLongitude));
            _planetRadius = cluster.PlanetRadius;
            _hasPlanet = _planetTexture is not null;
            _activePlanet = cluster.PlanetName;
        }
        else if (string.IsNullOrEmpty(cluster.PlanetName))
        {
            _hasPlanet = false;
        }
    }

    private static Vector3 ToXna(NativeSkyMath.Vector3Unit v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// Draw the native sky behind everything. Call once per frame with the
    /// flight camera, before any opaque scene geometry. Render states are pinned
    /// (P568 lesson): depth off, additive for stars, alpha-blend (premultiplied)
    /// for the colorkeyed dome and planet.
    /// </summary>
    public void Draw(Matrix view, Matrix projection, Vector3 cameraEye)
    {
        if (_stars.Length == 0 && _activeDome is null && !_hasPlanet)
        {
            // Nothing to draw (no cluster yet) — leave the caller's clear colour.
            return;
        }

        // The sky is a camera-centred backdrop at INFINITY, so it needs its OWN
        // projection: the scene camera's far plane (6000, ExternalCameras.cs:45)
        // is nearer than the native dome's 7500 shell, which would clip the sky.
        // FOV/aspect are recovered from the incoming projection so the sky still
        // aligns with the scene (angular appearance is unchanged by the larger
        // far). near=1 keeps the dome/stars inside [1, far].
        float focus = projection.M22;                      // cot(vFOV/2)
        float vFov = 2f * MathF.Atan(1f / focus);
        float aspect = projection.M22 / projection.M11;
        var skyProj = Matrix.CreatePerspectiveFieldOfView(vFov, aspect, 1f, 80000f);

        var gd = _gd;
        var prevDepth = gd.DepthStencilState;
        var prevBlend = gd.BlendState;
        var prevRaster = gd.RasterizerState;
        var prevSampler = gd.SamplerStates[0];

        try
        {
            gd.DepthStencilState = DepthStencilState.None;   // backdrop: no depth at all
            // CullNone: the globe dome is a full sphere seen from INSIDE, so its
            // winding (CW outward-facing) would otherwise be back-face culled and
            // the nebula would vanish (cf. P570's native inside-out winding note).
            gd.RasterizerState = RasterizerState.CullNone;

            // 1) Stars — additive points on a shell at StarRadius (native draws
            //    them at infinity; we place them just inside the dome). Drawn
            //    first so the dome's colorkeyed black reveals them.
            if (_stars.Length > 0)
                DrawStars(view, skyProj);

            // 2) Environment dome — unlit, colorkeyed-nebula texture (copy mode).
            if (_activeDome is not null && _activeEnvTexture is not null)
                DrawDome(view, skyProj);

            // 3) Planet poster — billboarded square at the planet direction.
            if (_hasPlanet && _planetTexture is not null)
                DrawPlanet(view, skyProj);
        }
        finally
        {
            gd.DepthStencilState = prevDepth;
            gd.BlendState = prevBlend;
            gd.RasterizerState = prevRaster;
            gd.SamplerStates[0] = prevSampler;
        }
    }

    private void DrawStars(Matrix view, Matrix projection)
    {
        var gd = _gd;
        gd.BlendState = BlendState.Additive;

        var scratch = new VertexPositionColor[_stars.Length];
        for (int i = 0; i < _stars.Length; i++)
        {
            var s = _stars[i];
            // Camera-relative: the flight view is CreateLookAt(Zero, …, …), i.e.
            // the camera sits at the origin — so stars go on a shell around the
            // origin, not around cameraEye (ChaseCamera.cs:205).
            var pos = new Vector3(s.DirX, s.DirY, s.DirZ) * StarRadius;
            byte b = (byte)(Math.Clamp(s.Brightness, 0f, 1f) * 255f);
            scratch[i] = new VertexPositionColor(pos, new Color(b, b, b));
        }

        using var effect = new BasicEffect(gd)
        {
            VertexColorEnabled = true,
            View = view,
            Projection = projection,
            World = Matrix.Identity,
            LightingEnabled = false,
        };
        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserPrimitives(PrimitiveType.PointList, scratch, 0, scratch.Length);
        }
    }

    private void DrawDome(Matrix view, Matrix projection)
    {
        var dome = _activeDome!;
        var gd = _gd;
        gd.BlendState = BlendState.AlphaBlend;   // premultiplied colorkeyed texture

        using var effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            Texture = _activeEnvTexture,
            VertexColorEnabled = true,           // white vertices (colorkeyed premultiplied tex)
            LightingEnabled = false,             // ShadeModeCopy: unlit
            View = view,
            Projection = projection,
            // Camera-relative view: the dome is centred ON the camera (origin).
            World = Matrix.Identity,
        };
        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, dome.Vertices, 0, dome.VertexCount, dome.Indices, 0, dome.PrimitiveCount);
        }
    }

    private void DrawPlanet(Matrix view, Matrix projection)
    {
        // Native places the poster at the planet's direction, billboarded to
        // face the camera (efimage.cpp:515-563), sized by planetRadius in the
        // 3D layer. We put a billboarded quad at direction*domeRadius with a
        // world half-size chosen to reproduce a clearly-readable planet.
        float dist = StarRadius;                              // sits on the sky shell
        var center = _planetDir * dist;                       // camera-relative
        float half = PlanetAngularScale(_planetRadius) * dist;

        var right = Vector3.Normalize(Vector3.Cross(Vector3.Up, _planetDir));
        var up = Vector3.Cross(_planetDir, right);

        var corners = new[]
        {
            new VertexPositionTexture(center - right * half - up * half, new Vector2(0, 1)),
            new VertexPositionTexture(center + right * half - up * half, new Vector2(1, 1)),
            new VertexPositionTexture(center - right * half + up * half, new Vector2(0, 0)),
            new VertexPositionTexture(center + right * half + up * half, new Vector2(1, 0)),
        };
        var indices = new short[] { 0, 1, 2, 2, 1, 3 };

        var gd = _gd;
        gd.BlendState = BlendState.AlphaBlend;

        using var effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            Texture = _planetTexture,
            LightingEnabled = false,
            View = view,
            Projection = projection,
            World = Matrix.Identity,
        };
        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserIndexedPrimitives(
                PrimitiveType.TriangleList, corners, 0, corners.Length, indices, 0, 2);
        }
    }

    /// <summary>
    /// World half-extent of the planet billboard relative to its distance, so
    /// its angular size scales with the native <c>planetRadius</c> (byte 50-100).
    /// radius 50 → ~2.5° half-angle, radius 100 → ~5° — a clearly-readable disc,
    /// not screen-filling (the 0.004×d earlier blew it up to a third of the
    /// screen and dwarfed the whole sky).
    /// </summary>
    private static float PlanetAngularScale(float radius)
        => MathF.Tan(MathHelper.ToRadians(radius / 100f * 5f));

    private SkyDome? DomeFor(string poster)
    {
        if (!TryPosterIndex(poster, out int n)) return null;
        if (_domes.TryGetValue($"globe{n}", out var d)) return d;
        string stem = $"globe{n}";
        if (_missing.Contains(stem)) return null;
        try
        {
            if (!_assets.Exists(stem + ".mdl")) { Missing(stem); return null; }
            var mdl = MdlFile.Parse(AssetSources.ReadAllBytes(_assets, stem + ".mdl"));
            if (mdl.Meshes.Count == 0) { Missing(stem); return null; }
            var mesh = mdl.Meshes[0];
            var verts = new VertexPositionColorTexture[mesh.VertexCount];
            var white = new Color(255, 255, 255, 255);
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                verts[i] = new VertexPositionColorTexture(
                    new Vector3(mesh.Positions[3 * i], mesh.Positions[3 * i + 1], mesh.Positions[3 * i + 2]),
                    white,
                    new Vector2(mesh.TexCoords[2 * i], mesh.TexCoords[2 * i + 1]));
            }
            var shorts = new short[mesh.Indices.Length];
            for (int i = 0; i < mesh.Indices.Length; i++) shorts[i] = (short)mesh.Indices[i];

            float radius = 0f;
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                float l = mesh.Positions[3 * i] * mesh.Positions[3 * i]
                        + mesh.Positions[3 * i + 1] * mesh.Positions[3 * i + 1]
                        + mesh.Positions[3 * i + 2] * mesh.Positions[3 * i + 2];
                if (l > radius) radius = l;
            }
            radius = MathF.Sqrt(radius);

            var dome = new SkyDome
            {
                Vertices = verts,
                Indices = shorts,
                VertexCount = verts.Length,
                PrimitiveCount = mesh.Indices.Length / 3,
                Radius = radius,
            };
            _domes[stem] = dome;
            return dome;
        }
        catch (Exception ex) { Missing(stem, ex.Message); return null; }
    }

    private Texture2D? EnvTextureFor(string poster)
    {
        if (!TryPosterIndex(poster, out int n)) return null;
        return TextureFor($"environment{n}bmp");
    }

    private Texture2D? PlanetTextureFor(string planetName)
    {
        // planetName is e.g. "nebplnt06bmp"; append ".mdl".
        return TextureFor(planetName);
    }

    private Texture2D? TextureFor(string stem)
    {
        if (_textures.TryGetValue(stem, out var t)) return t;
        if (_missing.Contains(stem)) return null;
        try
        {
            if (!_assets.Exists(stem + ".mdl"))
            {
                // nebplnt51-63 are ASCII "use ...ImportImageFromFile(.png)" stubs —
                // their real texture is a sibling PNG. Handle that as a fallback.
                if (stem.StartsWith("nebplnt", StringComparison.OrdinalIgnoreCase)
                    && _assets.Exists(stem.Replace("bmp", "") + ".png"))
                    return LoadPng(stem.Replace("bmp", ""));

                Missing(stem);
                return null;
            }
            var mdlTex = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(_assets, stem + ".mdl"));
            // Colour-key the near-black surround (the nebula/planet art sits on a
            // black backing; native keys it transparent via the surface colorkey).
            var rgba = MdlTextureDecoder.ToPremultipliedRgba(mdlTex, 10);
            var surf = new Texture2D(_gd, mdlTex.Width, mdlTex.Height, false, SurfaceFormat.Color);
            var cols = new Color[mdlTex.Width * mdlTex.Height];
            int n = 0;
            for (int i = 0; i < cols.Length; i++, n += 4)
                cols[i] = new Color(rgba[n], rgba[n + 1], rgba[n + 2], rgba[n + 3]);
            surf.SetData(cols);
            _textures[stem] = surf;
            return surf;
        }
        catch (Exception ex) { Missing(stem, ex.Message); return null; }
    }

    private Texture2D? LoadPng(string pngStem)
    {
        if (_textures.TryGetValue(pngStem, out var t)) return t;
        try
        {
            using var s = _assets.OpenRead(pngStem + ".png");
            var tex = Texture2D.FromStream(_gd, s);
            _textures[pngStem] = tex;
            return tex;
        }
        catch (Exception ex) { Missing(pngStem, ex.Message); return null; }
    }

    /// <summary>Extract the numeric index from "globe3" / "globe10" etc.</summary>
    private static bool TryPosterIndex(string poster, out int n)
    {
        n = 0;
        if (string.IsNullOrEmpty(poster)) return false;
        int digits = 0;
        for (int i = 0; i < poster.Length; i++)
            if (char.IsAsciiDigit(poster[i])) digits++;
        if (digits == 0) return false;
        int num = 0;
        for (int i = 0; i < poster.Length; i++)
            if (char.IsAsciiDigit(poster[i])) num = num * 10 + (poster[i] - '0');
        if (num <= 0) return false;
        n = num;
        return true;
    }

    private void Missing(string stem, string? why = null)
    {
        if (_missing.Add(stem))
            Console.WriteLine($"[monogame] native sky asset '{stem}' unavailable{(why is null ? "" : $" ({why})")} — skipping that component");
    }

    public void Dispose()
    {
        foreach (var t in _textures.Values) t.Dispose();
        _textures.Clear();
        _domes.Clear();
    }
}
