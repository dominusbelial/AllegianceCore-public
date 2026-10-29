using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P853b — the mission owner's GAME SETTINGS form, as pure state (no MonoGame):
/// the live-editable subset of native's create-game screen
/// (<c>WinTrek/newgamescreen.cpp</c>) on top of the P853 wire.
///
/// <para><b>Native rule.</b> The settings pane is offered only to the mission
/// owner while the mission has not started (<c>teamscreen.cpp:1586-1589</c>);
/// <c>OnButtonCreate</c> (<c>newgamescreen.cpp:1034</c>) reads the controls
/// into a <c>MissionParams</c>, runs <c>Invalid(true)</c> (<c>:1041</c>),
/// shows the returned string in a message box and sends nothing (<c>:1117-1120</c>),
/// and only a valid form queues <c>FM_CS_MISSIONPARAMS</c> (<c>:1124-1125</c>).
/// The server validates the same way and <c>TrapHackBoot</c>s a sender whose
/// params are invalid (<c>FedSrv.CPP:7534</c>), so an invalid form must never be
/// sent. Combo value lists come from <c>artwork/newgamescreen.mdl:283-353</c>;
/// the server adopts the whole blob except the IGC-static fields, the club flag,
/// the game name/password and the two player-immutable fields
/// (<c>fsmission.cpp:1803-1817</c>, <c>FedSrv.CPP:7543-7548,7562-7572</c>).</para>
///
/// <para><b>Port shape.</b> The draft starts as a clone of the mission's
/// advertised <see cref="MissionInfo.Params"/> and only the fields this form
/// models are edited, so every other byte the server reads is exactly what it
/// advertised (native's client sends its whole misdef copy the same way,
/// <c>clintlib.cpp:670-673</c>). Two native create-game checks are deliberately
/// not re-implemented client-side: the owner's own rank-window test and the
/// squad-leader test for <c>bSquadGame</c> (<c>newgamescreen.cpp:1043-1073</c>) —
/// the client has no authoritative persist-rank or squad-membership list here,
/// and the server owns the outcome.</para>
/// </summary>
public sealed class MissionSettingsForm
{
    /// <summary>The rows of the form, in native screen order (teams → players →
    /// skill → lives → winning conditions → option flags).</summary>
    public enum Field
    {
        Teams,
        MaxPlayersPerTeam,
        MinRank,
        MaxRank,
        Lives,
        Conquest,
        Territory,
        Flags,
        Artifacts,
        TeamKills,
        Prosperity,
        GameLength,
        FriendlyFire,
        AlliedViz,
        AlliedRip,
        EmptyTeams,
        Developments,
        SquadGame,
    }

    public static readonly Field[] Fields = (Field[])Enum.GetValues(typeof(Field));

    // ---- native combo values (artwork/newgamescreen.mdl:283-353) ------------
    private static readonly short[] TeamCountValues = [2, 3, 4, 5, 6];                       // :283
    private static readonly short[] MaxPlayersValues = [4, 6, 8, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100]; // :287 (deduped)
    private static readonly short[] LivesValues = [0x7FFF, 0, 1, 2, 3, 4, 9];                 // :310 — 32767 = unlimited
    private static readonly short[] ConquestPercentValues = [0, 80, 90, 100];                // :349
    private static readonly short[] TerritoryValues = [80, 90, 100];                         // :353 — 100 disables (native lists it descending)
    private static readonly short[] DeathWatchKillsValues = [0, 5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100]; // :333
    private static readonly float[] ProsperityTargetValues = [0f, 0.5f, 0.75f, 1f];          // :337
    private static readonly short[] ArtifactCountValues = [0, 5, 10, 25];                    // :341
    private static readonly short[] FlagCountValues = [0, 5, 10, 25];                        // :345
    private static readonly short[] CountdownMinutesValues = [0, 10, 20, 30, 60, 120, 180, 240, 300, 360]; // :329

    /// <summary>The draft params — a clone of the mission's advertisement. Every
    /// edit mutates this copy only.</summary>
    public MissionParamsMsg Draft { get; }

    /// <summary>The assembled side count the TEAMS control may not exceed. The
    /// server clamps a grow to it (P853 divergence 1 — <c>Mission.Sides</c> is
    /// fixed at assembly, <c>Mission.cs:37-38</c>), so the control is capped here
    /// instead of letting the owner pick a number the server will quietly
    /// reduce.</summary>
    public short MaxTeams { get; }

