using System.Net;
using System.Security.Cryptography;
using Allegiance.Core.Transport;

namespace Allegiance.Lobby;

/// <summary>Bounded UDP observations. Receipts are delivered only to the endpoint
/// they authorize and are never exposed in listings. No client supplied target.</summary>
public sealed class NatObservationStore(TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Observation> _observations = new(StringComparer.Ordinal);
    private readonly Dictionary<IPAddress, Window> _sources = new();
    private long _lastSweep;
    private Window _global = new(0, 0);
    public int Respond(ReadOnlySpan<byte> packet, IPEndPoint source, bool primary, Span<byte> response)
    {
        if (!StunBinding.IsRequest(packet)) return 0;
        source = Normalize(source);
        lock (_gate)
        {
            long now = time.GetUtcNow().ToUnixTimeMilliseconds();
            if (now - _lastSweep > 1000)
            {
                _lastSweep = now;
                foreach (var key in _observations.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) _observations.Remove(key);
                foreach (var key in _sources.Where(x => now - x.Value.Start > 10000).Select(x => x.Key).ToArray()) _sources.Remove(key);
            }
            if (!_sources.ContainsKey(source.Address) && _sources.Count >= 4096 || _observations.Count >= 8192) return 0;
            if (!Take(ref _global, now, 512, 1000)) return 0;
            var window = _sources.GetValueOrDefault(source.Address, new(now, 0));
            bool allowed = Take(ref window, now, 16, 1000);
            _sources[source.Address] = window;
            if (!allowed) return 0;
            Span<byte> receipt = stackalloc byte[16];
            RandomNumberGenerator.Fill(receipt);
            int written = StunBinding.WriteSuccess(packet, source, receipt, response);
            if (written != 0 && packet.Length >= StunBinding.RequestSize)
                _observations[Convert.ToHexString(receipt)] = new(source, primary, now + 30000);
            return written;
        }
    }

    public IPEndPoint? Resolve(string? receipt, IPAddress source)
    {
        if (receipt?.Length != 32) return null;
        source = source.IsIPv4MappedToIPv6 ? source.MapToIPv4() : source;
        lock (_gate)
            return _observations.TryGetValue(receipt, out var observation) && observation.Primary
                && observation.Expires > time.GetUtcNow().ToUnixTimeMilliseconds() && observation.Endpoint.Address.Equals(source)
                ? observation.Endpoint : null;
    }
    internal static IPEndPoint Normalize(IPEndPoint endpoint) => endpoint.Address.IsIPv4MappedToIPv6
        ? new(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint;
    private sealed record Observation(IPEndPoint Endpoint, bool Primary, long Expires);
    internal record struct Window(long Start, int Count);
    internal static bool Take(ref Window window, long now, int limit, int milliseconds)
    {
        if (now - window.Start >= milliseconds) window = new(now, 0);
        if (window.Count >= limit) return false;
        window.Count++;
        return true;
    }
}
