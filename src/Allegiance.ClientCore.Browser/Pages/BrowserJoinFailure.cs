using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Browser.Pages;

/// <summary>Translate the existing LOGONACK / DELPOSITIONREQ refusals; never retry automatically.</summary>
public readonly record struct BrowserJoinFailure(string Message, bool NameConflict = false)
{
    public static BrowserJoinFailure From(string reason, DelPositionReqReason? missionReason = null)
    {
        if (missionReason == DelPositionReqReason.BadPassword)
            return new("That game password is incorrect. Enter it again and retry.");
        if (reason.Contains("already in use", StringComparison.OrdinalIgnoreCase))
            return new("That callsign is already in use in this mission. Try another name.", true);
        if (reason.Contains("callsign is registered", StringComparison.OrdinalIgnoreCase))
            return new("That callsign is registered. Sign in to use it, or try another name.", true);
        if (reason.Contains("version", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("is older than this server's minimum", StringComparison.OrdinalIgnoreCase))
        {
            string detail = reason.Split(" — ", StringSplitOptions.None)[0];
            return new("Client version mismatch: " + detail + ". Reload this page after the browser build is updated.");
        }
        if (reason.Contains("requires lobby sign-in", StringComparison.OrdinalIgnoreCase))
            return new("Sign in to join this server.");
        return new(reason);
    }
}
