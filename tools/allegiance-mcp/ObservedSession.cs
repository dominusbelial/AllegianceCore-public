using System.Runtime.CompilerServices;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;
using BotClass = global::Allegiance.Bot.Bot;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// Bot-host-only transport decoration. Both upstream clients are sealed and have
/// no transport injection API. These narrow accessors install a normal TcpTransport
/// on a NEW client before Join starts any threads; client-core and wire behavior stay shared.
/// </summary>
internal sealed class ObservedSession : ITransportSite
{
    private readonly ITransportSite _client;
    private readonly Func<double> _clock;
    private readonly Action<ObservedSession, string, string> _loss;
    private long _lastReadMs;
    private int _ended;
    internal string Name { get; }
    internal TcpTransport Transport { get; }
    /// <summary>P813c — the wrapped client (a ConnectionSession for combat bots),
    /// so the host can arm its auto-reconnect with the endpoint it knows.</summary>
    internal ITransportSite Client => _client;
    internal bool Ended => Volatile.Read(ref _ended) != 0;
    internal double ReadAgeMs => Math.Max(0, _clock() - Interlocked.Read(ref _lastReadMs));

    internal ObservedSession(ITransportSite client, string name, Func<double> clock,
        Action<ObservedSession, string, string> loss)
    {
        _client = client;
        Name = name;
        _clock = clock;
        _loss = loss;
        _lastReadMs = (long)clock(); // No read yet: age since connection attempt.
        Transport = new TcpTransport(this);
    }

    internal void Attach()
    {
        if (_client is ConnectionSession cs)
        {
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

    public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
    {
        // TcpTransport invokes this immediately after the full frame read, BEFORE
        // decode acquires the session gate. It measures socket progress, not pose age.
        Interlocked.Exchange(ref _lastReadMs, (long)_clock());
        try { _client.OnReceive(from, data); }
        catch (Exception ex)
        {
            End("local-exception", $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void OnSessionLost()
    {
        string? detail = ReadFailure(Transport);
        End(IntentionalShutdown(Transport) ? "our-disposal" :
            detail == "server closed connection (EOF)" ? "server-eof" : "local-transport-error",
            detail ?? "read loop ended without a recorded error");
        _client.OnSessionLost();
    }
    public void OnPeerJoined(ConnectionId id, string name) => _client.OnPeerJoined(id, name);
    public void OnPeerLeft(ConnectionId id) => _client.OnPeerLeft(id);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_transport")]
    internal static extern ref TcpTransport? CombatTransport(ConnectionSession client);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_transport")]
    internal static extern ref TcpTransport? PlainTransport(BotClass client);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clientReadFailure")]
    internal static extern ref string? ReadFailure(TcpTransport transport);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_intentionalShutdown")]
    internal static extern ref bool IntentionalShutdown(TcpTransport transport);
}
