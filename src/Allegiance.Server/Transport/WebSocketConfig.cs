using System.Net;

namespace Allegiance.Server;

/// <summary>Default-off direct FedPacket listener. TLS terminates at an explicitly
/// trusted proxy; plaintext is otherwise allowed only for loopback development.</summary>
public sealed class WebSocketConfig
{
    public bool Enabled { get; set; }
    public ushort Port { get; set; } = 6080;
    public string BindAddress { get; set; } = "127.0.0.1";
    public string Path { get; set; } = "/game";
    public string[] AllowedOrigins { get; set; } = [];
    // Same explicit one-hop policy as Lobby.KnownProxies. No implicit loopback trust.
    public string[] KnownProxies { get; set; } = [];
    public bool AllowLoopbackDevelopment { get; set; }
    public int MaxConnections { get; set; } = 1024;
    public int MaxConnectionsPerIp { get; set; } = 64;
    public int MaxPendingHandshakes { get; set; } = 256;
    public int HandshakeAttemptsPerWindow { get; set; } = 2048;
    public int HandshakeAttemptsPerIpPerWindow { get; set; } = 128;
    public int MaxTrackedSources { get; set; } = 4096;
    public double HandshakeWindowSeconds { get; set; } = 60;
    public double HandshakeTimeoutSeconds { get; set; } = 10;
    public double AuthenticationTimeoutSeconds { get; set; } = 60;
    public double FragmentProgressTimeoutSeconds { get; set; } = 5;
    public double MessageTimeoutSeconds { get; set; } = 30;
    public double IdleTimeoutSeconds { get; set; } = 30;
    public int PreLogonMaxMessageBytes { get; set; } = 16 * 1024;
    public int MaxFragments { get; set; } = 256;
    // TCP's existing hard bound accommodates large legitimate join/export bursts.
    public long MaxQueuedBytes { get; set; } = 32L * 1024 * 1024;
    public int MaxQueuedEntries { get; set; } = 8192;
    public double MaxGuaranteedAgeSeconds { get; set; } = 60;
    public double SendTimeoutSeconds { get; set; } = 30;

    public void Validate()
    {
        if (!Enabled) return;
        if (!IPAddress.TryParse(BindAddress, out _)) throw new InvalidDataException("webSocket.bindAddress must be an IP address");
        if (Path.Length is < 2 or > 128 || Path[0] != '/' || Path.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_')))
            throw new InvalidDataException("webSocket.path must be a simple absolute path");
        if (AllowedOrigins.Length == 0 || AllowedOrigins.Length > 64) throw new InvalidDataException("webSocket.allowedOrigins is required (1–64 origins)");
        foreach (string origin in AllowedOrigins)
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/"
                || uri.Query.Length != 0 || uri.Fragment.Length != 0 || origin != uri.GetLeftPart(UriPartial.Authority)
                || !(uri.Scheme == "https" || (AllowLoopbackDevelopment && uri.Scheme == "http" && uri.IsLoopback)))
                throw new InvalidDataException("webSocket.allowedOrigins requires canonical HTTPS origins (HTTP loopback only for development)");
        if (KnownProxies.Length > 64 || KnownProxies.Any(p => !IPAddress.TryParse(p, out _)))
            throw new InvalidDataException("webSocket.knownProxies must contain explicit proxy IP addresses");
        int[] counts = [MaxConnections, MaxConnectionsPerIp, MaxPendingHandshakes, HandshakeAttemptsPerWindow,
            HandshakeAttemptsPerIpPerWindow, MaxTrackedSources, MaxFragments, MaxQueuedEntries];
        if (counts.Any(n => n <= 0)) throw new InvalidDataException("webSocket limits must be positive");
        double[] durations = [HandshakeWindowSeconds, HandshakeTimeoutSeconds, AuthenticationTimeoutSeconds,
            FragmentProgressTimeoutSeconds, MessageTimeoutSeconds, IdleTimeoutSeconds, MaxGuaranteedAgeSeconds, SendTimeoutSeconds];
        if (durations.Any(n => !double.IsFinite(n) || n <= 0 || n > 3600)) throw new InvalidDataException("webSocket deadlines must be finite and in (0, 3600]");
        if (PreLogonMaxMessageBytes is < 8192 or > 16384 || MaxQueuedBytes is < 1 or > 32L * 1024 * 1024)
            throw new InvalidDataException("webSocket pre-logon cap must be 8–16 KiB and queue cap 1–32 MiB");
    }
}
