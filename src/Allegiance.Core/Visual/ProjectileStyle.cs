using System.Collections.Generic;

namespace Allegiance.Core.Visual;

/// <summary>
/// The per-type render style for one projectile bolt, resolved from the loaded
/// static core's <c>ProjectileTypes</c> table. P465b — replaces the port's
/// single hard-coded amber tracer with each type's real data where that data is
/// meaningful, while keeping the amber tracer as the fallback for an unknown
/// type or a missing core.
/// </summary>
/// <remarks>
/// <para><b>Colour.</b> Native Allegiance does <b>not</b> drive the visible bolt
/// from <c>DataProjectileTypeIGC.color</c>. The projectile decal path
/// (<c>CprojectileIGC::Initialize</c> → <c>TmodelIGC::LoadDecal</c>,
/// <c>src/Igc/projectileIGC.cpp:59-66</c>, <c>modelIGC.cpp:276</c>) forwards the
/// colour only to <c>LoadCVH</c> (hit-test/icon setup) and builds the visible
/// bolt from the <c>&lt;textureName&gt;bmp</c> animated image tinted
/// <c>Color::White()</c> (<c>WinTrek/trekigc.cpp:1772-1809</c>). The real
/// per-type identity is therefore the <b>texture</b>, not the colour. Measured
/// across the whole PCore001 corpus every projectile type's colour is
/// (0,0,0,0), so treating a genuinely non-black colour as an override when one
/// is present is a pragmatic superset: PCore001 bolts stay the port's amber
/// tracer (native-equivalent, since native shows the texture un-tinted), while a
/// core that does populate colour can express it. A black colour must fall back
/// to amber rather than render black — as a <c>DiffuseColor</c> it would be
/// invisible.</para>
/// <para><b>Width / Radius.</b> The flat bolt's cross-section is driven by
/// <c>DataProjectileTypeIGC.width</c> (native's decal <c>width</c>), so a real
/// positive width is used and <see cref="DefaultWidth"/> otherwise (native
/// PCore001 weapon projectiles are almost all width 0, so they render at the
/// default). <c>radius</c> sets native's <c>ThingSite</c> radius and the
/// (out-of-scope) 3D-model path, not the flat decal cross-section; it is carried
/// here for fidelity/testing but does not resize the tracer.</para>
/// </remarks>
public readonly record struct ProjectileStyle(float R, float G, float B, float Width, float Radius)
{
    /// <summary>The tracer width used when a type carries no positive width
    /// (the port's P231/P243 default).</summary>
    public const float DefaultWidth = 0.35f;

    /// <summary>The amber tracer that stands in for every type when the type is
    /// unknown or the core is missing (P231 heritage; P243 retuned the 0.35
    /// width; the amber tint 1.0, 0.84, 0.32).</summary>
    public static readonly ProjectileStyle AmberFallback =
        new(1.0f, 0.84f, 0.32f, DefaultWidth, 0f);

    /// <summary>
    /// Resolve the render style for <paramref name="typeId"/> against the loaded
    /// core's <c>ProjectileTypes</c> table, which may be <c>null</c> when the
    /// client has no static core (the documented graceful path). An unknown id
    /// or a missing core yields <see cref="AmberFallback"/>; a known type's real
    /// positive width/radius are preserved and a non-black colour is honoured.
    /// </summary>
    public static ProjectileStyle For(
        short typeId,
        IReadOnlyDictionary<short, DataProjectileTypeIGC>? types)
    {
        if (types is not null && types.TryGetValue(typeId, out var p))
        {
            float width = p.Width > 0f ? p.Width : DefaultWidth;
            var c = p.Color;
            if (c is { Length: >= 3 } && (c[0] != 0f || c[1] != 0f || c[2] != 0f))
                return new ProjectileStyle(c[0], c[1], c[2], width, p.Radius);
            return new ProjectileStyle(AmberFallback.R, AmberFallback.G, AmberFallback.B, width, p.Radius);
        }
        return AmberFallback;
    }
}
