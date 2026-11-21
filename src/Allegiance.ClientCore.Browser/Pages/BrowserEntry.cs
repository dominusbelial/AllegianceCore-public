namespace Allegiance.ClientCore.Browser.Pages;

/// <summary>The entry flags the browser client reads from its own URL: the
/// <c>?online=1</c> round-trip marker and the <c>?server=</c> preselection.</summary>
public readonly record struct BrowserEntryState(bool Online, string? Server);

/// <summary>
/// P1062 — the browser client's URL contract for the lobby's "Play in browser"
/// actions and the sign-in round trip. Deliberately pure string work (no Blazor or
/// HTTP types) so the same rule can be unit-tested without a browser runtime.
///
/// Inbound: <see cref="Parse"/> accepts only <c>online=1</c> and a short
/// alphanumeric listing id; every other parameter is ignored.
/// Outbound: <see cref="ReturnTarget"/> always yields a same-origin, path-only
/// target under the client's own path with the entry flags, and
/// <see cref="SignInHref"/> is the identity page that carries it. The lobby
/// re-validates that target with its own closed rule before redirecting, so a
/// hand-edited URL cannot send the player anywhere else.
/// </summary>
public static class BrowserEntry
{
    /// <summary>Parses a query string (with or without the leading <c>?</c>).</summary>
    public static BrowserEntryState Parse(string? query)
    {
        bool online = false;
        string? server = null;
        if (string.IsNullOrEmpty(query)) return new(online, server);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            string name = separator < 0 ? pair : pair[..separator];
            string value = separator < 0 ? "" : pair[(separator + 1)..];
            if (name == "online") online = value == "1";
            else if (name == "server" && IsServerId(value)) server = value;
        }
        return new(online, server);
    }

    /// <summary>A listing id is a short token (the directory ids are GUIDs); anything
    /// else is ignored rather than carried into the join screen.</summary>
    private static bool IsServerId(string value) => value.Length is > 0 and <= 64
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>The return target this client hands the identity page: its own path
    /// (same origin, path-only) plus <c>online=1</c> and the preselected listing id.</summary>
    public static string ReturnTarget(string? pathAndQuery, string? serverId = null)
    {
        string path = "/";
        int query = pathAndQuery?.IndexOf('?') ?? -1;
        string candidate = string.IsNullOrEmpty(pathAndQuery) ? "" : query < 0 ? pathAndQuery : pathAndQuery[..query];
        if (candidate.Length > 1 && candidate[0] == '/' && !candidate.Contains("//", StringComparison.Ordinal)
            && !candidate.Contains('\\') && !candidate.Contains('%') && !candidate.Contains('#'))
            path = candidate;
        string target = path + "?online=1";
        return IsServerId(serverId ?? "") ? target + "&server=" + Uri.EscapeDataString(serverId!) : target;
    }

    /// <summary>The lobby identity page that carries the return target back here.</summary>
    public static string SignInHref(string returnTarget) => "/identity.html?return=" + Uri.EscapeDataString(returnTarget);
}
