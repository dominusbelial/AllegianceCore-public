using System.Globalization;
using Allegiance.Core.Model;

namespace Allegiance.Server;

/// <summary>Showcase/skirmish preset policy, not a native rule. Values are native
/// MissionParams (Igc/igc.h:1158-1179); result columns: FedSrv/fsmission.cpp:2777-2789.
/// Selected before map/world assembly and advertised through ordinary MISSIONDEF.</summary>
public sealed class GoalRotationConfig
{
    public string Mode { get; set; } = "cycle";
    public GoalRotationEntry[] Entries { get; set; } =
    [
        new() { Name = "conquest", Goals = MissionGoals.NativeDefaults() },
        new() { Name = "flags", Goals = new() { Flags = 1 } },
        new() { Name = "artifacts", Goals = new() { Artifacts = 1 } },
        new() { Name = "kills", Goals = MissionGoals.Preset("kills") },
        new() { Name = "prosperity", Goals = MissionGoals.Preset("prosperity") },
        new() { Name = "territory", Goals = MissionGoals.Preset("territory") },
        new() { Name = "countdown", Goals = MissionGoals.Preset("countdown") },
        new() { Name = "flags-artifacts", Goals = new() { Flags = 1, Artifacts = 1 } },
    ];

    internal void Validate(GameConfig game)
    {
        if (Mode is not ("cycle" or "random"))
            throw new InvalidDataException("showcase.goalRotation.mode must be cycle|random");
        if (Entries is null || Entries.Length < 2)
            throw new InvalidDataException("showcase.goalRotation.entries requires at least two entries");
        if (!game.ResolveGoals().IsNoWinningConditionsGame)
            throw new InvalidDataException("showcase.goalRotation conflicts with fixed goals/gameLengthSeconds");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var goals = new HashSet<MissionGoals>();
        foreach (var entry in Entries)
        {
            if (entry is null || string.IsNullOrEmpty(entry.Name) || entry.Name.Length > 64
                || entry.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || !names.Add(entry.Name))
                throw new InvalidDataException("showcase.goalRotation entry names must be unique ASCII letters/digits/hyphens (1..64)");
            if (entry.Goals is null)
                throw new InvalidDataException($"showcase.goalRotation '{entry.Name}': goals must be an object");
            var resolved = game.ValidateGoals(entry.Goals with { });
            if (resolved.IsNoWinningConditionsGame || !goals.Add(resolved))
                throw new InvalidDataException("showcase.goalRotation entries require distinct, enabled goals");
        }
    }

    // The match seed uses an independent RNG so civ selection/simulation consume
    // exactly the same random draws as before. Random excludes the previous entry.
    internal int Select(int match, int seed, int previousIndex = -1)
    {
        if (Mode == "cycle") return match % Entries.Length;
        int index = new Random(seed).Next(Entries.Length - (previousIndex >= 0 ? 1 : 0));
        return previousIndex >= 0 && index >= previousIndex ? index + 1 : index;
    }

    internal static string Describe(MissionGoals goals) => string.Join(",", new[]
    {
        goals.IsConquestGame ? $"conquest={goals.ConquestPercentage}" : null,
        goals.IsTerritoryGame ? $"territory={goals.TerritoryPercentage}" : null,
        goals.IsDeathMatchGame ? $"kills={goals.TeamKills}" : null,
        goals.IsFlagsGame ? $"flags={goals.Flags}" : null,
        goals.IsArtifactsGame ? $"artifacts={goals.Artifacts}" : null,
        goals.IsProsperityGame ? "prosperity=" + goals.TeamMoney.ToString("R", CultureInfo.InvariantCulture) : null,
        goals.IsCountdownGame ? "countdown=" + goals.GameLengthSeconds.ToString("R", CultureInfo.InvariantCulture) : null,
    }.OfType<string>());
}

public sealed class GoalRotationEntry
{
    public string Name { get; set; } = "";
    public MissionGoals Goals { get; set; } = new();
}
