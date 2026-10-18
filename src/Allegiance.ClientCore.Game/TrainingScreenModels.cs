using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// TR-2 — the chooser shown when the player picks "Single Player" on the intro
/// screen. Native's big single-player button IS training (WinTrek/introscreen.cpp:1221
/// trainBigButtonPane → OnButtonTraining :1558), so the port offers a choice between
/// Training and Skirmish. Skirmish keeps P727's setup; Training opens the mission list.
/// </summary>
public sealed class SinglePlayerChooserModel
{
    public const int RowCount = 2;

    /// <summary>The two choices, in order. Native trains from the big button; P727's
    /// skirmish lives behind the same single-player entry, hence the split.</summary>
    public static readonly string[] Labels = { "Training", "Skirmish" };

    public int Selected;

    public void Move(int delta) => Selected = (Selected + delta + Labels.Length) % Labels.Length;

    /// <summary>Rows for the header-less menu (pure label list).</summary>
    public string[] Rows => Labels;

    /// <summary>Activate the highlighted row: <c>true</c> = Training, <c>false</c> = Skirmish.</summary>
    public bool Activate() => Selected == 0;
}

/// <summary>
/// TR-2 — the pure, unit-testable model of the training screen: the list of native
/// missions with their names and descriptions. Native renders the mission numbers
/// (and their descriptive art) as <c>missionNumberN</c> bitmaps switched on the selected
/// mission (trainingscreen.mdl <c>missionInfoImage = SwitchImage(missionNumber, ...)</c>),
/// with one button per mission (trainingscreen.cpp:50-58). The port renders the same
/// list as text and marks every mission except Basic Flight (Mission 2) as disabled
/// "coming soon" — TR-2 ships Mission 2 only; the others are shown, never hidden.
/// </summary>
public sealed class TrainingScreenModel
{
    /// <summary>One selectable training mission (Training.h:21-32).</summary>
    public sealed record TrainingEntry(int MissionId, string Name, string Description, bool Playable);

    /// <summary>
    /// The native training screen's missions (trainingscreen.cpp:50-58 lists 1,2,3,4,5,6,8 —
    /// mission 7/Live was removed, noted "//TM7 removed" at :27). Names/descriptions are the
    /// well-known canonical Allegiance mission titles; native draws them as bitmaps
    /// (trainingmissionNbmp), so the port uses textual equivalents.
    /// </summary>
    public static readonly TrainingEntry[] Missions =
    {
        new(Tm1Introduction, "Introduction", "How to fly, shoot and command your wingmen.", Playable: false),
        new(Tm2BasicFlight, "Basic Flight", "Radar, turning, throttle, gauges, booster and autopilot home.", Playable: true),
        new(Tm3BasicWeaponry, "Basic Weaponry", "Weapon mounts, ammo, energy and firing techniques.", Playable: false),
        new(Tm4EnemyEngagement, "Enemy Engagement", "Engage and destroy hostile craft in combat.", Playable: false),
        new(Tm5CommandView, "Command View", "The command map, buying and giving orders.", Playable: false),
        new(Tm6PracticeArena, "Practice Arena", "A safe arena to practise ships and loadouts.", Playable: false),
        new(Tm8Nanite, "Nanite", "The nanite repair drone and survivability.", Playable: false),
    };

    public int Index;

    public TrainingEntry Selected => Missions[Index];

    public int Count => Missions.Length;

    public void Move(int delta) => Index = (Index + delta + Missions.Length) % Missions.Length;

    /// <summary>Row labels for the chooser menu, marking locked missions.</summary>
    public string[] Rows()
    {
        var rows = new string[Missions.Length];
        for (int i = 0; i < Missions.Length; i++)
            rows[i] = Missions[i].Playable ? Missions[i].Name : Missions[i].Name + "  (coming soon)";
        return rows;
    }

    /// <summary>The selected mission is playable iff it is the Basic Flight entry.</summary>
    public bool CanStart => Selected.Playable;
}
