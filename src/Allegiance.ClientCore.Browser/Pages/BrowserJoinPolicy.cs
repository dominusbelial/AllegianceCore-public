using Allegiance.Core.Transport;

namespace Allegiance.ClientCore.Browser.Pages;

/// <summary>
/// P1061 — one closed decision for whether this page may open a server's advertised
/// WebSocket route. A public lobby page and the game route must be the same HTTPS
/// origin (the TLS proxy routes both). Signed-in joins need a verified ticket
/// issuer; guests need explicit admission. The starter core must match, and a
/// server that enforces a minimum client
/// version needs a build identity this browser can actually report. The password is
/// not a refusal: the player types the game password, and the server still checks it.
/// </summary>
public static class BrowserJoinPolicy
{
    public static string? Refusal(Uri page, BrowserServer server, bool hasClientVersion, bool guest = false)
    {
        if (server.Route(page.IsLoopback) is not { } route)
            return "Joining this server is not available in the browser (no web route).";
        Uri game;
        try
        {
            game = page.IsLoopback
                ? WebSocketEndpoint.FromLoopbackDirectoryListing(route).Uri
                : WebSocketEndpoint.FromServerListing(route).Uri;
        }
        catch (ArgumentException) { return "Joining this server is not available in the browser (invalid web route)."; }
        // Outside loopback development the page itself must be the secure public origin.
        if (!string.Equals(page.Scheme, "https", StringComparison.OrdinalIgnoreCase) && !page.IsLoopback)
            return "Online play needs the HTTPS lobby page.";
        // Same origin as this page; a route to another host would leave the policy's
        // connect-src and the TLS proxy's single-origin shape.
        if (!string.Equals(game.Host, page.Host, StringComparison.OrdinalIgnoreCase) || game.Port != page.Port)
            return "The game route is not on this lobby's origin.";
        if (!page.IsLoopback && !string.Equals(game.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
            return "The game route must be secure.";
        if (guest && !server.AcceptsGuests)
            return "Sign in to join";
        if (!guest && (!server.Verified || string.IsNullOrEmpty(server.IdentityServerId)))
            return "Unverified identity servers are not available in the browser.";
        if (!string.Equals(server.CoreFile, "PCore001.igc", StringComparison.OrdinalIgnoreCase))
            return "This core is not available in the browser starter pack.";
        if (!string.IsNullOrEmpty(server.MinClientVersion) && !hasClientVersion)
            return "This server requires client " + server.MinClientVersion
                + "; this browser generation does not report a build version. Republish it with a client version.";
        return null;
    }
}
