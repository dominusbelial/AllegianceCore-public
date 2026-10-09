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
/// deterministic angular offset, smaller than the planet), drawn with the same
/// poster path as the planet (<see cref="SkyBillboard"/>).
///
/// P589b (Domingo live: "some really don't make sense"): the native moon art is
/// a <b>surface texture for the 3D <c>moon.mdl</c> sphere</b>, not a disc
/// image — drawing it as a flat billboard gave a mottled SQUARE. The fix
/// renders each moon as a <b>lit circular disc</b> generated from the texture
/// (<see cref="GenerateLitDisc"/>): a circular alpha mask clips the square's
/// corners at any size, and a Lambert terminator from the sector light
/// direction shades a day side toward a crescent night side (ambient floor),
/// matching the native planet posters' phase-lit look. <c>moonnebbmp</c> is
/// handled identically. No square edges at any size.
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
    private List<(Vector3 Dir, float HalfDeg, Texture2D Disc)> _moons = [];
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

        ClearDiscs();
        int count = SpaceObjectMath.MoonCount(cluster.ClusterId);
        var list = new List<(Vector3, float, Texture2D)>(count);
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
                var dir = new Vector3(m.Direction.X, m.Direction.Y, m.Direction.Z);
                var disc = MakeLitDisc(tex, dir);
                if (disc is null) continue;
                list.Add((dir, m.AngularHalfDeg, disc));
            }
        }
        _moons = list;
    }

    public void Draw(Matrix view, Matrix projection, Vector3 cameraEye, float shellDist)
    {
        foreach (var (dir, halfDeg, disc) in _moons)
        {
            float half = SpaceObjectMath.HalfSizeFromHalfDegrees(halfDeg, shellDist);
            SkyBillboard.Draw(_gd, view, projection, cameraEye, dir, shellDist, half, disc);
        }
    }

    /// <summary>Build a GPU texture for one moon: the lit circular disc baked
    /// from the source surface texture for this moon's direction and the
    /// sector light.</summary>
    private Texture2D? MakeLitDisc(Texture2D src, Vector3 dir)
    {
        int w = src.Width, h = src.Height;
        var cols = new Color[w * h];
        src.GetData(cols);

        var right = NativeSkyPlacement.BillboardRight(dir);
        var up = NativeSkyPlacement.BillboardUp(dir, right);
        var rgba = GenerateLitDisc(cols, w, h, right, up, dir, _towardLight, Ambient);

        var outCols = new Color[w * h];
        for (int i = 0; i < outCols.Length; i++)
            outCols[i] = new Color(rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]);
        var disc = new Texture2D(_gd, w, h, false, SurfaceFormat.Color);
        disc.SetData(outCols);
        return disc;
    }

    /// <summary>Night-side ambient floor (matches the old flat
    /// <see cref="SpaceObjectMath.MoonIllumination"/> floor of 0.25).</summary>
    private const float Ambient = 0.25f;

    /// <summary>
    /// Pure (device-free) lit-circular-disc generator — the P589b moon fix.
    ///
    /// Given the source moon <b>surface texture</b> (<paramref name="cols"/>,
    /// already premultiplied, from the .mdl) and the billboard basis of the
    /// moon's direction, produce a premultiplied RGBA byte buffer in which:
    ///   • the texture is clipped by a CIRCULAR mask (alpha → 0 at the
    ///     silhouette rim) so no square corner/edge can show at any size,
    ///   • a Lambert terminator from <paramref name="lightWorld"/> shades a
    ///     lit day side down to an <paramref name="ambient"/> night side —
    ///     a crescent phase consistent with the sector light, like a small
    ///     planet (native planet posters).
    /// </summary>
    public static byte[] GenerateLitDisc(
        Color[] cols, int w, int h, Vector3 right, Vector3 up, Vector3 facing,
        Vector3 lightWorld, float ambient = 0.25f, float rim = 0.12f)
    {
        var rgba = new byte[w * h * 4];
        float rad = Math.Min(w / 2f, h / 2f);
        var lightN = Vector3.Normalize(lightWorld);
        for (int y = 0; y < h; y++)
        {
            float dy = (y + 0.5f - h / 2f) / rad;
            for (int x = 0; x < w; x++)
            {
                float dx = (x + 0.5f - w / 2f) / rad;
                float r2 = dx * dx + dy * dy;
                int i = y * w + x;
                if (r2 >= 1f)
                {
                    rgba[4 * i + 3] = 0;
                    continue;
                }
                float z = MathF.Sqrt(Math.Max(0f, 1f - r2));
                var n = Vector3.Normalize(right * dx + up * dy + facing * z);
                float lam = MathF.Max(0f, Vector3.Dot(n, lightN));
                float shade = ambient + (1f - ambient) * lam;
                // soft rim falloff: alpha → 0 across the outer `rim` of the disc
                float t = Math.Clamp((1f - MathF.Sqrt(r2)) / rim, 0f, 1f);
                float geo = 0.5f - 0.5f * MathF.Cos(MathF.PI * t);
                var c = cols[i];
                float sa = c.A / 255f;
                rgba[4 * i] = (byte)Math.Clamp(c.R * shade * geo, 0f, 255f);
                rgba[4 * i + 1] = (byte)Math.Clamp(c.G * shade * geo, 0f, 255f);
                rgba[4 * i + 2] = (byte)Math.Clamp(c.B * shade * geo, 0f, 255f);
                rgba[4 * i + 3] = (byte)Math.Clamp(sa * geo * 255f, 0f, 255f);
            }
        }
        return rgba;
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

    private void ClearDiscs()
    {
        foreach (var (_, _, d) in _moons) d?.Dispose();
        _moons = [];
    }

    public void Dispose()
    {
        ClearDiscs();
        _mdl.Dispose();
    }
}
