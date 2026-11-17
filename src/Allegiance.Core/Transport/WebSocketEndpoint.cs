namespace Allegiance.Core.Transport;

/// <summary>An advertised/configured route, never a user-entered address.
/// Callers must obtain this value from the selected server listing or trusted configuration.</summary>
public sealed class WebSocketEndpoint
{
    public Uri Uri { get; }
    private WebSocketEndpoint(Uri uri) => Uri = uri;

    public static WebSocketEndpoint FromServerListing(string route) => Parse(route);
    public static WebSocketEndpoint FromConfiguration(string route) => Parse(route);

    private static bool HasUserInfo(string route)
    {
        int start = route.IndexOf("://", StringComparison.Ordinal) + 3;
        int end = route.IndexOfAny(['/', '?', '#'], start);
        return route.AsSpan(start, (end < 0 ? route.Length : end) - start).Contains('@');
    }

    private static WebSocketEndpoint Parse(string route)
    {
        if (string.IsNullOrWhiteSpace(route) || route.Length > 2048 || route != route.Trim()
            || !Uri.TryCreate(route, UriKind.Absolute, out var uri)
            || uri.Host.Length == 0 || uri.Port is < 1 or > 65535 || HasUserInfo(route) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || route.Contains('#') || route.Contains('\\') || route.Any(char.IsControl)
            || !(uri.Scheme == "wss" || uri.Scheme == "ws" && uri.IsLoopback))
            throw new ArgumentException("Expected an advertised wss endpoint (ws is permitted only on loopback)", nameof(route));
        return new(uri);
    }
}
