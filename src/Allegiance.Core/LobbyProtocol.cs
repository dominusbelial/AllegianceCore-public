namespace Allegiance.Core;

/// <summary>
/// P894 / LOB-0004 — the lobby ⇄ server protocol version, the port's stand-in
/// for native's <c>LOBBYVER_LS</c> (<c>lobby/server.cpp:93</c>, <c>:141-151</c>).
///
/// <para><b>Native rule.</b> A game server announces itself to the lobby with
/// <c>FM_S_LOGON_LOBBY</c>; the lobby accepts it only when
/// <c>pfmLogon-&gt;verLobby == LOBBYVER_LS</c> and otherwise sends
/// <c>LOGON_SERVER_NACK</c> with a tailored reason ("The Public Lobby server that
/// you connected to is older than your version…" / "Your server's version did
/// not get auto-updated properly…"). The lobby then drops the connection
/// (<c>lobby/server.cpp:141-151</c>).</para>
///
/// <para><b>Port shape.</b> Registration is an HTTP POST, so the same rule is a
/// refusal at <c>POST /api/servers/register</c>. The constant lives here (not in
/// either app) so the server's heartbeat and the lobby's gate cannot drift apart:
/// both projects reference <c>Allegiance.Core</c>.</para>
///
/// <para><b>Compatibility.</b> A registering host that omits the field is a
/// pre-P894 build; it is accepted (the port keeps community servers listing, and
/// nothing in the directory depends on the new field). A host that <i>states</i> a
/// version which differs from this one is refused with the native reason, exactly
/// like a native server whose build did not auto-update.</para>
/// </summary>
public static class LobbyProtocol
{
    /// <summary>The protocol version this release speaks. Bump it whenever the
    /// register/heartbeat payload or the server-claim endpoints change shape in a
    /// way an older server cannot speak.</summary>
    public const int Version = 1;

    /// <summary>Is a server that advertises <paramref name="advertised"/> (null =
    /// pre-P894 host, field absent) accepted by a lobby speaking
    /// <paramref name="lobbyVersion"/>? Native's rule is exact equality.</summary>
    public static bool IsAccepted(int? advertised, int lobbyVersion)
        => advertised is null || advertised == lobbyVersion;

    /// <summary>The native refusal text (<c>lobby/server.cpp:141-144</c>), so an
    /// operator sees the same explanation a native lobby gave.</summary>
    public static string RejectionReason(int advertised, int lobbyVersion)
        => advertised > lobbyVersion
            ? "The Public Lobby server that you connected to is older than your version.  The Zone needs to update their lobby server.  Please try again later."
            : "Your server's version did not get auto-updated properly.  Please try again later.";
}
