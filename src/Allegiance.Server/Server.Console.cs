using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    internal const int ConsoleChatCapacity = 200;
    private const int ConsoleCommandLength = 1024;
    private const string ConsoleUsage = "usage: help | players | boot <id> | say <text> | whisper <id> <text> | status | chat [n|on|off] | alerts on|off | skills | override-tech <game-index> <side> <bit> on|off";
    private readonly Channel<string> _consoleCommands = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly string?[] _consoleChats = new string?[ConsoleChatCapacity];
    private int _consoleChatNext, _consoleChatCount;
    private bool _consoleChatEnabled;
    private bool _consoleAlertsEnabled = true;
    private readonly HashSet<ConnectionId> _consoleConnections = [];
    private long _consoleStarted;
    private string[] _consoleSecrets = [];

    private void InitializeConsole()
    {
        _consoleStarted = Stopwatch.GetTimestamp();
        _consoleConnections.Clear();
        Array.Clear(_consoleChats);
        _consoleChatNext = _consoleChatCount = 0;
        _consoleChatEnabled = false;
        _consoleAlertsEnabled = true;
        while (_consoleCommands.Reader.TryRead(out _)) { }
        // Only configured credentials are read, never LOGONREQ or identity tickets.
        _consoleSecrets = _config.ModeratorAccounts.Concat(new[]
        {
            _config.LiveSettings?.AdminCheats?.Secret,
            _config.LiveSettings?.TestHarness?.Secret,
            _config.BotAuthenticationSecret,
        }.OfType<string>()).Where(s => !string.IsNullOrEmpty(s))
            .SelectMany(s => new[] { s, StripConsoleControls(s) }).Where(s => s.Length > 0)
            .Distinct().OrderByDescending(s => s.Length).ToArray();
    }

    /// <summary>Thread-safe stdin/embedding entry point. A full queue rejects immediately;
    /// at most eight commands execute per tick, on the same owner thread as RequestSkills.</summary>
    public bool RequestConsoleCommand(string line) =>
        _consoleCommands.Writer.TryWrite(line.Length > ConsoleCommandLength ? "?" : line);

    // Runs on the existing background stdin reader. EOF does not stop the server.
    // Discard oversize lines without retaining them or echoing untrusted input.
    internal void ReadConsoleInput(TextReader input, CancellationToken stop)
    {
        var line = new StringBuilder(ConsoleCommandLength);
        bool oversize = false;
        while (!stop.IsCancellationRequested)
        {
            int c = input.Read();
            if (c < 0)
            {
                if (oversize || line.Length > 0) RequestConsoleCommand(oversize ? "?" : line.ToString());
                return;
            }
            if (c == '\n')
            {
                RequestConsoleCommand(oversize ? "?" : line.ToString());
                line.Clear();
                oversize = false;
            }
            else if (c != '\r')
            {
                if (line.Length < ConsoleCommandLength) line.Append((char)c);
                else oversize = true;
            }
        }
    }

    private void ProcessConsoleCommands()
    {
        for (int i = 0; i < 8 && _consoleCommands.Reader.TryRead(out var line); i++)
            ExecuteConsoleCommand(line);
    }

    private void ExecuteConsoleCommand(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return;
        int split = line.IndexOfAny([' ', '\t']);
        string verb = (split < 0 ? line : line[..split]).ToLowerInvariant();
        string rest = split < 0 ? "" : line[(split + 1)..].Trim();
        switch (verb)
        {
            case "help" when rest.Length == 0: Console.WriteLine(ConsoleUsage); return;
            case "skills" when rest.Length == 0: RequestSkills(); return;
            case "players" when rest.Length == 0: PrintConsolePlayers(); return;
            case "status" when rest.Length == 0: PrintConsoleStatus(); return;
            case "boot" when TryConsoleId(rest, out short bootId):
                BootConsolePlayer(bootId); return;
            case "say" when rest.Length > 0:
                SendConsoleChat(null, rest); return;
            case "override-tech":
                var args = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (args.Length == 4 && TryConsoleId(args[0], out short game) && game < _missions.Count
                    && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int side)
                    && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bit)
                    && (args[3] == "on" || args[3] == "off"))
                {
                    bool accepted = _missions[game].OverrideTechBit(side, bit, args[3] == "on");
                    Console.WriteLine($"[console] override-tech game={game} side={side} bit={bit} {args[3]} {(accepted ? "accepted" : "refused")}");
                    return;
                }
                break;
            case "whisper":
                int space = rest.IndexOfAny([' ', '\t']);
                if (space > 0 && TryConsoleId(rest[..space], out short id) && rest[(space + 1)..].Trim().Length > 0)
                { SendConsoleChat(id, rest[(space + 1)..].Trim()); return; }
                break;
            case "chat":
                if (rest.Equals("on", StringComparison.OrdinalIgnoreCase) || rest.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    _consoleChatEnabled = rest.Equals("on", StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine($"[console] chat={(_consoleChatEnabled ? "on" : "off")} capacity={ConsoleChatCapacity}");
                    return;
                }
                int count = 20;
                if (rest.Length != 0 && (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out count)
                    || count < 1 || count > ConsoleChatCapacity)) break;
                count = Math.Min(count, _consoleChatCount);
                Console.WriteLine($"[console] chat history={count} retained={_consoleChatCount} enabled={_consoleChatEnabled}");
                for (int n = count; n > 0; n--)
                    Console.WriteLine(_consoleChats[(_consoleChatNext - n + ConsoleChatCapacity) % ConsoleChatCapacity]);
                return;
            case "alerts" when rest.Equals("on", StringComparison.OrdinalIgnoreCase) || rest.Equals("off", StringComparison.OrdinalIgnoreCase):
                _consoleAlertsEnabled = rest.Equals("on", StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"[console] alerts={(_consoleAlertsEnabled ? "on" : "off")}"); return;
        }
        // Never echo malformed input: it might be a credential pasted at the prompt.
        Console.WriteLine(ConsoleUsage);
    }

    private static bool TryConsoleId(string text, out short id) =>
        short.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id >= 0;

    // AllSrvUI/PagePlayers.cpp:94-120,431-454,472-511: login/logout/team/sector
    // events maintain name/team/sector; query the live state instead of a second roster.
    private void PrintConsolePlayers()
    {
        Console.WriteLine($"[console] players={_peerShips.Count + _pendingSideSelect.Count} (id=ship/user id)");
        foreach (var peer in _peerShips)
        {
            var ship = peer.Value.Ship;
            var mission = ship.Mission;
            var side = mission?.Sides.FirstOrDefault(s => s.Id == ship.SideId);
            Console.WriteLine($"[player] id={ship.Id} peer={peer.Key} name='{ConsoleText(_peerNames.GetValueOrDefault(peer.Key))}' " +
                $"game='{ConsoleText(mission is null ? "" : GameSettings(mission).Name)}' team='{ConsoleText(side?.Name ?? "lobby")}' " +
                $"sector='{ConsoleText(ship.Sector?.Name ?? "")}' state={(ship.ParentShip is not null ? "aboard" : ship.StationId is not null ? "docked" : ship.Sector is null ? "lobby" : "flying")}");
        }
        foreach (var peer in _pendingSideSelect)
            Console.WriteLine($"[player] id={peer.Value.ShipId} peer={peer.Key} name='{ConsoleText(peer.Value.Request.CharacterName)}' " +
                $"game='{ConsoleText(GameSettings(peer.Value.Mission).Name)}' team='lobby' sector='' state=side-select");
    }

    // AllSrvUISheet.cpp:155-204 shows stage. FedSrv/counters.h:4-36 supplies
    // activity counters; read cumulative counters without draining profiler windows.
    private void PrintConsoleStatus()
    {
        Console.WriteLine(FormattableString.Invariant($"[console] status connections={_consoleConnections.Count} players={_peerShips.Count + _pendingSideSelect.Count} seated={_peerShips.Count} missions={_missions.Count} uptime={Stopwatch.GetElapsedTime(_consoleStarted).TotalSeconds:F1}s ticks={LoopIterations} overruns={Overruns} packetsOut={Counters.CPacketsOut} bytesOut={Counters.CBytesOut} timeouts={Counters.CTimeouts}"));
        foreach (var mission in _missions)
            Console.WriteLine($"[console] game='{ConsoleText(GameSettings(mission).Name)}' stage={mission.Stage}");
    }

    // AllSrvUI/PagePlayers.cpp:776-829 -> FedSrv/AdminUser.cpp:168-175:
    // QSR_AdminBooted, via the same guaranteed notice/departure path as ADM-0001.
    private void BootConsolePlayer(short id)
    {
        foreach (var peer in _peerShips)
            if (peer.Value.Ship.Id == id)
            { BootAdminPlayer(peer.Key, id, QuitSideReason.AdminBooted, ""); Console.WriteLine($"[console] boot id={id}"); return; }
        foreach (var peer in _pendingSideSelect)
            if (peer.Value.ShipId == id)
            { BootAdminPlayer(peer.Key, id, QuitSideReason.AdminBooted, ""); Console.WriteLine($"[console] boot id={id}"); return; }
        Console.WriteLine($"[console] no player id={id}");
    }

    // AllSrvUI/PageChat.cpp:515-520 (all games); PagePlayers.cpp:392-418
    // (selected users). FedSrv/AdminUser.cpp:114-124 and AdminServer.cpp:121-133
    // stamp object-model system chat with NA targets and no command/voice.
    private void SendConsoleChat(short? id, string text)
    {
        text = ConsoleText(text, ConsoleCommandLength);
        if (text.Length == 0) { Console.WriteLine(ConsoleUsage); return; }
        if (id is { } target)
        {
            var peer = _peerShips.FirstOrDefault(p => p.Value.Ship.Id == target);
            var pending = _pendingSideSelect.FirstOrDefault(p => p.Value.ShipId == target);
            if (peer.Value is null && pending.Value.Mission is null) { Console.WriteLine($"[console] no player id={target}"); return; }
            var chat = new ChatMessage { SidSender = -1, ChatTarget = (int)ChatTarget.Individual, OidRecipient = target, CommandID = OrderChange.CidNone, BObjectModel = true, OtTarget = -1, OidTarget = -1, Message = text };
            SendPeer(peer.Value is null ? pending.Key : peer.Key, FedPacket.Pack([chat.Encode()]));
            ObserveConsoleChat(peer.Value?.Ship.Mission ?? pending.Value.Mission, "SERVER", chat);
        }
        else
            foreach (var mission in _missions)
            {
                var chat = new ChatMessage { SidSender = -1, ChatTarget = (int)ChatTarget.Everyone, OidRecipient = -1, CommandID = OrderChange.CidNone, BObjectModel = true, OtTarget = -1, OidTarget = -1, Message = text };
                var packet = FedPacket.Pack([chat.Encode()]);
                foreach (var peer in _peerShips)
                    if (ReferenceEquals(peer.Value.Ship.Mission, mission)) SendPeer(peer.Key, packet);
                foreach (var peer in _pendingSideSelect)
                    if (ReferenceEquals(peer.Value.Mission, mission)) SendPeer(peer.Key, packet);
                ObserveConsoleChat(mission, "SERVER", chat);
            }
        Console.WriteLine(id is { } to ? $"[console] whisper id={to} sent" : "[console] say sent to all games");
    }

    // FedSrv/FedSrv.CPP:3129-3130,3163-3171,3599: only CHAT_ADMIN raises
    // AdminPage. PageChat.cpp:107-183 displays events and dispatches preferences.
    // AdminPageThreadWnd.cpp:182-213: replace message box / shortcut with one
    // timestamped console line (no shell execution, no client delivery).
    internal void ObserveConsoleChat(Mission mission, string? sender, ChatMessage chat)
        => ObserveConsoleChat(mission, sender, chat.Cd, chat.Message);

    private void ObserveConsoleChat(Mission mission, string? sender, ChatData chat, string message)
    {
        bool alert = chat.ChatTarget == (int)ChatTarget.Admin;
        if (!_consoleChatEnabled && (!alert || !_consoleAlertsEnabled)) return;
        string entry = $"[{(alert && _consoleAlertsEnabled ? "admin-alert" : "admin-chat")}] {DateTimeOffset.UtcNow:O} " +
            $"game='{ConsoleText(GameSettings(mission).Name)}' sender='{ConsoleText(sender ?? "SERVER")}' " +
            $"target={(ChatTarget)chat.ChatTarget} recipient={chat.OidRecipient} message='{ConsoleText(message, ConsoleCommandLength)}'";
        if (_consoleChatEnabled)
        {
            _consoleChats[_consoleChatNext] = entry;
            _consoleChatNext = (_consoleChatNext + 1) % ConsoleChatCapacity;
            _consoleChatCount = Math.Min(_consoleChatCount + 1, ConsoleChatCapacity);
        }
        if (_consoleChatEnabled || (alert && _consoleAlertsEnabled)) Console.WriteLine(entry);
    }

    private string ConsoleText(string? text, int limit = 128)
    {
        text ??= "";
        // Redact BEFORE truncation so a cut-off credential cannot escape as a prefix.
        foreach (var secret in _consoleSecrets) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        text = StripConsoleControls(text);
        foreach (var secret in _consoleSecrets) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return text.Length > limit ? text[..limit] : text;
    }

    private static string StripConsoleControls(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (char c in text)
            if (!char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format) result.Append(c);
        return result.ToString();
    }
}
