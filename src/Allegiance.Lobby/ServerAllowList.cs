namespace Allegiance.Lobby;

/// <summary>
/// P894 / LOB-0005 — the approved-server filter, the port of native's
/// <c>IsServerAllowed</c> (<c>lobby/server.cpp:24-84</c>).
///
/// <para><b>Native rule.</b> At server logon the lobby first always allows
/// loopback (<c>if (!strncmp("127.0.0.1",szRemote,9)) break;</c>,
/// <c>lobby/server.cpp:131</c>), then calls <c>IsServerAllowed(ip)</c>. That reads
/// <c>[Lobby] NumberOfServers</c>, <c>FilterType</c> and <c>Server1..N</c> from
/// <c>Allegiance.cfg</c>: with no entries it allows everyone
/// (<c>:71-72</c>); with entries it allows (FilterType not "Block") or blocks
/// (<c>FilterType=Block</c>) an IP whose first <c>strlen(ip)</c> characters match
/// an entry (<c>:60-70</c>). A refusal gets the native reason at
/// <c>lobby/server.cpp:138</c> and the connection is dropped.</para>
///
/// <para><b>Port shape.</b> The same decision is made in
/// <c>POST /api/servers/register</c> against the socket source address the lobby
/// already derives (never a caller-supplied address). The default is an empty
/// list = allow all, so an unconfigured lobby keeps listing community servers.</para>
/// </summary>
public static class ServerAllowList
{
    /// <summary>Native's rejection text (<c>lobby/server.cpp:138</c>).</summary>
    public const string RejectionReason =
        "Your server IP address is not approved for connection to this Lobby.  Please contact the Lobby Admin.";

    /// <summary>Native's loopback exemption (<c>lobby/server.cpp:131</c>).</summary>
    public static bool IsLoopback(string? ip)
        => !string.IsNullOrWhiteSpace(ip)
           && (ip.StartsWith("127.", StringComparison.Ordinal) || ip is "::1" or "0:0:0:0:0:0:0:1");

    /// <summary>Native <c>FilterType</c>: anything that starts with "Block"
    /// (case-insensitive here; native compares the configured prefix) is a
    /// blocklist, everything else an allowlist (<c>lobby/server.cpp:50-51</c>).</summary>
    public static bool IsBlockMode(string? filterType)
        => !string.IsNullOrWhiteSpace(filterType)
           && filterType.Trim().StartsWith("Block", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// May <paramref name="sourceIp"/> register? Empty list = allow all (native's
    /// "no entry so allow all servers"); loopback is always allowed; otherwise the
    /// native prefix comparison decides, inverted in block mode.
    /// </summary>
    public static bool IsAllowed(string? sourceIp, IReadOnlyList<string>? entries, string? filterType)
    {
        if (IsLoopback(sourceIp)) return true;
        // Blank configured entries (an unset env var bound into the array) count as
        // no configuration at all — never as "refuse everyone".
        var configured = entries?.Where(e => !string.IsNullOrWhiteSpace(e)).ToArray() ?? [];
        if (configured.Length == 0) return true;
        if (string.IsNullOrWhiteSpace(sourceIp)) return false;

        bool blockMode = IsBlockMode(filterType);
        bool matched = configured.Any(e => Matches(sourceIp, e));
        return blockMode ? !matched : matched;
    }

    /// <summary>Native compares <c>strncmp(ip, entry, strlen(ip))</c>
    /// (<c>lobby/server.cpp:62</c>): the configured entry must START WITH the
    /// caller's address. In practice that means an exact address (a bare netblock
    /// prefix like <c>10.0.0.</c> is SHORTER than the address, so native never
    /// matched it either — the port keeps that behaviour instead of silently
    /// accepting a configuration native would not have honoured). A blank entry
    /// never matches (fail closed in allow mode).</summary>
    private static bool Matches(string sourceIp, string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return false;
        return entry.Trim().StartsWith(sourceIp, StringComparison.OrdinalIgnoreCase);
    }
}
