using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P593b — the widened render-model audit: at startup (and in a test) resolve
/// the mesh + native texture of EVERY 3D model any renderer can draw for
/// projectiles, missiles and probes across the WHOLE static core, plus the
/// mine/chaff sprite textures — and report any that would render lit-solid.
/// This extends P593's 4-model projectile audit to the full renderable model
/// set, so no civ's weapon/missile/loot model can silently fall back to the
/// untextured "white shard / white popcorn" look.
///
/// <para>A model counts as a lit-solid fallback when its <c>.mdl</c> fails to
/// parse (no geometry) or its native texture stem cannot be resolved. Both
/// cases would leave the renderer drawing the object lit-solid instead of
/// textured. Pure (no graphics device): it exercises the SAME texture-stem
/// resolution the renderers use
/// (<see cref="NativeShipResolver.ResolveTextureStem"/>), so it runs headless
/// in the unit suite.</para>
///
/// <para>Covered renderers: <see cref="ProjectileModelRenderer"/> (3D-model
/// projectile rounds), <see cref="NativeMissileRenderer"/> (every missile
/// type's own native model — the P593b fix), probe models, the mine sprite
/// (<see cref="MineModelSource.SpriteStem"/>) and the chaff sprites
/// (<see cref="ChaffModelSource.RealStems"/>). Treasure model resolution is
/// audited separately by <see cref="TreasureVisualResolver.SelfCheck"/>.</para>
///
/// <para>Case sensitivity: the on-disk stem probe is a case-insensitive index
/// of every <c>.mdl</c> basename (the P593/P586b lesson — a capitalised on-disk
/// unit must still resolve), and the file opened for parsing is the exact
/// on-disk casing.</para>
/// </summary>
public static class RenderModelAudit
{
    /// <summary>Run the audit over the artwork root (the resolved
    /// <see cref="AssetSources.ResolveRoot"/>). Returns the number of lit-solid
    /// fallbacks (0 on the real 221 core), logging each by model name.</summary>
    public static int AuditCore(StaticCore core, Action<string> log)
    {
        string root = AssetSources.ResolveRoot();
        var disk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(root, "*.mdl"))
            disk[Path.GetFileNameWithoutExtension(f)] = Path.GetFileNameWithoutExtension(f);

        int fallback = 0;
        fallback += AuditModels("P593 projectile",
            EnumerateModelNames(core.ProjectileTypes.Values.Select(p => p.ModelName)), disk,
            (mdl, stem) => ProjectileModelRenderer.ResolveTextureStemFor(mdl, stem, s => disk.ContainsKey(s)), log);

        fallback += AuditModels("P593b missile",
            EnumerateModelNames(core.MissileTypes.Values.Select(m => m.ModelName)), disk,
            (mdl, stem) => NativeMissileRenderer.ResolveTextureStemFor(mdl, null, stem, s => disk.ContainsKey(s)), log);

        fallback += AuditModels("probe",
            EnumerateModelNames(core.ProbeTypes.Values.Select(p => p.ModelName)), disk,
            (mdl, stem) => NativeMissileRenderer.ResolveTextureStemFor(mdl, null, stem, s => disk.ContainsKey(s)), log);

        fallback += AuditSprites("mine", new[] { MineModelSource.SpriteStem }, disk, log);
        fallback += AuditSprites("chaff", ChaffModelSource.RealStems, disk, log);
        return fallback;
    }

    /// <summary>Distinct, non-blank, alpha-leading model names (the model-loader
    /// branch, native <c>missileigc.cpp:49</c>), trimmed and case-folded.</summary>
    private static IEnumerable<string> EnumerateModelNames(IEnumerable<string?> raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in raw)
        {
            var n = name?.Trim();
            if (string.IsNullOrEmpty(n) || !char.IsLetter(n[0])) continue;
            if (seen.Add(n)) yield return n;
        }
    }

    private static int AuditModels(string label, IEnumerable<string> modelNames,
        Dictionary<string, string> disk, Func<MdlFile, string, string?> resolveTexture, Action<string> log)
    {
        int fallback = 0;
        foreach (var stem in modelNames)
        {
            MdlFile? mdl = null;
            try
            {
                if (disk.TryGetValue(stem, out var exact))
                    mdl = MdlFile.Parse(AssetSources.ReadAllBytes(AssetSources.Artwork, exact + ".mdl"));
            }
            catch { /* missing/undecodable → counted below */ }
            var tex = mdl is null ? null : resolveTexture(mdl, stem);
            if (mdl is null || tex is null)
            {
                fallback++;
                log($"[monogame] P593b {label} model '{stem}': mesh={(mdl is null ? "MISSING" : "ok")} texture={(tex is null ? "LIT-SOLID" : "ok")}");
            }
        }
        return fallback;
    }

    private static int AuditSprites(string label, IEnumerable<string> stems,
        Dictionary<string, string> disk, Action<string> log)
    {
        int fallback = 0;
        foreach (var stem in stems)
        {
            if (!disk.ContainsKey(stem))
            {
                fallback++;
                log($"[monogame] P593b {label} sprite '{stem}bmp': MISSING");
            }
        }
        return fallback;
    }
}
