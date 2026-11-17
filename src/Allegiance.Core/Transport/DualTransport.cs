using System.Net;

namespace Allegiance.Core.Transport;

/// <summary>Compatibility composition of TCP and UDP, using the same collision-free
/// routing as other listener combinations. There are no reserved peer-ID bits.</summary>
public sealed class DualTransport : ServerTransportMux
{
    public TcpTransport Tcp { get; }
    public UdpTransport Udp { get; }
    public DualTransport(ITransportSite site, Func<ITransportSite, TcpTransport> tcpFactory, ushort udpPort,
        EgressAccounting? egress, ReliableTelemetry? reliable, ServerCounters counters, bool park,
        bool measureReceiveDelay = false, IPAddress? udpBindAddress = null, bool requireIntroductions = false) : base(site)
    {
        Tcp = Add(tcpFactory);
        Udp = Add(s => new UdpTransport(s)
        {
            Egress = egress, Reliable = reliable, ServerCounters = counters, ReliableOneShot = park,
            MeasureReceiveDelay = measureReceiveDelay, RequireIntroductions = requireIntroductions,
            IdleTimeout = TcpTransport.DataLinkIdleTimeout,
            MaxBufferedBytes = (int)Math.Min(int.MaxValue, TcpTransport.HardCapBytes),
        }, udpPort == 0 ? null : udpPort, udpBindAddress);
    }
}
