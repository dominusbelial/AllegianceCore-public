namespace Allegiance.Tools.McpSpawner;

/// <summary>Owns a peer until the registry accepts it, including post-seat failures.</summary>
internal sealed class PendingOwnership : IDisposable
{
    private IDisposable? _peer;
    private Action? _rollback;
    public T Own<T>(T peer) where T : IDisposable { _peer = peer; return peer; }
    public void OnFailure(Action rollback) => _rollback = rollback;
    public void Transfer() { _peer = null; _rollback = null; }
    public void Dispose()
    {
        try { _rollback?.Invoke(); }
        finally { _peer?.Dispose(); }
    }
}
