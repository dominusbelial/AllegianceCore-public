using System.Globalization;
using System.Text.Json;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

/// <summary>P889 — who else lives on this machine.</summary>
public enum HostProfile
{
    /// <summary>The server owns the box (a dedicated server, the rig). The
    /// historical P887 behaviour is the large-player case of this profile.</summary>
    Dedicated,
    /// <summary>The server shares the machine with a game client and/or the
    /// local bot driver (single-player skirmish and training).</summary>
    Shared,
}

/// <summary>
/// P889 — the one sizing decision for the server's per-peer worker pools.
///
/// P887 made the rig's winning settings the defaults, but sized both pools from
/// the logical core count alone: <c>broadcast.parallelBuild</c> used
/// <c>cores-1</c> builders and <c>transport.mode="pool"</c> used <c>cores</c>
/// send workers. That is right for the 394-connection rig and wrong at both
/// ends: a 20-player dedicated server on a 16-thread box started 15+16 workers
/// it cannot use, and a single-player skirmish (server + renderer + bot driver
/// on ONE PC) took the dedicated-server pools while the renderer and the bots
/// needed the same cores.
///
/// <para><b>Inputs.</b> logical cores (<see cref="Environment.ProcessorCount"/>,
/// which honours container CPU limits), the expected player count, and the
/// <see cref="HostProfile"/>. <b>Outputs.</b> parallel-build on/off + max
/// workers, transport mode + pool workers, and the cores reserved for the
/// client/bot side of a shared machine.</para>
///
/// <para><b>Precedence.</b> The resolver is only the DEFAULT. An explicit
/// <c>broadcast.parallelBuild</c>, <c>broadcast.maxDegreeOfParallelism</c>,
/// <c>transport.mode</c> or <c>transport.poolWorkers</c> still wins, and the
/// environment overrides still beat the config exactly as they did before
/// P889. A per-machine calibration profile (see <see cref="MachineSizingProfile"/>)
/// sits between the resolver and explicit config: it replaces the resolver's
/// recommendation for its machine/build only.</para>
///
/// <para><b>Why these thresholds.</b> They are not guesses: the P889 report
/// records the offscreen skirmish measurements (Small/Medium/Large, serial /
/// P887 / resolver) and the dedicated 20-player case. The per-worker player
/// counts are the points where the pool's fixed cost stops paying for itself:
/// below ~12 players a thread per peer is cheaper than a pool, below ~20
/// players a serial build is cheaper than a parallel one. The shared reserve is
/// the cores the renderer and the bot driver actually used at 60 Hz in those
/// runs. Change them only with a measurement in
/// <c>docs/reports/P889_REPORT.md</c>.</para>
/// </summary>
public sealed record PoolSizing(
    int Cores,
    int Players,
    HostProfile Profile,
    int ReservedCores,
    bool ParallelBuild,
    int BuildWorkers,
    TransportMode TransportMode,
    int TransportWorkers,
    string Reason,
    string Source)
{
    public bool Shared => Profile == HostProfile.Shared;
    /// <summary>Cores the shared profile leaves to the game client, renderer
    /// and bot driver; 0 on a dedicated server.</summary>
    public int ClientReservedCores => ReservedCores;
    public string TransportName => TransportMode == TransportMode.Pool ? "pool" : "thread-per-peer";

    /// <summary>The one boot line: <c>[boot] sizing: profile=… cores=…
    /// players=… → build=…, transport=…</c> (P889).</summary>
    public string BootLine()
    {
        string build = ParallelBuild ? $"parallel ({BuildWorkers} workers)" : "serial (1 worker)";
        string transport = TransportMode == TransportMode.Pool
            ? $"pool ({TransportWorkers} workers)"
            : "thread-per-peer";
        var text = new System.Text.StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"[boot] sizing: profile={ProfileName(Profile)} cores={Cores} players={Players} → build={build}, transport={transport}");
        if (ReservedCores > 0)
            text.Append(CultureInfo.InvariantCulture, $" (reserved {ReservedCores} cores for the client/bot driver)");
        text.Append(" — ").Append(Reason);
        text.Append(" [").Append(Source).Append(']');
        return text.ToString();
    }

    public static string ProfileName(HostProfile profile) =>
        profile == HostProfile.Shared ? "shared" : "dedicated";
}

