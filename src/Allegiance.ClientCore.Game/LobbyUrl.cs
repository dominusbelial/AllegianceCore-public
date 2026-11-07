namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P947 — lobby URL precedence, resolved once at startup and then read by every
/// lobby-backed consumer (the host internet form, the Play Online fetch, the
/// directory chat, career/identity, the new-game wizard):
/// <list type="number">
/// <item>an explicit <c>--lobby</c> from this session's command line;</item>
/// <item>the saved connect profile (<see cref="ConnectProfile"/>, Android) and
/// the saved player.json preferences (<see cref="IntroPreferences"/>);</item>
/// <item><see cref="ServerListing.LobbyUrlDefault"/>.</item>
/// </list>
/// The connect-profile load used to overwrite the command line unconditionally
/// (P936: both clients were started with <c>--lobby https://&lt;vps&gt;</c> yet the
/// host internet form dialled <c>http://127.0.0.1:5000</c> and failed with
/// "Connection refused"), the same class of bug P871b fixed for the
/// preferences path in <c>Game1.IntroScreen.cs</c>.
/// </summary>
public static class LobbyUrl
{
    /// <summary>Source label for the startup log: a real command-line pin.</summary>
    public const string FromCommandLine = "--lobby";

    /// <summary>Source label for the startup log: a saved profile/preferences value.</summary>
    public const string FromSaved = "profile";

    /// <summary>Source label for the startup log: nothing saved, the built-in default.</summary>
    public const string FromDefault = "default";

    /// <summary>
    /// Apply the precedence: an explicit <c>--lobby</c> beats the saved connect
    /// profile, which beats the saved player.json preferences; with nothing
    /// saved the caller's command-line/default URL stands (an empty result means
    /// "no lobby", which the Android head uses for manual-connect-only).
    /// A connect profile is only fed here when a store exists (Android); the
    /// desktop head passes <c>null</c> and lets the player.json preferences tier
    /// speak (the constructor wires this, see <c>Game1.cs</c>).</summary>
    public static (string Url, string Source) Resolve(
        string? commandLineUrl, bool commandLineExplicit, string? profileUrl, string? prefsUrl)
    {
        // An explicit --lobby pins the session even when blank (--lobby= means
        // "manual connect only"), so neither saved tier can override it.
        if (commandLineExplicit)
            return (commandLineUrl ?? "",
                string.IsNullOrWhiteSpace(commandLineUrl) ? FromDefault : FromCommandLine);
        if (!string.IsNullOrWhiteSpace(profileUrl))
            return (profileUrl, FromSaved);
        if (!string.IsNullOrWhiteSpace(prefsUrl))
            return (prefsUrl, FromSaved);
        return (commandLineUrl ?? "", FromDefault);
    }

    /// <summary>The URL a lobby-backed feature uses when the resolved
    /// <c>_lobbyUrl</c> is empty: the built-in default (the http base address,
    /// the host form, Play Online, career/identity all fall back to it).</summary>
    public static string EffectiveUrl(string? lobbyUrl) =>
        string.IsNullOrWhiteSpace(lobbyUrl) ? ServerListing.LobbyUrlDefault : lobbyUrl;

    /// <summary>The startup-log form of the URL (an empty resolved URL means the
    /// client polls nothing — manual connect only).</summary>
    public static string Display(string? lobbyUrl) =>
        string.IsNullOrWhiteSpace(lobbyUrl) ? "(none)" : lobbyUrl;

    /// <summary>Which tier the URL came from, for the one-time startup log.
    /// A saved value that equals the built-in default is reported as the
    /// default: operationally that is exactly what it means.</summary>
    public static string DescribeSource(string? lobbyUrl, bool commandLineExplicit)
    {
        if (commandLineExplicit && !string.IsNullOrWhiteSpace(lobbyUrl)) return FromCommandLine;
        if (string.IsNullOrWhiteSpace(lobbyUrl)) return FromDefault;
        if (string.Equals(lobbyUrl.TrimEnd('/'), ServerListing.LobbyUrlDefault.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase)) return FromDefault;
        return FromSaved;
    }
}
