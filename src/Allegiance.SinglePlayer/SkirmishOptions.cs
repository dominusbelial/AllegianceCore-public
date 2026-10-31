using Allegiance.Server;

namespace Allegiance.SinglePlayer;

public enum SkirmishSize { Small, Medium, Large, Epic, Grand }
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
    /// <summary>Explicit multiplayer exposure; ordinary skirmishes remain loopback only.</summary>
    public bool HostLan { get; init; }
    public const int DefaultHostPort = 6073;
    public int HostPort { get; init; } = DefaultHostPort;
    public string GameName { get; init; } = "LAN game";
    public string Password { get; init; } = "";
    public int? Seed { get; init; }
    public double ResearchSpeed { get; init; } = 1;
    public bool SpectatorDefault { get; init; }
    public int PlayerCiv { get; init; } = 25;
    public int EnemyCiv { get; init; } = 3;
    public SkirmishSize Size { get; init; }
    public SkirmishDifficulty Difficulty { get; init; } = SkirmishDifficulty.Normal;
    public SkirmishCommander Commander { get; init; }
    public SkirmishSpeed Speed { get; init; } = SkirmishSpeed.Normal;
    /// <summary>P799 — the skirmish lives option: the native per-player life
    /// budget (<c>MissionParams::iLives</c>, igc.h:1185) passed straight into
    /// <see cref="GameConfig.Lives"/>. Default
    /// <see cref="Allegiance.Core.Wire.MissionDef.UnlimitedLives"/> — the native
    /// default and every pre-P799 skirmish's behaviour.</summary>
    public Allegiance.Core.Model.MissionGoals Goals { get; init; } = new();
    /// <summary>Optional unattended goal coverage preset. Omitted preserves fixed goals.</summary>
    public GoalRotationConfig? GoalRotation { get; init; }
    /// <summary>P856 — native <c>MissionParams::bAllowFriendlyFire</c>
    /// (igc.h:1149), the create-game option: same-side and allied damage when
    /// on. Default off, exactly native <c>MissionParams::Reset</c>
    /// (igc.h:1246 zeroes the bitfield).</summary>
    public bool AllowFriendlyFire { get; init; }
    /// <summary>P856 — native <c>MissionParams::bAllowAlliedViz</c>
    /// (igc.h:1127): allied sides share models and scans. Default off, native
    /// <c>Reset</c> (igc.h:1243).</summary>
    public bool AllowAlliedViz { get; init; }
    public short Lives { get; init; } = Allegiance.Core.Wire.MissionDef.UnlimitedLives;
    /// <summary>P799 — the values the setup screen's Lives row cycles through:
    /// the unlimited sentinel, then the finite budgets. Native's lobby option
    /// ("Lives", fsmission.cpp:4086 fLimitedLives) accepts any count.</summary>
    public static readonly short[] LivesChoices = [Allegiance.Core.Wire.MissionDef.UnlimitedLives, 5, 3, 1];
    public string TechMode { get; init; } = "research";
    public string Map { get; init; } = "m2xspiderw.igc";
    /// <summary>P814 — bot turret gunners in skirmish (item 4c): ON by default
    /// (native behaviour, not a flagged experiment); the setup screen can turn
    /// them off. Reaches the bot driver via <c>ALLEGIANCE_GUNNERS</c>.</summary>
    public bool BotGunners { get; init; } = true;
    private bool? _startAsSpectator;
    public bool StartAsSpectator { get => _startAsSpectator ?? Size is SkirmishSize.Large or SkirmishSize.Epic or SkirmishSize.Grand; init => _startAsSpectator = value; }
    /// <summary>
    /// P889 — the skirmish sizing candidate a calibration run is testing
    /// (null fields keep the resolver's choice). Explicit values reach the
    /// server config exactly like hand-written config, so the boot resolvers'
    /// precedence is unchanged.
    /// </summary>
    public SkirmishSizing? Sizing { get; init; }

    /// <summary>P889 — measurement-only override of the pre-launch countdown
    /// (native default 60 s / <c>fStartCountdown</c> 15 s). The bounded
    /// calibration uses a short countdown so each candidate measures fight time
    /// rather than lobby time; a normal skirmish leaves it null.</summary>
    public double? StartCountdownSeconds { get; init; }

    /// <summary>
    /// P889 — the seats a skirmish actually asks the server to size for: both
    /// teams plus the commanders/probes they seat (one research commander and
    /// one probe pilot per side). The server uses this with
    /// <c>hostProfile = "shared"</c> instead of the 200v200 capacity default.
    /// </summary>
    public int SeatCount => TeamSize * 2 + 4;

    public int TeamSize => Size switch
    {
        SkirmishSize.Small => 5, SkirmishSize.Medium => 15, SkirmishSize.Large => 30,
        SkirmishSize.Epic => 60,
        // P789 — the largest size the local host's own limits already support:
        // transport MaxConnections floors at 1024 (2×200 + commanders + workers
        // fit easily) and MaxPlayersPerTeam is TeamSize + 10. No bot or server
        // behaviour changes — Grand is just the rig's 200v200 preset exposed.
        SkirmishSize.Grand => 200, _ => throw new ArgumentOutOfRangeException(nameof(Size))
    };
    public string Warning => Size switch
    {
        SkirmishSize.Epic => "Epic runs 120 pilots on this machine. Expect higher CPU use and automatic slow motion.",
        SkirmishSize.Grand => "Grand runs 400 pilots — the rig's 200v200 Showcase preset. Expect heavy CPU use and automatic slow motion.",
        _ => ""
    };
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
        if (HostLan)
        {
            if (HostPort is < 1024 or > 65534) throw new ArgumentException("LAN port must be 1024..65534 (discovery uses port + 1).");
            if (string.IsNullOrWhiteSpace(GameName) || GameName.Length > 63 || GameName.Any(char.IsControl))
                throw new ArgumentException("Enter a game name of 1..63 characters.");
            if (Password.Length > 15 || Password.Any(c => c < 32 || c > 126))
                throw new ArgumentException("Password must be at most 15 printable ASCII characters.");
            if (TechMode != "research" || Seed is not null || Sizing is not null || StartCountdownSeconds is not null
                || ResearchSpeed != 1 || Speed != SkirmishSpeed.Normal || GoalRotation is not null)
                throw new ArgumentException("LAN hosting requires research tech and normal speed; benchmark and test overrides are unavailable.");
            if (StartAsSpectator || SpectatorDefault) throw new ArgumentException("The LAN host must join as a normal pilot.");
            if (Size is not (SkirmishSize.Small or SkirmishSize.Medium))
                throw new ArgumentException("LAN hosting supports Small or Medium fleets.");
        }
        var sizing = Sizing ?? new SkirmishSizing();
        return new()
        {
            BenchmarkSeed = Seed,
            // P889 — single-player: the server shares this PC with the game
            // client and the bot driver, and it is sized for the seats the
            // skirmish actually asks for (not the configured 200v200 capacity).
            HostProfile = "shared",
            ExpectedPlayers = SeatCount,
            // All owned pilots, commanders and probes originate from loopback.
            Transport = new()
            {
                MaxConnections = HostLan ? 128 : Math.Max(1024, TeamSize * 2 + 32),
                MaxConnectionsPerIp = HostLan ? 64 : Math.Max(64, TeamSize * 2 + 32),
                Protocol = HostLan ? "udp" : "tcp",
                Mode = sizing.TransportMode,
                PoolWorkers = sizing.PoolWorkers,
            },
            // A calibration candidate may pin the build; otherwise the resolver
            // sizes it from the shared profile (an absent section = resolver).
            Broadcast = sizing is { ParallelBuild: null, BuildWorkers: null }
                ? null
                : new() { ParallelBuild = sizing.ParallelBuild, MaxDegreeOfParallelism = sizing.BuildWorkers },
            ArtPath = artwork ?? Allegiance.Core.ExternalPaths.Resolve(Allegiance.Core.ExternalResourceKind.Artwork),
            Port = HostLan ? HostPort : 0, BindAddress = HostLan ? "0.0.0.0" : "127.0.0.1",
            Discovery = new() { Enabled = HostLan },
            SideSelectAutoSeatSeconds = Speed == SkirmishSpeed.Showcase ? ShowcaseSideSelectAutoSeatSeconds : null,
            Showcase = new() { TechMode = TechMode, ResearchTimeScale = SpeedProfile.Research, IncomeMultiplier = SpeedProfile.Income, FinaleTimeoutSeconds = SpeedProfile.FinaleSeconds, GoalRotation = GoalRotation },
            LiveSettings = new()
            {
                TestUnlock = new() { Enabled = !HostLan && TechMode == "unlocked", Money = 1_000_000 },
                AdminCheats = !HostLan && Seed is not null ? new() { Enabled = true, AllowNames = [Benchmark.BenchmarkCommand.PilotName] } : null,
                TimeDilation = "auto", AutoNextMatch = new() { Enabled = true },
                TestHarness = new() { Enabled = !HostLan && TechMode == "unlocked", Money = 1_000_000, IgnoreTechGate = TechMode == "unlocked" }
            },
            Games = [new()
            {
                Name = HostLan ? GameName.Trim() : "Skirmish", Password = HostLan ? Password : "", Map = Map, Teams = 2, CivIds = [PlayerCiv, EnemyCiv],
                SideNames = [Civilizations.First(c => c.Id == PlayerCiv).Name, Civilizations.First(c => c.Id == EnemyCiv).Name],
                // Worker probes and the research commander may occupy additional seats.
                MaxPlayersPerTeam = TeamSize + (HostLan ? 6 : 10), MaxTeamImbalance = HostLan ? 1 : 0x7fff,
                ResearchPlans = new() { [0] = "auto", [1] = "auto" },
                // P798 — native fog on the rig scale: the bot scouts unveil the map
                // and the clash forms around the resources they find.
                // P837 — the reveal scales with the match. Native bShowMap reveals
                // the WARPS only, never stations or rocks (Igc/igc.h:1123,
                // Igc/mapmakerigc.cpp:1002-1017 RevealMap), and a Small/Medium game
                // is a quick single-player fight on the authored 13-sector map with
                // two-to-five scouts: measured on master, the first aleph out of the
                // home sector alone costs 100-160 s of sweeping, so a 9-bot skirmish
                // had fired ZERO shots after 210 s (P727 live test) — bots that never
                // meet never fight, and bot offence is a single-player feature.
                // Revealing the warps lets both fleets route and meet in about a
                // minute while the objects they fight over stay per-side discovery;
                // Large and up (the 30v30+ showcase/rig scale P798/P830/P831 were
                // designed and measured on) keep native fog exactly as shipped.
                ShowMap = TeamSize < 30,
                // P799 — the skirmish lives option reaches the wire (MISSIONDEF
                // iLives) and the server's ghost gates through the game config.
                Lives = Lives, Goals = Goals with { }, CivSeed = GoalRotation is null ? null : Seed,
                StartCountdownSeconds = StartCountdownSeconds,
                AllowFriendlyFire = AllowFriendlyFire, AllowAlliedViz = AllowAlliedViz,
                // P767 / SIM-0100 — a skirmish embeds the offline case: native's
                // wingman resupply latch only fires when the client runs the sim
                // (clintlib.cpp:3672-3715, disconnected else branch).
                NpcWingmanAmmoResupply = !HostLan
            }]
        };
    }
}

