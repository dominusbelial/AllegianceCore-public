using System;
using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P275 — pure (MonoGame-free) layout of the "Pilot Info" overlay: the local
/// player's <b>own</b> live session stats pulled from the periodic
/// <c>ScoreRosterUpdate(209)</c> snapshot in <c>ClientWorld.ScoreRoster</c>,
/// turned into aligned label/value pairs the
/// <see cref="PilotInfoOverlayRenderer"/> draws.
///
/// <para>Kept MonoGame-free (mirroring <see cref="ScoreboardLayout"/> /
/// <see cref="KeyBindingTable"/>) so the extraction, ordering and ASCII-safety
/// are headless-unit-testable without a graphics device.</para>
///
/// <para><b>Scope.</b> Strictly session/connection-scoped, matching this port's
/// documented no-persistence design (see <c>PlayerScore.cs</c>): every field is
/// a live per-connection value and the overlay shows only the local player's
/// row of the current mission roster. There is deliberately no Zone Club,
/// squad, medal, civ-history or cross-session storage — native's full
/// <c>CharInfoScreen</c> (<c>WinTrek/CharInfoScreen.cpp</c>) is backed by the
/// persistent Zone Club meta-server (<c>REQ_CHARACTER_INFO</c> →
/// <c>trekClient.m_fmClub</c>, <c>CharInfoScreen.cpp:242-260</c>), and this port
/// has no such backend at all (it never persists anything). So this overlay is
/// the honest in-mission slice of that screen: career stats, squads, medals and
/// civ selection are out of scope because no backend exists to feed them.</para>
///
/// <para><b>Fields shown.</b> Kills/Deaths/CombatRating/Rank/Score — all five
/// live fields P193's <c>PlayerScore</c> carries, all of which P275 now
/// broadcasts live via the widened roster record (previously Rank/Score only
/// ever travelled at game-over via <c>GameOverPlayers</c>(63), and CombatRating
/// never travelled at all).</para>
/// </summary>
public static class PilotInfoLayout
{
    /// <summary>One labelled stat row ready to draw: label + pure-ASCII value.</summary>
    public readonly record struct Stat(string Label, string Value);

    /// <summary>The local player's extracted pilot info: their name, a rendered
    /// side label (numeric id, like the scoreboard) and the ordered stat rows.</summary>
    public readonly record struct PilotInfo(string Name, string SideLabel, IReadOnlyList<Stat> Stats);

    /// <summary>
    /// Extract the local player's stats from a roster snapshot, matched by name
    /// (the same identity the scoreboard self-highlight uses —
    /// <c>Ships[MyShipId].CharacterName</c>). Returns <c>null</c> when the local
    /// player hasn't appeared in a roster yet (pre-join / no roster received) —
    /// the renderer draws nothing in that case rather than a placeholder.
    /// </summary>
    public static PilotInfo? Build(IReadOnlyList<Allegiance.Core.Wire.ScoreRosterEntry> entries, string? selfName)
    {
        if (selfName is null) return null;
        Allegiance.Core.Wire.ScoreRosterEntry? self = null;
        foreach (var e in entries)
        {
            if (string.Equals(e.CharacterName, selfName, StringComparison.Ordinal))
            {
                self = e;
                break;
            }
        }
        if (self is not { } s) return null;

        return new PilotInfo(
            Name: ScoreboardLayout.AsciiSafe(s.CharacterName),
            SideLabel: $"S{s.SideId}",
            Stats:
            [
                new Stat("KILLS", s.Kills.ToString()),
                new Stat("DEATHS", s.Deaths.ToString()),
                new Stat("COMBAT RATING", s.CombatRating.ToString("F1")),
                new Stat("RANK", s.Rank.ToString()),
                new Stat("SCORE", s.Score.ToString("F0")),
            ]);
    }
}