/// <summary>
/// P889 — the sizing resolver. Pure and deterministic (cores/players/profile
/// in, sizes out) so it is unit-testable without booting a server.
/// </summary>
public static class PoolSizingResolver
{
    // ── measured thresholds (see docs/reports/P889_REPORT.md) ───────────────
    /// <summary>Players per parallel-build worker. Below this ratio a parallel
    /// build costs more in dispatch than it saves in encoding.</summary>
    public const int PlayersPerBuildWorker = 16;
    /// <summary>Players per transport-pool worker. Keeps the rig's large case
    /// at about the core count (400 players → cores on a 24-core box), which
    /// is the P887 pool default.</summary>
    public const int PlayersPerTransportWorker = 4;
    /// <summary>Dedicated: below this player count the serial build is faster
    /// (few peers, tiny per-peer payloads). Measured: a 34-player dedicated
    /// server is level between a 3-worker build and P887's 23 workers, and a
    /// 20-player one has too little per-peer work to spread.</summary>
    public const int DedicatedParallelBuildMinPlayers = 20;
    /// <summary>Shared: measured crossover on the P889 workstation. At a
    /// 15v15 skirmish (34 seats) a serial build ticked better than a 3-worker
    /// one (1.65 vs 1.9 ms mean); at 30v30 (64 seats) parallel was level with
    /// serial and the P887 pools, so the shared rule only turns the build on
    /// from 48 seats, where there is real per-peer work to spread.</summary>
    public const int SharedParallelBuildMinPlayers = 48;
    /// <summary>Dedicated: below this player count thread-per-peer is cheaper
    /// than a pool (no queue hand-off, no worker wake-up).</summary>
    public const int DedicatedPoolMinPlayers = 12;
    /// <summary>Shared: a small skirmish keeps thread-per-peer until this
    /// player count (same measured crossover as the build: Medium favoured
    /// thread-per-peer, Large was level and P887's 24 workers bought nothing).</summary>
    public const int SharedPoolMinPlayers = 48;
    /// <summary>Most players a single socket server is expected to host; keeps
    /// a typo'd expectedPlayers from asking for absurd pools.</summary>
    public const int MaxExpectedPlayers = 4096;

    /// <summary>
    /// P889 — resolve the sizes.
    /// <paramref name="cores"/> is logical cores (container-aware
    /// <see cref="Environment.ProcessorCount"/>); <paramref name="players"/> is
    /// the expected player count (team size × 2 + command/probe seats for a
    /// skirmish, the configured capacity for a dedicated server);
    /// <paramref name="calibration"/> is this machine's measured profile (null
    /// when absent or invalidated).
    /// </summary>
    public static PoolSizing Resolve(int cores, int players, HostProfile profile,
        MachineSizingProfile? calibration = null)
    {
        cores = Math.Max(1, cores);
        players = Math.Clamp(players, 1, MaxExpectedPlayers);

        int reserved = profile == HostProfile.Shared ? SharedReserve(cores) : 0;
        int serverBudget = Math.Max(1, cores - reserved);

        // P889 — a validated calibration entry for this machine/profile/size
        // beats the static rule (and is itself beaten by explicit config/env,
        // applied by the boot resolvers AFTER this call).
        if (calibration?.Match(cores, profile, players) is { } measured)
            return FromCalibration(cores, players, profile, reserved, measured);

        bool parallel = players >= (profile == HostProfile.Shared
            ? SharedParallelBuildMinPlayers : DedicatedParallelBuildMinPlayers);
        int buildWorkers = parallel
            ? Math.Clamp(CeilDiv(players, PlayersPerBuildWorker), 1, Math.Max(1, serverBudget - 1))
            : 1;
        // A single worker is the serial path; never claim "parallel (1 worker)".
        parallel = buildWorkers > 1 && parallel;
        if (!parallel) buildWorkers = 1;

        bool pool = players >= (profile == HostProfile.Shared
            ? SharedPoolMinPlayers : DedicatedPoolMinPlayers);
        int transportWorkers = pool
            ? Math.Clamp(CeilDiv(players, PlayersPerTransportWorker), 1, serverBudget)
            : 0;

        string reason = profile == HostProfile.Shared
            ? $"shared machine: reserved {reserved} of {cores} cores for the game client/bot driver; "
              + $"{players} players need {(parallel ? buildWorkers : 1)} build and "
              + (pool ? $"{transportWorkers} send" : "no pool") + " workers"
            : $"{players} players on a dedicated {cores}-core server: "
              + $"{(parallel ? $"parallel build ({buildWorkers} workers)" : "serial build")}, "
              + (pool ? $"pool transport ({transportWorkers} workers)" : "thread-per-peer transport");

        return new(cores, players, profile, reserved, parallel, buildWorkers,
            pool ? TransportMode.Pool : TransportMode.ThreadPerPeer,
            transportWorkers, reason, "resolver");
    }

