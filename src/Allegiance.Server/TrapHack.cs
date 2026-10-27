using Allegiance.Core.Transport;
using ConsoleLog = global::Allegiance.Core.ConsoleLog;

namespace Allegiance.Server;

/// <summary>
/// P855/SIM-0809 — the native trap-hack event kinds
/// (<c>FedSrv/FedSrv.CPP:2749-2798 LogTrapHack</c>, dispatched by the
/// <c>TrapHackLog</c>/<c>TrapHackBoot</c>/<c>TrapHackBootNoPlayer</c> macros,
/// FedSrv.CPP:4662-4665): a message-validation failure is either logged
/// (<c>EventID_HackLog</c>), logged AND the offending client disconnected
/// (<c>EventID_HackBoot</c>), or, when no player is associated with the
/// connection yet, logged and disconnected (<c>EventID_HackBootNoPlayer</c>,
/// e.g. the logon-var checks at FedSrv.CPP:5817-5821).
/// </summary>
internal enum TrapHackKind
{
    Log,
    Boot,
    BootNoPlayer,
}

/// <summary>
/// P855/SIM-0809 — formatting for the structured operator-log line one
/// validation failure produces. Native's <c>LogTrapHack</c> reports who
/// (<c>pszSubject</c> = the player name), what (<c>pExpr</c> = the failed
/// check, stringized by the macros) and where (<c>nLine</c> = the native
/// <c>__LINE__</c>), then triggers an <c>AGCEventID_Hack*</c> context event —
/// <see cref="FormatLine"/> is that operator record. The check text and the
/// native source site are always present; player/ship are omitted for the
/// no-player kinds exactly as native asserts them absent.
/// <para>Everything a client can influence (player name) goes through
/// <see cref="ConsoleLog.Sanitize"/> first — the same log-forging defence the
/// rest of the server applies to client-supplied strings.</para>
/// </summary>
internal static class TrapHackEvents
{
    /// <summary>One structured operator-log line. Native emits
    /// "HackBoot &lt;subject&gt;: &lt;expr&gt; &lt;line&gt;" (FedSrv.CPP:2777);
    /// the port's line is key=value so an operator can grep any single field.</summary>
    public static string FormatLine(TrapHackKind kind, string? player, int? shipId,
        string? connection, string check, string nativeSite)
    {
        var sb = new System.Text.StringBuilder("[traphack] kind=");
        sb.Append(kind switch
        {
            TrapHackKind.Log => "log",
            TrapHackKind.Boot => "boot",
            _ => "boot-no-player",
        });
        sb.Append(" check=\"").Append(check).Append('"');
        sb.Append(" site=").Append(nativeSite);
        if (player is not null) sb.Append(" player=\"").Append(ConsoleLog.Sanitize(player, 64)).Append('"');
        if (shipId is not null) sb.Append(" ship=").Append(shipId.Value);
        if (connection is not null) sb.Append(" conn=").Append(connection);
        // The recorded design decision (standing convention since P182/P849):
        // native boots the offending connection (g.fm.DeleteConnection,
        // FedSrv.CPP:2779-2788); this port deliberately keeps log-and-ignore.
        sb.Append(" action=log-and-ignore (native boots; this port does not disconnect)");
        return sb.ToString();
    }
}

/// <summary>
/// P855/SIM-0809 — per-source rate limit for trap-hack operator events, so a
/// client that spams invalid messages cannot flood the operator log (native
/// disconnects the spammer on the first bad message instead — one of the two
/// behaviours the port's softening trades; the operator event must not inherit
/// the flood risk). Fixed-window per connection: at most
/// <see cref="_maxPerWindow"/> events per <see cref="_windowMs"/>; events
/// beyond the budget are suppressed and their count is reported once when the
/// next window opens (<see cref="TakeSuppressed"/>).
/// </summary>
internal sealed class TrapHackRateLimiter
{
    private readonly int _maxPerWindow;
    private readonly long _windowMs;
    private readonly Func<long> _nowMs;
    private sealed class Bucket { public long Start; public int Count; public int Suppressed; }
    private readonly Dictionary<string, Bucket> _buckets = new();

