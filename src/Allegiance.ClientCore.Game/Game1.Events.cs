using System.Text;
using Allegiance.ClientCore.MonoGame.Career;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P820b — the Events sub-screen of the career screen, rendered from the
/// shared <see cref="EventsScreenModel"/> (the port of native
/// <c>WinTrek/zoneeventsscreen.cpp</c>) and driven over the lobby's zone-events
/// HTTP API (<see cref="EventApiClient"/>). Reached from the online hub
/// (career screen) alongside the Squads sub-screen. Behaviour lives in the
/// model; this slice translates keyboard input into model transitions and
/// lobby calls, and surfaces the configurable Zone web link as a footer.
/// </summary>
public sealed partial class Game1
{
    private void UpdateEventsScreen(KeyboardState ks, bool up, bool down, bool enter, bool esc, bool tab)
    {
        if (_careerLoad is { IsCompleted: false }) return;

        if (tab && !_careerTabPrev) { CycleCareerMode(1); return; }
        if (esc && !_careerEscPrev) { _introView = IntroView.Main; _introIndex = 10; return; }

        if (up && !_careerUpPrev) _events.MoveCursor(-1);
        if (down && !_careerDownPrev) _events.MoveCursor(+1);
        if (enter && !_careerEnterPrev)
        {
            // Enter "opens" the selected event — for a read-only listing it just
            // acknowledges selection; native highlights the row on cursor move.
            _events.Status = _events.Selected is { } e ? $"Event: {e.Name}" : "No events.";
            return;
        }

        _careerUpPrev = up; _careerDownPrev = down; _careerEnterPrev = enter; _careerEscPrev = esc; _careerTabPrev = tab;
    }

    private async Task LoadEventsAsync()
    {
        try
        {
            _events.Ingest(await _eventClient!.GetEventsAsync().ConfigureAwait(false));
            _events.ZoneWebUrl = await _eventClient.GetZoneWebUrlAsync().ConfigureAwait(false) ?? "";
            _events.Status = _events.Events.Count == 0 ? "No zone events scheduled." : $"{_events.Events.Count} upcoming event(s).";
            _careerStatus = _events.Status;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _events.Status = "Events service unreachable: " + e.Message;
            _careerStatus = _events.Status;
        }
    }
}
