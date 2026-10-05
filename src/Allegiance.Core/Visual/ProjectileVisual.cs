using System;

namespace Allegiance.Core.Visual;

/// <summary>
/// The four ways a projectile round can be drawn, mirroring native
/// <c>CprojectileIGC::Initialize</c> (<c>src/Igc/projectileIGC.cpp:50-80</c>):
/// a 3D model, a directional decal (stretched along motion), a camera-facing
/// billboard decal, or — this port's grace path — the legacy tinted box when a
/// type's asset is missing/unresolvable.
/// </summary>
public enum ProjectileVisualKind
{
    /// <summary>Load a 3D model (<c>modelName</c>), oriented along travel, at
    /// <c>Radius</c>.</summary>
    Model,

    /// <summary>A decal stretched along the travel direction with
    /// <c>Width</c> (native <c>bDirectional</c> — e.g. the Pulse Laser).</summary>
    Directional,

    /// <summary>A camera-facing textured sprite, size from <c>Radius</c>,
    /// spun by <c>Rotation</c> rad/s.</summary>
    Billboard,

    /// <summary>The legacy tinted tracer box (fallback when an asset is missing).</summary>
    Box,
}

/// <summary>
/// P544 — the resolved per-type projectile visual. Pure logic (no MonoGame
/// types on the surface), so the mapper is unit-testable headless exactly like
/// <see cref="ProjectileStyle"/> / <see cref="Allegiance.Core.Data.MdlFile"/>.
/// The renderer consumes this plus the real decoded texture/model assets.
/// </summary>
/// <remarks>
/// <para><b>The native rule.</b> <c>projectileIGC.cpp:50-80</c>: if
/// <c>iswalpha(modelName[0])</c> the projectile <c>Load</c>s a 3D model and
/// <c>SetRadius(radius)</c>; otherwise it <c>LoadDecal(textureName, …,
/// bDirectional, width, …)</c> with thing-site radius = <c>radius</c> and a spin
/// <c>Rotation(forward, rotation)</c> (<c>:76-80</c>). The classifier below
/// reproduces exactly that branch.</para>
/// <para><b>Colour.</b> The model path ignores <c>color</c>; the decal path
/// forwards it, but every PCore001 projectile type measures (0,0,0,0) — the
/// texture carries the colour (RGB565 sprites), and native builds the visible
/// bolt from the texture. So a textured kind resolves to a white tint (let the
/// texture show) and only a genuinely non-black <c>color</c> overrides it; a
/// box fallback keeps the amber P465b tracer so a missing asset reads distinctly
/// rather than invisibly.</para>
/// </remarks>
public readonly record struct ProjectileVisual(
    ProjectileVisualKind Kind,
    string ModelName,
    string TextureName,
    float Radius,
    float Rotation,
    float Width,
    float R,
    float G,
    float B)
{
    /// <summary>The amber tracer tint used for the box fallback (P465b
    /// <see cref="ProjectileStyle.AmberFallback"/> equivalence).</summary>
    public static readonly (float R, float G, float B) Amber = (1.0f, 0.84f, 0.32f);

    /// <summary>Classify one <see cref="DataProjectileTypeIGC"/> into its render
    /// path, exactly mirroring native's <c>modelName</c>-vs-decal branch.</summary>
    public static ProjectileVisual Resolve(DataProjectileTypeIGC p)
    {
        string model = (p.ModelName ?? "").Trim();
        string tex = (p.TextureName ?? "").Trim();

        bool hasColor = p.Color is { Length: >= 3 }
                        && (p.Color[0] != 0f || p.Color[1] != 0f || p.Color[2] != 0f);
        float cr = hasColor ? p.Color[0] : 1f;
        float cg = hasColor ? p.Color[1] : 1f;
        float cb = hasColor ? p.Color[2] : 1f;

        // Native branch 1: a real model name (first char a letter) -> 3D model.
        if (model.Length > 0 && char.IsLetter(model[0]))
            return new ProjectileVisual(
                ProjectileVisualKind.Model, model, tex,
                p.Radius, p.Rotation, p.Width, cr, cg, cb);

        // Native branch 2: decal. Directional stretches along motion with width;
        // otherwise a camera-facing billboard.
        if (p.Directional)
            return new ProjectileVisual(
                ProjectileVisualKind.Directional, "", tex,
                p.Radius, p.Rotation, p.Width, cr, cg, cb);

        // A plain decal needs a texture; without one there is nothing to draw.
        if (tex.Length == 0)
            return Box();

        return new ProjectileVisual(
            ProjectileVisualKind.Billboard, "", tex,
            p.Radius, p.Rotation, p.Width, cr, cg, cb);
    }

    /// <summary>The amber box fallback (no texture/model — the P465b tracer).</summary>
    public static ProjectileVisual Box() => new(
        ProjectileVisualKind.Box, "", "", 0f, 0f, 0f, Amber.R, Amber.G, Amber.B);

    /// <summary>
    /// Degrade to the box if the asset this visual needs failed to load, so a
    /// missing texture/model never renders the round invisible (scope rule #5,
    /// logging is the caller's job — one line per missing asset). Textured kinds
    /// need their texture; models need their mesh.
    /// </summary>
    public ProjectileVisual RequireAssets(bool modelAvailable, bool textureAvailable) => Kind switch
    {
        ProjectileVisualKind.Model => modelAvailable ? this : Box(),
        ProjectileVisualKind.Directional => textureAvailable ? this : Box(),
        ProjectileVisualKind.Billboard => textureAvailable ? this : Box(),
        _ => this,
    };

    /// <summary>The normalised width to render, honouring the type's real width
    /// where positive (the shared <see cref="ProjectileStyle"/> default).</summary>
    public float EffectiveWidth => Width > 0f ? Width : ProjectileStyle.DefaultWidth;

    public override string ToString() =>
        $"{Kind} model='{ModelName}' tex='{TextureName}' r={Radius:0.##} rot={Rotation:0.##} w={Width:0.##} tint=({R:0.##},{G:0.##},{B:0.##})";
}
