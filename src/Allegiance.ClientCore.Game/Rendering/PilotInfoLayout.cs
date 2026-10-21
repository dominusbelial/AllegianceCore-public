using System;
using System.Collections.Generic;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P639 — pure (MonoGame-free) layout of the CHARACTER SHEET (the upgraded
/// Pilot Info screen): the local player's <b>own</b> live session stats pulled
/// from the derived roster (P774: PLAYERINFO + KILL_SHIP) in
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
/// CharInfoScreen.cpp:739) and a full stat breakdown backed by the Zone Club
/// career database (<c>REQ_CHARACTER_INFO</c> → <c>trekClient.m_fmClub</c>,
/// CharInfoScreen.cpp:242-260,717-774) which this port does not have (no
/// persistence — P129/P275). P774b — the in-mission sheet shows exactly the
/// roster fields with a native in-mission source: RANK (via the real
/// RANK_INFO wire table, faithfully resolved through
/// <see cref="RankNameLookup"/> = native <c>BaseClient::LookupRankName</c>,
/// clintlib.h:359-361), KILLS, DEATHS and EJECTIONS (the PlayerInfo counters,
/// clintlib.h:346-348). Rescues, base kills/captures, artifacts, flags, combat
/// rating and score have no native in-mission display (never shown, or
/// game-over-only — see <see cref="ScoreboardLayout"/>) and are dropped.
/// Medals, squads and civ selection are an era/infra constraint (classified in
/// the report), not faked.</para>
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
                new Stat("RANK", string.IsNullOrWhiteSpace(rankName) ? "(none)" : ScoreboardLayout.AsciiSafe(rankName)),
                new Stat("KILLS", s.Kills.ToString()),
                new Stat("DEATHS", s.Deaths.ToString()),
                new Stat("EJECTIONS", s.Ejections.ToString()),
            ]);
    }

    /// <summary>
    /// P761 — the character sheet's profile-description editor, the port of
    /// native <c>CharInfoScreen</c>'s Edit/Apply button pair
    /// (<c>WinTrek/CharInfoScreen.cpp:837-880</c>):
    /// <list type="bullet">
    /// <item><b>Edit</b> (OnButtonEdit, :875-880): the description pane becomes
    /// editable and focused; Edit hides/disables, Apply shows/enables, and the
    /// read-only display string clears (the draft starts empty).</item>
    /// <item><b>Apply</b> (OnButtonApply, :839-858): the pane turns read-only
    /// again, the edited text is copied into the display, and the buttons swap
    /// back. Native then sends <c>CHARACTER_INFO_EDIT_DESCRIPTION</c> to the
    /// club server — a channel this port does not have — so the applied text is
    /// persisted LOCALLY in the client's <c>player.json</c> preferences
    /// (<c>IntroPreferences.PilotDescription</c>). The parity row stays
    /// <c>partial</c> for exactly that reason: no other player can see it.</item>
    /// </list>
    /// Pure state machine: keyboard edges in, state out; Game1 performs the
    /// preference write. Headless-testable (the state toggles are the pinned
    /// behaviour).
    /// </summary>
    public sealed class ProfileEditor
    {
        public const int MaxDescriptionLen = 200;

        private readonly System.Text.StringBuilder _draft = new();

        /// <summary>Whether the description is being edited (native's edit pane
        /// not read-only and not hidden).</summary>
        public bool Editing { get; private set; }

        /// <summary>The applied (read-only) description — what the display
        /// string shows when not editing.</summary>
        public string Description { get; set; } = "";

        /// <summary>The live draft while editing; empty when read-only.</summary>
        public string Draft => _draft.ToString();

        /// <summary>Native's per-toggle button/pane flags (the state the tests
        /// pin): ReadOnly mirrors <c>SetReadOnly</c>, the two button pairs
        /// mirror <c>SetEnabled</c>/<c>SetHidden</c> for Edit and Apply.</summary>
        public readonly record struct State(
            bool Editing, bool ReadOnly, bool EditVisible, bool EditEnabled,
            bool ApplyVisible, bool ApplyEnabled);

        public State Snapshot => new(
            Editing: Editing,
            ReadOnly: !Editing,
            EditVisible: !Editing,
            EditEnabled: !Editing,
            ApplyVisible: Editing,
            ApplyEnabled: Editing);

        /// <summary>OnButtonEdit: enter edit mode — editable + focused (the
        /// port's compose-style text entry), Edit hidden, Apply shown; the
        /// display string clears (native SetValue("")), i.e. the draft starts
        /// EMPTY, not from the stored text.</summary>
        public void BeginEdit()
        {
            if (Editing) return;
            Editing = true;
            _draft.Clear();
        }

        /// <summary>OnButtonApply: leave edit mode read-only, copy the draft
        /// into the display text, swap the buttons back. Returns the applied
        /// text for the caller to persist (null when not editing — no-op, like
        /// native's disabled Apply).</summary>
        public string? Apply()
        {
            if (!Editing) return null;
            Description = _draft.ToString();
            Editing = false;
            _draft.Clear();
            return Description;
        }

        /// <summary>Cancel an edit (the port's Esc path — native cancels by not
        /// pressing Apply): nothing is copied, buttons swap back.</summary>
        public void Cancel()
        {
            Editing = false;
            _draft.Clear();
        }

        /// <summary>Typed text into the draft (the edit pane's OnChar), only
        /// while editing, capped.</summary>
        public void Append(char c)
        {
            if (Editing && _draft.Length < MaxDescriptionLen) _draft.Append(c);
        }

        /// <summary>Backspace into the draft, only while editing.</summary>
        public void Backspace()
        {
            if (Editing && _draft.Length > 0) _draft.Length -= 1;
        }
    }
}
