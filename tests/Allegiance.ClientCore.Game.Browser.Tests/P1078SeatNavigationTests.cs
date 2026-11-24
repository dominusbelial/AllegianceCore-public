using Allegiance.ClientCore.Browser.Pages;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1078SeatNavigationTests
{
    [Theory]
    [InlineData("Hangar", false, false, (short)-2, true, "Sides")]
    [InlineData("Flight", false, false, (short)-2, true, "Sides")]
    [InlineData("Sides", false, false, (short)0, true, "Hangar")]
    [InlineData("Sides", false, false, (short)-2, true, "Sides")]
    [InlineData("Hangar", true, false, (short)-2, true, "Hangar")]
    [InlineData("Hangar", false, true, (short)-2, true, "Hangar")]
    [InlineData("Flight", false, false, null, true, "Flight")]
    [InlineData("Sides", false, false, (short)0, false, "Sides")]
    public void Lobby_return_offers_side_choice_and_auto_seat_opens_hangar_after_join_is_complete(
        string current, bool busy, bool reconnecting, short? side, bool assigned, string expected) =>
        Assert.Equal(expected, BrowserSeatNavigation.Next(current, busy, reconnecting, side, assigned));
}
