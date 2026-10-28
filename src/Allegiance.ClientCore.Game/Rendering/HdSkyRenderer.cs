using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P577 — the <b>HD sky tier</b> (<c>--sky hd</c>): the same per-sector sky
/// structure as the native tier (P576-A) but with every layer upgraded from
/// real telescope public data, matched per native library entry:
/// <list type="bullet">
/// <item><b>Stars</b>: the NASA SVS <i>Deep Star Maps 2020</i> baked into a
/// 6-face skybox cubemap (<c>hdsky/stars/</c> desktop 2048²/face, Android 1024²)
/// replaces the native seeded point stars.</item>
/// <item><b>Environment dome</b>: the native <c>globeN</c> sky-dome mesh (UV,
/// radius, draw order unchanged) textured with an HD equirect nebula
/// (<c>hdsky/nebula/envN.png</c>) composited from ESA/Hubble/Webb or NASA
/// imagery and matched to the native environment's palette/layout.</item>
/// <item><b>Planet</b>: the native planet billboard (lat/long/radius unchanged)
/// with the <b>native alien planet art</b> (<c>nebplntNNbmp.mdl</c> /
/// <c>nebplntNN.png</c>) — P577b keeps the original planets (real Solar-System
/// bodies were too recognisable for Allegiance sectors); the native 2048²
/// textures are used as-is.</item>
/// </list>
/// Every asset carries its source/licence/credit in
/// <c>docs/reports/P577_assets/sky-hd.json</c> + <c>licenses/</c> (CC BY 4.0 for
/// ESA/Hubble/Webb, public domain for NASA), shown on the in-game
/// Credits/Licences screen.
/// <para>
/// Readability guard (P577 scope item 3): textures are clamped on load to a
/// per-entry brightness/saturation ceiling so ships, projectiles and the HUD
/// stay readable, and a global <c>SkyIntensity</c> multiplier (default 1.0,
/// <c>--sky-intensity</c>) scales the whole HD backdrop. The contract is:
/// draw order and dome geometry identical to native; only the textures and the
/// star layer change.
/// </para>
/// </summary>
public sealed class HdSkyRenderer : ISkyRenderer, IDisposable
{
    /// <summary>Star dome radius — inside the native 7500 nebula dome (so the
    /// alpha-keyed nebula reveals the stars) and outside the planet shell.</summary>
    private const float StarDomeRadius = 7110f;

    /// <summary>Maximum sky luminance (0-255) any HD sky pixel may reach after
    /// the readability cap. Keeps ships/projectiles/HUD legible over the sky.</summary>
    private const byte LumaCap = HdSkyGuard.DefaultLumaCap;

    /// <summary>Planet billboard distance on the sky shell (native StarRadius).</summary>
    private const float PlanetDist = 6960f;

    /// <summary>Saturation ceiling (chroma fraction of max) applied on load.</summary>
    private const float SatCap = HdSkyGuard.DefaultSatCap;

    private readonly GraphicsDevice _gd;
    private readonly IAssetSource _assets;
    private readonly bool _lowRes;

    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkyDome> _domes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

    private Texture2D? _starTexture;
    private SkyDome? _starDome;

    private SkyDome? _activeDome;
    private string? _activePoster;
    private Texture2D? _activeEnvTexture;

    // P588 — the native planet poster, shared with the native/black tiers so
    // all three draw the identical native alien planet billboard (with the HD
    // tier's global intensity tint applied on draw).
    private readonly PlanetPoster _planet;

    /// <summary>Global sky intensity (P577 readability guard; default 1.0).</summary>
    public float SkyIntensity { get; set; } = 1.0f;

    /// <param name="lowRes">Use the Android/low-res asset set (nebulae 1024x512,
    /// planets 512, star faces 1024) instead of the desktop set.</param>
    public HdSkyRenderer(GraphicsDevice gd, IAssetSource assets, bool lowRes = false)
    {
        _gd = gd;
        _assets = assets;
        _lowRes = lowRes;
        _planet = new PlanetPoster(gd, assets);
        EnsureStarLayer();
    }

    private sealed class SkyDome
    {
        public required VertexPositionColorTexture[] Vertices;
        public required short[] Indices;
        public required int VertexCount;
        public required int PrimitiveCount;
    }

    private void EnsureStarLayer()
    {
        if (_starTexture is not null) return;
        // Seam-free equirectangular star dome (NASA Deep Star Map baked to a
        // 2:1 equirect), NOT a 6-face cube — a cube skybox shows face seams and
        // corner gaps in the real window. Loaded lazily so the star layer can
        // fail gracefully (falls back to no stars, nebula still draws).
        string stem = _lowRes
            ? "hdsky/stars_android/stars_equirect_1024x512"
            : "hdsky/stars/stars_equirect_2048x1024";
        _starTexture = LoadPngCapped(stem);
    }

