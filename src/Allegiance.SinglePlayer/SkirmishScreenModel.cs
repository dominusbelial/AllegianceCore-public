namespace Allegiance.SinglePlayer;

public sealed class SkirmishScreenModel
{
    public SkirmishOptions Options { get; private set; } = new();
    public int Selected { get; private set; }
    public const int StartRow = 5, BackRow = 6, RowCount = 7;
    public void Move(int delta) => Selected = (Selected + delta + RowCount) % RowCount;
    public void Change(int delta)
    {
        var races = SkirmishOptions.Civilizations;
        int Race(int id) => races[(Array.FindIndex(races, c => c.Id == id) + delta + races.Length) % races.Length].Id;
        Options = Selected switch
        {
            0 => Options with { PlayerCiv = Race(Options.PlayerCiv) },
            1 => Options with { EnemyCiv = Race(Options.EnemyCiv) },
            2 => Options with { Size = (SkirmishSize)(((int)Options.Size + delta + 4) % 4) },
            3 => Options with { TechMode = Options.TechMode == "research" ? "unlocked" : "research" },
            4 => Options with { Difficulty = (SkirmishDifficulty)(((int)Options.Difficulty + delta + 3) % 3) },
            _ => Options
        };
    }
    public string[] Rows =>
    [
        "Your race: " + SkirmishOptions.Civilizations.First(c => c.Id == Options.PlayerCiv).Name,
        "Enemy race: " + SkirmishOptions.Civilizations.First(c => c.Id == Options.EnemyCiv).Name,
        $"Size: {Options.Size} {Options.TeamSize}v{Options.TeamSize}",
        "Tech: " + Options.TechMode,
        "Bot difficulty: " + Options.Difficulty,
        "Start Skirmish", "Back"
    ];
}
