using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P802 — the team lobby's civilization picker, as pure state (no MonoGame).
///
/// <para><b>Native rule.</b> The side's team leader may change the side's
/// civilization while the mission has not started:
/// <c>FM_CS_CHANGE_TEAM_CIV(51)</c> is accepted only from the side's leader
/// and only at <c>STAGE_NOTSTARTED</c> (<c>FedSrv/FedSrv.CPP:7344-7366</c>);
/// the client-side picker is native's TEAM SETTINGS dialog civilization combo
/// (<c>WinTrek/teamscreen.cpp:860-1104</c>, sent from <c>OnCivChosen</c>,
/// <c>:3049-3065</c>) plus the lobby's <c>Random</c> entry
/// (<c>RANDOM_ID = 9999</c>, <c>Igc/igc.h:501</c>; <c>teamscreen.cpp:1007-1075</c>).
/// The combo lists the mission core's civilizations below the native
/// "prevent special civs" cut of 300 (<c>teamscreen.cpp:897-916</c>,
/// <c>:2667</c>). A side's current civ is drawn from the same state the
/// lobby renders: the roster's civ id and the live <c>CHANGE_TEAM_CIV</c>
/// echo (<see cref="ClientWorld.SideCivilizations"/>).</para>
///
/// <para><b>Port additions.</b> (1) The picker is a first-class lobby pane
/// rather than a modal settings dialog — the native message and permission
/// rule are unchanged. (2) Choosing <c>Random</c> is delegated to the server,
/// which resolves it to a concrete civ (<see cref="ClientWorld"/>'s
/// <c>ChangeTeamCiv</c> echo carries <c>Random = true</c>), so every peer and
/// the authoritative side data agree without the native owner re-roll loop
/// (<c>teamscreen.cpp:2662-2695</c>).</para>
/// </summary>
public static class TeamCivPicker
{
    /// <summary>Native <c>RANDOM_ID</c> (<c>Igc/igc.h:501</c>) — the random
    /// faction entry's object id. The picker never sends this as a civ; it
    /// sends the current civ with <c>random=true</c> and lets the server
    /// resolve it.</summary>
    public const short RandomCivId = 9999;

    /// <summary>Native's "prevent special civs" cut: only civilizations whose
    /// object id is below 300 are offered to non-cheat players
    /// (<c>teamscreen.cpp:897-916</c>, <c>:2667</c>).</summary>
    public const short SpecialCivIdMin = 300;

    /// <summary>One picker entry: a real core civilization, or the trailing
    /// <c>Random</c> choice (<see cref="RandomCivId"/>). <see cref="IconName"/>
    /// is the native <c>DataCivilizationIGC.IconName</c> the lobby draws as
    /// <c>icon&lt;name&gt;bmp</c> (<c>teamscreen.cpp:586-591</c>,
    /// <c>:2479-2492</c>).</summary>
    public readonly record struct Option(short CivId, string Name, string IconName)
    {
        public bool IsRandom => CivId == RandomCivId;
    }

    /// <summary>The options in native combo order (core civs ascending, then
    /// <c>Random</c>): the native dialog walks the core's civilization list and
    /// appends the random entry (<c>teamscreen.cpp:898-916</c>). The trailing
    /// random entry is the port's version of the native item Xynth commented
    /// out of the dialog (<c>:915-916</c>) but kept in the lobby's
    /// <c>Random</c> selection (<c>:1007</c>). Empty when the core is not
    /// loaded yet (a peer that has not received JOINED_MISSION).</summary>
    public static IReadOnlyList<Option> Options(StaticCore? core)
    {
        if (core is null) return [];
        return core.Civilizations.Values
            .Where(c => c.CivilizationId is >= 0 and < SpecialCivIdMin)
            .OrderBy(c => c.CivilizationId)
            .Select(c => new Option(c.CivilizationId, c.Name, c.IconName))
            .Append(new Option(RandomCivId, "RANDOM", ""))
            .ToArray();
    }

    /// <summary>Whether the local player may change the side's civ: they must be
    /// that side's current team leader (<see cref="ClientWorld.IsTeamLeader"/>)
    /// and the mission must still be <c>STAGE_NOTSTARTED</c> — the server's own
    /// gate (<c>FedSrv.CPP:7348-7360</c>). Everyone else sees the current
    /// civilization read-only.</summary>
    public static bool CanPick(ClientWorld world) =>
        world.IsTeamLeader && world.MySideId is >= 0 && world.Mission is { Stage: (int)MissionStage.NotStarted };

    /// <summary>The own side's current civilization id from the roster /
    /// <c>CHANGE_TEAM_CIV</c> state (null when unknown).</summary>
    public static short? CurrentCivId(ClientWorld world)
    {
        if (world.MySideId is not { } side) return null;
        if (world.SideCivilizations.TryGetValue(side, out var civ)) return civ.CivId;
        if (world.SideRoster is not { } roster) return null;
        foreach (var record in roster.Sides)
            if (record.SideId == side) return record.CivId;
        return null;
    }

    /// <summary>The current civ's entry in <paramref name="options"/>, or -1 when
    /// the civ is not offered (a special/bonus civ, or no state yet).</summary>
    public static int IndexOf(IReadOnlyList<Option> options, short? civId)
    {
        if (civId is not { } id) return -1;
        for (int i = 0; i < options.Count; i++)
            if (options[i].CivId == id) return i;
        return -1;
    }

    /// <summary>Move the picker cursor, wrapping (the lobby picker idiom —
    /// native's combo clamps, but a wrapping cursor is the port's established
    /// pane behaviour as in <c>SideSelectLayout</c>).</summary>
    public static int Move(int index, int delta, int count)
    {
        if (count <= 0) return 0;
        int start = index < 0 ? 0 : index;
        return ((start + delta) % count + count) % count;
    }

    /// <summary>The request for <paramref name="option"/>: a concrete civ sends
    /// that civ id with <c>random=false</c>; <c>Random</c> sends the side's
    /// current civ id as a placeholder with <c>random=true</c> (the server
    /// resolves it — see the class remarks).</summary>
    public static (short CivId, bool Random) Request(Option option, short? currentCivId) =>
        option.IsRandom
            ? (currentCivId ?? 0, true)
            : (option.CivId, false);

    /// <summary>The side's civilization name for display (the roster's civ id
    /// resolved against the local core), or a honest fallback.</summary>
    public static string DisplayName(ClientWorld world, short? civId = null)
    {
        short? id = civId ?? CurrentCivId(world);
        if (id is not { } civ) return "UNKNOWN";
        if (world.MissionCore is { } core && core.Civilizations.TryGetValue(civ, out var data))
            return string.IsNullOrWhiteSpace(data.Name) ? $"CIV {civ}" : data.Name;
        return $"CIV {civ}";
    }

    /// <summary>The animation/icon stem the lobby draws for the current civ:
    /// native <c>icon&lt;civ.GetIconName()&gt;bmp</c> (<c>teamscreen.cpp:586-591</c>),
    /// lower-cased; empty when unknown.</summary>
    public static string IconStem(ClientWorld world, short? civId = null)
    {
        short? id = civId ?? CurrentCivId(world);
        if (id is not { } civ || world.MissionCore is not { } core
            || !core.Civilizations.TryGetValue(civ, out var data) || string.IsNullOrWhiteSpace(data.IconName))
            return "";
        return "icon" + data.IconName.ToLowerInvariant() + "bmp";
    }
}
