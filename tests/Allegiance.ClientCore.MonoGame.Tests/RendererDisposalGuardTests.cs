using System.Reflection;
using System.Text.RegularExpressions;
using G1 = Allegiance.ClientCore.MonoGame.Game1;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P646 (review finding L1) — guard that every disposable RENDERER field of
/// <see cref="Game1"/> is disposed in <c>UnloadContent</c>. The review found two
/// renderers (<c>_nativeShipRenderer</c>, <c>_nativeTreasureRenderer</c> — both
/// owning GPU buffers, textures and effects) that were never disposed. Rather
/// than rely on a future author remembering, this reflects over Game1's
/// private IDisposable renderer fields and asserts each is in the
/// <see cref="DisposedOnUnload"/> set (the renderer fields <c>UnloadContent</c>
/// actually disposes). Adding a new renderer field without registering it here
/// (and disposing it in UnloadContent) FAILS this test instead of leaking
/// silently.
/// </summary>
public class RendererDisposalGuardTests
{
    /// <summary>Renderer/media fields <c>Game1.UnloadContent</c> disposes.
    /// Kept in sync with UnloadContent; the guard below is what forces that
    /// sync. Entries named for each *.Dispose() call on a renderer field there.</summary>
    private static readonly HashSet<string> DisposedOnUnload = new(StringComparer.Ordinal)
    {
        "_nativeAsteroidRenderer",
        "_modernHudRenderer",        // HUD-2: owned distance-font raster caches
        "_nativeStationRenderer",
        "_nativeShipRenderer",        // P646 — was never disposed
        "_nativeTreasureRenderer",    // P646 — was never disposed
        "_stationRenderer",           // P646 — was never disposed
        "_buildingEffectRenderer",    // P646 — was never disposed
        "_treasureRenderer",          // P646 — was never disposed
        "_chaffRenderer",             // P646 — was never disposed
        "_damageDebrisRenderer",      // P646 — was never disposed
        "_commandGridRenderer",       // P640 — disposed by UnloadContent
        "_debrisRenderer",            // P646 — was never disposed
        "_engineTrailRenderer",       // P646 — was never disposed
        "_nozzleGlowRenderer",        // P646 — was never disposed
        "_navStrobeRenderer",         // P646 — was never disposed
        "_motionTrailRenderer",       // P646 — was never disposed
        "_alephGlimpseRenderer",      // P646 — was never disposed
        "_targetModelPreviewRenderer", // HUD-21 — the 256×256 target-model preview RT
        "_warpRenderer",
        "_projectileRenderer",
        "_projectileSpriteRenderer",
        "_projectileModelRenderer",
        "_missileRenderer",
        "_nativeMissileRenderer",
        "_probeRenderer",
        "_mineRenderer",
        "_trailRenderer",
        "_spoofRetargetRenderer",
        "_warpParticleRenderer",
        "_explosionRenderer",
        "_hullHitSparkRenderer",
        "_shieldFlareRenderer",
        "_enhancedExplosionRenderer",
        "_volumetricRenderer",
        "_displacedRenderer",
        "_post",
        "_shaders",
        "_nativeEffects",
        "_shipLodSpriteRenderer",   // P628 — far-tier sprite batcher
    };

    [Fact]
    public void Every_disposable_renderer_field_is_disposed_in_UnloadContent()
    {
        var type = typeof(G1);
        var rendererFields = type
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => typeof(IDisposable).IsAssignableFrom(f.FieldType)
                        && f.Name.Contains("Renderer", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // The two P646 offenders must be disposed (present in the set).
        Assert.Contains("_nativeShipRenderer", DisposedOnUnload);
        Assert.Contains("_nativeTreasureRenderer", DisposedOnUnload);

        // Every disposable renderer field must be disposed — none may be added
        // silently. (Collections/arrays like _asteroidRenderers are iterated and
        // disposed; the guard's name filter still catches them via plural "s".)
        var missing = rendererFields.Where(n => !DisposedOnUnload.Contains(n)).ToList();
        Assert.True(missing.Count == 0,
            "Renderer fields not disposed in UnloadContent: " + string.Join(", ", missing));

        // P642b: the registry alone can pass even if a Dispose call is removed.
        // Verify the actual method too, following the other source guards.
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game", "Game1.cs")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        string source = File.ReadAllText(Path.Combine(root, "src", "Allegiance.ClientCore.Game", "Game1.cs"));
        source = Regex.Replace(source, @"//[^\r\n]*|/\*[\s\S]*?\*/", "");
        int start = source.IndexOf("protected override void UnloadContent()", StringComparison.Ordinal);
        Assert.True(start >= 0, "UnloadContent must exist");
        int end = source.IndexOf("base.UnloadContent();", start, StringComparison.Ordinal);
        Assert.True(end > start, "UnloadContent must call its base implementation");
        string unload = source[start..end];
        foreach (string field in rendererFields)
            Assert.Matches($@"\b{Regex.Escape(field)}\s*\?\s*\.\s*Dispose\s*\(\s*\)", unload);
    }
}
