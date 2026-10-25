using Allegiance.ClientCore.MonoGame.Career;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P820b — screen-model tests for the Zone Events screen (native
/// <c>WinTrek/zoneeventsscreen.cpp</c>). Pure, no graphics device: list ingest,
/// cursor navigation, selection with the event's sign-up URL and attached-file
/// list (<c>EventListing</c>, zoneeventsscreen.cpp:42-73), and the retained
/// Zone web link footer.
/// </summary>
public sealed class P820bEventsScreenModelTests
{
    private static EventDto.ZoneEvent Ev(string name, string date, string time, string desc = "", string url = "", string[]? files = null)
        => new("ev-" + name, date, time, name, desc, url, files ?? [], 1);

    private static EventsScreenModel Model() => new();

    [Fact]
    public void Ingest_presents_rows_in_order_and_selection_follows_the_cursor()
    {
        var m = Model();
        m.Ingest([
            Ev("Alpha cup", "2026-11-05", "19:00"),
            Ev("Beta duel", "2026-11-03", "12:00"),
        ]);
        Assert.Equal(2, m.Events.Count);
        Assert.Equal("Alpha cup", m.Selected!.Name);            // cursor starts at 0

        m.MoveCursor(+1);
        Assert.Equal("Beta duel", m.Selected!.Name);
        m.MoveCursor(+1);                                       // wraps
        Assert.Equal("Alpha cup", m.Selected!.Name);
        m.MoveCursor(-1);
        Assert.Equal("Beta duel", m.Selected!.Name);
    }

    [Fact]
    public void Selection_exposes_description_signup_url_and_files()
    {
        var m = Model();
        m.Ingest([Ev("Night war", "2026-12-01", "21:00", "Big battle night", "https://signup.example/n", ["rules.pdf", "map.bmp"])]);
        var e = m.Selected!;
        Assert.Equal("Night war", e.Name);
        Assert.Equal("2026-12-01", e.Date);
        Assert.Equal("21:00", e.Time);
        Assert.Equal("Big battle night", e.Description);
        Assert.Equal("https://signup.example/n", e.SignUpUrl);
        Assert.Equal(new[] { "rules.pdf", "map.bmp" }, m.SelectedFiles);
        Assert.Equal("https://signup.example/n", m.SignUpLabel);
    }

    [Fact]
    public void Cursor_is_clamped_when_the_list_shrinks()
    {
        var m = Model();
        m.Ingest([Ev("A", "2026-11-01", "10:00"), Ev("B", "2026-11-02", "10:00"), Ev("C", "2026-11-03", "10:00")]);
        m.MoveCursor(+2);
        Assert.Equal("C", m.Selected!.Name);
        m.Ingest([Ev("A", "2026-11-01", "10:00")]);              // list shrank
        Assert.Equal(0, m.Cursor);
        Assert.Equal("A", m.Selected!.Name);
    }

    [Fact]
    public void Empty_list_has_no_selection_and_empty_status_path_is_clear()
    {
        var m = Model();
        m.Ingest([]);
        Assert.Empty(m.Events);
        Assert.Null(m.Selected);
        Assert.Empty(m.SelectedFiles);
        m.MoveCursor(+1);                                        // no-op on empty
        Assert.Equal(0, m.Cursor);
    }

    [Fact]
    public void Zone_web_link_is_retained_and_presented()
    {
        var m = Model();
        m.ZoneWebUrl = "https://zone.example";
        Assert.Equal("https://zone.example", m.ZoneWebUrl);
        // Cleared/absent config is represented by empty (renderer hides it).
        m.ZoneWebUrl = "";
        Assert.Equal("", m.ZoneWebUrl);
    }
}
