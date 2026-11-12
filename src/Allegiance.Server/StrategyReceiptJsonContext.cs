using System.Text.Json.Serialization;
using Allegiance.Core.Strategy;

namespace Allegiance.Server;

internal sealed record StrategyChoiceReceipt(string Kind, long Match, uint Cookie, int Seed, short Side, short Civ,
    string Core, int Version, string Map, int HomeSlot, short[] HomeSectors, ProfileSettings Settings,
    string InputHash, short[] Opponents, PortfolioSettings Assumptions, StrategyChoiceSummary? Primary,
    StrategyChoiceSummary? Counter, StrategyChoiceSummary?[] Alternatives, StrategyChoiceSummary?[] CounterAlternatives, string[] Rejections);
internal sealed record StrategyChoiceSummary(StrategyNode Target, double Score, long Funding, double CriticalSeconds,
    double SerialSeconds, int RouteCount, StrategyNode[] Route, double[] Gains, StrategySpikeReceipt[] Spikes);
internal sealed record StrategySpikeReceipt(string Duty, short Hull, short Station, FittedPart[] Parts, int Ships,
    int Crew, double Price, double TravelSeconds, double Power, double OreCycleSeconds);
internal sealed record StrategyResearchReceipt(string Kind, long Match, uint Cookie, short Side, short Civ, short Tech, double Sim);
internal sealed record StrategyGrantReceipt(short Side, string Kind, short Id, short Hull, long Count, double Amount, double First, double Last);
internal sealed record StrategyDamageReceipt(short Side, short VictimSide, short VictimHull, string SourceKind, short Source,
    byte DamageType, long Hits, double Raw, double Removed);
internal sealed record StrategySideReceipt(short Side, short Civ, int Miners, float OreAboard, long Paydays,
    double Income, double Ore, double OreCredits, long StationKills, long StationLosses);
internal sealed record StrategyWindowReceipt(string Kind, long Match, uint Cookie, double Sim, int Sequence, bool Final,
    StrategyGrantReceipt[] Grants, StrategyDamageReceipt[] Damage, StrategySideReceipt[] Sides, long OverflowEvents, double OverflowAmount);

internal sealed record StrategyCoverageEvent(short Side, string Stage, string Mechanism, short Id, long Count);
internal sealed record StrategyCoverageReachable(short Side, short[] Hulls, short[] Parts, string[] Mechanisms);
internal sealed record StrategyCoverageWindow(string Kind, long Match, uint Cookie, double Sim, int Sequence, bool Final,
    double Share, StrategyCoverageEvent[] Events, StrategyCoverageReachable[] Reachable, long OverflowEvents, double OverflowAmount);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StrategyChoiceReceipt))]
[JsonSerializable(typeof(StrategyResearchReceipt))]
[JsonSerializable(typeof(StrategyWindowReceipt))]
[JsonSerializable(typeof(StrategyCoverageWindow))]
internal sealed partial class StrategyReceiptJsonContext : JsonSerializerContext;
