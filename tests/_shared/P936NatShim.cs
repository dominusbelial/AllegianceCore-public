using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Allegiance.Core.Transport;

namespace Allegiance.TestInfra;

public enum P936NatKind { FullCone, PortRestricted, Symmetric }

/// <summary>Real in-process UDP NAT: an external socket provides the mapping,
/// and a per-destination internal gateway preserves the logical remote address.
/// No packet delivery is simulated and no privileged network namespace is needed.
/// Only UdpTransport's internal route seams connect its socket to the gateways.</summary>
public sealed class P936NatShim : IDisposable
{
    private readonly P936NatKind _kind;
    private readonly UdpTransport _transport;
    private readonly object _gate = new();
    private readonly Dictionary<IPEndPoint, Gateway> _routes = new();
    private readonly ConcurrentDictionary<int, IPEndPoint> _reverse = new();
    private readonly Dictionary<string, Mapping> _mappings = new();
    private readonly List<Task> _workers = [];
    private readonly CancellationTokenSource _stop = new();
    private int _dropped, _forwarded;
    public int Dropped => Volatile.Read(ref _dropped);
    public int Forwarded => Volatile.Read(ref _forwarded);

    public P936NatShim(UdpTransport transport, P936NatKind kind)
    {
        _transport = transport; _kind = kind;
        transport.OutboundRouteForTesting = Route;
        transport.InboundRouteForTesting = endpoint => _reverse.TryGetValue(endpoint.Port, out var original)
            ? new(original.Address.MapToIPv6(), original.Port) : endpoint;
    }
    private static IPEndPoint V4(IPEndPoint ep) => new(ep.Address.MapToIPv4(), ep.Port);
    private static Socket Bind()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }
    private IPEndPoint Route(IPEndPoint destination)
    {
        destination = V4(destination);
        lock (_gate)
        {
            string key = _kind == P936NatKind.Symmetric ? destination.ToString() : "shared";
            if (!_mappings.TryGetValue(key, out var mapping))
            {
                mapping = new(Bind()); _mappings[key] = mapping;
                var owned = mapping;
                _workers.Add(OutsideAsync(owned));
            }
            return (IPEndPoint)GatewayFor(destination, mapping).Socket.LocalEndPoint!;
        }
    }
    private Gateway GatewayFor(IPEndPoint destination, Mapping mapping)
    {
        if (_routes.TryGetValue(destination, out var existing)) return existing;
        var gateway = new Gateway(Bind(), destination, mapping);
        _routes.Add(destination, gateway);
        _reverse[((IPEndPoint)gateway.Socket.LocalEndPoint!).Port] = destination;
        _workers.Add(InsideAsync(gateway));
        return gateway;
    }
    private async Task InsideAsync(Gateway gateway)
    {
        var buffer = new byte[2048];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = await gateway.Socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _stop.Token);
                gateway.Mapping.Allowed[gateway.Destination] = 0;
                await gateway.Mapping.Socket.SendToAsync(buffer.AsMemory(0, packet.ReceivedBytes), SocketFlags.None, gateway.Destination, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }
    private async Task OutsideAsync(Mapping mapping)
    {
        var buffer = new byte[2048];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = await mapping.Socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _stop.Token);
                var source = V4((IPEndPoint)packet.RemoteEndPoint);
                if (_kind != P936NatKind.FullCone && !mapping.Allowed.ContainsKey(source)) { Interlocked.Increment(ref _dropped); continue; }
                Gateway gateway;
                lock (_gate) gateway = GatewayFor(source, mapping);
                await gateway.Socket.SendToAsync(buffer.AsMemory(0, packet.ReceivedBytes), SocketFlags.None,
                    new IPEndPoint(IPAddress.Loopback, _transport.SocketPort), _stop.Token);
                Interlocked.Increment(ref _forwarded);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }
    private sealed record Gateway(Socket Socket, IPEndPoint Destination, Mapping Mapping);
    private sealed record Mapping(Socket Socket)
    { public ConcurrentDictionary<IPEndPoint, byte> Allowed { get; } = new(); }

    public void Dispose()
    {
        _stop.Cancel();
        Task[] workers;
        lock (_gate)
        {
            foreach (var gateway in _routes.Values) gateway.Socket.Dispose();
            foreach (var mapping in _mappings.Values) mapping.Socket.Dispose();
            workers = _workers.ToArray();
        }
        Task.WhenAll(workers).GetAwaiter().GetResult();
        _stop.Dispose();
    }
}

/// <summary>Loopback-only fake lobby STUN for the transport tests.</summary>
public sealed class P936BindingFake : IDisposable
{
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    public IPEndPoint Endpoint => (IPEndPoint)_socket.Client.LocalEndPoint!;
    public P936BindingFake() => _worker = RunAsync();
    private async Task RunAsync()
    {
        var buffer = new byte[256];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var received = await _socket.ReceiveAsync(_stop.Token);
                int length = StunBinding.WriteSuccess(received.Buffer, received.RemoteEndPoint, new byte[16], buffer);
                if (length > 0) await _socket.SendAsync(buffer.AsMemory(0, length), received.RemoteEndPoint, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public void Dispose() { _stop.Cancel(); _worker.GetAwaiter().GetResult(); _socket.Dispose(); _stop.Dispose(); }
}
