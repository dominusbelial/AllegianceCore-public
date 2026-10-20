namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P500 — pure-geometry tests for the connect-screen layout
/// (<see cref="ConnectScreenLayout"/>): the same rects the Draw path renders
/// are what the touch path hit-tests against, so a tap lands exactly where a
/// row/button is drawn. These tests keep the geometry sane across the two
/// consumers and across screen sizes — no graphics device involved.
/// </summary>
public class ConnectScreenLayoutTests
{
    [Fact]
    public void Six_buttons_in_definition_order_with_connect_first()
    {
        var buttons = ConnectScreenLayout.ComputeButtons(1280f, 720f);
        Assert.Equal(6, buttons.Count);
        Assert.Equal(ConnectScreenLayout.Button.Connect, buttons[0].Kind);
        Assert.Equal(ConnectScreenLayout.Button.Add, buttons[1].Kind);
        Assert.Equal(ConnectScreenLayout.Button.Edit, buttons[2].Kind);
        Assert.Equal(ConnectScreenLayout.Button.Delete, buttons[3].Kind);
        Assert.Equal(ConnectScreenLayout.Button.Name, buttons[4].Kind);
        Assert.Equal(ConnectScreenLayout.Button.Lobby, buttons[5].Kind);
    }

    [Fact]
    public void Buttons_sit_inside_the_viewport_and_do_not_overlap()
    {
        var buttons = ConnectScreenLayout.ComputeButtons(1280f, 720f);
        foreach (var (_, r) in buttons)
        {
            Assert.InRange(r.X, 0f, 1280f);
            Assert.InRange(r.X + r.W, 0f, 1280f);
            Assert.InRange(r.Y, 0f, 720f);
            Assert.InRange(r.Y + r.H, 0f, 720f);
            Assert.True(r.W > 0 && r.H > 0);
        }
        for (int i = 1; i < buttons.Count; i++)
        {
            var prev = buttons[i - 1].Rect;
            var cur = buttons[i].Rect;
            Assert.True(cur.X >= prev.X + prev.W,
                $"button {i} overlaps button {i - 1}");
        }
    }

    [Fact]
    public void Button_centers_are_inside_their_rects()
    {
        foreach (var (_, r) in ConnectScreenLayout.ComputeButtons(1280f, 720f))
            Assert.True(r.Contains(r.CenterX, r.CenterY));
    }

    [Fact]
    public void Row_rects_step_downward_and_match_row_height()
    {
        float w = 1280f;
        var r0 = ConnectScreenLayout.RowRect(0, w);
        var r1 = ConnectScreenLayout.RowRect(1, w);
        var r5 = ConnectScreenLayout.RowRect(5, w);
        Assert.Equal(r0.Y + ConnectScreenLayout.RowH, r1.Y);
        Assert.Equal(r0.Y + 5 * ConnectScreenLayout.RowH, r5.Y);
        Assert.Equal(w * ConnectScreenLayout.RowWidthFactor, r0.W);
    }

    [Fact]
    public void Row_contains_point_inside_but_not_adjacent_row()
    {
        var r0 = ConnectScreenLayout.RowRect(0, 1280f);
        var r1 = ConnectScreenLayout.RowRect(1, 1280f);
        float midX = r0.X + 10f;
        float inY = r0.Y + r0.H / 2f;      // inside row 0
        float below = r1.Y + r1.H / 2f;    // inside row 1
        Assert.True(r0.Contains(midX, inY));
        Assert.False(r0.Contains(midX, below));
        Assert.True(r1.Contains(midX, below));
    }

    // P759 — OnLogonGameServerFailed(false, reason) abort routing
    // (introscreen.cpp:1667-1699): intro wins, then lobby, then exit.
    [Fact]
    public void Abort_destination_prefers_intro_then_lobby_then_exit()
    {
        Assert.Equal(ConnectScreenLayout.ConnectAbortDestination.Intro,
            ConnectScreenLayout.AbortDestination(returnToIntro: true, returnToLobby: true));
        Assert.Equal(ConnectScreenLayout.ConnectAbortDestination.Intro,
            ConnectScreenLayout.AbortDestination(returnToIntro: true, returnToLobby: false));
        Assert.Equal(ConnectScreenLayout.ConnectAbortDestination.Lobby,
            ConnectScreenLayout.AbortDestination(returnToIntro: false, returnToLobby: true));
        Assert.Equal(ConnectScreenLayout.ConnectAbortDestination.Exit,
            ConnectScreenLayout.AbortDestination(returnToIntro: false, returnToLobby: false));
    }
}
