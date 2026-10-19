using Allegiance.Core.Model;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    private readonly Dictionary<ConnectionId, (Mission Mission, byte[] Notice)> _lastBaseNotices = [];

    // Complete per-peer snapshots: changed station/countdown state is sent immediately;
    // a new join/drop-in receives it even when the mission state has not changed.
    private void AdvanceLastBaseObjectives(double now)
    {
        foreach (var gone in _lastBaseNotices.Keys.Where(p => !_peerShips.ContainsKey(p)).ToArray())
            _lastBaseNotices.Remove(gone);
        foreach (var mission in _missions)
        {
            var notice = new LastBaseNotice
            {
                FinaleSecondsRemaining = _researchFinales.TryGetValue(mission, out var finale)
                    ? (float)Math.Ceiling(Math.Max(0, finale.Deadline - now)) : -1,
                Targets = mission.LastRestartBases().Select(st => new LastBaseTarget(st.SideId, st.StationId,
                    st.Data.ClusterId, st.Center.X, st.Center.Y, st.Center.Z, st.Data.Name)).ToArray(),
            }.Encode();
            byte[]? packet = null;
            foreach (var (peer, live) in _peerShips)
            {
                if (!ReferenceEquals(live.Ship.Mission, mission)) continue;
                if (_lastBaseNotices.TryGetValue(peer, out var old) && ReferenceEquals(old.Mission, mission) && old.Notice.AsSpan().SequenceEqual(notice)) continue;
                packet ??= FedPacket.Pack([notice]);
                SendPeer(peer, packet);
                _lastBaseNotices[peer] = (mission, notice);
            }
        }
    }
}
