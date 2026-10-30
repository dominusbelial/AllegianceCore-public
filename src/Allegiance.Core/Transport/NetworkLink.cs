using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Allegiance.Core.Transport;

public enum NetworkLink { Unknown, Wired, Wireless }

/// <summary>Best-effort classification of the adapter selected by the OS route.
/// No probe packet is sent. VPNs, loopback and unavailable platform facts stay unknown.</summary>
public static class NetworkLinkDetector
{
    public static NetworkLink Detect(string host, ushort port)
    {
        if (string.IsNullOrWhiteSpace(host)) return NetworkLink.Unknown;
        try
        {
            var addresses = IPAddress.TryParse(host, out var ip) ? [ip] : Dns.GetHostAddresses(host);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            if (address is not null) return Detect(address, port);
        }
        catch (Exception) { /* Detection must never prevent a connection. */ }
        return NetworkLink.Unknown;
    }

    public static NetworkLink Detect(IPAddress remote, int port)
    {
        try
        {
            using var route = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            route.Connect(new IPEndPoint(remote, port));
            var local = ((IPEndPoint)route.LocalEndPoint!).Address;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                if (adapter.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local)))
                    return Classify(adapter);
        }
        catch (Exception) { /* Missing APIs/permissions/routes are ordinary unknowns. */ }
        return NetworkLink.Unknown;
    }

    private static NetworkLink Classify(NetworkInterface adapter)
    {
        var type = adapter.NetworkInterfaceType;
        if (type == NetworkInterfaceType.Wireless80211) return NetworkLink.Wireless;
        if (OperatingSystem.IsWindows())
            return type switch
            {
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => NetworkLink.Wired,
                _ => NetworkLink.Unknown,
            };
        // Unix Wi-Fi often reports Ethernet (ARPHRD_ETHER / IFT_ETHER). Never
        // infer wired from that enum alone. Android can report type 0 and deny
        // sysfs; macOS needs SystemConfiguration, neither is guessed here.
        if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
        {
            string path = Path.Combine("/sys/class/net", adapter.Name);
            // Enumerate the parent so denied access becomes unknown rather
            // than treating an unreadable wireless marker as absent.
            var entries = Directory.GetFileSystemEntries(path).Select(Path.GetFileName).ToArray();
            if (entries.Contains("wireless") || entries.Contains("phy80211"))
                return NetworkLink.Wireless;
            // Require a physical device as well: bridges, veth and tunnels can
            // also report Ethernet, but reveal nothing about the underlying link.
            if (type == NetworkInterfaceType.Ethernet && entries.Contains("device"))
                return NetworkLink.Wired;
        }
        return NetworkLink.Unknown;
    }

    public static NetworkLink Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "wired" => NetworkLink.Wired,
        "wireless" => NetworkLink.Wireless,
        _ => NetworkLink.Unknown,
    };
}