    /// <summary>The side count the mission was advertised with (native's
    /// <c>GetBaseMissionParams().nTeams</c>, read by the all-inactive shrink
    /// check, <c>newgamescreen.cpp:1092-1107</c>).</summary>
    public short BaseTeams { get; }

    private MissionSettingsForm(MissionParamsMsg draft, short maxTeams, short baseTeams, int cursor)
    {
        Draft = draft;
        MaxTeams = maxTeams;
        BaseTeams = baseTeams;
        Cursor = cursor;
    }

    /// <summary>Open the form for the local player, or return null when they may
    /// not edit (not the mission owner / the game already started — native's
    /// <c>bCanChangeSettings</c>, <c>teamscreen.cpp:1586-1589</c>, plus the
    /// server's STAGE_NOTSTARTED gate, <c>FedSrv.CPP:7560</c>).</summary>
    public static MissionSettingsForm? TryOpen(ClientWorld world, int cursor = 0)
    {
        if (!CanEdit(world)) return null;
        if (world.Mission?.Params is not { } advertised) return null;
        short max = MaxTeamCount(world);
        short baseTeams = world.Mission.Params.NTems;
        return new MissionSettingsForm(advertised.Clone(), max, baseTeams, Clamp(cursor, 0, Fields.Length - 1));
    }

    /// <summary>May the local player edit the mission's settings right now?</summary>
    public static bool CanEdit(ClientWorld world) =>
        world.AmMissionOwner
        && world.Mission is { Stage: (int)MissionStage.NotStarted }
        && world.Mission.Params is not null;

    /// <summary>The highest team count the owner may SET. Native's team combo
    /// spans two..<c>c_cSidesMax</c> (six) and <c>SetMissionParams</c> GROWS the
    /// mission with <c>InitSide</c> when the count rises
    /// (<c>FedSrv/fsmission.cpp:1846-1852</c>, <c>Igc/missionigc.cpp:3749-3779</c>),
    /// so the ceiling is the six-slot native maximum — P871 removed the former
    /// "assembled side count" cap that blocked a live growth.</summary>
    public static short MaxTeamCount(ClientWorld world) => 6;

    /// <summary>The row the cursor is on.</summary>
    public int Cursor { get; private set; }

    /// <summary>The row under the cursor (the one LEFT/RIGHT/SPACE edit).</summary>
    public Field CursorField => Fields[Clamp(Cursor, 0, Fields.Length - 1)];

    /// <summary>Move the cursor, wrapping (the port's lobby-pane idiom).</summary>
    public void Move(int delta)
    {
        int n = Fields.Length;
        Cursor = ((Cursor + delta) % n + n) % n;
    }

    /// <summary>Is this row a checkbox (native's <c>ButtonPane</c> flags) rather
    /// than a combo (<c>newgamescreen.cpp:970-987</c>)?</summary>
    public static bool IsToggle(Field field) => field >= Field.FriendlyFire;

    /// <summary>Native <c>GetChecked()</c> for a checkbox row.</summary>
    public bool IsChecked(Field field) => Draft[BitFor(field)];

