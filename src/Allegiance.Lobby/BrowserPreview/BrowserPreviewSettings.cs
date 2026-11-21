namespace Allegiance.Lobby.BrowserPreview;

/// <summary>
/// Closed browser-bundle registry. Production turns it on with <see cref="Enabled"/>;
/// the defaults describe the local preview prefix so a developer generation keeps
/// working without configuration.
/// </summary>
public sealed class BrowserPreviewSettings
{
    public bool Enabled { get; set; }
    /// <summary>Served URL base. The production host publishes the generated bundle at
    /// <c>/play</c>; the local proof generation keeps the longer preview prefix.</summary>
    public string Prefix { get; set; } = "/play/preview";
    // A published browser wwwroot, outside the lobby's ordinary wwwroot.
    public string BundlePath { get; set; } = "browser-preview";
    public string ArtworkPath { get; set; } = "";
    /// <summary>The externally served HTTPS origin (for example
    /// <c>https://&lt;public-origin&gt;</c>) when a TLS proxy fronts the lobby. Used only
    /// to add the same-origin <c>wss</c> route to the <c>/play/</c> CSP and to reject a
    /// route that belongs to another origin. Empty = same-origin only (loopback
    /// development), exactly the policy of the local proof host.</summary>
    public string? PublicOrigin { get; set; }
    /// <summary>The game server's advertised WebSocket path behind the same origin.</summary>
    public string WebSocketPath { get; set; } = "/game";

    public void Validate()
    {
        if (!Enabled) return;
        if (Prefix.Length is < 2 or > 64 || Prefix[0] != '/' || Prefix[^1] == '/'
            || Prefix.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_'))
            || Prefix.Equals("/api", StringComparison.OrdinalIgnoreCase)
            || Prefix.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("BrowserPreview.prefix must be a simple absolute path that is not /api");
        if (WebSocketPath.Length is < 2 or > 128 || WebSocketPath[0] != '/'
            || WebSocketPath.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_')))
            throw new InvalidDataException("BrowserPreview.webSocketPath must be a simple absolute path");
        if (string.IsNullOrWhiteSpace(PublicOrigin)) return;
        if (!Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https"
            || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0
            || origin.Fragment.Length != 0 || PublicOrigin != origin.GetLeftPart(UriPartial.Authority))
            throw new InvalidDataException("BrowserPreview.publicOrigin must be one canonical HTTPS origin");
    }
}