/// <summary>One measured/declared bot-driver sizing for a skirmish.</summary>
public sealed record SkirmishSizing(
    bool? ParallelBuild = null, int? BuildWorkers = null, string? TransportMode = null,
    int? PoolWorkers = null, int? BotWorkers = null, int? BotCoresPerWorker = null);

/// <summary>Reserve the client's cores for rendering and sim; bound compute workers even on large machines.</summary>
public sealed record SkirmishBudget(int Workers, int CoresPerWorker)
{
    public static SkirmishBudget ForCores(int cores, int bots = 120)
    {
        int available = Math.Max(1, cores - 2);
        int workers = Math.Min(Math.Clamp(available / 2, 1, 4), Math.Max(1, (bots + 63) / 64));
        return new(workers, Math.Max(1, available / workers));
    }

    /// <summary>
    /// P889 — size the bot driver from the SAME shared-machine budget the
    /// server resolver uses: the resolver's client/bot reserve is what the bots
    /// and the renderer keep, and any calibration override replaces only its
    /// own value. This replaces <see cref="ForCores"/>'s flat "cores - 2" for a
    /// real skirmish host.
    /// </summary>
    public static SkirmishBudget ForOptions(SkirmishOptions options, int cores)
    {
        int bots = Math.Max(1, options.SeatCount);
        var sizing = PoolSizingResolver.Resolve(Math.Max(1, cores), bots, HostProfile.Shared);
        int available = Math.Max(1, cores - sizing.ClientReservedCores);
        int workers = options.Sizing?.BotWorkers
            ?? Math.Min(Math.Clamp(available / 2, 1, 4), Math.Max(1, (bots + 63) / 64));
        workers = Math.Clamp(workers, 1, Math.Max(1, available));
        int perWorker = options.Sizing?.BotCoresPerWorker ?? Math.Max(1, available / workers);
        return new(workers, perWorker);
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
    bool SpectatorDefault = false,
    bool BotGunners = true,
    short Lives = Allegiance.Core.Wire.MissionDef.UnlimitedLives,
    Allegiance.Core.Model.MissionGoals? Goals = null,
    bool AllowFriendlyFire = false,
    bool AllowAlliedViz = false)
{
    public static SkirmishPrefs From(SkirmishOptions o) => new(o.Size, o.Difficulty, o.Commander, o.Speed,
        o.PlayerCiv, o.EnemyCiv, o.TechMode, o.StartAsSpectator, o.SpectatorDefault, o.BotGunners, o.Lives, o.Goals with { },
        o.AllowFriendlyFire, o.AllowAlliedViz);

    public SkirmishOptions ToOptions() => new()
    {
        Size = Size, Difficulty = Difficulty, Commander = Commander, Speed = Speed,
        PlayerCiv = PlayerCiv, EnemyCiv = EnemyCiv, TechMode = TechMode,
        StartAsSpectator = StartAsSpectator, SpectatorDefault = SpectatorDefault, BotGunners = BotGunners, Lives = Lives, Goals = Goals is { } goals ? goals with { } : new(),
        AllowFriendlyFire = AllowFriendlyFire, AllowAlliedViz = AllowAlliedViz
    };
}
