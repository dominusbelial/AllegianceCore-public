using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P588 — the per-sector <b>native planet poster</b>, extracted so the native,
/// HD and black sky tiers all draw the identical native alien planet billboard
/// (P576 placement/size, camera-relative, no far clipping). In black mode this
/// is the <em>only</em> cluster-dependent sky element (there is no nebula dome).
///
/// Native rule ported (cited in docs/reports/P576_SECTOR_SKY_AND_ALEPH_GLIMPSE.md):
/// <c>clusterIGC.cpp:44-57</c> direction + radius via <c>AddPoster</c> →
/// <c>PosterImage::AddPoster</c> (<c>trekigc.cpp:243-248</c>,
/// <c>efimage.cpp:515-563</c>): a camera-facing billboarded square at the
/// planet's direction, sized by <c>planetRadius</c>, textured with the planet
/// texture (<c>nebplntNNbmp.mdl</c> / the ASCII-stub sibling
/// <c>nebplntNN.png</c>).
/// </summary>
public sealed class PlanetPoster : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly IAssetSource _assets;
    private static readonly Color XnaWhite = Color.White;
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

    private Texture2D? _texture;
    private string? _activePlanet;
    private Vector3 _dir;
    private float _radius;
    private bool _has;

    public PlanetPoster(GraphicsDevice gd, IAssetSource assets)
    {
        _gd = gd;
        _assets = assets;
    }

    /// <summary>Whether a planet is currently available (a texture decoded).</summary>
    public bool HasPlanet => _has;

    /// <summary>P862 — decode a cluster's planet poster texture into the cache
    /// WITHOUT drawing (the sector asset warm-up), so the first frame in a new
    /// sector does not pay for a 2048² native planet decode + upload. Uses the same
    /// loader <see cref="SetCluster"/> uses; a missing asset stays a logged miss.</summary>
    /// <returns>1 when the texture was created now, 0 when it was already resident,
    /// -1 when the cluster has no planet or its art is unavailable.</returns>
    public int Warm(string planetName)
    {
        if (string.IsNullOrEmpty(planetName)) return -1;
        if (_textures.ContainsKey(planetName)) return 0;
        return PlanetTextureFor(planetName) is null ? -1 : 1;
    }

    /// <summary>P862 — how many planet poster textures are resident.</summary>
    public int LoadedTextureCount => _textures.Count;

    /// <summary>Update the planet from a cluster's data (called on sector change).</summary>
    public void SetCluster(DataClusterIGC cluster)
    {
        if (!string.IsNullOrEmpty(cluster.PlanetName)
            && !string.Equals(cluster.PlanetName, _activePlanet, StringComparison.OrdinalIgnoreCase))
        {
            _texture = PlanetTextureFor(cluster.PlanetName);
            var pd = NativeSkyMath.PlanetDirection(cluster.PlanetSinLatitude, cluster.PlanetLongitude);
            _dir = new Vector3(pd.X, pd.Y, pd.Z);
            _radius = cluster.PlanetRadius;
            _has = _texture is not null;
            _activePlanet = cluster.PlanetName;
        }
        else if (string.IsNullOrEmpty(cluster.PlanetName))
        {
            _has = false;
        }
    }

    /// <summary>
    /// Draw the planet poster as a camera-facing billboard on the sky shell at
    /// <paramref name="dist"/> along the planet direction (camera-relative,
    /// <see cref="NativeSkyPlacement.PlanetShellCenter"/>), sized from the
    /// native <c>planetRadius</c>. An optional <paramref name="tint"/> scales
    /// the diffuse colour (the HD tier's intensity; the native/black tiers pass
    /// white). Render states are pinned by the caller.
    /// </summary>
    public void Draw(Matrix view, Matrix projection, Vector3 cameraEye, float dist, Color? tint = null)
    {
        if (!_has || _texture is null) return;
        var center = NativeSkyPlacement.PlanetShellCenter(_dir, dist, cameraEye);
        float half = PlanetAngularScale(_radius) * dist;

        var right = NativeSkyPlacement.BillboardRight(_dir);
        var up = NativeSkyPlacement.BillboardUp(_dir, right);

        var corners = new[]
        {
            new VertexPositionColorTexture(center - right * half - up * half, XnaWhite, new Vector2(0, 1)),
            new VertexPositionColorTexture(center + right * half - up * half, XnaWhite, new Vector2(1, 1)),
            new VertexPositionColorTexture(center - right * half + up * half, XnaWhite, new Vector2(0, 0)),
            new VertexPositionColorTexture(center + right * half + up * half, XnaWhite, new Vector2(1, 0)),
        };
        var indices = new short[] { 0, 1, 2, 2, 1, 3 };

        var gd = _gd;
        gd.BlendState = BlendState.AlphaBlend;

        using var effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            Texture = _texture,
            VertexColorEnabled = true,   // match native's drawing path (dome/stars all set it)
            DiffuseColor = tint?.ToVector3() ?? Vector3.One,
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
    /// radius 50 → ~2.5° half-angle, radius 100 → ~5° (native
    /// <c>efimage.cpp:515-563</c>, poster sized by planetRadius).
    /// </summary>
    private static float PlanetAngularScale(float radius)
        => MathF.Tan(MathHelper.ToRadians(radius / 100f * 5f));

    private Texture2D? PlanetTextureFor(string planetName)
    {
        if (_textures.TryGetValue(planetName, out var t)) return t;
        if (_missing.Contains(planetName)) return null;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var (mdlStem, pngStem) = HdSkyGuard.NativePlanetTextureStems(planetName);
            // planetName e.g. "nebplnt06bmp" — the native .mdl stem.
            if (_assets.Exists(mdlStem + ".mdl"))
            {
                var mdlTex = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(_assets, mdlStem + ".mdl"));
                // Colour-key the near-black surround (the planet art sits on a
                // black backing; native keys it transparent via the surface colorkey).
                var rgba = MdlTextureDecoder.ToPremultipliedRgba(mdlTex, 10);
                var surf = new Texture2D(_gd, mdlTex.Width, mdlTex.Height, false, SurfaceFormat.Color);
                // P862 — upload the premultiplied RGBA bytes straight (SurfaceFormat.Color
                // is RGBA8); the Color[] staging was another 16.7 MB per 2048² planet.
                surf.SetData(rgba);
                _textures[planetName] = surf;
                AssetLoadLog.Note("planet poster", planetName,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    $"{mdlTex.Width}x{mdlTex.Height}");
                return surf;
            }
            // ASCII "use ...ImportImageFromFile(.png)" stub — sibling PNG.
            if (_assets.Exists(pngStem + ".png"))
            {
                using var s = _assets.OpenRead(pngStem + ".png");
                var tex = Texture2D.FromStream(_gd, s);
                _textures[planetName] = tex;
                return tex;
            }
            Missing(planetName);
            return null;
        }
        catch (Exception ex) { Missing(planetName, ex.Message); return null; }
    }

    private void Missing(string stem, string? why = null)
    {
        if (_missing.Add(stem))
            Console.WriteLine($"[monogame] planet poster asset '{stem}' unavailable{(why is null ? "" : $" ({why})")} — skipping the planet");
    }

    public void Dispose()
    {
        foreach (var t in _textures.Values) t.Dispose();
        _textures.Clear();
    }
}
