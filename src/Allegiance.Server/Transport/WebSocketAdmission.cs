using System.Net;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

/// <summary>Bounded, fail-closed source table. Attempt windows survive disconnects;
/// live sources never expire. Used separately for physical connections and upgrade sources.</summary>
internal sealed class WebSocketAdmission(WebSocketConfig settings, IEnumerable<IPAddress>? perIpExemptions = null)
{
    private readonly object _gate = new();
    internal sealed class Source { public int Live, Attempts; public long Since; }
    private readonly Dictionary<IPAddress, Source> _sources = [];
    // Only the physical guard receives exact known proxy addresses. Never mask
    // trust to /64: another socket in the proxy's subnet is not a trusted proxy.
    private readonly HashSet<IPAddress> _perIpExemptions = (perIpExemptions ?? []).Select(Canonical).ToHashSet();
    private int _live, _pending, _attempts;
    private long _since;
    internal static IPAddress Canonical(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    internal bool TryAttempt(IPAddress address, long now)
    {
        bool exempt = _perIpExemptions.Contains(Canonical(address));
        address = SourceAddressBudget.Key(address);
        lock (_gate)
        {
            long window = (long)(settings.HandshakeWindowSeconds * 1000);
            if (now - _since >= window) { _since = now; _attempts = 0; }
            _sources.TryGetValue(address, out var source);
            if (source is not null)
            {
                if (now - source.Since >= window) { source.Since = now; source.Attempts = 0; }
                // A source already denied by its own quota must not exhaust the
                // global quota for unrelated clients (the same R1 F11 principle).
                if (!exempt && source.Attempts >= settings.HandshakeAttemptsPerIpPerWindow) return false;
            }
            if (_attempts >= settings.HandshakeAttemptsPerWindow) return false;
            if (source is null)
            {
                if (_sources.Count >= settings.MaxTrackedSources)
                    foreach (var stale in _sources.Where(p => p.Value.Live == 0 && now - p.Value.Since >= window).Select(p => p.Key).ToArray())
                        _sources.Remove(stale);
                if (_sources.Count >= settings.MaxTrackedSources) return false;
                _sources[address] = source = new Source { Since = now };
            }
            _attempts++;
            source.Attempts++;
            return true;
        }
    }
    internal Lease? TryEnter(IPAddress address)
    {
        bool exempt = _perIpExemptions.Contains(Canonical(address));
        address = SourceAddressBudget.Key(address);
        lock (_gate)
        {
            if (!_sources.TryGetValue(address, out var source) || _live >= settings.MaxConnections
                || !exempt && source.Live >= settings.MaxConnectionsPerIp || _pending >= settings.MaxPendingHandshakes) return null;
            source.Live++; _live++; _pending++;
            return new Lease(this, source);
        }
    }
    internal (int Live, int Pending, int Sources) Snapshot { get { lock (_gate) return (_live, _pending, _sources.Count); } }
    internal sealed class Lease : IDisposable
    {
        private readonly WebSocketAdmission _owner;
        private readonly Source _source;
        private bool _pending = true, _closed;
        internal Lease(WebSocketAdmission owner, Source source) { _owner = owner; _source = source; }
        internal void Promoted()
        {
            lock (_owner._gate) if (!_closed && _pending) { _pending = false; _owner._pending--; }
        }
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_closed) return;
                _closed = true; _source.Live--; _owner._live--;
                if (_pending) _owner._pending--;
            }
        }
    }
}
