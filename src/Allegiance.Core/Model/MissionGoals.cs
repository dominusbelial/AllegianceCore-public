namespace Allegiance.Core.Model;

/// <summary>Native MissionParams goal fields (Igc/igc.h:1158-1179).
/// Zero disables each goal except territory, whose disabled sentinel is 100
/// (Igc/igc.h:1511-1554). The constructor preserves dedicated-server defaults;
/// NativeDefaults reproduces MissionParams::Reset (Igc/igc.h:1286-1311).</summary>
public sealed record MissionGoals
{
    public short ConquestPercentage { get; set; }
    public short TerritoryPercentage { get; set; } = 100;
    public short TeamKills { get; set; }
    public short Flags { get; set; }
    public short Artifacts { get; set; }
    public float TeamMoney { get; set; }
    public float GameLengthSeconds { get; set; }

    public bool IsConquestGame => ConquestPercentage != 0;
    public bool IsTerritoryGame => TerritoryPercentage != 100;
    public bool IsDeathMatchGame => TeamKills != 0;
    public bool IsFlagsGame => Flags != 0;
    public bool IsArtifactsGame => Artifacts != 0;
    public bool IsProsperityGame => TeamMoney != 0;
    public bool IsCountdownGame => GameLengthSeconds != 0;
    public bool IsNoWinningConditionsGame => !(IsConquestGame || IsTerritoryGame ||
        IsDeathMatchGame || IsFlagsGame || IsArtifactsGame || IsProsperityGame || IsCountdownGame);

    public static MissionGoals NativeDefaults() => new() { ConquestPercentage = 100 };

    /// <summary>Goal portion of WinTrek/gametypes.cpp:114-144 presets.
    /// Prosperity (multiplier 1) and countdown (30 minutes) are explicit port
    /// convenience presets; native has fields, but no presets for these two.</summary>
    public static MissionGoals Preset(string name) => name.ToLowerInvariant() switch
    {
        "none" => new(),
        "conquest" => NativeDefaults(),
        "territory" => new() { TerritoryPercentage = 70 },
        "kills" => new() { TeamKills = 20 },
        "flags" => new() { Flags = 10 },
        "artifacts" => new() { Artifacts = 10 },
        "prosperity" => new() { TeamMoney = 1 },
        "countdown" => new() { GameLengthSeconds = 1800 },
        _ => throw new InvalidDataException("goal must be none|conquest|territory|kills|flags|artifacts|prosperity|countdown")
    };

    public void Validate(string path = "goals")
    {
        // Native validates flags/artifacts 0..100 and money 0..9
        // (Igc/igc.h:1408-1422). Reject invalid percentages/counts and nonfinite
        // clocks too, instead of silently enabling an impossible rule.
        if (ConquestPercentage is < 0 or > 100)
            throw new InvalidDataException($"{path}.conquestPercentage must be between 0 and 100 (0 disables)");
        if (TerritoryPercentage is < 0 or > 100)
            throw new InvalidDataException($"{path}.territoryPercentage must be between 0 and 100 (100 disables)");
        if (TeamKills < 0)
            throw new InvalidDataException($"{path}.teamKills must be between 0 and 32767 (0 disables)");
        if (Flags is < 0 or > 100)
            throw new InvalidDataException($"{path}.flags must be between 0 and 100 (0 disables)");
        if (Artifacts is < 0 or > 100)
            throw new InvalidDataException($"{path}.artifacts must be between 0 and 100 (0 disables)");
        if (!float.IsFinite(TeamMoney) || TeamMoney is < 0 or > 9)
            throw new InvalidDataException($"{path}.teamMoney must be finite and between 0 and 9 (0 disables)");
        if (!float.IsFinite(GameLengthSeconds) || GameLengthSeconds < 0)
            throw new InvalidDataException($"{path}.gameLengthSeconds must be finite and nonnegative (0 disables)");
    }
}
