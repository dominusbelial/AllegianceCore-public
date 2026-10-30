using Allegiance.Core.Transport;

namespace Allegiance.ClientCore.MonoGame.Discovery;

public enum ClientTransportSetting { Auto, TCP, UDP }
public readonly record struct ClientTransportChoice(string Protocol, string Reason);

/// <summary>P897 modern connection policy. Evaluated once before joining;
/// UDP handshake fallback and session reconnect remain transport concerns.</summary>
public static class ClientTransportPolicy
{
    public static ClientTransportChoice Choose(ClientTransportSetting setting, DiscoverySource source,
        int? udpPort, NetworkLink client, NetworkLink host)
    {
        if (setting == ClientTransportSetting.TCP) return new("tcp", "Settings: TCP");
        if (setting == ClientTransportSetting.UDP) return new("udp", "Settings: UDP (TCP fallback available)");
        if (udpPort is not (> 0 and <= ushort.MaxValue)) return new("tcp", "Auto: no advertised UDP port");
        if (source is DiscoverySource.Lan or DiscoverySource.Multicast or DiscoverySource.Subnet)
        {
            if (client == NetworkLink.Wired && host == NetworkLink.Wired)
                return new("tcp", "Auto: wired LAN at both ends");
            return new("udp", $"Auto: LAN client={client}, host={host}");
        }
        return new("udp", $"Auto: {source} connection");
    }

    /// <summary>The existing environment override takes precedence over Auto,
    /// while an explicit Settings TCP/UDP choice takes precedence over Auto inputs.</summary>
    public static ClientTransportChoice Resolve(ClientTransportSetting setting, DiscoverySource source,
        int? udpPort, NetworkLink client, NetworkLink host, string? environmentProtocol)
    {
        if (setting == ClientTransportSetting.Auto && !string.IsNullOrWhiteSpace(environmentProtocol))
            return new(environmentProtocol.Trim().ToLowerInvariant(), "ALLEGIANCE_TRANSPORT_PROTOCOL override");
        return Choose(setting, source, udpPort, client, host);
    }
}
