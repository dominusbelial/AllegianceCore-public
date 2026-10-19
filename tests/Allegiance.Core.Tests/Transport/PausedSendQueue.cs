using System.Reflection;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

/// <summary>Hold the real sender's queue lock while testing queue policy. Both
/// writer modes use this lock before draining. The LiveNetwork collection
/// isolates the temporary capacity override; no socket saturation is assumed.</summary>
internal sealed class PausedSendQueue : IDisposable
{
    private readonly object _sync;
    private readonly long _capacity = TcpTransport.CapacityBytes;

    public PausedSendQueue(object sender, long capacity)
    {
        _sync = sender.GetType().GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
        Monitor.Enter(_sync);
        TcpTransport.CapacityBytes = capacity;
    }

    public void Dispose()
    {
        TcpTransport.CapacityBytes = _capacity;
        Monitor.Exit(_sync);
    }
}
