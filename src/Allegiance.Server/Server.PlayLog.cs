using Allegiance.Core.Model;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

public sealed partial class Server
{
    private PlayLogWriter? _playLog;
    private readonly string _playRun = Guid.NewGuid().ToString("N");
    private readonly Dictionary<Mission, PlayMatch> _playMatches = new();
    private readonly Dictionary<ConnectionId, PlaySession> _playSessions = new();
    public long PlayLogDropped => _playLog?.Dropped ?? 0;
    private sealed class PlayMatch(string id, PlayCiv[] civs, string map)
    {
        internal readonly string Id = id;
        internal readonly DateTimeOffset Start = DateTimeOffset.UtcNow;
        internal readonly PlayCiv[] Civs = civs;
        internal readonly string Map = map;
        internal readonly HashSet<string> Humans = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Bots = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class PlaySession(string callsign, string kind, string client, Mission mission, PlayMatch match, bool bot)
    {
        internal readonly DateTimeOffset Start = DateTimeOffset.UtcNow;
        internal readonly string Callsign = callsign, Kind = kind, Client = client;
        internal readonly Mission Mission = mission;
        internal readonly PlayMatch Match = match;
        internal readonly bool Bot = bot;
        internal PlayerScore? Score;
        internal int Kills, Deaths, Ejects;
        internal bool Launched;
        internal PlayCiv? SideCiv;
    }

    private void BeginPlayMatch(Mission mission)
    {
        if (_playLog is null) return;
        var match = _matchups[mission];
        _playMatches[mission] = new($"{_playRun}-{match.Number}",
            mission.Sides.Select(s => new PlayCiv(s.Id, s.Civilization.CivilizationId, s.Civilization.Name)).ToArray(),
            match.MapName);
    }

    private void BeginPlaySession(ConnectionId peer, string name, Mission mission)
    {
        if (_playLog is null || _playSessions.ContainsKey(peer) || !_playMatches.TryGetValue(mission, out var match)) return;
        bool bot = _config.PlayLog.IsBot(name);
        string protocol = _host.Transport?.PeerProtocolName(peer) ?? "unknown";
        string client = protocol switch { "websocket" => "browser", "tcp" or "udp" => "desktop", _ => "unknown" };
        _playSessions[peer] = new(name, _peerIdentities.ContainsKey(peer) ? "account" : "guest", client, mission, match, bot);
        (bot ? match.Bots : match.Humans).Add(name);
    }

    private void AttachPlayScore(ConnectionId peer, LiveShip live)
    {
        if (_playLog is null) return;
        if (!_playSessions.ContainsKey(peer) && live.Ship.Mission is { } mission)
            BeginPlaySession(peer, _peerNames.GetValueOrDefault(peer) ?? live.Ship.Name ?? "", mission);
        if (!_playSessions.TryGetValue(peer, out var session)) return;
        if (!ReferenceEquals(session.Score, live.PlayerScore))
        {
            session.Score = live.PlayerScore;
            // Native old-player scores survive rejoin (fsmission.cpp:378-401).
            // Subtract the entry baseline so a session never recounts earlier kills.
            session.Kills = live.PlayerScore.Kills;
            session.Deaths = live.PlayerScore.Deaths;
            session.Ejects = live.PlayerScore.Ejections;
        }
        session.Launched |= live.Ship.StationId is null && live.Ship.Sector is not null && live.Ship.SideId is >= 0;
        short side = live.Ship.SideId is >= 0 ? live.Ship.SideId.Value : live.LastSideId;
        session.SideCiv = session.Match.Civs.FirstOrDefault(c => c.Side == side);
    }

    private void EndPlaySession(ConnectionId peer)
    {
        if (_playLog is null || !_playSessions.TryGetValue(peer, out var session)) return;
        if (_peerShips.TryGetValue(peer, out var live)) AttachPlayScore(peer, live);
        _playSessions.Remove(peer);
        if (session.Bot) return;
        var end = DateTimeOffset.UtcNow;
        _playLog.TryWrite(new PlaySessionEntry(session.Start, end, Math.Max(0, (end - session.Start).TotalSeconds),
            session.Callsign, session.Kind, session.Client, session.Match.Id, session.Match.Civs, session.SideCiv,
            Math.Max(0, (session.Score?.Kills ?? 0) - session.Kills),
            Math.Max(0, (session.Score?.Deaths ?? 0) - session.Deaths),
            Math.Max(0, (session.Score?.Ejections ?? 0) - session.Ejects), session.Launched));
    }

    private void EndPlayMatch(Mission mission, short? winner)
    {
        if (_playLog is null || !_playMatches.TryGetValue(mission, out var match)) return;
        foreach (var peer in _playSessions.Where(p => ReferenceEquals(p.Value.Mission, mission)).Select(p => p.Key).ToArray())
            EndPlaySession(peer);
        _playMatches.Remove(mission);
        _playLog.TryWrite(new PlayMatchEntry(match.Id, match.Start, DateTimeOffset.UtcNow, match.Civs,
            match.Map, match.Humans.Count, match.Bots.Count, winner));
    }

    private void ShutdownPlayLog()
    {
        if (_playLog is null) return;
        foreach (var mission in _playMatches.Keys.ToArray()) EndPlayMatch(mission, null);
        _playLog.Dispose();
        _playLog = null;
    }
}
