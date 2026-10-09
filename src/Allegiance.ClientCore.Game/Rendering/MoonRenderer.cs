using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P589 — <b>moons</b>: 0-2 per sector, seeded by the cluster id
/// (<see cref="SpaceObjectMath"/>, deterministic), using the <b>native</b>
/// unused moon art (<c>moonbmp.mdl</c>, <c>moon7bmp.mdl</c>,
/// <c>moonnebbmp.mdl</c>). Placement is non-native (the task's "non-native
/// placement, native art"): each moon sits near the native planet (a
/// deterministic angular offset, "a sensible angular offset", smaller than the
/// planet), drawn with the same poster path as the planet
/// (<see cref="SkyBillboard"/>), lit consistently with the sector light
/// direction: the moon's day-side faces the sun and its night side is shaded
/// (Lambert with an ambient floor) — a phase consistent with the sun
/// (<see cref="SpaceObjectMath.MoonIllumination"/>).
///
/// Toggle: <c>--moons off</c> (ON by default in the black sky).
/// </summary>
public sealed class MoonRenderer : IDisposable
{
    /// <summary>The native moon textures, indexed by
    /// <see cref="SpaceObjectMath.MoonPlacement.TextureIndex"/>.</summary>
    private static readonly string[] MoonArt = ["moonbmp", "moon7bmp", "moonnebbmp"];

    private readonly GraphicsDevice _gd;
    private readonly MdlTexture2D _mdl;
    private readonly List<Texture2D?> _textures = [];

    private bool _loaded;
    private List<(Vector3 Dir, float HalfWorld, float HalfDeg, Texture2D Tex, float Illum)> _moons = [];
    private Vector3 _towardLight = new(0, 0, 1);

    public MoonRenderer(GraphicsDevice gd, IAssetSource assets)
    {
        _gd = gd;
        _mdl = new MdlTexture2D(gd, assets, keyBelow: 10);
    }

    /// <summary>Recompute the moons for a cluster (called on sector change).</summary>
    public void SetCluster(DataClusterIGC cluster)
    {
        _towardLight = TowardLight(cluster);
        EnsureLoaded();

        int count = SpaceObjectMath.MoonCount(cluster.ClusterId);
        var list = new List<(Vector3, float, float, Texture2D, float)>(count);
        if (_textures.Count > 0 && !string.IsNullOrEmpty(cluster.PlanetName))
        {
            var pd = NativeSkyMath.PlanetDirection(cluster.PlanetSinLatitude, cluster.PlanetLongitude);
            var planetDir = new Vector3(pd.X, pd.Y, pd.Z);
            float planetHalfDeg = cluster.PlanetRadius / 100f * 5f;
            for (int i = 0; i < count; i++)
            {
                var m = SpaceObjectMath.PlaceMoon(cluster.ClusterId, i, new NativeSkyMath.Vector3Unit(pd.X, pd.Y, pd.Z), planetHalfDeg, _textures.Count);
                var tex = m.TextureIndex < _textures.Count ? _textures[m.TextureIndex] : null;
                if (tex is null) continue;
                float illum = SpaceObjectMath.MoonIllumination(
                    new NativeSkyMath.Vector3Unit(m.Direction.X, m.Direction.Y, m.Direction.Z),
                    new NativeSkyMath.Vector3Unit(_towardLight.X, _towardLight.Y, _towardLight.Z));
                list.Add((new Vector3(m.Direction.X, m.Direction.Y, m.Direction.Z), m.AngularHalfDeg, m.AngularHalfDeg, tex, illum));
            }
        }
        _moons = list;
    }

    public void Draw(Matrix view, Matrix projection, Vector3 cameraEye, float shellDist)
    {
        foreach (var (dir, _, halfDeg, tex, illum) in _moons)
        {
            float half = SpaceObjectMath.HalfSizeFromHalfDegrees(halfDeg, shellDist);
            // Illumination as a diffuse grey (ambient floor keeps the night
            // side visible but darker); the moon texture itself is white-lit.
            byte b = (byte)Math.Clamp(illum * 255f, 0f, 255f);
            SkyBillboard.Draw(_gd, view, projection, cameraEye, dir, shellDist, half, tex, new Color(b, b, b));
        }
    }

    private static Vector3 TowardLight(DataClusterIGC cluster)
    {
        var ld = cluster.LightDirection;
        if (ld is { Length: 3 })
        {
            float len = MathF.Sqrt(ld[0] * ld[0] + ld[1] * ld[1] + ld[2] * ld[2]);
            if (len >= 1e-6f) return new Vector3(ld[0] / len, ld[1] / len, ld[2] / len);
        }
        return new Vector3(0, 0, 1);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        foreach (var stem in MoonArt)
        {
            var t = _mdl.Get(stem);
            if (t is not null) _textures.Add(t);
        }
        _loaded = true;
    }

    public void Dispose() => _mdl.Dispose();
}