    /// <summary>P862 — decode a planet poster texture into this tier's cache
    /// WITHOUT drawing (the sector asset warm-up). Same loader <see cref="SetCluster"/>
    /// uses. Returns 1 when created now, 0 when already resident, -1 when unavailable.</summary>
    public int WarmPlanet(string planetName) => _planet.Warm(planetName);

    /// <summary>P862 — how many planet poster textures this tier has resident.</summary>
    public int LoadedPlanetTextures => _planet.LoadedTextureCount;

    /// <summary>Switch the HD sky to a cluster's data (called on sector change).</summary>
    public void SetCluster(DataClusterIGC cluster)
    {
        var poster = cluster.PosterName;
        if (!string.IsNullOrEmpty(poster) && !string.Equals(poster, _activePoster, StringComparison.OrdinalIgnoreCase))
        {
            _activeDome = DomeFor(poster);
            _activeEnvTexture = EnvTextureFor(poster);
            _activePoster = poster;
        }
        else if (string.IsNullOrEmpty(poster))
        {
            // P862b — no poster, no dome (native clusterIGC.cpp:41-42). See the
            // native tier: one renderer is shared with the aleph glimpse now.
            _activeDome = null;
            _activeEnvTexture = null;
            _activePoster = null;
        }

        // Native planet poster (shared, P588).
        _planet.SetCluster(cluster);
    }

    public void Draw(Matrix view, Matrix projection, Vector3 cameraEye)
    {
        if (_activeDome is null && !_planet.HasPlanet) return;

        var skyProj = NativeSkyPlacement.SkyProjection(projection);
        var gd = _gd;
        var prevDepth = gd.DepthStencilState;
        var prevBlend = gd.BlendState;
        var prevRaster = gd.RasterizerState;
        var prevSampler = gd.SamplerStates[0];

        try
        {
            gd.DepthStencilState = DepthStencilState.None;
            gd.RasterizerState = RasterizerState.CullNone;

            // 1) HD star dome (NASA equirect) — replaces native seeded points.
            DrawStarDome(view, skyProj, cameraEye);

            // 2) HD environment dome (native globe geometry + HD equirect).
            if (_activeDome is not null && _activeEnvTexture is not null)
                DrawDome(view, skyProj, cameraEye);

            // 3) Native planet billboard (shared PlanetPoster, P588) tinted by
            //    the HD global intensity.
            _planet.Draw(view, skyProj, cameraEye, PlanetDist, Tint);
        }
        finally
        {
            gd.DepthStencilState = prevDepth;
            gd.BlendState = prevBlend;
            gd.RasterizerState = prevRaster;
            gd.SamplerStates[0] = prevSampler;
        }
    }

    private Color Tint => new(
        (byte)Math.Clamp(255f * SkyIntensity, 0f, 255f),
        (byte)Math.Clamp(255f * SkyIntensity, 0f, 255f),
        (byte)Math.Clamp(255f * SkyIntensity, 0f, 255f),
        (byte)255);

