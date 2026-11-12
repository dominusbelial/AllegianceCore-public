using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.Core.Strategy;

internal sealed record CoreProfileIdentity(int Version, DataConstantsIGC Constants,
    DataCivilizationIGC[] Civs, DataHullTypeIGC[] Hulls, IdentityPart[] Parts,
    DataLauncherTypeIGC[] Launchers, DataStationTypeIGC[] Stations, DataDevelopmentIGC[] Developments,
    DataDroneTypeIGC[] Drones, DataProjectileTypeIGC[] Projectiles, DataMissileTypeIGC[] Missiles,
    DataMineTypeIGC[] Mines, DataProbeTypeIGC[] Probes, DataChaffTypeIGC[] Chaff, DataTreasureSetIGC[] Treasures);
internal sealed record IdentityPart(DataPartTypeIGC Part, JsonElement Tail);
internal sealed record PortfolioInput(string CoreIdentity, int CoreVersion, short Civ, ProfileSettings Settings,
    long Seed, PortfolioSettings Options, ImmutableArray<short> Opponents);

[JsonSerializable(typeof(CoreProfileIdentity))]
[JsonSerializable(typeof(PortfolioInput))]
[JsonSerializable(typeof(CivProfile))]
[JsonSerializable(typeof(PortfolioChoice))]
[JsonSerializable(typeof(DataPartTypeIGC.Weapon))]
[JsonSerializable(typeof(DataPartTypeIGC.Shield))]
[JsonSerializable(typeof(DataPartTypeIGC.Cloak))]
[JsonSerializable(typeof(DataPartTypeIGC.Afterburner))]
[JsonSerializable(typeof(DataPartTypeIGC.Pack))]
[JsonSerializable(typeof(DataPartTypeIGC.MissileLauncher))]
[JsonSerializable(typeof(DataPartTypeIGC.MineLauncher))]
[JsonSerializable(typeof(DataPartTypeIGC.ProbeLauncher))]
[JsonSerializable(typeof(DataPartTypeIGC.ChaffLauncher))]
internal sealed partial class StrategyJsonContext : JsonSerializerContext;
