namespace Allegiance.Lobby.Events;

/// <summary>
/// Lobby zone-events wire contracts (P820b). These mirror the native
/// <c>WinTrek/zoneeventsscreen.cpp</c> event listing the club's HTTP download
/// fed to the client (<c>EventListing</c>, zoneeventsscreen.cpp:42-73): each
/// row is a date, time, name, description, sign-up URL and a list of attached
/// file names/URLs, rendered in the four columns
/// <c>colDate / colTime / colName / colDescription</c>
/// (zoneeventsscreen.cpp:19-26). Delivered over HTTP instead of the native
/// club download; the modern lobby stores events in its shared store and serves
/// them read-only to players, with an operator-authenticated management
/// surface.
/// </summary>
public static class EventContracts
{
    /// <summary>Maximum event name length (generous; native has no fixed cap on
    /// the event name field).</summary>
    public const int MaxName = 64;
    /// <summary>Maximum event description length.</summary>
    public const int MaxDescription = 512;
    /// <summary>Maximum sign-up URL length (native <c>c_cbURLDB</c>,
    /// <c>src/Zone/Zone.h:5</c>).</summary>
    public const int MaxUrl = 255;
    /// <summary>Maximum number of attached files per event.</summary>
    public const int MaxFiles = 16;

    /// <summary>
    /// One zone event — the port of the native <c>EventListing</c>
    /// (zoneeventsscreen.cpp:42-73). <c>Date</c>/<c>Time</c> are display strings
    /// exactly as the operator enters them; <c>SignUpUrl</c> is the native
    /// sign-up link; <c>Files</c> is the native attached-file list
    /// (<c>TList&lt;ZString&gt; m_listFiles</c>).
    /// </summary>
    public sealed record ZoneEvent(
        string Id,
        string Date,
        string Time,
        string Name,
        string Description,
        string SignUpUrl,
        IReadOnlyList<string> Files,
        long CreatedAt);

    /// <summary>Create-an-event body (operator only).</summary>
    public sealed record CreateEventRequest(
        string Date,
        string Time,
        string Name,
        string Description,
        string SignUpUrl,
        IReadOnlyList<string> Files);

    /// <summary>Edit-an-event body (operator only; a full replace).</summary>
    public sealed record UpdateEventRequest(
        string Date,
        string Time,
        string Name,
        string Description,
        string SignUpUrl,
        IReadOnlyList<string> Files);

    /// <summary>Outcome of a mutating event action.</summary>
    public sealed record EventActionResult(bool Ok, string? Error = null)
    {
        public static EventActionResult Success => new(true);
        public static EventActionResult Fail(string error) => new(false, error);
    }
}