    /// <summary>Cores a shared machine keeps for the renderer + bot driver.
    /// Measured on the P889 workstation (24 logical cores): the renderer used
    /// about two cores at 60 Hz and the bot driver's workers about four, so the
    /// reserve scales with the box rather than being a flat number.</summary>
    public static int SharedReserve(int cores) => cores switch
    {
        <= 2 => 1,
        <= 4 => 2,
        <= 8 => 3,
        <= 16 => 4,
        _ => 6,
    };

    private static PoolSizing FromCalibration(int cores, int players, HostProfile profile,
        int reserved, SizingCalibrationEntry measured)
    {
        int serverBudget = Math.Max(1, cores - reserved);
        bool parallel = measured.ParallelBuild && measured.BuildWorkers > 1;
        int buildWorkers = parallel
            ? Math.Clamp(measured.BuildWorkers, 1, Math.Max(1, serverBudget - 1)) : 1;
        bool pool = string.Equals(measured.Transport, "pool", StringComparison.OrdinalIgnoreCase)
            && measured.PoolWorkers > 0;
        int workers = pool ? Math.Clamp(measured.PoolWorkers, 1, serverBudget) : 0;
        string reason = $"calibrated on this machine (score {measured.Score:0.##}): "
            + $"{(parallel ? $"parallel build ({buildWorkers} workers)" : "serial build")}, "
            + (pool ? $"pool transport ({workers} workers)" : "thread-per-peer transport");
        return new(cores, players, profile, reserved, parallel, buildWorkers,
            pool ? TransportMode.Pool : TransportMode.ThreadPerPeer, workers, reason,
            $"calibration {measured.UpdatedUtc:yyyy-MM-dd}");
    }

    /// <summary>The player count a dedicated config expects: the configured
    /// capacity (sum of <c>teams × maxPlayersPerTeam</c>), unless an explicit
    /// <c>expectedPlayers</c> says otherwise. This preserves P887 at the rig's
    /// 200v200 capacity while letting a small server declare 20 players.</summary>
    public static int ExpectedPlayersFromConfig(ServerConfig config)
    {
        if (config.ExpectedPlayers is { } explicitPlayers && explicitPlayers > 0)
            return Math.Clamp(explicitPlayers, 1, MaxExpectedPlayers);
        long capacity = 0;
        foreach (var game in config.Games)
            capacity += (long)Math.Max(0, game.Teams) * Math.Max(0, game.MaxPlayersPerTeam);
        return (int)Math.Clamp(capacity, 1, MaxExpectedPlayers);
    }

    /// <summary>Parse a <c>hostProfile</c> string; throws (like the transport
    /// mode) so a typo cannot silently select the wrong profile.</summary>
    public static HostProfile ParseProfile(string value) => value.Trim().ToLowerInvariant() switch
    {
        "dedicated" or "server" or "default" => HostProfile.Dedicated,
        "shared" or "skirmish" or "training" or "single-player" or "singleplayer" => HostProfile.Shared,
        _ => throw new InvalidDataException(
            $"unknown hostProfile '{value}' (expected 'dedicated' or 'shared'); "
            + "set it in server.json hostProfile or $ALLEGIANCE_HOST_PROFILE"),
    };