    /// <summary>Apply one step to a row: LEFT/RIGHT (or SPACE/ENTER on a
    /// checkbox). Value rows step through the native combo lists.</summary>
    public void Adjust(Field field, int delta)
    {
        switch (field)
        {
            case Field.Teams:
                Draft.NTems = Step(TeamCountValues, Draft.NTems, delta, (short)Math.Min(2, (int)MaxTeams), MaxTeams);
                if (Draft.NTems > MaxTeams) Draft.NTems = MaxTeams;
                break;
            case Field.MaxPlayersPerTeam:
                Draft.NMaxPlayersPerTeam = (byte)Step(MaxPlayersValues, Draft.NMaxPlayersPerTeam, delta, 1, 100);
                // Native non-club ReadControls pins the minimum to 1 and never
                // lets it exceed the maximum (newgamescreen.cpp:985-995).
                if (Draft.NMinPlayersPerTeam < 1) Draft.NMinPlayersPerTeam = 1;
                if (Draft.NMinPlayersPerTeam > Draft.NMaxPlayersPerTeam)
                    Draft.NMinPlayersPerTeam = Draft.NMaxPlayersPerTeam;
                break;
            case Field.MinRank:
                Draft.IMinRank = (short)Math.Clamp(Draft.IMinRank + delta, -1, 1000);
                if (Draft.IMinRank > Draft.IMaxRank) Draft.IMaxRank = Draft.IMinRank;
                break;
            case Field.MaxRank:
                Draft.IMaxRank = (short)Math.Clamp(Draft.IMaxRank + delta, -1, 1000);
                if (Draft.IMaxRank < Draft.IMinRank) Draft.IMinRank = Draft.IMaxRank;
                break;
            case Field.Lives:
                Draft.ILives = Step(LivesValues, Draft.ILives, delta, 0, 0x7FFF);
                break;
            case Field.Conquest:
                Draft.IGoalConquestPercentage = Step(ConquestPercentValues, Draft.IGoalConquestPercentage, delta, 0, 100);
                break;
            case Field.Territory:
                Draft.IGoalTerritoryPercentage = Step(TerritoryValues, Draft.IGoalTerritoryPercentage, delta, 80, 100);
                break;
            case Field.Flags:
                Draft.NGoalFlagsCount = Step(FlagCountValues, Draft.NGoalFlagsCount, delta, 0, 100);
                break;
            case Field.Artifacts:
                Draft.NGoalArtifactsCount = Step(ArtifactCountValues, Draft.NGoalArtifactsCount, delta, 0, 100);
                break;
            case Field.TeamKills:
                Draft.NGoalTeamKills = Step(DeathWatchKillsValues, Draft.NGoalTeamKills, delta, 0, 100);
                break;
            case Field.Prosperity:
                Draft.FGoalTeamMoney = Step(ProsperityTargetValues, Draft.FGoalTeamMoney, delta);
                break;
            case Field.GameLength:
                Draft.DtGameLength = Step(CountdownMinutesValues, (short)MathF.Round(Draft.DtGameLength / 60f), delta, 0, 360) * 60f;
                break;
            default:
                int bit = BitFor(field);
                Draft[bit] = !Draft[bit];
                break;
        }
    }

