using System.Net;
using System.Net.Sockets;
using Allegiance.Core.Transport;

namespace Allegiance.Lobby;

/// <summary>The lobby itself observes the game socket on two destinations.
/// Buffers are reused; malformed packets never enter the observation store.</summary>
public sealed class NatBindingResponder(LobbySettings settings, NatObservationStore observations) : BackgroundService
{
    private Socket? _primary, _secondary;
    public int PrimaryPort => (_primary?.LocalEndPoint as IPEndPoint)?.Port ?? 0;
    public int SecondaryPort => (_secondary?.LocalEndPoint as IPEndPoint)?.Port ?? 0;
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!settings.PlayerHostedGames) return Task.CompletedTask;
        try
        {
            _primary = Bind(settings.StunPrimaryPort);
            _secondary = Bind(settings.StunSecondaryPort);
            return base.StartAsync(cancellationToken);
        }
        catch { _primary?.Dispose(); _secondary?.Dispose(); throw; }
    }
    private Socket Bind(int port)
    {
        var address = IPAddress.Parse(settings.StunBindAddress);
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6) socket.DualMode = true;
            socket.Bind(new IPEndPoint(address, port));
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        ReceiveAsync(_primary!, true, stoppingToken), ReceiveAsync(_secondary!, false, stoppingToken));
    private async Task ReceiveAsync(Socket socket, bool primary, CancellationToken ct)
    {
        var input = new byte[StunBinding.MaxSize + 1];
        var output = new byte[StunBinding.MaxSize];
        EndPoint any = socket.AddressFamily == AddressFamily.InterNetworkV6 ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try { received = await socket.ReceiveFromAsync(input, SocketFlags.None, any, ct).ConfigureAwait(false); }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset) { continue; }
                int length = observations.Respond(input.AsSpan(0, received.ReceivedBytes), (IPEndPoint)received.RemoteEndPoint, primary, output);
                if (length > 0)
                    try { await socket.SendToAsync(output.AsMemory(0, length), SocketFlags.None, received.RemoteEndPoint, ct).ConfigureAwait(false); }
                    catch (SocketException) { } // a disappearing UDP client cannot stop the responder
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
    }
    public override void Dispose() { base.Dispose(); _primary?.Dispose(); _secondary?.Dispose(); }
}