    /// <summary>Resolve everything the config/environment says before boot. The
    /// env vars win over the config; the calibration profile (if any) only
    /// replaces the resolver's recommendation.</summary>
    public static PoolSizing ResolveConfig(ServerConfig config, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        int cores = Math.Max(1, Environment.ProcessorCount);

        string? profileName = environment("ALLEGIANCE_HOST_PROFILE");
        if (string.IsNullOrWhiteSpace(profileName)) profileName = config.HostProfile;
        var profile = string.IsNullOrWhiteSpace(profileName)
            ? HostProfile.Dedicated : ParseProfile(profileName);

        int players;
        string? envPlayers = environment("ALLEGIANCE_EXPECTED_PLAYERS");
        if (!string.IsNullOrWhiteSpace(envPlayers))
        {
            if (!int.TryParse(envPlayers, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
                throw new InvalidDataException(
                    $"ALLEGIANCE_EXPECTED_PLAYERS must be a positive integer, got '{envPlayers}'");
            players = Math.Clamp(parsed, 1, MaxExpectedPlayers);
        }
        else players = ExpectedPlayersFromConfig(config);

        var calibration = MachineSizingProfile.LoadForThisMachine(cores);
        return Resolve(cores, players, profile, calibration);
    }

    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;
}

/// <summary>
/// P889 — one measured sizing candidate saved by <c>--calibrate</c>. The
/// profile is per-machine and per-build: <see cref="Cores"/> and
/// <see cref="Version"/> must both match the running game or the entry is
/// ignored (a hardware or game-version change invalidates it), and explicit
/// config/env still wins over it.
/// </summary>
public sealed record SizingCalibrationEntry
{
    public int Players { get; init; }
    public string Profile { get; init; } = "shared";
    public bool ParallelBuild { get; init; }
    public int BuildWorkers { get; init; } = 1;
    public string Transport { get; init; } = "thread-per-peer";
    public int PoolWorkers { get; init; }
    public int BotWorkers { get; init; }
    public int BotCoresPerWorker { get; init; }
    public double Score { get; init; }
    public string Reason { get; init; } = "";
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A per-machine calibration file (<c>sizing-profile.json</c>).</summary>
public sealed record MachineSizingProfile
{
    public int SchemaVersion { get; init; } = 1;
    public int Cores { get; init; }
    public string Version { get; init; } = "";
    public string Machine { get; init; } = Environment.MachineName;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<SizingCalibrationEntry> Entries { get; init; } = [];

    /// <summary>How far apart (as a fraction) a calibrated player count may be
    /// from the boot's expected count and still apply. Sizes are coarse
    /// (5v5/15v15/30v30), so a 25 % band covers the seat arithmetic.</summary>
    public const double PlayersTolerance = 0.25;

    /// <summary>The entry for this exact machine/profile/player size, or null
    /// when the profile was measured on other cores/another build.</summary>
    public SizingCalibrationEntry? Match(int cores, HostProfile profile, int players)
    {
        if (Cores != cores || !Valid) return null;
        string name = PoolSizing.ProfileName(profile);
        SizingCalibrationEntry? best = null;
        double bestDelta = double.MaxValue;
        foreach (var entry in Entries)
        {
            if (!string.Equals(entry.Profile, name, StringComparison.OrdinalIgnoreCase)) continue;
            double delta = Math.Abs(entry.Players - players) / (double)Math.Max(1, players);
            if (delta <= PlayersTolerance && delta < bestDelta) { best = entry; bestDelta = delta; }
        }
        return best;
    }

    public bool Valid => Version == PoolSizingProfileStore.CurrentVersion;

    public static string DefaultPath => Environment.GetEnvironmentVariable("ALLEGIANCE_SIZING_PROFILE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AllegianceCore", "sizing-profile.json");

    /// <summary>Load this machine's profile; a missing, corrupt, other-core or
    /// other-build file is simply ignored (the resolver then applies its
    /// rules), never an error that could block a boot.</summary>
    public static MachineSizingProfile? LoadForThisMachine(int cores, string? path = null)
    {
        try
        {
            var profile = PoolSizingProfileStore.Load(path);
            return profile is not null && profile.Cores == cores && profile.Valid ? profile : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Merge one measured entry into this machine's profile and save
    /// it (a re-runnable calibration replaces the entry for the same size).</summary>
    public MachineSizingProfile With(SizingCalibrationEntry entry, int cores, string? path = null)
    {
        var entries = Entries.Where(e =>
            !(string.Equals(e.Profile, entry.Profile, StringComparison.OrdinalIgnoreCase)
              && Math.Abs(e.Players - entry.Players) <= Math.Max(1, entry.Players / 4))).ToList();
        entries.Add(entry);
        entries.Sort((a, b) => a.Profile == b.Profile ? a.Players.CompareTo(b.Players)
            : string.CompareOrdinal(a.Profile, b.Profile));
        var merged = this with
        {
            SchemaVersion = 1, Cores = cores, Version = PoolSizingProfileStore.CurrentVersion,
            Machine = Environment.MachineName, UpdatedUtc = DateTimeOffset.UtcNow, Entries = entries,
        };
        PoolSizingProfileStore.Save(merged, path);
        return merged;
    }
}

/// <summary>
/// P889 — JSON I/O for the per-machine sizing profile. Written with
/// <see cref="Utf8JsonWriter"/>/<see cref="JsonDocument"/> so it needs no
/// reflection metadata and stays Native-AOT clean on the server side.
/// </summary>
public static class PoolSizingProfileStore
{
    /// <summary>The game version stamp the profile is tied to. Uses the
    /// Allegiance.Core assembly (shared by client and server): its
    /// informational version plus the build's file timestamp. A rebuild or a
    /// version bump invalidates older calibrations instead of silently keeping
    /// stale sizes.</summary>
    public static string CurrentVersion { get; } = ComputeVersion();

    private static string ComputeVersion()
    {
        try
        {
            // The SDK stamps the git commit into AssemblyInformationalVersion
            // (seen as "1.0.0+<hash>" in this build), so a rebuilt game or a
            // new commit invalidates an older calibration. No reflection-based
            // serialization and no Assembly.Location (AOT single-file safe).
            var assembly = typeof(Allegiance.Core.ExternalPaths).Assembly;
            string version = assembly.GetName().Version?.ToString() ?? "0";
            string? informational = assembly.GetCustomAttributesData()
                .FirstOrDefault(a => a.AttributeType == typeof(System.Reflection.AssemblyInformationalVersionAttribute))
                ?.ConstructorArguments[0].Value?.ToString();
            return string.IsNullOrWhiteSpace(informational) ? version : informational;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "0";
        }
    }

    public static MachineSizingProfile? Load(string? path = null)
    {
        path ??= MachineSizingProfile.DefaultPath;
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var entries = new List<SizingCalibrationEntry>();
        if (root.TryGetProperty("entries", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object)
                    entries.Add(new()
                    {
                        Players = Int(item, "players"),
                        Profile = String(item, "profile", "shared"),
                        ParallelBuild = Bool(item, "parallelBuild"),
                        BuildWorkers = Int(item, "buildWorkers", 1),
                        Transport = String(item, "transport", "thread-per-peer"),
                        PoolWorkers = Int(item, "poolWorkers"),
                        BotWorkers = Int(item, "botWorkers"),
                        BotCoresPerWorker = Int(item, "botCoresPerWorker"),
                        Score = Double(item, "score"),
                        Reason = String(item, "reason", ""),
                        UpdatedUtc = Time(item, "updatedUtc"),
                    });
        return new()
        {
            SchemaVersion = Int(root, "schemaVersion", 1),
            Cores = Int(root, "cores"),
            Version = String(root, "version", ""),
            Machine = String(root, "machine", ""),
            UpdatedUtc = Time(root, "updatedUtc"),
            Entries = entries,
        };
    }

    public static void Save(MachineSizingProfile profile, string? path = null)
    {
        path ??= MachineSizingProfile.DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var stream = File.Create(path + ".tmp"))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", profile.SchemaVersion);
            writer.WriteNumber("cores", profile.Cores);
            writer.WriteString("version", profile.Version);
            writer.WriteString("machine", profile.Machine);
            writer.WriteString("updatedUtc", profile.UpdatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteStartArray("entries");
            foreach (var entry in profile.Entries)
            {
                writer.WriteStartObject();
                writer.WriteNumber("players", entry.Players);
                writer.WriteString("profile", entry.Profile);
                writer.WriteBoolean("parallelBuild", entry.ParallelBuild);
                writer.WriteNumber("buildWorkers", entry.BuildWorkers);
                writer.WriteString("transport", entry.Transport);
                writer.WriteNumber("poolWorkers", entry.PoolWorkers);
                writer.WriteNumber("botWorkers", entry.BotWorkers);
                writer.WriteNumber("botCoresPerWorker", entry.BotCoresPerWorker);
                writer.WriteNumber("score", entry.Score);
                writer.WriteString("reason", entry.Reason);
                writer.WriteString("updatedUtc", entry.UpdatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        File.Move(path + ".tmp", path, true);
    }

    private static int Int(JsonElement element, string name, int fallback = 0) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;
    private static double Double(JsonElement element, string name, double fallback = 0) =>
        element.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed) && double.IsFinite(parsed) ? parsed : fallback;
    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string String(JsonElement element, string name, string fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;
    private static DateTimeOffset Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.TryGetDateTimeOffset(out var parsed) ? parsed : DateTimeOffset.UtcNow;
}
