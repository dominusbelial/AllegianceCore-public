using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P547 — the runtime custom-shader loader. Compiles a committed
/// <c>.ogl.mgfxo</c> byte blob into a live <see cref="Effect"/> via
/// <c>new Effect(gd, bytes)</c> (MonoGame's raw-bytes loader — no content
/// pipeline, no Wine needed to build OR run; the bytecode is committed and
/// shipped). Results are <b>cached</b> per content path (including a null
/// result, so a missing/corrupt shader is attempted once, not every frame),
/// and a failure is <b>logged exactly once</b> then returns null so callers
/// fall back (soft particles → near-fade; bloom → off). Never throws.
/// </summary>
/// <remarks>
/// The actual <see cref="Effect"/> construction is behind a
/// <see cref="Func{TResult}"/> (<paramref name="compile"/>) so the cache /
/// log-once / null-fallback behaviour is unit-testable headless without a
/// <see cref="GraphicsDevice"/>: tests inject a compile delegate that throws
/// for corrupt bytes or a stream opener that throws for a missing file, and
/// assert null + a single logged failure.
/// </remarks>
public sealed class ShaderAssetCache : IDisposable
{
    private readonly Func<byte[], Effect?> _compile;
    private readonly Dictionary<string, Effect?> _cache = new();
    private readonly HashSet<string> _logged = new();

    /// <param name="compile">Turns raw <c>.mgfxo</c> bytes into an
    /// <see cref="Effect"/>. Production: <c>bytes =&gt; new Effect(gd, bytes)</c>.</param>
    public ShaderAssetCache(Func<byte[], Effect?> compile)
    {
        _compile = compile;
    }

    /// <summary>Build a cache bound to a real <see cref="GraphicsDevice"/>.</summary>
    public static ShaderAssetCache ForDevice(GraphicsDevice gd) => new(bytes => new Effect(gd, bytes));

    /// <summary>
    /// Load (once) and cache the shader at the TitleContainer-relative
    /// <paramref name="contentPath"/> (e.g. <c>Content/Shaders/Bloom.ogl.mgfxo</c>).
    /// Returns the effect, or null if the file is missing/unreadable or the
    /// compile fails — logging the failure once and caching the null so
    /// callers degrade gracefully (soft → near-fade, bloom → off).
    /// </summary>
    public Effect? Load(string contentPath)
        => LoadFrom(contentPath, () => TitleContainer.OpenStream(contentPath));

    /// <summary>Same as <see cref="Load(string)"/> but with an explicit stream
    /// opener (used by tests to simulate a missing/corrupt source).</summary>
    public Effect? LoadFrom(string contentPath, Func<Stream> open)
    {
        if (_cache.TryGetValue(contentPath, out var cached)) return cached;

        Effect? effect = null;
        try
        {
            using var s = open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            effect = _compile(ms.ToArray());
        }
        catch (Exception ex)
        {
            if (_logged.Add(contentPath))
                Console.WriteLine($"[monogame] shader '{contentPath}' unavailable: {ex.Message} — degraded");
        }
        _cache[contentPath] = effect;
        return effect;
    }

    /// <summary>Number of distinct content paths that failed to load (for the
    /// "logs exactly once" unit test).</summary>
    public int LoggedFailureCount => _logged.Count;

    /// <summary>Whether <paramref name="contentPath"/> was ever attempted
    /// (whether or not it succeeded).</summary>
    public bool WasAttempted(string contentPath) => _cache.ContainsKey(contentPath);

    public void Dispose()
    {
        foreach (var e in _cache.Values)
            if (e is not null) e.Dispose();
        _cache.Clear();
    }
}