    public TrapHackRateLimiter(int maxPerWindow, long windowMs, Func<long>? nowMs = null)
    {
        _maxPerWindow = maxPerWindow;
        _windowMs = windowMs;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>Try to spend one event budget for <paramref name="source"/>.
    /// true = emit the event; false = suppress it (the count accumulates).</summary>
    public bool TryAcquire(string source)
    {
        long now = _nowMs();
        lock (_buckets)
        {
            if (!_buckets.TryGetValue(source, out var b))
            {
                _buckets[source] = new Bucket { Start = now, Count = 1 };
                return true;
            }
            if (now - b.Start >= _windowMs)
            {
                // Window rolled over; a fresh window starts with the budget reset.
                b.Start = now; b.Count = 1; b.Suppressed = 0;
                return true;
            }
            if (b.Count < _maxPerWindow) { b.Count++; return true; }
            b.Suppressed++;
            return false;
        }
    }

    /// <summary>How many events were suppressed for <paramref name="source"/>
    /// since the last take (and reported only once).</summary>
    public int TakeSuppressed(string source)
    {
        lock (_buckets)
        {
            if (!_buckets.TryGetValue(source, out var b)) return 0;
            int n = b.Suppressed;
            b.Suppressed = 0;
            return n;
        }
    }
}

/// <summary>P855/SIM-0809 — the server half: every soft refusal that stands in
/// for a native <c>TrapHack*</c> check reports through <see cref="ReportTrapHack"/>
/// (see the call sites in Server.ShipCommands/Economy/Dispatch/Moderation/
/// Lobby/TeamCiv). The port's standing convention (P182/P184/P186, P441, P849)
/// is that native's harsh trap — a disconnect — is softened to
/// log-and-ignore; what was missing is the structured operator event, not the
/// boot. <b>Design decision (recorded, not an oversight):</b> the port does
/// NOT disconnect on the Boot kinds. The kind is still recorded on the line so
/// an operator can see what native would have done.</summary>
public sealed partial class Server
{
    /// <summary>Per-connection trap-hack event budget (8 events per second;
    /// a validation-failure flood beyond that is suppressed, not logged).</summary>
    private readonly TrapHackRateLimiter _trapHackLimiter = new(maxPerWindow: 8, windowMs: 1_000);

    /// <summary>Emit one structured operator event for a message-validation
    /// failure (the port's LogTrapHack). Rate-limited per connection; a
    /// suppressed burst collapses into one summary line when the window rolls.
    /// The player name comes from the server's own peer table, never from the
    /// message. Runs on the sim thread — the line is a single Console write.</summary>
    private void ReportTrapHack(TrapHackKind kind, ConnectionId from, int? shipId,
        string check, string nativeSite)
    {
        string? player = _peerNames.TryGetValue(from, out var n) ? n : null;
        if (!_trapHackLimiter.TryAcquire(from.ToString()))
            return;
        int suppressed = _trapHackLimiter.TakeSuppressed(from.ToString());
        if (suppressed > 0)
            Console.WriteLine($"[traphack] conn={from}: suppressed {suppressed} further trap-hack event(s) within the rate-limit window");
        Console.WriteLine(TrapHackEvents.FormatLine(kind, player, shipId, from.ToString(), check, nativeSite));
    }

    /// <summary>No-player variant (native <c>TrapHackBootNoPlayer</c>): the
    /// connection has no seated ship/player to name — only the connection is
    /// known (native carries no IP either, FedSrv.CPP:4665's REVIEW note).</summary>
    private void ReportTrapHackNoPlayer(ConnectionId from, string check, string nativeSite)
    {
        if (!_trapHackLimiter.TryAcquire(from.ToString()))
            return;
        int suppressed = _trapHackLimiter.TakeSuppressed(from.ToString());
        if (suppressed > 0)
            Console.WriteLine($"[traphack] conn={from}: suppressed {suppressed} further trap-hack event(s) within the rate-limit window");
        Console.WriteLine(TrapHackEvents.FormatLine(TrapHackKind.BootNoPlayer, null, null, from.ToString(), check, nativeSite));
    }
}
