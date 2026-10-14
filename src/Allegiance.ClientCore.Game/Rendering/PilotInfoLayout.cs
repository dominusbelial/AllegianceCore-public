using System;
using System.Collections.Generic;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P639 — pure (MonoGame-free) layout of the CHARACTER SHEET (the upgraded
/// Pilot Info screen): the local player's <b>own</b> live session stats pulled
/// from the periodic <c>ScoreRosterUpdate(209)</c> snapshot in
/// <c>ClientWorld.ScoreRoster</c>, plus their rank name resolved from the
/// <c>RANK_INFO(13)</c> table (<c>ClientWorld.RankTable</c>), turned into
/// aligned label/value rows the <see cref="PilotInfoOverlayRenderer"/> draws.
///
/// <para>Kept MonoGame-free (mirroring <see cref="ScoreboardLayout"/> /
/// <see cref="KeyBindingTable"/>) so the extraction, ordering and ASCII-safety
/// are headless-unit-testable without a graphics device.</para>
///
/// <para><b>Native fidelity.</b> Native's <c>CharInfoScreen</c>
/// (<c>WinTrek/CharInfoScreen.cpp</c>) is the career sheet: pilot identity, the
/// rank banner (<c>m_pstringRank-&gt;SetValue(trekClient.LookupRankName(...))</c>,
/// CharInfoScreen.cpp:739) and a full stat breakdown. The persistent half
/// (medals, squads, civ selection, cross-session career counters) is backed by
/// the Zone Club meta-server (<c>REQ_CHARACTER_INFO</c> →
/// <c>trekClient.m_fmClub</c>, CharInfoScreen.cpp:242-260) which this port does
/// not have (no persistence — P129/P275). So this sheet is the honest in-mission
/// slice: identity + rank name (via the real RANK_INFO wire table, faithfully
/// resolved through <see cref="RankNameLookup"/> = native
/// <c>BaseClient::LookupRankName</c>) + every live roster counter. Medals,
/// squads and civ selection are an era/infra constraint (classified in the
/// report), not faked.</para>
/// </summary>
public static class PilotInfoLayout
{
    /// <summary>One labelled stat row ready to draw: label + pure-ASCII value.</summary>
    public readonly record struct Stat(string Label, string Value);

    /// <summary>The local player's extracted character sheet: their name, a
    /// rendered side label (numeric id, like the scoreboard), their resolved
    /// rank name and the ordered stat rows.</summary>
    public readonly record struct PilotInfo(
        string Name, string SideLabel, string RankName, IReadOnlyList<Stat> Stats);

    /// <summary>
    /// Extract the local player's character sheet from a roster snapshot,
    /// matched by name (the same identity the scoreboard self-highlight uses —
    /// <c>Ships[MyShipId].CharacterName</c>), resolving their rank name from the
    /// server's rank table. Returns <c>null</c> when the local player hasn't
    /// appeared in a roster yet (pre-join / no roster received) — the renderer
    /// draws nothing in that case rather than a placeholder.
    /// </summary>
    public static PilotInfo? Build(
        IReadOnlyList<ScoreRosterEntry> entries, string? selfName,
        IReadOnlyList<RankInfoEntry> rankTable)
    {
        if (selfName is null) return null;
        ScoreRosterEntry? self = null;
        foreach (var e in entries)
        {
            if (string.Equals(e.CharacterName, selfName, StringComparison.Ordinal))
            {
                self = e;
                break;
            }
        }
        if (self is not { } s) return null;

        string rankName = RankNameLookup.Lookup(rankTable, s.Rank);
        return new PilotInfo(
            Name: ScoreboardLayout.AsciiSafe(s.CharacterName),
            SideLabel: $"S{s.SideId}",
            RankName: ScoreboardLayout.AsciiSafe(rankName),
            Stats:
            [
                new Stat("RANK NAME", string.IsNullOrWhiteSpace(rankName) ? "(none)" : ScoreboardLayout.AsciiSafe(rankName)),
                new Stat("KILLS", s.Kills.ToString()),
                new Stat("DEATHS", s.Deaths.ToString()),
                new Stat("EJECTIONS", s.Ejections.ToString()),
                new Stat("RESCUES", s.Rescues.ToString()),
                new Stat("BASE KILLS", s.BaseKills.ToString()),
                new Stat("BASE CAPTURES", s.BaseCaptures.ToString()),
                new Stat("ARTIFACTS", s.Artifacts.ToString()),
                new Stat("FLAGS", s.Flags.ToString()),
                new Stat("COMBAT RATING", s.CombatRating.ToString("F1")),
                new Stat("SCORE", s.Score.ToString("F0")),
            ]);
    }
}
