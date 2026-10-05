using System.Collections.Generic;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Visual;

namespace Allegiance.Core.Tests;

/// <summary>
/// P544 — the pure <see cref="ProjectileVisual.Resolve"/> mapper classifies
/// each <see cref="DataProjectileTypeIGC"/> into the four native render paths
/// (3D model / directional decal / billboard decal / box fallback) from the
/// fields native's <c>CprojectileIGC::Initialize</c> keys on
/// (<c>projectileIGC.cpp:50-80</c>). Tested against the REAL PCore001 corpus
/// (the exact types Domingo called out) plus synthetic entries and the
/// missing-asset fallback, so the real data path is exercised end-to-end.
/// </summary>
/// <remarks>
/// Native rule (<c>src/Igc/projectileIGC.cpp:50-80</c>): if
/// <c>iswalpha(modelName[0])</c> the projectile loads a 3D model at
/// <c>radius</c>; otherwise it loads a decal of <c>textureName</c> — stretched
/// along motion with <c>width</c> when <c>bDirectional</c>, else a
/// camera-facing billboard — with the thing-site radius = <c>radius</c> and a
/// spin <c>rotation</c> (rad/s). PCore001 measures colour (0,0,0,0) everywhere,
/// so native (and thus this resolver) tints textured kinds white and lets the
/// texture carry the colour; a box fallback keeps the amber P465b tracer so a
/// missing asset never renders invisible.
/// </remarks>
public class ProjectileVisualTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, "PCore001.igc"));
        return StaticCore.Load(file);
    }

    private static DataProjectileTypeIGC Pt(
        string tex, string model = "", bool directional = false,
        float radius = 1f, float rotation = 0f, float width = 0f,
        float[]? color = null)
        => new(
            Color: color ?? [0f, 0f, 0f, 0f],
            Radius: radius,
            Rotation: rotation,
            ModelName: model,
            TextureName: tex,
            Power: 0f, BlastPower: 0f, BlastRadius: 0f,
            Speed: 1000f, Lifespan: 1f,
            ProjectileTypeId: 555, DamageType: 0,
            AbsoluteF: false, Directional: directional,
            Width: width, AmbientSound: -1);

    /// <summary>PE Gat Gun 1 (pid 1, PCore001) — billboard f99, radius 1,
    /// spin 3.84 rad/s.</summary>
    [Fact]
    public void GatGun_is_a_spinning_billboard_f99()
    {
        var v = ProjectileVisual.Resolve(Core().ProjectileTypes[1]);
        Assert.Equal(ProjectileVisualKind.Billboard, v.Kind);
        Assert.Equal("f99", v.TextureName);
        Assert.Equal(1f, v.Radius);
        Assert.Equal(3.84f, v.Rotation, 3);
        Assert.Equal(1f, v.R);   // white tint — the texture carries the colour
    }

    /// <summary>EW Pulse Laser (pid 66, PCore001) — directional fx95, width 0.1,
    /// radius 15.</summary>
    [Fact]
    public void PulseLaser_is_a_directional_fx95_decal()
    {
        var v = ProjectileVisual.Resolve(Core().ProjectileTypes[66]);
        Assert.Equal(ProjectileVisualKind.Directional, v.Kind);
        Assert.Equal("fx95", v.TextureName);
        Assert.Equal(0.1f, v.Width);
        Assert.Equal(15f, v.Radius);
    }

    /// <summary>EW Lsr Can 1 (pid 75, PCore001) — a 3D model named
    /// dn_ptlascan1 at radius 125.</summary>
    [Fact]
    public void LsrCan_is_a_3d_model()
    {
        var v = ProjectileVisual.Resolve(Core().ProjectileTypes[75]);
        Assert.Equal(ProjectileVisualKind.Model, v.Kind);
        Assert.Equal("dn_ptlascan1", v.ModelName);
        Assert.Equal(125f, v.Radius);
    }

    /// <summary>ER Nanite 1 (pid 58, PCore001) — billboard f44 (Domingo's
    /// "donut"), radius 1, spinning.</summary>
    [Fact]
    public void Nanite_is_a_spinning_billboard_f44()
    {
        var v = ProjectileVisual.Resolve(Core().ProjectileTypes[58]);
        Assert.Equal(ProjectileVisualKind.Billboard, v.Kind);
        Assert.Equal("f44", v.TextureName);
        Assert.Equal(1f, v.Radius);
        Assert.Equal(3.84f, v.Rotation, 3);
    }

    /// <summary>A billboard whose texture is a non-black colour keeps that tint
    /// (the mechanism a real core exercises once it populates colour).</summary>
    [Fact]
    public void Real_nonblack_colour_tints_the_sprite()
    {
        var v = ProjectileVisual.Resolve(Pt("f99", color: [0.2f, 0.9f, 0.4f, 1f]));
        Assert.Equal(ProjectileVisualKind.Billboard, v.Kind);
        Assert.Equal(0.2f, v.R, 3);
        Assert.Equal(0.9f, v.G, 3);
        Assert.Equal(0.4f, v.B, 3);
    }

    /// <summary>Missing-asset fallback: a billboard whose texture fails to load
    /// (RequireAssets with textureAvailable=false) degrades to the box so the
    /// round never renders invisible, per the brief's scope rule #5.</summary>
    [Fact]
    public void Missing_asset_falls_back_to_box()
    {
        var ideal = ProjectileVisual.Resolve(Pt("f99"));
        Assert.Equal(ProjectileVisualKind.Billboard, ideal.Kind);
        var boxed = ideal.RequireAssets(modelAvailable: false, textureAvailable: false);
        Assert.Equal(ProjectileVisualKind.Box, boxed.Kind);
        // The box keeps the amber P465b tracer look (distinguishable, not black).
        Assert.Equal(1.0f, boxed.R);
        Assert.Equal(0.84f, boxed.G);
        Assert.Equal(0.32f, boxed.B);
    }

    /// <summary>A model whose mesh fails to load also falls back to the box.</summary>
    [Fact]
    public void Missing_model_falls_back_to_box()
    {
        var ideal = ProjectileVisual.Resolve(Pt("", model: "dn_ptlascan1"));
        Assert.Equal(ProjectileVisualKind.Model, ideal.Kind);
        Assert.Equal(ProjectileVisualKind.Box, ideal.RequireAssets(false, true).Kind);
    }

    /// <summary>An unresolvable type (no model and no texture) resolves straight
    /// to the box — native would LoadDecal("") and render nothing, our scope keeps
    /// the box so it stays visible.</summary>
    [Fact]
    public void No_model_no_texture_resolves_to_box()
    {
        var v = ProjectileVisual.Resolve(Pt(""));
        Assert.Equal(ProjectileVisualKind.Box, v.Kind);
    }
}
