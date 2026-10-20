using Allegiance.Core.Sim;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    private readonly Dictionary<ConnectionId, (Mission Mission, byte[] Notice)> _lastBaseNotices = [];
    // Peak restart-capable station count per side this match. Every side STARTS with one Garrison, so "one
    // restart base left" alone is the normal opening state, not a threat (Domingo's skirmish showed "Our last
    // Garrison: defend it" from minute one). The objective appears when the finale is armed, or when a side
    // that had more than one restart base is down to its last.
    private readonly Dictionary<(Mission Mission, short Side), int> _restartBasePeak = [];

    // Complete per-peer snapshots: changed station/countdown state is sent immediately;
    // a new join/drop-in receives it even when the mission state has not changed.
    private void AdvanceLastBaseObjectives(double now)
    {
        foreach (var gone in _lastBaseNotices.Keys.Where(p => !_peerShips.ContainsKey(p)).ToArray())
            _lastBaseNotices.Remove(gone);
        foreach (var key in _restartBasePeak.Keys.Where(k => !_missions.Contains(k.Mission)
                     || k.Mission.Stage != MissionStage.Started).ToArray())
            _restartBasePeak.Remove(key);   // a new match starts counting from scratch
        foreach (var mission in _missions)
        {
            if (mission.Stage == MissionStage.Started)
                foreach (var group in mission.Sectors.SelectMany(s => s.StationRuntimes)
                             .Where(st => !st.Dead && st.Type.HasCapability(DataStationTypeIGC.SabmRestart))
                             .GroupBy(st => st.SideId))
                    _restartBasePeak[(mission, group.Key)] =
                        Math.Max(_restartBasePeak.GetValueOrDefault((mission, group.Key)), group.Count());
            bool finaleArmed = _researchFinales.TryGetValue(mission, out var finale);
            var notice = new LastBaseNotice
            {
                FinaleSecondsRemaining = finaleArmed ? (float)Math.Ceiling(Math.Max(0, finale.Deadline - now)) : -1,
                Targets = mission.LastRestartBases()
                    .Where(st => finaleArmed || _restartBasePeak.GetValueOrDefault((mission, st.SideId)) > 1)
                    .Select(st => new LastBaseTarget(st.SideId, st.StationId,
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