    private void DrawStarDome(Matrix view, Matrix projection, Vector3 cameraEye)
    {
        if (_starTexture is null) return;
        if (_starDome is null && !TryBuildStarDome()) return;

        var dome = _starDome!;
        var gd = _gd;
        gd.BlendState = BlendState.AlphaBlend;
        using var effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            Texture = _starTexture,
            VertexColorEnabled = true,
            LightingEnabled = false,
            View = view,
            Projection = projection,
            World = NativeSkyPlacement.DomeWorld(cameraEye),
        };
        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, dome.Vertices, 0, dome.VertexCount, dome.Indices, 0, dome.PrimitiveCount);
        }
    }

    /// <summary>Build the star dome by scaling the native <c>globe1</c> full
    /// sphere (radius 7500) down to <see cref="StarDomeRadius"/> so the stars
    /// sit inside the alpha-keyed nebula dome. Reuses the proven dome-draw path
    /// — no cube seams.</summary>
    private bool TryBuildStarDome()
    {
        const string stem = "globe1";
        if (_missing.Contains(stem)) return false;
        try
        {
            if (!_assets.Exists(stem + ".mdl")) { Missing(stem); return false; }
            var mdl = MdlFile.Parse(AssetSources.ReadAllBytes(_assets, stem + ".mdl"));
            if (mdl.Meshes.Count == 0) { Missing(stem); return false; }
            var mesh = mdl.Meshes[0];
            float scale = StarDomeRadius / 7500f;
            var verts = new VertexPositionColorTexture[mesh.VertexCount];
            var white = new Color(255, 255, 255, 255);
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                verts[i] = new VertexPositionColorTexture(
                    new Vector3(mesh.Positions[3 * i] * scale, mesh.Positions[3 * i + 1] * scale, mesh.Positions[3 * i + 2] * scale),
                    white,
                    new Vector2(mesh.TexCoords[2 * i], mesh.TexCoords[2 * i + 1]));
            }
            var shorts = new short[mesh.Indices.Length];
            for (int i = 0; i < mesh.Indices.Length; i++) shorts[i] = (short)mesh.Indices[i];
            _starDome = new SkyDome
            {
                Vertices = verts,
                Indices = shorts,
                VertexCount = verts.Length,
                PrimitiveCount = mesh.Indices.Length / 3,
            };
            return true;
        }
        catch (Exception ex) { Missing(stem, ex.Message); return false; }
    }

    private void DrawDome(Matrix view, Matrix projection, Vector3 cameraEye)
    {
        var dome = _activeDome!;
        var gd = _gd;
        gd.BlendState = BlendState.AlphaBlend;   // premultiplied alpha-keyed HD nebula
        using var effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            Texture = _activeEnvTexture,
            VertexColorEnabled = true,
            DiffuseColor = Tint.ToVector3(),
            LightingEnabled = false,
            View = view,
            Projection = projection,
            World = NativeSkyPlacement.DomeWorld(cameraEye),
        };
        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, dome.Vertices, 0, dome.VertexCount, dome.Indices, 0, dome.PrimitiveCount);
        }
    }

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
            var dome = new SkyDome
            {
                Vertices = verts,
                Indices = shorts,
                VertexCount = verts.Length,
                PrimitiveCount = mesh.Indices.Length / 3,
            };
            _domes[stem] = dome;
            return dome;
        }
        catch (Exception ex) { Missing(stem, ex.Message); return null; }
    }

    /// <summary>P862 — resolve a poster's HD environment dome texture into the
    /// cache WITHOUT drawing (the sector asset warm-up), so a warp into a sector
    /// whose HD nebula has never been seen does not decode/upload a large PNG in
    /// the transit frame. Returns 1 when a new texture was created, 0 when it was
    /// already resident, -1 when the poster has no HD environment art.</summary>
    public int Warm(string poster)
    {
        if (string.IsNullOrEmpty(poster)) return -1;
        if (!TryPosterIndex(poster, out int n)) return -1;
        string stem = _lowRes ? $"hdsky/nebula_a/env{n}" : $"hdsky/nebula/env{n}";
        if (_textures.ContainsKey(stem)) return 0;
        return LoadPngCapped(stem) is null ? -1 : 1;
    }

    /// <summary>P862 — how many HD sky textures are resident.</summary>
    public int LoadedTextureCount => _textures.Count;

    private Texture2D? EnvTextureFor(string poster)
    {
        if (!TryPosterIndex(poster, out int n)) return null;
        string stem = _lowRes ? $"hdsky/nebula_a/env{n}" : $"hdsky/nebula/env{n}";
        return LoadPngCapped(stem);
    }

    /// <summary>
    /// P577b — HD planets render the <b>native alien planet art</b> via the
    /// shared <see cref="PlanetPoster"/> (the P576 path:
    /// <c>nebplntNNbmp.mdl</c> / <c>nebplntNN.png</c>), NOT NASA Solar-System
    /// imagery (Domingo's review: real bodies are too recognisable for
    /// Allegiance sectors). Native planet textures are already 2048² RGB565, so
    /// "native as-is" is genuinely HD — and identical across every sky tier.
    /// </summary>

    /// <summary>Load an HD sky PNG and apply the per-entry readability cap
    /// (brightness ceiling + saturation clamp) so ships/HUD stay legible.</summary>
    private Texture2D? LoadPngCapped(string stem)
    {
        if (_textures.TryGetValue(stem, out var t)) return t;
        if (_missing.Contains(stem)) return null;
        try
        {
            if (!_assets.Exists(stem + ".png")) { Missing(stem); return null; }
            using var s = _assets.OpenRead(stem + ".png");
            var tex = Texture2D.FromStream(_gd, s);
            var data = new Color[tex.Width * tex.Height];
            tex.GetData(data);
            if (HdSkyGuard.ApplyCap(data, LumaCap, SatCap)) tex.SetData(data);
            _textures[stem] = tex;
            return tex;
        }
        catch (Exception ex) { Missing(stem, ex.Message); return null; }
    }

    private static bool TryPosterIndex(string poster, out int n)
    {
        n = HdSkyGuard.EnvironmentIndex(poster);
        return n > 0;
    }

    private void Missing(string stem, string? why = null)
    {
        if (_missing.Add(stem))
            Console.WriteLine($"[monogame] HD sky asset '{stem}' unavailable{(why is null ? "" : $" ({why})")} — skipping that component");
    }

    public void Dispose()
    {
        foreach (var t in _textures.Values) t.Dispose();
        _textures.Clear();
        _starTexture?.Dispose();
        _starDome = null;
        _domes.Clear();
    }
}
