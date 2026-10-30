using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Allegiance.TransportProof;

/// <summary>Proof-only reliable stream impairment model. Chunks of at most 1200
/// bytes get seeded delay/jitter and a 200 ms recovery penalty for modeled loss.
/// FIFO delivery makes that penalty block subsequent chunks. This is explicitly
/// NOT kernel TCP segment loss or a measurement of the OS TCP retransmission timer.</summary>
public sealed class ImpairedTcpProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private TcpClient? _front, _back;
    private long _bytes, _losses;
    public ushort Port => (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Dropped => Interlocked.Read(ref _losses);
    public ImpairedTcpProxy(ushort serverPort, double loss, int delay, int jitter, int seed)
    {
        _listener.Start();
        _run = Run(serverPort, loss, delay, jitter, seed);
    }
    private async Task Run(ushort port, double loss, int delay, int jitter, int seed)
    {
        try
        {
            _front = await _listener.AcceptTcpClientAsync(_stop.Token);
            _back = new TcpClient(); await _back.ConnectAsync(IPAddress.Loopback, port, _stop.Token);
            _front.NoDelay = _back.NoDelay = true;
            await Task.WhenAll(Pipe(_front.GetStream(), _back.GetStream(), loss, delay, jitter, seed),
                Pipe(_back.GetStream(), _front.GetStream(), loss, delay, jitter, seed + 1));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
    }
    private async Task Pipe(NetworkStream source, NetworkStream target, double loss, int delay, int jitter, int seed)
    {
        var pending = Channel.CreateBounded<(byte[] Data, long Due)>(4096);
        var rng = new Random(seed);
        var reader = Task.Run(async () =>
        {
            var buffer = new byte[1200];
            try
            {
                while (true)
                {
                    int n = await source.ReadAsync(buffer, _stop.Token);
                    if (n == 0) break;
                    long due = Environment.TickCount64 + Math.Max(0, delay + (jitter == 0 ? 0 : rng.Next(-jitter, jitter + 1)));
                    for (int retries = 0; retries < 10 && rng.NextDouble() < loss; retries++)
                    { due += 200; Interlocked.Increment(ref _losses); }
                    await pending.Writer.WriteAsync((buffer.AsSpan(0, n).ToArray(), due), _stop.Token);
                }
            }
            finally { pending.Writer.TryComplete(); }
        }, _stop.Token);
        try
        {
            await foreach (var item in pending.Reader.ReadAllAsync(_stop.Token))
            {
                int wait = (int)Math.Max(0, item.Due - Environment.TickCount64);
                if (wait > 0) await Task.Delay(wait, _stop.Token);
                await target.WriteAsync(item.Data, _stop.Token);
                Interlocked.Add(ref _bytes, item.Data.Length);
            }
        }
        finally { await reader; }
    }
    public void Dispose()
    {
        _stop.Cancel(); _listener.Stop(); _front?.Dispose(); _back?.Dispose();
        try { _run.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
