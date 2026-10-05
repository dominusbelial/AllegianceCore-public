namespace Allegiance.Core.Rendering;

using Allegiance.Core.Collision;

/// <summary>
/// P529 — the pure, GPU-free bridge between a cluster's sector lighting map
/// data (<c>DataClusterIGC.LightDirection</c> / <c>LightColor</c>) and the two
/// directional lights a MonoGame <c>BasicEffect</c> can render. It is the exact
/// replication of native's per-sector bidirectional lighting
/// (<c>TrekWindowImpl::UpdateBidirectionalLighting</c> feeding
/// <c>Device3D::BidirectionalLightVertex</c>) — see docs/reports/P529.
///
/// The five members are the effect-ready parameters:
/// <list type="bullet">
/// <item><see cref="KeyDirection"/> — the <em>key</em> light's BasicEffect
/// <b>propagation</b> direction (the way its rays travel) and
/// <see cref="KeyColor"/> its diffuse colour.</item>
/// <item><see cref="AltDirection"/>/<see cref="AltColor"/> — the back/alt light
/// from the opposite side: the native constant blue <c>(0, 0.5, 1.0)</c>.</item>
/// <item><see cref="Ambient"/> — the native bidirectional ambient 0.25 on every
/// channel (native adds a scalar to r,g,b alike).</item>
/// </list>
///
/// <b>No specular by construction:</b> native's rock lighting is purely
/// ambient + Lambertian diffuse (Branch A / Branch B below) with no specular
/// term at all, so this type carries no specular and the renderer pins every
/// directional light's <c>SpecularColor</c> to zero.
/// </summary>
public readonly record struct SectorLighting(
    Collision.Vector3 KeyDirection,
    Collision.Vector3 KeyColor,
    Collision.Vector3 AltDirection,
    Collision.Vector3 AltColor,
    Collision.Vector3 Ambient)
{
    /// <summary>Native bidirectional ambient (<c>clusterIGC.h:331-334</c>),
    /// applied equally to the lit and backlit faces.</summary>
    public const float BidirectionalAmbient = 0.25f;

    /// <summary>Native constant back/alt light (<c>clusterIGC.h:321-324</c>):
    /// a cool blue bounced off the face opposite the key light.</summary>
    public static readonly Collision.Vector3 AltColorConst = new(0f, 0.5f, 1f);

    /// <summary>Fallback "toward-the-light" direction when a sector's
    /// <c>LightDirection</c> is degenerate or missing: native's default light
    /// direction (<c>(0,0,1)</c>, the <c>GeoImage</c> ctor), which doubles as
    /// the canonical sane fixed direction.</summary>
    private static readonly Collision.Vector3 FallbackTowardLight = new(0f, 0f, 1f);

    /// <summary>
    /// Compute the sector lighting for a rock from the local cluster's map data.
    /// </summary>
    /// <param name="lightDirection">The sector's <c>DataClusterIGC.LightDirection</c>
    /// — a direction <em>toward</em> the light, per the native sign convention
    /// (reasoned in the report). May be null, wrongly-sized or degenerate
    /// (<c>DataClusterIGC.cs:27</c> notes rare near-zero magnitudes); it is
    /// defensively normalised and a degenerate/missing value falls back to a
    /// sane fixed direction paired with a white light.</param>
    /// <param name="lightColor">The sector's <c>DataClusterIGC.LightColor</c>,
    /// a 0xAARRGGBB D3DCOLOR (alpha ignored), decoded exactly as native's
    /// <c>RGB_GETRED/GREEN/BLUE</c> macros (<c>olddxdefns.h:22-24</c>).</param>
    public static SectorLighting FromCluster(float[]? lightDirection, uint lightColor)
    {
        uint effectiveLightColor = lightColor;
        Collision.Vector3 toward;
        if (lightDirection is { Length: 3 } d
            && (double)(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]) >= 1e-9)
        {
            toward = new Collision.Vector3(d[0], d[1], d[2]).Normalize();
        }
        else
        {
            // Degenerate or missing direction (rare near-zero magnitudes noted
            // at DataClusterIGC.cs:27) → sane fixed direction; pair it with a
            // white light so the rock still reads, never black.
            toward = FallbackTowardLight;
            effectiveLightColor = 0xFFFFFFFF;
        }

        // Sign reasoning (full derivation in docs/reports/P529):
        //   Native:   brightest face has its normal aligned WITH the sector's
        //             "toward-light" vector  (bright = dot(normal, vecLight),
        //             BidirectionalLightVertex), i.e. vecLight points TOWARD
        //             the sun; the key colour lands on faces whose normal =
        //             +toward.
        //   BasicEffect: lights the face whose normal OPPOSES its Direction
        //             (Direction is propagation, the P355 precedent). To light
        //             the face whose normal = +toward we need -Direction = +toward
        //             ⇒ Direction = -toward. The alt/back light comes from the
        //             opposite side ⇒ its propagation = +toward.
        return new SectorLighting(
            KeyDirection: -toward,
            KeyColor: DecodeColor(effectiveLightColor),
            AltDirection: toward,
            AltColor: AltColorConst,
            Ambient: new Collision.Vector3(BidirectionalAmbient, BidirectionalAmbient, BidirectionalAmbient));
    }

    /// <summary>Decode a 0xAARRGGBB D3DCOLOR into (R,G,B)/255, alpha ignored —
    /// the native <c>RGB_GETRED</c> (<c>&gt;&gt;16&amp;0xff</c>) / GREEN
    /// (<c>&gt;&gt;8&amp;0xff</c>) / BLUE (<c>&amp;0xff</c>) byte layout.</summary>
    public static Collision.Vector3 DecodeColor(uint rgba)
    {
        float r = ((rgba >> 16) & 0xFF) / 255f;
        float g = ((rgba >> 8) & 0xFF) / 255f;
        float b = (rgba & 0xFF) / 255f;
        return new Collision.Vector3(r, g, b);
    }
}
