namespace Allegiance.ClientCore.MonoGame.Career;

/// <summary>
/// Pure, unit-testable model of the zone Events screen — the port of native
/// <c>WinTrek/zoneeventsscreen.cpp</c>: a list of zone events each showing its
/// date, time, name and description (<c>colDate / colTime / colName /
/// colDescription</c>), with the selected event's details — including its
/// sign-up URL and attached-file list (<c>EventListing</c>,
/// zoneeventsscreen.cpp:42-73) — and the configurable Zone web link shown as a
/// footer. Free of MonoGame and HTTP so every navigation path is testable
/// headless; <c>Game1</c> feeds it data from the lobby
/// (<see cref="EventApiClient"/>) and the renderer presents it in both styles.
/// </summary>
public sealed class EventsScreenModel
{
    public const int LinesPerScreen = 21;

    private EventDto.ZoneEvent[] _events = [];
    private int _cursor;

    /// <summary>The selected event's index within <see cref="Events"/> (native
    /// list cursor).</summary>
    public int Cursor => _cursor;

    public IReadOnlyList<EventDto.ZoneEvent> Events => _events;

    /// <summary>The configurable Zone web link (from the lobby), shown as a
    /// footer on the Events screen. Empty when not configured.</summary>
    public string ZoneWebUrl { get; set; } = "";

    /// <summary>Transient status / message text.</summary>
    public string Status { get; set; } = "";

    public EventDto.ZoneEvent? Selected => _events.Length > 0 && _cursor < _events.Length ? _events[_cursor] : null;

    public void Ingest(EventDto.ZoneEvent[] events)
    {
        _events = events;
        _cursor = Math.Min(_cursor, Math.Max(0, events.Length - 1));
    }

    public void MoveCursor(int delta)
    {
        if (_events.Length == 0) return;
        _cursor = (_cursor + delta + _events.Length) % _events.Length;
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _events.Length) return;
        _cursor = index;
    }

    /// <summary>Native-friendly display: the event's sign-up URL, or empty when
    /// none.</summary>
    public string SignUpLabel => Selected?.SignUpUrl is { Length: > 0 } u ? u : "(no sign-up)";

    /// <summary>The attached-file list of the selected event (native
    /// <c>TList&lt;ZString&gt; m_listFiles</c>).</summary>
    public IReadOnlyList<string> SelectedFiles => Selected?.Files ?? [];
}
