namespace Allegiance.Core.Identity;

/// <summary>Operator classification for analytics and external seat orchestration.
/// Exact or trailing-* prefix, case-insensitive, as adminCheats.allowNames.
/// Names grant no authority and do not change native admission.</summary>
public static class BotNameRules
{
    public static string[] Defaults() => ["mcp-*", "research-cmd*"];
    public static bool Matches(string pattern, string name) => !string.IsNullOrEmpty(pattern) && !string.IsNullOrEmpty(name)
        && (pattern.Length > 1 && pattern[^1] == '*'
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : pattern.Equals(name, StringComparison.OrdinalIgnoreCase));
    public static bool IsBot(IEnumerable<string> patterns, string name) => patterns.Any(p => Matches(p, name));
}
