using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Session;

public sealed partial class ConnectionSession
{
    // All publication and callback admission uses Gate. Never wait for a worker
    // or shut down a transport while holding Gate: receive needs it to drain.
    private sealed class ConnectionOwner(CancellationTokenSource cancellation)
    {
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal Task Work = Task.CompletedTask;
        internal ITransport? Transport;
        internal OwnerSite? Site;
        internal bool ExplicitlyCancelled;
        internal CancellationToken Token => Cancellation.Token;
        internal void Cancel() { ExplicitlyCancelled = true; Cancellation.Cancel(); }
    }

    private ConnectionOwner? _owner;
    private bool _disposed;
    private TaskCompletionSource? _disposeCompletion;
    public bool IsDisposed { get { lock (_gate) return _disposed; } }

    // A caller may synchronously Dispose from its await continuation. Do not let
    // that continuation run inline ahead of the cleanup task's continuation on
    // the same completion stack (it would then wait for itself).
    private static Task<T> RunBackground<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try { completion.TrySetResult(await action().ConfigureAwait(false)); }
            catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    private bool Owns(ConnectionOwner owner) => !_disposed && ReferenceEquals(_owner, owner) && !owner.Token.IsCancellationRequested;
    private void RequireOwner(ConnectionOwner owner)
    {
        owner.Token.ThrowIfCancellationRequested();
        if (!Owns(owner)) throw new OperationCanceledException("connection superseded", owner.Token);
    }

    private sealed class OwnerSite(ConnectionSession session, ConnectionOwner owner) : ITransportSite
    {
        private bool Current => session.Owns(owner) && ReferenceEquals(owner.Site, this);
        public void OnPeerJoined(ConnectionId id, string name) { }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost()
            => session.RaiseLost(owner, this);
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
        {
            lock (session._gate)
                if (Current) ((ITransportSite)session).OnReceive(from, data);
        }
    }