    /// <summary>The native bool:1 index for a checkbox row
    /// (<c>Igc/igc.h:1120-1156</c> declaration order).</summary>
    public static int BitFor(Field field) => field switch
    {
        Field.FriendlyFire => MissionParamsMsg.BitAllowFriendlyFire,   // igc.h:1149
        Field.AlliedViz => MissionParamsMsg.BitAllowAlliedViz,         // igc.h:1127
        Field.AlliedRip => MissionParamsMsg.BitAllowAlliedRip,         // igc.h:1126
        Field.EmptyTeams => MissionParamsMsg.BitAllowEmptyTeams,       // igc.h:1125
        Field.Developments => MissionParamsMsg.BitAllowDevelopments,   // igc.h:1128
        Field.SquadGame => MissionParamsMsg.BitSquadGame,              // igc.h:1142
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "not a checkbox row"),
    };

    /// <summary>The row's label, ASCII (the HUD spritefonts bake ASCII only).</summary>
    public static string Label(Field field) => field switch
    {
        Field.Teams => "TEAMS",
        Field.MaxPlayersPerTeam => "MAX PLAYERS/TEAM",
        Field.MinRank => "MIN RANK",
        Field.MaxRank => "MAX RANK",
        Field.Lives => "LIVES",
        Field.Conquest => "CONQUEST GOAL",
        Field.Territory => "TERRITORY GOAL",
        Field.Flags => "FLAGS TO WIN",
        Field.Artifacts => "ARTIFACTS TO WIN",
        Field.TeamKills => "KILLS TO WIN",
        Field.Prosperity => "PROSPERITY TARGET",
        Field.GameLength => "GAME LENGTH",
        Field.FriendlyFire => "FRIENDLY FIRE",
        Field.AlliedViz => "ALLIED VISIBILITY",
        Field.AlliedRip => "ALLIED RIP",
        Field.EmptyTeams => "ALLOW EMPTY TEAMS",
        Field.Developments => "DEVELOPMENTS",
        Field.SquadGame => "SQUAD GAME",
        _ => field.ToString().ToUpperInvariant(),
    };

    /// <summary>The row's current value, formatted the way the native combos show
    /// it (unlimited lives, minutes, percentages, checkboxes).</summary>
    public string Format(Field field) => field switch
    {
        Field.Teams => Draft.NTems + (MaxTeams > 0 && Draft.NTems == MaxTeams ? " (MAX)" : ""),
        Field.MaxPlayersPerTeam => Draft.NMaxPlayersPerTeam.ToString(),
        Field.MinRank => Draft.IMinRank <= -1 ? "NONE" : Draft.IMinRank.ToString(),
        Field.MaxRank => Draft.IMaxRank.ToString(),
        Field.Lives => Draft.ILives >= 0x7FFF ? "UNLIMITED" : Draft.ILives.ToString(),
        Field.Conquest => Draft.IGoalConquestPercentage + "%",
        Field.Territory => Draft.IGoalTerritoryPercentage >= 100 ? "OFF" : Draft.IGoalTerritoryPercentage + "%",
        Field.Flags => Draft.NGoalFlagsCount.ToString(),
        Field.Artifacts => Draft.NGoalArtifactsCount.ToString(),
        Field.TeamKills => Draft.NGoalTeamKills.ToString(),
        Field.Prosperity => Draft.FGoalTeamMoney <= 0f ? "OFF" : Draft.FGoalTeamMoney.ToString("0.##"),
        Field.GameLength => Draft.DtGameLength <= 0f ? "OFF" : (Draft.DtGameLength / 60f).ToString("0") + " MIN",
        _ => IsChecked(field) ? "YES" : "NO",
    };

    /// <summary>Native's <c>Invalid(true)</c> string for the current draft, or
    /// null when it would be accepted (<c>igc.h:1339-1506</c> through the port's
    /// <see cref="MissionParamsMsg.Invalid"/>). This is the form's error display:
    /// native shows the identical string in a message box and sends nothing
    /// (<c>newgamescreen.cpp:1117-1120</c>).</summary>
    public string? Validate()
    {
        if (Draft.Invalid(bFromClient: true) is { } why) return why;
        // Native's additional client-side check (newgamescreen.cpp:1092-1107):
        // a shrink that would leave every surviving team inactive is refused.
        if (Draft.NTems < BaseTeams && AllRemainingSidesInactive()) 
            return "You can't reduce the number of teams if all remaining teams would be inactive.";
        return null;
    }

    /// <summary>Per-side activity the shrink check needs (SIDE_INACTIVE state,
    /// <see cref="ClientWorld.IsSideActive"/>); captured at open so the check is
    /// pure.</summary>
    private bool[]? _sideActive;

    /// <summary>Snapshot which sides are active (called by the host when it opens
    /// the form — the check itself must not read the world from a background
    /// thread).</summary>
    public void CaptureSideActivity(ClientWorld world)
    {
        int n = Math.Max(BaseTeams, MaxTeams);
        var active = new bool[Math.Max(1, n)];
        for (short i = 0; i < active.Length; i++) active[i] = world.IsSideActive(i);
        _sideActive = active;
    }

    private bool AllRemainingSidesInactive()
    {
        if (_sideActive is null) return false;
        for (int i = 0; i < Math.Min(Draft.NTems, _sideActive.Length); i++)
            if (_sideActive[i]) return false;
        return true;
    }

    /// <summary>Build the message to send. Returns false with the error string when
    /// the permissions lapsed (the game started / ownership changed) or the params
    /// are invalid — in both cases NOTHING may go on the wire: the server
    /// <c>TrapHackBoot</c>s an invalid MISSIONPARAMS sender
    /// (<c>FedSrv.CPP:7534</c>).</summary>
    public bool TryBuildPlan(ClientWorld world, out MissionParamsMsg plan, out string? error)
    {
        plan = Draft;
        if (!CanEdit(world))
        {
            error = "ONLY THE MISSION OWNER CAN CHANGE SETTINGS BEFORE THE START";
            return false;
        }
        if (Validate() is { } why)
        {
            error = why;
            return false;
        }
        error = null;
        return true;
    }

    // ---- stepping helpers ---------------------------------------------------

    /// <summary>Step through a native combo list: snap to the nearest entry (so a
    /// value the server advertised that is not in the list still edits sensibly,
    /// native's <c>FindClosestValue</c>, the combo-selection helper) and move
    /// one entry, clamped to the list ends.</summary>
    private static short Step(short[] values, short current, int delta, short? min = null, short? max = null)
    {
        if (values.Length == 0) return current;
        int idx = 0;
        int best = int.MaxValue;
        for (int i = 0; i < values.Length; i++)
        {
            int d = Math.Abs(values[i] - current);
            if (d < best) { best = d; idx = i; }
        }
        int next = Math.Clamp(idx + delta, 0, values.Length - 1);
        short v = values[next];
        if (min is { } lo && v < lo) v = lo;
        if (max is { } hi && v > hi) v = hi;
        return v;
    }

    private static float Step(float[] values, float current, int delta)
    {
        if (values.Length == 0) return current;
        int idx = 0;
        float best = float.MaxValue;
        for (int i = 0; i < values.Length; i++)
        {
            float d = MathF.Abs(values[i] - current);
            if (d < best) { best = d; idx = i; }
        }
        return values[Math.Clamp(idx + delta, 0, values.Length - 1)];
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}
