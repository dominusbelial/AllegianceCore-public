using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Allegiance.SinglePlayer;

public sealed partial class SkirmishHost
{
    // Read-only local interface enumeration; no route probing, firewall detection, or NAT changes.
    public string[] JoinAddresses => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => $"{a.Address}:{Port}").Distinct().ToArray();

    private long _lanStatusAt;
    private string _lanStatus = "";
    public string LanStatus
    {
        get
        {
            if (Environment.TickCount64 < _lanStatusAt) return _lanStatus;
            _lanStatusAt = Environment.TickCount64 + 1000;
            return _lanStatus = OnSimulation(server =>
                $"{Options.GameName} | Join: {string.Join(" / ", JoinAddresses.DefaultIfEmpty("<this PC's IPv4 address>:" + Port))} | "
                + $"{server.LivePlayerCount} players (humans + bots) | Ctrl+F10 ends game for everyone");
        }
    }
}
