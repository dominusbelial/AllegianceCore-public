using System;
using System.IO;
using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P593 — the 3D-model projectile texture-resolution rule (and size rule) ported
/// from native. The recorded "white popcorn" build rendered 3D-model projectile
/// rounds as large untextured white faceted chunks because the round's native
/// texture was not applied — the renderer's naive hardcoded
/// <c>&lt;modelName&gt;bmp</c> lookup (a case-sensitive, name-guessing lookup) fell
/// back to lit-solid white whenever the real texture unit is named differently
/// (a shared texture namespace, a doubled-'b', or a capitalised on-disk file).
/// ProjectileModelRenderer.TextureFor now resolves the texture from the model's
/// OWN imported texture namespace via
/// <see cref="NativeShipResolver.ResolveTextureStem"/> (native
/// <c>m_pgeoMesh-&gt;FindTexture()</c>, <c>thinggeo.cpp:1038-1043</c> /
/// <c>xfile.cpp:577</c>) — the P586b rule — so a round never renders lit-solid.
///
/// Failing-first: these tests encode requirements the old naive
/// <c>&lt;modelName&gt;bmp</c> lookup violated (a shared namespace, and the
/// model's real imported namespace over the name guess), so they fail on the
/// pre-P593 code and pass with the native resolution.
/// </summary>
public class ProjectileModelTextureTests
{
    private static string ArtRoot =>
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static bool FileExists(string stem) => File.Exists(Path.Combine(ArtRoot, stem + ".mdl"));

    // P593 — the renderer's texture probe is the case-insensitive disk-stem index
    // (ProjectileModelRenderer._diskStems, StringComparer.OrdinalIgnoreCase), not a
    // case-sensitive File.Exists — a capitalised on-disk unit like "Wep07bbmp.mdl"
    // is still found from the lowercase import. Mirror that here.
    private static bool FileExistsCI(string stem)
    {
        foreach (var f in Directory.GetFiles(ArtRoot, "*.mdl"))
            if (string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static MdlFile Parse(string stem) => MdlFile.Parse(Path.Combine(ArtRoot, stem + ".mdl"));

    /// <summary>EW Lsr Can 1 (pid 75) — its texture resolves from the model's own
    /// imported namespace <c>dn_ptlascan1bmp</c>, which is exactly what
    /// ProjectileModelRenderer.TextureFor now looks up.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void LsrCan1_texture_resolves_from_its_own_imported_namespace()
    {
        Require.Artwork();
        var mdl = Parse("dn_ptlascan1");
        Assert.Equal("dn_ptlascan1bmp", ProjectileModelRenderer.ResolveTextureStemFor(mdl, "dn_ptlascan1", FileExists));
    }

    /// <summary>PE MassDriver (pid 71) — the slug's texture resolves from its own
    /// imported namespace <c>bgrnd55bmp</c> (native: the model's own bitmap), not
    /// a guess.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MassDriver_texture_resolves_from_its_own_imported_namespace()
    {
        Require.Artwork();
        var mdl = Parse("bgrnd55");
        Assert.Equal("bgrnd55bmp", ProjectileModelRenderer.ResolveTextureStemFor(mdl, "bgrnd55", FileExists));
    }

    /// <summary>FAILING-FIRST — the native rule resolves a model's texture from its
    /// REAL imported (possibly shared) namespace, never a <c>&lt;modelName&gt;bmp</c>
    /// guess. <c>utl18a</c> imports the shared <c>utl18bmp</c>; the naive guess
    /// <c>utl18abmp</c> does not exist, so the OLD naive lookup would render
    /// lit-solid white. The native resolution must return <c>utl18bmp</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void TextureRule_resolves_shared_namespace_not_naive_namebmp_guess()
    {
        Require.Artwork();
        var mdl = Parse("utl18a");                       // imports the shared "utl18bmp"
        Assert.NotEqual("utl18abmp", ProjectileModelRenderer.ResolveTextureStemFor(mdl, "utl18a", FileExists));
        Assert.Equal("utl18bmp", ProjectileModelRenderer.ResolveTextureStemFor(mdl, "utl18a", FileExists));
    }

    /// <summary>FAILING-FIRST — a weapon-style projectile model whose on-disk
    /// texture is capitalised must still resolve (the P586b case lesson): the
    /// lowercase-stem probe finds <c>Wep07bbmp.mdl</c> from the model's lowercase
    /// import <c>wep07bbmp</c>. The old naive lookup (case-sensitive
    /// <c>File.Exists</c> on Linux) would miss it → lit-solid white.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void TextureRule_resolves_capitalised_on_disk_unit_case_insensitively()
    {
        Require.Artwork();
        // The EW Disruptor's gun model imports "wep07bbmp"; the on-disk unit is the
        // capitalised "Wep07bbmp.mdl". ResolveTextureStem returns the bare import
        // stem, and the P593 case-insensitive disk index is what maps it to the
        // capitalised file — the missing link the naive <name>bmp lookup lacked.
        var mdl = Parse("wep07");
        Assert.Equal("wep07bbmp", ProjectileModelRenderer.ResolveTextureStemFor(mdl, "wep07", FileExistsCI));
    }

    /// <summary>SIZE RULE (native fidelity) — the beam round renders at its
    /// authored native size; no uniform rescale is applied. The Lsr Can beam's
    /// half-length about its centre equals its projectile radius (125), matching
    /// native's <c>SetRadius(radius)</c> = thing half-length
    /// (<c>projectileIGC.cpp:50-56</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void LsrCan1_mesh_half_length_matches_its_native_radius()
    {
        Require.Artwork();
        var mdl = Parse("dn_ptlascan1");
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var mesh in mdl.Meshes)
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                float z = mesh.Positions[3 * v + 2];
                minZ = Math.Min(minZ, z);
                maxZ = Math.Max(maxZ, z);
            }
        float halfLength = (maxZ - minZ) / 2f;   // ~125: the beam spans ~0..250, centre ~125
        Assert.True(Math.Abs(halfLength - 125f) <= 2f,
            $"Lsr Can beam half-length {halfLength} should equal its native radius 125 (no uniform rescale)");
    }

    /// <summary>SIZE RULE — the MassDriver slug's native mesh half-extent (~5) is
    /// far smaller than the laser-beam rounds; it renders at its own authored
    /// size, and native's radius (20) is the collision thing radius, not a visual
    /// rescale. Guards the "just oversized?" suspicion: the model is NOT scaled up
    /// to radius; it stays at its authored extent (native behaviour).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MassDriver_mesh_authored_extent_is_not_rescaled_to_radius()
    {
        Require.Artwork();
        var mdl = Parse("bgrnd55");
        float maxAbs = 0f;
        foreach (var mesh in mdl.Meshes)
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                float x = mesh.Positions[3 * v], y = mesh.Positions[3 * v + 1], z = mesh.Positions[3 * v + 2];
                maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))));
            }
        // Authored half-extent ~5, NOT rescaled up to the radius-20 collision size.
        Assert.True(maxAbs >= 4f && maxAbs <= 6f,
            $"MassDriver authored half-extent {maxAbs} should be its native ~5 (no rescale to radius 20)");
    }
}
