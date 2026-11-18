using System.Net;
using System.Net.Sockets;

namespace Allegiance.Core.Transport;

/// <summary>Shared admission/credential source key. IPv6 subscribers share a /64;
/// IPv4 and IPv4-mapped sources share the same IPv4 key. Never use this key for
/// proxy trust, peer identity or loopback privilege decisions.</summary>
public static class SourceAddressBudget
{
    public static IPAddress Key(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address;
        var bytes = address.GetAddressBytes();
        bytes.AsSpan(8).Clear();
        // Scope IDs do not create independent subscriber budgets.
        return new IPAddress(bytes);
    }
}
