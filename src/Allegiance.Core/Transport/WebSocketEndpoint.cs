using System.Net;
using System.Net.Sockets;

namespace Allegiance.Core.Transport;

/// <summary>An advertised/configured route, never a user-entered address.
/// Callers must obtain this value from the selected server listing or trusted configuration.</summary>
public sealed class WebSocketEndpoint
{
    public Uri Uri { get; }
    private WebSocketEndpoint(Uri uri) => Uri = uri;

    /// <summary>Untrusted directory input: secure transport and a public host.</summary>
    public static WebSocketEndpoint FromServerListing(string route) => Parse(route, listing: true);
    /// <summary>Optional discovery fields fail by omission, preserving old listings.</summary>
    public static string? NormalizeServerListing(string? route)
    {
        if (route is null) return null;
        try { return FromServerListing(route).Uri.AbsoluteUri; }
        catch (ArgumentException) { return null; }
    }
    /// <summary>Operator-owned configuration: also permits plaintext loopback development.</summary>
    public static WebSocketEndpoint FromConfiguration(string route) => Parse(route, listing: false);

    private static bool HasUserInfo(string route)
    {
        int start = route.IndexOf("://", StringComparison.Ordinal) + 3;
        int end = route.IndexOfAny(['/', '?', '#'], start);
        return route.AsSpan(start, (end < 0 ? route.Length : end) - start).Contains('@');
    }

    private static WebSocketEndpoint Parse(string route, bool listing)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(route) || route.Length > 2048 || route != route.Trim()
                || !Uri.TryCreate(route, UriKind.Absolute, out var uri)
                || uri.Host.Length == 0 || uri.Port is < 1 or > 65535 || HasUserInfo(route) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || route.Contains('#') || route.Contains('\\') || route.Any(char.IsControl)
                || !(uri.Scheme == "wss" || !listing && uri.Scheme == "ws" && uri.IsLoopback))
                throw new ArgumentException("Expected a public wss listing endpoint (trusted configuration also permits ws loopback)", nameof(route));
            // IDN conversion is deferred by Uri and can throw even after
            // TryCreate succeeds. Validate the converted labels too: conversion
            // can introduce spaces or empty labels from Unicode input.
            string host = uri.IdnHost.TrimEnd('.');
            if (uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
                || uri.HostNameType == UriHostNameType.Dns && !IsDnsHost(host)
                || listing && !IsPublicHost(uri, host))
                throw new ArgumentException("Expected a valid public listing host or trusted configuration host", nameof(route));
            return new(uri);
        }
        catch (UriFormatException ex) { throw new ArgumentException("Invalid WebSocket endpoint host", nameof(route), ex); }
    }

    private static bool IsDnsHost(string host) => host.Length <= 253 && host.Split('.').All(label =>
        label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-'
        && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    private static bool IsPublicHost(Uri uri, string host)
    {
        if (uri.IsLoopback) return false;
        if (IPAddress.TryParse(host, out var address))
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                // Global unicast only; excludes unspecified, loopback, ULA,
                // link/site local, multicast and scoped interface addresses.
                return (bytes[0] & 0xe0) == 0x20 && address.ScopeId == 0;
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0);
        }
        // DNS is not resolved during listing ingestion or client selection.
        // Only authenticated operators may publish DNS routes; exclude local
        // names as well as private IP literals without an outbound DNS request.
        if (uri.HostNameType != UriHostNameType.Dns || !host.Contains('.')) return false;
        string[] localDomains = ["localhost", "localdomain", "localdomain6", "local", "internal", "lan", "home", "home.arpa"];
        return !localDomains.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }
}
