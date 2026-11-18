namespace Allegiance.ClientCore.Session;

public sealed partial class ConnectionSession
{
    /// <summary>Native voluntary LOGOFF (FedSrv/FedSrv.CPP:5787-5802), then
    /// retire exactly this owner. Await the server close so WS queue cancellation
    /// cannot discard LOGOFF and accidentally park a deliberately departed ship.</summary>
    public async Task LeaveBrowserAsync()
    {
        ConnectionOwner? owner;
        lock (_gate)
        {
            owner = _owner;
            _autoReconnect = false;
            if (_transport?.IsConnected == true) SendLogoff();
        }
        long deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            lock (_gate)
                if (!ReferenceEquals(_owner, owner) || _transport?.IsConnected != true) break;
            await Task.Delay(10);
        }
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner)) return;
            owner?.Cancel();
        }
        if (owner is not null) await RetireTransportAsync(owner);
    }
}
