namespace Allegiance.SinglePlayer;

public sealed class SkirmishScreenModel
{
    public SkirmishOptions Options { get; private set; } = new();
    public void ApplyRemembered(SkirmishPrefs prefs) => Options = prefs.ToOptions();
    public void ApplyDefaults(Benchmark.BenchmarkRecommendation settings) => Options = Options with
    { Size = settings.BattleSize, Difficulty = settings.Difficulty, ResearchSpeed = settings.ResearchSpeed, Speed = settings.Speed, SpectatorDefault = settings.SpectatorDefault };
    public int Selected { get; private set; }
    public const int StartRow = 12, BackRow = 13, RowCount = 14;
    public void Move(int delta) => Selected = (Selected + delta + RowCount) % RowCount;
    public void Change(int delta)
    {
        var races = SkirmishOptions.Civilizations;
        int Race(int id) => races[(Array.FindIndex(races, c => c.Id == id) + delta + races.Length) % races.Length].Id;
        Options = Selected switch
        {
            0 => Options with { PlayerCiv = Race(Options.PlayerCiv) },
            1 => Options with { EnemyCiv = Race(Options.EnemyCiv) },
            2 => Options with { Size = (SkirmishSize)(((int)Options.Size + delta + 5) % 5) },
            3 => Options with { TechMode = Options.TechMode == "research" ? "unlocked" : "research" },
            4 => Options with { Difficulty = (SkirmishDifficulty)(((int)Options.Difficulty + delta + 3) % 3) },
            5 => Options with { Commander = Options.Commander == SkirmishCommander.AI ? SkirmishCommander.You : SkirmishCommander.AI },
            6 => Options with { Speed = (SkirmishSpeed)(((int)Options.Speed + delta + 3) % 3) },
            7 => Options with { StartAsSpectator = !Options.StartAsSpectator },
            // P799 — the lives row cycles the unlimited sentinel and the finite budgets.
            8 => Options with { Lives = SkirmishOptions.LivesChoices[(Array.IndexOf(SkirmishOptions.LivesChoices, Options.Lives) + delta + SkirmishOptions.LivesChoices.Length) % SkirmishOptions.LivesChoices.Length] },
            // P856 — the two native create-game options (Igc/igc.h:1149,1127):
            // friendly fire off by default, allied visualization off by default.
            9 => Options with { AllowFriendlyFire = !Options.AllowFriendlyFire },
            10 => Options with { AllowAlliedViz = !Options.AllowAlliedViz },
            // P814 — the one switch that turns bot turret gunners off (default ON).
            11 => Options with { BotGunners = !Options.BotGunners },
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
        "Commander: " + Options.Commander,
        "Game speed: " + Options.Speed,
        "Start as spectator: " + (Options.StartAsSpectator ? "Yes" : "No"),
        "Lives: " + (Options.Lives == Allegiance.Core.Wire.MissionDef.UnlimitedLives ? "Unlimited" : Options.Lives.ToString()),
        "Friendly fire: " + (Options.AllowFriendlyFire ? "On" : "Off"),
        "Allied visibility: " + (Options.AllowAlliedViz ? "On" : "Off"),
        "Bot gunners: " + (Options.BotGunners ? "On" : "Off"),
        "Start Skirmish", "Back"
    ];
}