    private Task<T> StartOwned<T>(Func<ConnectionOwner, Task<T>> operation, TimeSpan timeout,
        CancellationToken cancellationToken, bool reconnect = false)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = _owner;
            previous?.Cancel();
            var previousTransport = _transport;
            _transport = null;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellation.CancelAfter(timeout);
            var owner = new ConnectionOwner(cancellation);
            _owner = owner;
            _reconnecting = reconnect;
            var work = RunBackground(async () =>
            {
                try
                {
                    if (previous is not null)
                    {
                        try { await previous.Work.ConfigureAwait(false); }
                        catch (Exception) { /* observed; its caller owns the result */ }
                        RetireTransport(previous);
                        previous.Cancellation.Dispose();
                    }
                    previousTransport?.Shutdown();
                    lock (_gate) RequireOwner(owner);
                    var result = await operation(owner).ConfigureAwait(false);
                    lock (_gate)
                    {
                        RequireOwner(owner);
                        owner.Cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                    }
                    return result;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && OwnsDeadline(owner))
                {
                    RetireTransport(owner);
                    throw new TimeoutException("timed out joining the server");
                }
                catch
                {
                    RetireTransport(owner);
                    throw;
                }
                finally
                {
                    lock (_gate)
                        if (ReferenceEquals(_owner, owner)) _reconnecting = false;
                }
            });
            owner.Work = work;
            return work;
        }
    }

    private bool OwnsDeadline(ConnectionOwner owner)
    {
        lock (_gate) return !_disposed && ReferenceEquals(_owner, owner) && !owner.ExplicitlyCancelled && owner.Token.IsCancellationRequested;
    }

    private void RetireTransport(ConnectionOwner owner)
    {
        ITransport? transport;
        lock (_gate)
        {
            transport = owner.Transport;
            owner.Transport = null;
            owner.Site = null;
            if (ReferenceEquals(_owner, owner) && ReferenceEquals(_transport, transport)) _transport = null;
        }
        transport?.Shutdown();
    }

    private async Task ConnectTransportAsync(ConnectionOwner owner, string host, ushort port, string playerName)
    {
        var site = new OwnerSite(this, owner);
        ITransport transport;
        lock (_gate)
        {
            RequireOwner(owner);
            transport = _joinedInternet is not null ? new UdpTransport(site)
                : TransportFactoryForTesting?.Invoke(site) ?? TransportFactory.CreateClient(site, _joinedProtocol, _joinedUdpPort);
            owner.Site = site;
            owner.Transport = transport;
            _transport = transport;
        }
        if (_joinedInternet is { } internet)
        {
            var udp = (UdpTransport)transport;
            ConfigureTraversalForTesting?.Invoke(udp);
            if (TraversalHttpForTesting is { } http)
                await NatTraversalClient.ConnectAsync(udp, http, internet.ListingId, playerName, owner.Token).ConfigureAwait(false);
            else await NatTraversalClient.ConnectAsync(udp, internet, playerName, owner.Token).ConfigureAwait(false);
        }
        else await transport.JoinAsync(host, port, playerName, owner.Token).ConfigureAwait(false);
        lock (_gate) RequireOwner(owner);
    }

    /// <summary>One deadline covers TCP/UDP, identity HTTP, LOGONACK and the ordered
    /// mission-join answer. No UI state is read or written by this operation.</summary>
    public Task<DelPositionReq?> JoinAsync(ushort port, string playerName, LogonRequest request,
        string host = "127.0.0.1", Func<CancellationToken, Task<string>>? identityTicket = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? prepareTransport = null)
        => StartOwned(async owner =>
        {
            if (prepareTransport is not null) await prepareTransport(owner.Token).ConfigureAwait(false);
            await ConnectFreshAsync(owner, host, port, playerName).ConfigureAwait(false);
            if (identityTicket is not null)
            {
                string ticket = await identityTicket(owner.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    RequireOwner(owner);
                    request.ZoneTicket = System.Text.Encoding.UTF8.GetBytes(ticket);
                }
            }
            lock (_gate) { RequireOwner(owner); SendLogonRequest(request); }
            var ack = await WaitForAckAsync(owner).ConfigureAwait(false);
            if (!ack.FValidated)
                throw new InvalidOperationException(ack.CharNameOrFailureReason is { Length: > 0 } reason ? reason : "the server refused the logon");
            return await WaitForJoinAnswerAsync(owner).ConfigureAwait(false);
        }, timeout ?? TimeSpan.FromSeconds(15), cancellationToken);

    private async Task<LogonAck> WaitForAckAsync(ConnectionOwner owner)
    {
        while (true)
        {
            lock (_gate)
            {
                RequireOwner(owner);
                if (_ack is { } ack) return ack;
                if (_lost) throw new IOException("connection lost before logon completed");
            }
            await Task.Delay(5, owner.Token).ConfigureAwait(false);
        }
    }

    private async Task<DelPositionReq?> WaitForJoinAnswerAsync(ConnectionOwner owner)
    {
        while (true)
        {
            lock (_gate)
            {
                RequireOwner(owner);
                if (_lastPositionRefusal is { } refusal) return refusal;
                if (_world.SideRoster is not null || _world.MyShipId is not null) return null;
                if (_lost) throw new IOException("connection lost before the mission join completed");
            }
            await Task.Delay(5, owner.Token).ConfigureAwait(false);
        }
    }

    // Cancellation is immediate; completion means the connector and its socket
    // have actually drained. The game thread polls this task, never blocks on it.
    public Task ShutdownAsync()
    {
        lock (_gate)
        {
            var previous = _owner;
            previous?.Cancel();
            var priorWork = previous?.Work;
            var transport = _transport;
            _transport = null;
            _reconnecting = false;
            var work = RunBackground(async () =>
            {
                if (previous is not null)
                {
                    try { await priorWork!.ConfigureAwait(false); }
                    catch (Exception) { /* observe cancelled/failed establishment */ }
                    RetireTransport(previous);
                }
                transport?.Shutdown();
                return true;
            });
            if (previous is not null) previous.Work = work;
            return work;
        }
    }

    // Receive dispatch holds Gate and runs on the socket's own read thread.
    // It must return before shutdown can join that reader or acquire Gate.
    private void EndMissionConnection()
    {
        var owner = _owner;
        _lost = true;
        _ = NotifyMissionEndAsync(owner, ShutdownAsync());
    }

    private async Task NotifyMissionEndAsync(ConnectionOwner? owner, Task shutdown)
    {
        try { await shutdown.ConfigureAwait(false); }
        catch (Exception ex) { Console.WriteLine($"[session] mission departure shutdown failed: {ex.Message}"); }
        Action? notification;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_owner, owner)) return;
            notification = SessionLost;
        }
        notification?.Invoke();
    }
}
