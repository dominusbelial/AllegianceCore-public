using Allegiance.Server;

namespace Allegiance.SinglePlayer;

public enum SkirmishSize { Small, Medium, Large, Epic }
public enum SkirmishCommander { AI, You }
public enum SkirmishDifficulty { Easy, Normal, Hard }

/// <summary>SP-6 — single-player economy pace. Determines showcase income/research/finale
/// pacing for the embedded skirmish host (all local; offline).</summary>
public enum SkirmishSpeed { Normal, Fast, Showcase }

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
    public SkirmishSpeed Speed { get; init; } = SkirmishSpeed.Normal;
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

    // SP-6 economy pace constants. Showcase mirrors the rig's authored showcase config
    // (docs/reports/P733_assets/rig-showcase-delta.json: incomeMultiplier 5000,
    // researchTimeScale 8, finaleTimeoutSeconds 300). Fast is a local SP-6 measurement:
    // a Small/Medium match reaches mid tech in ~20-30 min at these values.
    public const double ShowcaseIncomeMultiplier = 5000;
    public const double ShowcaseResearchTimeScale = 8;
    public const double RigFinaleTimeoutSeconds = 300;
    /// <summary>P763 — showcase (rig and single-player) keeps the pre-flag 30 s
    /// side-select auto-seat, which is a port adaptation, not native; normal
    /// matches leave <c>ServerConfig.SideSelectAutoSeatSeconds</c> null so they
    /// wait for a POSITIONREQ exactly like native.</summary>
    public const double ShowcaseSideSelectAutoSeatSeconds = 30;
    public const double FastIncomeMultiplier = 8;
    public const double FastResearchTimeScale = 4;

    /// <summary>Showcase economy/research/finale pacing for the chosen speed. Normal uses
    /// <see cref="ResearchSpeed"/> (the native ×1 default, or a benchmark/legacy override).</summary>
    public (double Income, double Research, double FinaleSeconds) SpeedProfile => Speed switch
    {
        SkirmishSpeed.Normal => (1, ResearchSpeed, RigFinaleTimeoutSeconds),
        SkirmishSpeed.Fast => (FastIncomeMultiplier, FastResearchTimeScale, RigFinaleTimeoutSeconds),
        SkirmishSpeed.Showcase => (ShowcaseIncomeMultiplier, ShowcaseResearchTimeScale, RigFinaleTimeoutSeconds),
        _ => throw new ArgumentOutOfRangeException(nameof(Speed))
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
            SideSelectAutoSeatSeconds = Speed == SkirmishSpeed.Showcase ? ShowcaseSideSelectAutoSeatSeconds : null,
            Showcase = new() { TechMode = TechMode, ResearchTimeScale = SpeedProfile.Research, IncomeMultiplier = SpeedProfile.Income, FinaleTimeoutSeconds = SpeedProfile.FinaleSeconds },
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
                ResearchPlans = new() { [0] = "auto", [1] = "auto" },
                // P767 / SIM-0100 — a skirmish embeds the offline case: native's
                // wingman resupply latch only fires when the client runs the sim
                // (!m_fm.IsConnected(), clintlib.cpp:3697-3717).
                NpcWingmanAmmoResupply = true
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

/// <summary>SP-6 — remembered local skirmish choices, persisted in the player config
/// (player.json via <c>IntroPreferences</c>) so a fresh launch restores them offline.
/// Mirrors the setup rows; absent JSON fields fall back to defaults.</summary>
public sealed record SkirmishPrefs(
    SkirmishSize Size = SkirmishSize.Small,
    SkirmishDifficulty Difficulty = SkirmishDifficulty.Normal,
    SkirmishCommander Commander = SkirmishCommander.AI,
    SkirmishSpeed Speed = SkirmishSpeed.Normal,
    int PlayerCiv = 25,
    int EnemyCiv = 3,
    string TechMode = "research",
    bool StartAsSpectator = false,
    bool SpectatorDefault = false)
{
    public static SkirmishPrefs From(SkirmishOptions o) => new(o.Size, o.Difficulty, o.Commander, o.Speed,
        o.PlayerCiv, o.EnemyCiv, o.TechMode, o.StartAsSpectator, o.SpectatorDefault);

    public SkirmishOptions ToOptions() => new()
    {
        Size = Size, Difficulty = Difficulty, Commander = Commander, Speed = Speed,
        PlayerCiv = PlayerCiv, EnemyCiv = EnemyCiv, TechMode = TechMode,
        StartAsSpectator = StartAsSpectator, SpectatorDefault = SpectatorDefault
    };
}
