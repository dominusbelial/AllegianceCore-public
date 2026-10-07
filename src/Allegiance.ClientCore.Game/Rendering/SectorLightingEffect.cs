using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P530 — the single shared bridge that maps a sector's native per-sector
/// bidirectional lighting (<see cref="SectorLighting"/>, from
/// <c>SectorLighting.FromCluster</c>) onto a MonoGame <c>BasicEffect</c>. Every
/// 3D scene renderer — ships, stations, missiles, probes, building effects and
/// asteroids — programs its <c>BasicEffect</c> through this one helper, so the
/// native lighting contract (key + constant blue back/alt light + 0.25 ambient,
/// <b>no specular</b>, no third light) is enforced identically everywhere
/// instead of being copied into six draw paths. See docs/reports/P530.
/// </summary>
/// <remarks>
/// The derivation (<see cref="Derive"/>) is a PURE mapping from a
/// <see cref="SectorLighting"/> to effect-ready XNA parameters, so it is unit
/// testable without a <see cref="GraphicsDevice"/> (BasicEffect itself requires
/// a real device, which the unit suite does not have — the same division P529
/// used: pure mapping tested in the suite, the real BasicEffect application
/// proven offscreen in the visual proof).
/// </remarks>
public static class SectorLightingEffect
{
    /// <summary>
    /// The effect-ready parameters a <see cref="SectorLighting"/> maps to:
    /// two directional lights (key + back/alt), the sector ambient, and the
    /// native parity invariants. <see cref="SpecularZero"/> and
    /// <see cref="ThirdLightEnabled"/> encode the two ways this diverges from
    /// MonoGame's stock <c>EnableDefaultLighting</c> rig (which carries a warm
    /// specular key light and a third light) — native has neither.
    /// </summary>
    public readonly record struct SectorLightSettings(
        Vector3 KeyDirection,
        Vector3 KeyColor,
        Vector3 AltDirection,
        Vector3 AltColor,
        Vector3 Ambient,
        bool SpecularZero = true,
        bool ThirdLightEnabled = false,
        bool LightingEnabled = true);

    /// <summary>
    /// Pure derivation: turn a <see cref="SectorLighting"/> into the exact XNA
    /// vectors (and native-parity flags) that <see cref="Apply"/> writes onto a
    /// <c>BasicEffect</c>. Kept separate from <see cref="Apply"/> so the mapping
    /// is verifiable in the unit suite without a graphics device.
    /// </summary>
    public static SectorLightSettings Derive(SectorLighting lighting)
        => new(
            ToXna(lighting.KeyDirection),
            ToXna(lighting.KeyColor),
            ToXna(lighting.AltDirection),
            ToXna(lighting.AltColor),
            ToXna(lighting.Ambient),
            SpecularZero: true,
            ThirdLightEnabled: false,
            LightingEnabled: true);

    /// <summary>
    /// Program <paramref name="effect"/> with the sector's native bidirectional
    /// lighting. Light0 lights the faces whose normal opposes its propagation
    /// (the key-lit side); Light1 lights the opposite faces (the native blue
    /// back/alt light). Summing them reproduces native's Branch A (front →
    /// key colour) / Branch B (back → alt colour) with the 0.25 ambient added
    /// to both. Specular is pinned to zero on both lights (native's
    /// <c>BidirectionalLightVertex</c> has no specular term) and the stock
    /// third light is disabled (native has no headlight/no third light) —
    /// unless the caller re-enables it (none of the sector-lit passes do).
    /// </summary>
    public static void Apply(BasicEffect effect, SectorLighting lighting)
    {
        var s = Derive(lighting);
        // P564 — native always runs with D3DRS_LIGHTING TRUE (device3d.cpp:749
        // non-bidirectional, :791 bidirectional), so the sector-lit pass MUST have
        // lighting enabled. BasicEffect's LightingEnabled defaults to FALSE, and
        // until P564 this helper programmed the two directional lights without ever
        // turning lighting on — so every sector-lit object rendered FLAT (ambient
        // + texture only), the "everything reads as a flat silhouette" report.
        // Turning it on restores native's directional shading (no invented lights,
        // no ambient change — pure native). The legacy null-lighting callers call
        // EnableDefaultLighting() which already enables lighting, so they are unaffected.
        effect.LightingEnabled = s.LightingEnabled;
        effect.AmbientLightColor = s.Ambient;
        effect.DirectionalLight0.Direction = s.KeyDirection;
        effect.DirectionalLight0.DiffuseColor = s.KeyColor;
        effect.DirectionalLight0.SpecularColor = Vector3.Zero;
        effect.DirectionalLight0.Enabled = true;
        effect.DirectionalLight1.Direction = s.AltDirection;
        effect.DirectionalLight1.DiffuseColor = s.AltColor;
        effect.DirectionalLight1.SpecularColor = Vector3.Zero;
        effect.DirectionalLight1.Enabled = true;
        effect.DirectionalLight2.Enabled = s.ThirdLightEnabled;
    }

    private static Vector3 ToXna(Allegiance.Core.Collision.Vector3 v) => new(v.X, v.Y, v.Z);
}
