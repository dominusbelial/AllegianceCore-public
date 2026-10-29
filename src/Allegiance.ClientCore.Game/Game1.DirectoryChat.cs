using Allegiance.ClientCore.MonoGame.Chat;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P871 / lobby::Lobby chat — the client's directory-wide chat slice (partial
/// <see cref="Game1"/>), rendered in the existing chat log so both HUD styles
/// (Classic and Modern) draw it from one shared model.
///
/// <para><b>Native scope (P871b correction).</b> Native has <b>no</b> lobby or
/// directory chat: every chat target is in-mission (<c>Igc/igc.h:2731-2734</c>)
/// and the lobby only ingests <c>FM_S_LOG_CHAT_MESSAGE(216)</c> for moderation
/// logging (<c>lobby/messagesls.h:152</c>, <c>lobby/server.cpp:341-372</c>).
/// P871 cited <c>clintlib/appmsg.cpp:2640-2665</c> for this row, but that is
/// <c>FM_CS_CHATMESSAGE</c> — the in-mission relay. Directory chat is therefore
/// a <b>port extension</b>; the rules it reuses are native's in-mission ones:
/// the sender's own echo is not re-shown (<c>trekigc.cpp:4492</c>), a muted
/// player's lines are dropped (<c>trekigc.cpp:4492</c> + the per-player
/// <c>PlayerInfo::SetMute</c>, <c>clintlib/clintlib.h:282,326</c>, toggled by
/// the context menu's 'M' <c>WinTrek/WinTrek.cpp:3731-3734,4310</c> and the
/// <c>!mute</c>/<c>!unmute</c> shell command, <c>clintlib/clintlib.cpp:2281-2296</c>).</para>
///
/// <para><b>Where it runs (P871b).</b> The relay is polled from the top-level
/// frame update, so the directory log fills on the intro/splash/browser screens
/// and the pre-game team lobby exactly as it does in flight — P871 only polled
/// inside <c>UpdateFlying</c>, so a player sitting in the lobby saw no chat at
/// all. Every mutating call carries the signed-in identity bearer.</para>
/// </summary>
public sealed partial class Game1
{
    /// <summary>The directory channel window (default channel = "directory").</summary>
    private readonly DirectoryChatLog _directoryChat = new();

    private LobbyChatClient? _directoryChatClient;
    private string? _directoryChatLobby;
    private Task? _directoryChatPollInFlight;
    private double _directoryChatNextPoll;

    /// <summary>The directory channel's window (the chat pane and the Options
    /// chat page read it; both HUD styles share it).</summary>
    internal DirectoryChatLog DirectoryChat => _directoryChat;

    /// <summary>The newest relayed line (the Options chat page's mute target).</summary>
    internal DirectoryChatLine? NewestDirectoryLine
    {
        get
        {
            var lines = _directoryChat.Lines;
            return lines.Count > 0 ? lines[^1] : null;
        }
    }

    /// <summary>Throttled live relay read (native chat traffic is push; the HTTP
    /// lobby is polled, so a short interval keeps it responsive without hammering
    /// the directory). Runs in EVERY client state: the directory log is where the
    /// chat pane's history comes from before the player is seated.</summary>
    private void UpdateDirectoryChat()
    {
        // Throttle BEFORE touching the preferences file: the poll interval is the
        // only cadence this needs (native chat is pushed, the HTTP lobby is polled).
        if (_nowSeconds < _directoryChatNextPoll) return;
        _directoryChatNextPoll = _nowSeconds + 2.0;
        // The client's configured lobby (`--lobby`, or the connect screen's
        // setting) wins over the remembered one: P871 read only player.json, so a
        // command-line lobby never received the chat relay.
        string lobby = string.IsNullOrWhiteSpace(_lobbyUrl) ? IntroPreferences.Load().Lobby : _lobbyUrl;
        if (string.IsNullOrWhiteSpace(lobby)) return;
        if (_directoryChatClient is null || _directoryChatLobby != lobby)
        {
            _directoryChatClient = new LobbyChatClient(_http, lobby, bearer: () => _identityClient?.SessionBearer);
            _directoryChatLobby = lobby;
        }
        if (_directoryChatPollInFlight is { IsCompleted: false }) return;
        var client = _directoryChatClient;
        // Fire-and-forget: the continuation appends to the log (the log is
        // internally locked; this never touches the session gate).
        _directoryChatPollInFlight = client.PollAsync(_directoryChat, ownAccountId: CurrentOwnAccountId());
    }

    /// <summary>The signed-in lobby account id (the relay's own-echo filter).</summary>
    private string? CurrentOwnAccountId() => _identityClient?.Account?.Id;

    /// <summary>Send a line to the directory channel (the <c>/lobby</c> console
    /// prefix, the port's channel selector for the HTTP directory).</summary>
    private void SendDirectoryChat(string text)
    {
        UpdateDirectoryChat();   // make sure the client exists
        if (_directoryChatClient is not { } client)
        {
            Console.WriteLine("[monogame] directory chat: no lobby URL configured");
            return;
        }
        var send = client.SendAsync(text);
        _ = send.ContinueWith(t =>
        {
            if (t.Result is { } sequence)
                Console.WriteLine($"[monogame] directory chat >> {text} (seq {sequence})");
            else
                Console.WriteLine("[monogame] directory chat: lobby refused the line (sign in to chat)");
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Mute/unmute a directory-chat sender — the port extension's use of
    /// native's per-player mute (<c>PlayerInfo::SetMute</c>,
    /// <c>clintlib/clintlib.h:326</c>; the context menu's 'M'
    /// <c>WinTrek/WinTrek.cpp:3731-3734,4310</c>). The line is dropped from the
    /// shared chat pane immediately (native drops it in
    /// <c>ReceiveChat</c>, <c>trekigc.cpp:4492</c>) and the lobby records the
    /// mute for the signed-in account so it survives a reconnect.
    /// <paramref name="sender"/> matches the relayed display name or account id,
    /// case-insensitively (native's <c>FindPlayer</c> is a name match,
    /// <c>clintlib/clintlib.cpp:2282</c>). Returns true when a sender matched.
    /// </summary>
    private bool SetDirectorySenderMuted(string? sender, bool mute)
    {
        if (string.IsNullOrWhiteSpace(sender)) return false;
        DirectoryChatLine? match = null;
        foreach (var line in _directoryChat.Lines)
            if (string.Equals(line.Sender, sender, StringComparison.OrdinalIgnoreCase)
                || string.Equals(line.SenderId, sender, StringComparison.OrdinalIgnoreCase))
                match = line;                       // keep the NEWEST match
        if (match is null) return false;
        if (mute) _directoryChat.Mute(match.SenderId);
        else _directoryChat.Unmute(match.SenderId);
        UpdateDirectoryChat();
        if (_directoryChatClient is { } client)
        {
            var call = mute ? client.MuteAsync(match.SenderId) : client.UnmuteAsync(match.SenderId);
            _ = call.ContinueWith(t => Console.WriteLine(
                $"[monogame] directory chat: {(mute ? "mute" : "unmute")} {match.Sender} -> "
                + (t.Result ? "lobby accepted" : "local only (lobby refused or unreachable)")),
                TaskScheduler.Default);
        }
        Console.WriteLine($"[monogame] directory chat: {(mute ? "muted" : "unmuted")} {match.Sender} ({match.SenderId})");
        return true;
    }
}
