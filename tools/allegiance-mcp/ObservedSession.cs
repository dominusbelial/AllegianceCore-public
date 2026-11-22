using System.Runtime.CompilerServices;
using System.Buffers.Binary;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;
using BotClass = global::Allegiance.Bot.Bot;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// Bot-host-only receive observation. Owned ConnectionSessions publish frame
/// arrivals while retaining their normal connect/reconnect lifecycle. The narrow
/// accessors retain the established plain-bot and legacy test decoration seam.
/// </summary>
internal sealed class ObservedSession : ITransportSite
{
    private readonly ITransportSite _client;
    private readonly Func<double> _clock;
    private readonly Action<ObservedSession, string, string> _loss;
    private long _lastReadMs, _lastUpdateMs = -1;
    private readonly bool _metrics = Environment.GetEnvironmentVariable("ALLEGIANCE_BENCHMARK_METRICS") == "1";
    internal double? UpdateAgeMs => Interlocked.Read(ref _lastUpdateMs) is var tick && tick >= 0 ? Math.Max(0, _clock() - tick) : null;
    private int _ended;
    internal double StartedMs { get; private set; }
    internal bool LogonRecorded;
    private long _logonMs = -1;
    internal double? LogonMs => Interlocked.Read(ref _logonMs) is var value && value >= 0 ? value : null;
    internal void MarkLogon() => Interlocked.CompareExchange(ref _logonMs, Math.Max(0, (long)(_clock() - StartedMs)), -1);
    internal int LastReconnects;
    internal string Name { get; }
    private readonly ITransport? _transport;
    private readonly bool _owned;
    internal ITransport Transport => _owned && _client is ConnectionSession cs
        ? CombatTransport(cs) ?? throw new InvalidOperationException("session is between transports") : _transport!;
    /// <summary>P813c — the wrapped client (a ConnectionSession for combat bots),
    /// so the host can arm its auto-reconnect with the endpoint it knows.</summary>
    internal ITransportSite Client => _client;
    internal bool Ended => Volatile.Read(ref _ended) != 0;
    internal double ReadAgeMs => Math.Max(0, _clock() - Interlocked.Read(ref _lastReadMs));

    internal ObservedSession(ITransportSite client, string name, Func<double> clock,
        Action<ObservedSession, string, string> loss, BotTransportOptions? options = null, bool owned = false)
    {
        _client = client;
        Name = name;
        _clock = clock;
        _loss = loss;
        StartedMs = _lastReadMs = (long)clock(); // No read yet: age since connection attempt.
        _owned = owned;
        options ??= new();
        if (!owned) _transport = TransportFactory.CreateClient(this, options.Protocol,
            webSocketEndpoint: options.Protocol == "ws" ? WebSocketEndpoint.FromConfiguration(options.Url!) : null,
            webSocketOrigin: options.Origin, webSocketCertificateValidation: options.CertificateValidator());
    }

    internal void Attach()
    {
        if (_client is ConnectionSession cs)
        {
            if (_owned)
            {
                cs.PacketReceived += Received;
                cs.SessionLost += () => End("session-lost", "owned session exhausted reconnects");
                return;
            }
            if (CombatTransport(cs) is not null) throw new InvalidOperationException("telemetry requires a fresh session");
            // Mirror ConnectionSession.Connect's initial world reset before
            // any receive thread exists (including its mission generation).
            lock (cs.Gate) cs.World.Reset();
            CombatTransport(cs) = Transport;
        }
        else if (_client is BotClass bot)
        {
            if (PlainTransport(bot) is not null) throw new InvalidOperationException("telemetry requires a fresh bot");
            PlainTransport(bot) = Transport;
        }
        else throw new ArgumentException("unsupported bot-host client");
    }

    internal void End(string reason, string detail)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0) _loss(this, reason, detail);
    }

    private void Received() => Interlocked.Exchange(ref _lastReadMs, (long)_clock());

    public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
    {
        // The transport invokes this immediately after receiving a full frame, BEFORE
        // decode acquires the session gate. It measures socket progress, not pose age.
        Received();
        try
        {
            _client.OnReceive(from, data);
            if (_metrics)
                for (int offset = 0; offset + 4 <= data.Length - FedPacket.CrcWidth;)
                {
                    int size = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
                    if (size < 4 || offset + size > data.Length - FedPacket.CrcWidth) break;
                    int fmid = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
                    if (fmid is SetCluster.Fmid or SingleShipUpdate.Fmid or LightShipsUpdate.Fmid or HeavyShipsUpdate.Fmid)
                    { Interlocked.Exchange(ref _lastUpdateMs, (long)_clock()); break; }
                    offset += size;
                }
        }
        catch (Exception ex)
        {
            End("local-exception", $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void OnSessionLost()
    {
        string? detail = Transport is WebSocketTransport ? "WebSocket session ended" : ReadFailure(Transport);
        End(Transport is not WebSocketTransport && IntentionalShutdown(Transport) ? "our-disposal" :
            detail == "server closed connection (EOF)" ? "server-eof" : "local-transport-error",
            detail ?? "read loop ended without a recorded error");
        _client.OnSessionLost();
    }
    public void OnPeerJoined(ConnectionId id, string name) => _client.OnPeerJoined(id, name);
    public void OnPeerLeft(ConnectionId id) => _client.OnPeerLeft(id);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_transport")]
    internal static extern ref ITransport? CombatTransport(ConnectionSession client);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_transport")]
    internal static extern ref ITransport? PlainTransport(BotClass client);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clientReadFailure")]
    private static extern ref string? TcpReadFailure(TcpTransport transport);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_intentionalShutdown")]
    private static extern ref bool TcpIntentionalShutdown(TcpTransport transport);
    // Keep the ref-return TCP diagnostic seam used by the watchdog tests.
    internal static ref string? ReadFailure(ITransport transport)
    {
        transport = TransportFactory.ActiveTransport(transport);
        if (transport is TcpTransport tcp) return ref TcpReadFailure(tcp);
        if (transport is UdpTransport udp) return ref udp.ClientReadFailure;
        throw new InvalidOperationException("transport has not joined");
    }
    internal static ref bool IntentionalShutdown(ITransport transport)
    {
        transport = TransportFactory.ActiveTransport(transport);
        if (transport is TcpTransport tcp) return ref TcpIntentionalShutdown(tcp);
        if (transport is UdpTransport udp) return ref udp.IntentionalShutdown;
        throw new InvalidOperationException("transport has not joined");
    }

}
