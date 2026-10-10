using System;
using System.IO;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P593b — the missile native-texture-resolution rule ported from native. The
/// port's pre-P593b missile path rendered EVERY non-content missile type with a
/// single shared <c>mis05</c> mesh lit-solid (a fixed grey diffuse) — so of the
/// 56 missile types in PCore001, the 45 whose <c>ModelName</c> is not one of the
/// three P287-imported content assets (<c>mis05</c>/<c>mis400</c>/<c>artifact</c>)
/// rendered as light-grey faceted solids: the "white shards" seen in combat.
/// Native loads every model missile with its own real mesh + texture:
/// <c>CmissileIGC::Initialize</c> → <c>Load(0, modelName, textureName, iconName,
/// …)</c> (<c>src/Igc/missileigc.cpp:49-51</c>).
///
/// <see cref="NativeMissileRenderer"/> now resolves each missile type's own
/// <c>.mdl</c> and its native texture from the model's imported namespace via
/// <see cref="NativeShipResolver.ResolveTextureStem"/> (native
/// <c>m_pgeoMesh-&gt;FindTexture()</c>, <c>thinggeo.cpp:1038-1043</c> /
/// <c>xfile.cpp:577</c>) — the P593 rule — so no missile model ever renders
/// lit-solid.
///
/// Failing-first: these tests require <see cref="NativeMissileRenderer"/> /
/// <see cref="RenderModelAudit"/> (they do not exist on the pre-P593b code), so
/// they fail on today's code and pass with the fix — exactly the P593
/// demonstration for the projectile-round texture bug.
/// </summary>
public class MissileModelTextureTests
{
    private static string ArtRoot =>
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static bool FileExists(string stem) => File.Exists(Path.Combine(ArtRoot, stem + ".mdl"));

    private static MdlFile Parse(string stem) => MdlFile.Parse(Path.Combine(ArtRoot, stem + ".mdl"));

    private static StaticCore LoadCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        return StaticCore.Load(file);
    }

    /// <summary>FAILING-FIRST — the MRM Seeker family (mis06, types 7/35/36/55-57),
    /// which the pre-P593b port rendered lit-solid, resolves its native texture
    /// <c>mis06bmp</c> from its own imported namespace.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mis06_seeker_texture_resolves_from_its_own_imported_namespace()
    {
        Require.Artwork();
        var mdl = Parse("mis06");
        Assert.Equal("mis06bmp", NativeMissileRenderer.ResolveTextureStemFor(mdl, null, "mis06", FileExists));
    }

    /// <summary>The MRM Quickfire family (mis08, types 25/45/46/58-60) — the exact
    /// missile family used in the P593b reproduction — resolves <c>mis08bmp</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mis08_quickfire_texture_resolves_from_its_own_imported_namespace()
    {
        Require.Artwork();
        var mdl = Parse("mis08");
        Assert.Equal("mis08bmp", NativeMissileRenderer.ResolveTextureStemFor(mdl, null, "mis08", FileExists));
    }

    /// <summary>The Kinetic Torpedo (mis404, type 77) resolves <c>mis404bmp</c> — and
    /// its mesh is Y-forward, so it needs the nose-alignment prerotation to fly
    /// nose-first (measured: longest axis Y).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mis404_kinetic_torpedo_texture_resolves_and_is_y_forward()
    {
        Require.Artwork();
        var mdl = Parse("mis404");
        Assert.Equal("mis404bmp", NativeMissileRenderer.ResolveTextureStemFor(mdl, null, "mis404", FileExists));

        float minY = float.MaxValue, maxY = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var mesh in mdl.Meshes)
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                minY = Math.Min(minY, mesh.Positions[3 * v + 1]); maxY = Math.Max(maxY, mesh.Positions[3 * v + 1]);
                minZ = Math.Min(minZ, mesh.Positions[3 * v + 2]); maxZ = Math.Max(maxZ, mesh.Positions[3 * v + 2]);
            }
        Assert.True((maxY - minY) > (maxZ - minZ),
            $"mis404 nose should be along +Y (Y-extent {maxY - minY} > Z-extent {maxZ - minZ})");
    }

    /// <summary>EVERY distinct missile model in the core resolves its native
    /// texture (0 lit-solid fallbacks) — the widened per-type missile audit.
    /// FAILING-FIRST: the pre-P593b code has no such per-type resolution (all
    /// non-content missiles fell back to the shared mis05 lit-solid demo).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Every_distinct_missile_model_resolves_mesh_and_native_texture()
    {
        Require.Artwork();
        var core = LoadCore();
        int fallback = 0;
        foreach (var mn in core.MissileTypes.Values
                     .Where(m => m.ModelName is { Length: > 0 } n && char.IsLetter(n[0]))
                     .Select(m => m.ModelName.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mdl = Parse(mn);   // throws if the mesh is missing/undecodable
            var tex = NativeMissileRenderer.ResolveTextureStemFor(mdl, null, mn, FileExists);
            if (tex is null) { fallback++; Console.WriteLine($"lit-solid fallback: {mn}"); }
        }
        Assert.Equal(0, fallback);
    }

    /// <summary>The widened render-model audit — every 3D model any renderer can
    /// draw for projectiles, missiles and probes across the WHOLE core, plus the
    /// mine/chaff sprites — has 0 lit-solid fallbacks (the P593b guarantee).
    /// FAILING-FIRST: pre-P593b, 45/56 missile models render lit-solid.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Widened_render_model_audit_over_whole_core_is_zero()
    {
        Require.Artwork();
        var core = LoadCore();
        int fallbacks = RenderModelAudit.AuditCore(core, s => Console.WriteLine(s));
        Assert.Equal(0, fallbacks);
    }
}
