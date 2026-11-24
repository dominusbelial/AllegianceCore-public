namespace Allegiance.ClientCore.Browser.Pages;

/// <summary>Presentation only: a carried-forward NOAT player must be able to
/// choose a side again (native team screen, WinTrek/teamscreen.cpp:1662-1672).
/// A timed server seat follows the same hangar entry as an explicit choice.</summary>
public static class BrowserSeatNavigation
{
    public static string Next(string current, bool busy, bool reconnecting, short? side, bool shipAssigned)
    {
        if (busy || reconnecting || side is null || !shipAssigned) return current;
        if (side < 0 && current is "Hangar" or "Flight") return "Sides";
        if (side >= 0 && current == "Sides") return "Hangar";
        return current;
    }
}
