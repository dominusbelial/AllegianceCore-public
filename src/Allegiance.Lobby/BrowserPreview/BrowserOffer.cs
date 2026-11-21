namespace Allegiance.Lobby.BrowserPreview;

/// <summary>P1062 — what the lobby's own pages may say about the browser build.
/// Static configuration only: <c>Enabled</c>, the served <c>Prefix</c>, and the
/// caller's <c>return</c> target after it passed <see cref="BrowserReturnTarget"/>.
/// No secret, no origin, no token.</summary>
public sealed record BrowserOffer(bool Enabled, string Prefix, string? ReturnTarget);

/// <summary>
/// P1062 — one closed rule for the browser client's sign-in round trip. The
/// identity page goes back to the browser game only through a target built here:
/// a same-origin, path-only path under the configured browser prefix
/// (the production <c>/play/…</c>). Absolute URLs, protocol-relative
/// <c>//host</c> forms, schemes, backslashes, percent-encoded variants, fragments,
/// dot segments, whitespace/control characters and an out-of-prefix path are all
/// refused — a refused target is reported as <c>null</c>, never repaired.
/// The caller then shows the ordinary browser entry instead of redirecting.
/// </summary>
public static class BrowserReturnTarget
{
    /// <summary>Upper bound of a target the page will ever build; anything longer is refused.</summary>
    public const int MaxLength = 2_048;

    /// <summary>Returns the target unchanged when it is one allowlisted, path-only
    /// location under <paramref name="prefix"/>; otherwise <c>null</c>.</summary>
    public static string? Normalize(string? raw, string prefix)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxLength) return null;
        // The browser client is the only allowed destination. Ordinal: a path is
        // case-sensitive, and the prefix was validated at startup.
        if (!raw.StartsWith(prefix + "/", StringComparison.Ordinal)) return null;
        foreach (char c in raw)
        {
            // Visible ASCII only: whitespace, controls, a backslash, a fragment
            // and any percent escape (a second decoding layer) are refused.
            if (c < 0x21 || c == 0x7f || c is '\\' or '%' or '#') return null;
        }
        int query = raw.IndexOf('?');
        string path = query < 0 ? raw : raw[..query];
        if (path.Contains("//", StringComparison.Ordinal)) return null;
        foreach (string segment in path.Split('/'))
            if (segment is "." or "..") return null;
        // A bare '?' would be an empty query the browser client never generates.
        if (query >= 0 && raw.Length == query + 1) return null;
        return raw;
    }

    /// <summary>The identity page an external-provider callback returns to. The
    /// caller's target is appended only after <see cref="Normalize"/>; a refused
    /// value leaves the plain page, exactly like the page's own fallback.</summary>
    public static string IdentityPage(string? rawReturn, string prefix)
    {
        var target = Normalize(rawReturn, prefix);
        return target is null ? "/identity.html" : "/identity.html?return=" + Uri.EscapeDataString(target);
    }
}
