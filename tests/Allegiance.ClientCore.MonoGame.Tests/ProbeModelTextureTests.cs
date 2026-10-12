using System;
using System.IO;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P615 — the probe native-texture rule ported from native. The pre-P615
/// <see cref="ProbeRenderer"/> never decoded textures (P233 scoped them out):
/// it rendered the shared real probe <c>.mdl</c> (utl02) lit-solid with a
/// friendly/enemy side tint and <c>TextureEnabled=false</c> — a tinted flat
/// model, while native paints every probe from a real texture
/// (<c>CprobeIGC::Initialize</c> → <c>Load(0, modelName, textureName,
/// iconName, …)</c>, <c>src/Igc/probeigc.cpp:112-113</c>).
///
/// Decoded from PCore001, ALL 53 probe types carry an <em>empty</em>
/// <c>textureName</c>, so native paints each probe from its model's OWN
/// imported namespace (<c>m_pgeoMesh-&gt;FindTexture()</c>,
/// <c>thinggeo.cpp:1038-1043</c> / <c>xfile.cpp:577</c>) — the P593 rule. For
/// the port's shared canonical demo mesh <c>utl02</c> that resolves
/// <c>utl02bmp</c> (a real unit). (The P615 task brief guessed
/// "probebmp"; that is incorrect for the rendered mesh — probebmp.mdl exists
/// in the Artwork but is referenced nowhere in the native src, and no probe
/// type carries it as a textureName. The rendered probe's true native texture
/// is the model's own import, utl02bmp.)
///
/// Failing-first: these tests require <see cref="ProbeModelSource.ResolveTextureStemFor"/>
/// (absent on the pre-P615 code), so they fail on today's code and pass with the fix.
/// </summary>
public class ProbeModelTextureTests
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

    /// <summary>FAILING-FIRST — the canonical demo probe mesh (utl02, the base
    /// "Probe" shared by every deployed probe) resolves its native texture
    /// utl02bmp from its own imported namespace. Pre-P615 the ProbeRenderer
    /// drew it lit-solid.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Utl02_demo_probe_texture_resolves_from_its_own_imported_namespace()
    {
        Require.Artwork();
        var mdl = Parse("utl02");
        Assert.Equal("utl02bmp", ProbeModelSource.ResolveTextureStemFor(mdl, null, "utl02", FileExists));
    }

    /// <summary>The resolved probe texture unit actually decodes to a real,
    /// non-empty bitmap via the SAME decoder missiles use (P593b/P609) — so
    /// binding it is a genuine texture, not a degenerate unit.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Utl02bmp_texture_decodes_to_a_real_bitmap()
    {
        Require.Artwork();
        var tex = MdlTextureDecoder.Decode(File.ReadAllBytes(Path.Combine(ArtRoot, "utl02bmp.mdl")));
        Assert.True(tex.Width > 0 && tex.Height > 0, $"utl02bmp decoded to {tex.Width}x{tex.Height}");
        Assert.Equal(tex.Width * tex.Height * 4, tex.ByteCount);
    }

    /// <summary>The demo probe's texture resolves against the real artwork
    /// source (the exact path the client uses at startup), so a live client
    /// binds a real texture.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Demo_probe_texture_resolves_from_the_artwork_source()
    {
        Require.Artwork();
        var stem = ProbeModelSource.ResolveDemoTextureStem(new DirectoryAssetSource(ArtRoot));
        Assert.Equal("utl02bmp", stem);
    }

    /// <summary>EVERY distinct probe model in the core resolves its native
    /// texture (0 lit-solid fallbacks) — the widened per-probe audit, the same
    /// P593b guarantee as missiles. FAILING-FIRST: pre-P615 the probe path
    /// never resolved any texture.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Every_distinct_probe_model_resolves_mesh_and_native_texture()
    {
        Require.Artwork();
        var core = LoadCore();
        int fallback = 0;
        foreach (var mn in core.ProbeTypes.Values
                     .Where(p => p.ModelName is { Length: > 0 } n && char.IsLetter(n[0]))
                     .Select(p => p.ModelName.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mdl = Parse(mn);   // throws if the mesh is missing/undecodable
            var tex = ProbeModelSource.ResolveTextureStemFor(mdl, null, mn, FileExists);
            if (tex is null) { fallback++; Console.WriteLine($"lit-solid fallback: {mn}"); }
        }
        Assert.Equal(0, fallback);
    }

    /// <summary>
    /// P615 (C4) — the probe RENDERER actually applies its native texture (not
    /// merely that a stem resolves). This is the texture-APPLICATION contract
    /// <see cref="ProbeRenderer.AppliesTexture"/> exposes for the audit.
    /// FAILING-FIRST: pre-P615 there was no such contract — the probe drew
    /// lit-solid by design and the stem-only audit reported 0 (the blind spot).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_renderer_applies_texture_on_real_artwork()
    {
        Require.Artwork();
        Assert.True(ProbeRenderer.AppliesTexture(FileExists),
            "the probe renderer must bind its native utl02bmp texture on the real artwork");
    }

    /// <summary>
    /// P615 (C4) — the texture-application audit FLAGS an untextured
    /// non-allowlisted renderer. Here the probe's texture unit is simulated
    /// ABSENT (a fileExists predicate that rejects utl02bmp): the probe is not
    /// on <see cref="RenderModelAudit.AllowedUntextured"/> (it has a real
    /// native texture that must be applied), so the audit must report it as a
    /// lit-solid fallback rather than letting it pass. FAILING-FIRST on today's
    /// code: pre-C3 the probe always drew lit-solid and the audit reported 0.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Audit_flags_a_non_allowlisted_untextured_renderer()
    {
        Require.Artwork();

        // The probe is a native-textured model — NOT on the allowlist.
        Assert.DoesNotContain(RenderModelAudit.AllowedUntextured,
            a => a.Contains("ProbeRenderer", StringComparison.OrdinalIgnoreCase));

        // Its contract must be true on real artwork…
        Assert.True(ProbeRenderer.AppliesTexture(FileExists));

        // …and the audit would flag it as lit-solid if its texture went missing.
        Assert.False(ProbeRenderer.AppliesTexture(s => false));
    }

    /// <summary>
    /// P615 (C4) — the whole-client render-model audit (stem resolution AND the
    /// new texture-application contract) reports 0 untextured native models on
    /// the real core. FAILING-FIRST: pre-P615 the audit knew nothing of the
    /// probe's by-design-untextured renderer, so it reported 0 while probes drew
    /// lit-solid — the blind spot this task closes (P609 closed it for the
    /// hull-hit bit; P615 extends the same contract to the probe).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Whole_client_audit_including_probe_texture_application_is_zero()
    {
        Require.Artwork();
        var core = LoadCore();
        int fallbacks = RenderModelAudit.AuditCore(core, s => Console.WriteLine(s));
        Assert.Equal(0, fallbacks);
    }
}
