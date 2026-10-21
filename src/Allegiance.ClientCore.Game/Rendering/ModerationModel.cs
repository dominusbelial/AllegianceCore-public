using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Native teampane.cpp:1115-1125 confirms a boot before sending it;
/// WinTrek.cpp:3731-3734 toggles a local mute. Keyboard chat commands expose
/// the same actions without depending on the native Win32 context menu.</summary>
public sealed class ModerationModel
{
    public const string Hint = "CHAT: /boot ID, /confirmboot, /mute ID, /unmute ID";
    private short? _pendingBoot;
    public string? Notice { get; private set; }
    public void Reset() { _pendingBoot = null; Notice = null; }

    /// <summary>Call under the session gate. A recognized command never becomes public chat.</summary>
    public bool Handle(string text, ClientWorld world, out short? boot)
    {
        boot = null;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        string verb = words[0].ToLowerInvariant();
        if (verb is not ("/boot" or "/confirmboot" or "/cancelboot" or "/mute" or "/unmute")) return false;
        if (verb == "/cancelboot") { Reset(); return true; }
        if (verb == "/confirmboot")
        {
            if (words.Length == 1 && _pendingBoot is { } id && CanBoot(world, id)) boot = id;
            Notice = boot is not null ? "Boot request sent." : "No valid boot selected.";
            _pendingBoot = null;
            return true;
        }
        if (words.Length != 2 || !short.TryParse(words[1], out short target)
            || !world.Ships.TryGetValue(target, out var pilot) || target == world.MyShipId)
        {
            _pendingBoot = null;
            Notice = "Choose another pilot's ship ID from the team pane.";
            return true;
        }
        string name = ScoreboardLayout.AsciiSafe(pilot.CharacterName);
        if (verb == "/boot")
        {
            _pendingBoot = CanBoot(world, target) ? target : null;
            Notice = _pendingBoot is not null ? $"Boot {name}? /confirmboot or /cancelboot."
                : "Only your team leader can boot a teammate.";
        }
        else
        {
            world.SetPilotMuted(target, verb == "/mute");
            Notice = $"{name}: {(world.IsPilotMuted(target) ? "muted" : "unmuted")}.";
        }
        return true;
    }

    private static bool CanBoot(ClientWorld world, short id) => world.IsTeamLeader
        && id != world.MyShipId && world.Ships.TryGetValue(id, out var target)
        // HUD-20 — a drone now has a PLAYERINFO too; native boot/ban is for
        // human pilots only (dtidDrone == NA, FedSrv.CPP:2088-2091).
        && target.SideId == world.MySideId
        && target.Player is { DtidDrone: Allegiance.Core.Wire.PlayerInfo.NA };
}
