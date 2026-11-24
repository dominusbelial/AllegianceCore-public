namespace Allegiance.ClientCore.MonoGame;

/// <summary>Explicit proof opt-in, independent of persisted desktop visual preferences.</summary>
public static class BrowserShaderOptions
{
    public static bool Volumetric(string? query, bool developerEnabled)
    {
        if (!developerEnabled || string.IsNullOrEmpty(query)) return false;
        // Require one unambiguous, exact flag. Encoded or duplicate flags do not opt in.
        var flags = query.TrimStart('?').Split('&').Where(p => p.StartsWith("volumetric=", StringComparison.Ordinal)).ToArray();
        return flags is ["volumetric=1"];
    }
}
