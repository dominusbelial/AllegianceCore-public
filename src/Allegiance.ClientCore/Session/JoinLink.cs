using System.Net;

namespace Allegiance.ClientCore.Session;

/// <summary>Versioned, untrusted OS handoff. This is data, never command-line text.</summary>
public sealed record JoinLink(Uri Lobby, string ListingId)
{
    public const int MaxLength = 4096;
    public const string InvalidMessage = "Invalid Join link. Expected allegiance://join?lobby=<https URL>&id=<listing id>&v=1.";

    public static bool LooksLikeLink(string argument) =>
        argument.Contains("allegiance:", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? value, out JoinLink? link)
    {
        link = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || UnsafeText(value) ||
            (!value.StartsWith("allegiance://join?", StringComparison.OrdinalIgnoreCase) &&
             !value.StartsWith("allegiance://join/?", StringComparison.OrdinalIgnoreCase)) || value.Contains('#')) return false;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in value[(value.IndexOf('?') + 1)..].Split('&'))
        {
            int equals = pair.IndexOf('=');
            if (equals < 1 || !Decode(pair[..equals], out var key) || !Decode(pair[(equals + 1)..], out var decoded)) return false;
            if (key is "lobby" or "id" or "v")
            {
                if (!fields.TryAdd(key, decoded)) return false;
            }
            // Unknown fields are deliberately ignored, never converted into switches.
        }
        if (fields.TryGetValue("v", out var version) && version != "1") return false;
        if (!fields.TryGetValue("id", out var id) || id.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(id[0]) ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return false;
        if (!fields.TryGetValue("lobby", out var lobby) || lobby.Contains('\\') || lobby.Contains('%') ||
            !Uri.TryCreate(lobby, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        string asciiHost;
        try { asciiHost = uri.IdnHost; }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException) { return false; }
        if (asciiHost.Length > 253) return false;
        bool loopback = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(asciiHost.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && loopback)) return false;
        link = new(uri, id);
        return true;
    }

    private static bool UnsafeText(string text) => text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\'');

    private static bool Decode(string text, out string decoded)
    {
        decoded = "";
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '%') continue;
            if (i + 2 >= text.Length || !Uri.IsHexDigit(text[i + 1]) || !Uri.IsHexDigit(text[i + 2])) return false;
            i += 2;
        }
        decoded = Uri.UnescapeDataString(text);
        return !UnsafeText(decoded);
    }
}

/// <summary>Run before every switch parser, including updater/benchmark/training.</summary>
public sealed record JoinLinkLaunch(bool IsLinkLaunch, JoinLink? Link, string? Error)
{
    public static JoinLinkLaunch Take(ref string[] args)
    {
        string[] candidates = args.Where(JoinLink.LooksLikeLink).ToArray();
        if (candidates.Length == 0 && args.Length == 1 && !args[0].StartsWith('-') && args[0].Contains("://", StringComparison.Ordinal)) candidates = args;
        if (candidates.Length == 0) return new(false, null, null);
        args = []; // Even malformed or multiple links suppress ALL other arguments.
        return candidates.Length == 1 && JoinLink.TryParse(candidates[0], out var link)
            ? new(true, link, null) : new(true, null, JoinLink.InvalidMessage);
    }
}
