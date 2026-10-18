using System.Globalization;

namespace Allegiance.Tools.McpSpawner;

/// <summary>Skirmish policy knobs; absent options retain the existing bot behavior.</summary>
internal sealed record BotDifficulty(float Accuracy, double ReactionSeconds, float Aggression)
{
    internal static BotDifficulty FromEnvironment() => new(
        Read("ALLEGIANCE_BOT_ACCURACY", 1, .1f, 3),
        Read("ALLEGIANCE_BOT_REACTION", 0, 0, 3),
        Read("ALLEGIANCE_BOT_AGGRESSION", 1, .1f, 2));

    private static float Read(string name, float fallback, float min, float max)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (text is null) return fallback;
        if (!float.TryParse(text, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value) || value < min || value > max)
            throw new ArgumentException($"{name} must be between {min} and {max}");
        return value;
    }
}
