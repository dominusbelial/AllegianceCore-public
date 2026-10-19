using Allegiance.Server;

namespace Allegiance.SinglePlayer;

public enum SkirmishSize { Small, Medium, Large, Epic }
public enum SkirmishCommander { AI, You }
public enum SkirmishDifficulty { Easy, Normal, Hard }

/// <summary>Local orchestration choices; race identities come from PCore001 (P702).</summary>
public sealed record SkirmishOptions
{
    public static readonly (int Id, string Name)[] Civilizations =
    [
        (1, "Dreghklar"), (2, "Technoflux"), (3, "Ga'Taraan"), (4, "Phoenix"), (5, "Omicron"),
        (18, "Gigacorp"), (25, "Iron Coalition"), (35, "Bios"), (101, "Belters"), (201, "Rixian")
    ];
    public int? Seed { get; init; }
    public double ResearchSpeed { get; init; } = 1;
    public bool SpectatorDefault { get; init; }
    public int PlayerCiv { get; init; } = 25;
    public int EnemyCiv { get; init; } = 3;
    public SkirmishSize Size { get; init; }
    public SkirmishDifficulty Difficulty { get; init; } = SkirmishDifficulty.Normal;
    public SkirmishCommander Commander { get; init; }
    public string TechMode { get; init; } = "research";
    public string Map { get; init; } = "m2xspiderw.igc";
    private bool? _startAsSpectator;
    public bool StartAsSpectator { get => _startAsSpectator ?? Size is SkirmishSize.Large or SkirmishSize.Epic; init => _startAsSpectator = value; }
    public int TeamSize => Size switch
    {
        SkirmishSize.Small => 5, SkirmishSize.Medium => 15, SkirmishSize.Large => 30,
        SkirmishSize.Epic => 60, _ => throw new ArgumentOutOfRangeException(nameof(Size))
    };
    public string Warning => Size == SkirmishSize.Epic
        ? "Epic runs 120 pilots on this machine. Expect higher CPU use and automatic slow motion." : "";
    public (float Accuracy, double Reaction, float Aggression) Knobs => Difficulty switch
    {
        SkirmishDifficulty.Easy => (.55f, .4, .75f),
        SkirmishDifficulty.Normal => (1, 0, 1),
        SkirmishDifficulty.Hard => (1.5f, 0, 1.25f),
        _ => throw new ArgumentOutOfRangeException(nameof(Difficulty))
    };

    public ServerConfig CreateConfig(string? artwork = null)
    {
        if (!Civilizations.Any(c => c.Id == PlayerCiv) || !Civilizations.Any(c => c.Id == EnemyCiv))
            throw new ArgumentException("Select a PCore001 civilization");
        if (TechMode is not ("research" or "unlocked")) throw new ArgumentException("Invalid tech mode");
        if (!Enum.IsDefined(Commander)) throw new ArgumentException("Invalid commander");
        _ = Knobs;
        return new()
        {
            BenchmarkSeed = Seed,
            // All owned pilots, commanders and probes originate from loopback.
            Transport = new()
            {
                MaxConnections = Math.Max(1024, TeamSize * 2 + 32),
                MaxConnectionsPerIp = Math.Max(64, TeamSize * 2 + 32)
            },
            ArtPath = artwork ?? Allegiance.Core.ExternalPaths.Resolve(Allegiance.Core.ExternalResourceKind.Artwork),
            Port = 0, BindAddress = "127.0.0.1", Discovery = new() { Enabled = false },
            Showcase = new() { TechMode = TechMode, ResearchTimeScale = ResearchSpeed },
            LiveSettings = new()
            {
                TestUnlock = new() { Enabled = TechMode == "unlocked", Money = 1_000_000 },
                AdminCheats = Seed is not null ? new() { Enabled = true, AllowNames = [Benchmark.BenchmarkCommand.PilotName] } : null,
                TimeDilation = "auto", AutoNextMatch = new() { Enabled = true },
                TestHarness = new() { Enabled = TechMode == "unlocked", Money = 1_000_000, IgnoreTechGate = TechMode == "unlocked" }
            },
            Games = [new()
            {
                Name = "Skirmish", Map = Map, Teams = 2, CivIds = [PlayerCiv, EnemyCiv],
                SideNames = [Civilizations.First(c => c.Id == PlayerCiv).Name, Civilizations.First(c => c.Id == EnemyCiv).Name],
                // Worker probes and the research commander may occupy additional seats.
                MaxPlayersPerTeam = TeamSize + 10, MaxTeamImbalance = 0x7fff,
                ResearchPlans = new() { [0] = "auto", [1] = "auto" }
            }]
        };
    }
}

/// <summary>Reserve two logical CPUs for rendering and sim; bound compute workers even on large machines.</summary>
public sealed record SkirmishBudget(int Workers, int CoresPerWorker)
{
    public static SkirmishBudget ForCores(int cores, int bots = 120)
    {
        int available = Math.Max(1, cores - 2);
        int workers = Math.Min(Math.Clamp(available / 2, 1, 4), Math.Max(1, (bots + 63) / 64));
        return new(workers, Math.Max(1, available / workers));
    }
}
