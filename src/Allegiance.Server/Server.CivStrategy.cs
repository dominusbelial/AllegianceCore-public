using System.Runtime.CompilerServices;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Strategy;

namespace Allegiance.Server;

public sealed partial class Server
{
    private sealed class StrategyReceipts
    {
        internal double NextFlush;
        internal int Seed;
        internal string Map = "";
        internal int? MapSeed;
        internal bool Swapped;
        internal bool ChoicesWritten;
        internal readonly Dictionary<short, PortfolioChoice> Choices = new();
        internal int Sequence;
        internal readonly Dictionary<(short Side, string Kind, short Id, short Hull), (long Count, double Amount, double First, double Last)> Events = new();
        internal readonly Dictionary<(short Side, short VictimSide, short Hull, HitKind Kind, short Source, byte Damage), (long Count, double Raw, double Removed)> Damage = new();
        internal readonly Dictionary<short, (long Kills, long Losses)> Stations = new();
        internal readonly Dictionary<short, (long Paydays, double Income, double Ore, double OreCredits)> Economy = new();
        internal long OverflowEvents;
        internal double OverflowAmount;
    }
    private sealed class InventoryReceipt
    {
        internal short Hull = -1;
        internal Dictionary<short, int> Parts = new();
    }
    private readonly ConditionalWeakTable<Ship, InventoryReceipt> _strategyInventories = new();
    private readonly ConditionalWeakTable<Mission, StrategyReceipts> _strategyReceipts = new();
    private void RecordStrategyInventory(Ship ship)
    {
        if (_config?.Bots.CivStrategy != true) return;
        var prior = _strategyInventories.GetValue(ship, _ => new());
        if (prior.Hull != ship.HullType.HullId)
        { StrategyEvent(ship, "hull", ship.HullType.HullId); prior.Hull = ship.HullType.HullId; }
        var parts = ship.Parts.Concat(ship.CargoHold.OfType<MountedPart>()).GroupBy(p => p.Type.PartId)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.EquipmentType is EquipmentType.Magazine or EquipmentType.Dispenser or EquipmentType.ChaffLauncher or EquipmentType.Pack
                ? Math.Max(0, (int)p.Amount) : 1));
        foreach (var (id, count) in parts)
        {
            int increase = count - prior.Parts.GetValueOrDefault(id);
            if (increase > 0) StrategyEvent(ship, "part", id, increase);
        }
        prior.Parts = parts;
    }
    private static void StrategyLog<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        => Console.WriteLine("[civ-strategy] " + JsonSerializer.Serialize(value, type));

    private void InitializeCivStrategy(Mission mission, int seed, string map, int? mapSeed, bool swapped)
    {
        if (_config?.Bots.CivStrategy != true) return;
        var receipts = _strategyReceipts.GetValue(mission, _ => new());
        receipts.NextFlush = mission.LastUpdate + 60;
        receipts.Seed = seed; receipts.Map = map; receipts.MapSeed = mapSeed; receipts.Swapped = swapped;
        InstallCivStrategyPlans(mission);
    }
    private void InstallCivStrategyPlans(Mission mission)
    {
        if (_config?.Bots.CivStrategy != true || !_strategyReceipts.TryGetValue(mission, out var receipts)) return;
        receipts.Choices.Clear();
        var profiles = mission.Sides.ToDictionary(s => s.Id, s => CivProfile.Get(mission.Core, s.Civilization.CivilizationId, mission.Research));
        foreach (var side in mission.Sides)
        {
            var choice = CivPortfolio.Choose(mission.Core, profiles[side.Id], profiles.Where(p => p.Key != side.Id).Select(p => p.Value), receipts.Seed,
                new PortfolioSettings(ResearchTimeScale: _config.Showcase.TechMode == "research" ? _config.Showcase.ResearchTimeScale : 1,
                    Goals: GoalRotationConfig.Describe(mission.Goals), Map: receipts.Map, MapSeed: receipts.MapSeed));
            receipts.Choices[side.Id] = choice;
            // Also supply an execution marker to unlocked showcases: the fleet
            // shares the same policy and uses its live shop, without inventing tech.
            mission.ResearchPlans[side.Id] = CivExecutionPlan.Build(new ResearchTree(mission.Core,
                profiles[side.Id].Civ, mission.Research), choice, Path.GetFileNameWithoutExtension(_config.StaticCore),
                _config.Bots.CivStrategyCoverageShare);
        }
    }
    private void WriteCivStrategyChoices(Mission mission, StrategyReceipts receipts)
    {
        if (receipts.ChoicesWritten) return;
        receipts.ChoicesWritten = true;
        int seed = receipts.Seed; string map = receipts.Map; bool swapped = receipts.Swapped;
        var profiles = mission.Sides.ToDictionary(s => s.Id, s => CivProfile.Get(mission.Core, s.Civilization.CivilizationId, mission.Research));
        foreach (var side in mission.Sides)
        {
            var profile = profiles[side.Id];
            var choice = receipts.Choices[side.Id];
            StrategyLog(new StrategyChoiceReceipt("choice", mission.Skills?.Match ?? 0, mission.Cookie, seed, side.Id,
                profile.Civ, profile.CoreIdentity, profile.CoreVersion, map,
                swapped ? mission.Sides.Count - 1 - side.Id : side.Id,
                mission.Sectors.SelectMany(s => s.StationRuntimes).Where(s => s.SideId == side.Id).Select(s => s.ClusterId).Distinct().Order().ToArray(),
                profile.Settings, choice.InputHash, choice.Opponents.ToArray(), choice.Assumptions,
                ChoiceSummary(choice.Primary), ChoiceSummary(choice.Counter),
                choice.Alternatives.Select(ChoiceSummary).ToArray(), choice.CounterAlternatives.Select(ChoiceSummary).ToArray(), choice.Rejections.ToArray()), StrategyReceiptJsonContext.Default.StrategyChoiceReceipt);
        }
    }
    private static StrategyChoiceSummary? ChoiceSummary(PortfolioCandidate? c) => c is null ? null : new(
        c.Route.Target, c.Score, c.Route.Funding, c.Route.CriticalSeconds, c.Route.SerialSeconds,
        c.Route.Steps.Length, c.Route.Steps.Take(40).ToArray(), [c.ShipGain, c.BaseGain, c.EconomyGain, c.SupportGain], c.Spikes.Select(s => new StrategySpikeReceipt(s.Duty, s.Hull, s.Station, s.Parts.ToArray(), s.Ships, s.Crew, s.Price, s.TravelSeconds, s.Power, s.OreCycleSeconds)).ToArray());
    private void StrategyEvent(Ship ship, string kind, short id, double amount = 1)
    {
        if (_config?.Bots.CivStrategy != true || ship.Mission is not { } mission || ship.SideId is not { } side) return;
        var r = _strategyReceipts.GetValue(mission, _ => new() { NextFlush = _simNow + 60 });
        var key = (side, kind, id, ship.HullType.HullId);
        if (!r.Events.ContainsKey(key) && r.Events.Count >= 256) { r.OverflowEvents++; r.OverflowAmount += amount; return; }
        var p = r.Events.GetValueOrDefault(key);
        r.Events[key] = (p.Count + 1, p.Amount + amount, p.Count == 0 ? _simNow : p.First, _simNow);
    }
    private void RecordStrategyDamage(Ship victim, Model? attacker, HitSource hit, byte type, float raw, double removed)
    {
        if (victim.Mission is not { } mission) return;
        var r = _strategyReceipts.GetValue(mission, _ => new() { NextFlush = _simNow + 60 });
        short side = attacker is null ? (short)-1 : EquipmentTarget.SideOf(attacker) ?? (short)-1;
        short source = hit.Kind == HitKind.None || (hit.Kind == HitKind.Blast && hit.WeaponKind == HitKind.None) ? (short)-1 : hit.SourceId;
        var key = (side, victim.SideId ?? (short)-1, victim.HullType.HullId, hit.WeaponKind == HitKind.None ? hit.Kind : hit.WeaponKind, source, type);
        if (!r.Damage.ContainsKey(key) && r.Damage.Count >= 256) { r.OverflowEvents++; r.OverflowAmount += removed; return; }
        var p = r.Damage.GetValueOrDefault(key);
        r.Damage[key] = (p.Count + 1, p.Raw + raw, p.Removed + removed);
    }
    private void RecordStrategyOre(Ship ship, double ore, double proceeds)
    {
        if (ship.Mission is not { } mission || ship.SideId is not { } side) return;
        var r = _strategyReceipts.GetValue(mission, _ => new());
        var p = r.Economy.GetValueOrDefault(side);
        r.Economy[side] = (p.Paydays, p.Income, p.Ore + ore, p.OreCredits + proceeds);
    }
    private void RecordStrategyPayday(Side side, long income)
    {
        if (_config?.Bots.CivStrategy != true || side.Mission is not { } mission) return;
        var r = _strategyReceipts.GetValue(mission, _ => new());
        var p = r.Economy.GetValueOrDefault(side.Id);
        r.Economy[side.Id] = (p.Paydays + 1, p.Income + income, p.Ore, p.OreCredits);
    }
    private void RecordStrategyStation(Station station, Model? launcher)
    {
        if (_config?.Bots.CivStrategy != true || station.Mission is not { } mission) return;
        var r = _strategyReceipts.GetValue(mission, _ => new());
        var loss = r.Stations.GetValueOrDefault(station.SideId);
        r.Stations[station.SideId] = (loss.Kills, loss.Losses + 1);
        if (launcher is not null && EquipmentTarget.SideOf(launcher) is { } killer)
        { var p = r.Stations.GetValueOrDefault(killer); r.Stations[killer] = (p.Kills + 1, p.Losses); }
    }
    private void FlushCivStrategy(Mission mission, double now, bool final = false)
    {
        if (_config?.Bots.CivStrategy != true || !_strategyReceipts.TryGetValue(mission, out var r)) return;
        if (mission.Stage == MissionStage.Started || final) WriteCivStrategyChoices(mission, r);
        if (!final && now < r.NextFlush) return;
        r.NextFlush = now + 60;
        long match;
        lock (_matchups) match = _matchups.TryGetValue(mission, out var m) ? m.Number : 0;
        // At most one bounded JSON window per simulation minute and one final flush.
        StrategyLog(new StrategyWindowReceipt("window", match, mission.Cookie, now, ++r.Sequence, final,
            r.Events.Where(p => p.Key.Kind is "hull" or "part").OrderBy(p => p.Key).Select(p => new StrategyGrantReceipt(p.Key.Side, p.Key.Kind, p.Key.Id,
                p.Key.Hull, p.Value.Count, p.Value.Amount, p.Value.First, p.Value.Last)).ToArray(),
            r.Damage.OrderBy(p => p.Key).Select(p => new StrategyDamageReceipt(p.Key.Side, p.Key.VictimSide, p.Key.Hull,
                p.Key.Kind.ToString(), p.Key.Source, p.Key.Damage, p.Value.Count, p.Value.Raw, p.Value.Removed)).ToArray(),
            mission.Sides.Select(side =>
            {
                var e = r.Economy.GetValueOrDefault(side.Id); var s = r.Stations.GetValueOrDefault(side.Id);
                var miners = mission.GetShips().Distinct()
                    .Where(ship => ship.SideId == side.Id && ship.IsNpc && ship.PilotType == Ship.PilotTypeMiner && !ship.Dead).ToArray();
                return new StrategySideReceipt(side.Id, side.Civilization.CivilizationId, miners.Length,
                    miners.Sum(ship => ship.Ore), e.Paydays, e.Income, e.Ore, e.OreCredits, s.Kills, s.Losses);
            }).ToArray(), r.OverflowEvents, r.OverflowAmount), StrategyReceiptJsonContext.Default.StrategyWindowReceipt);
        WriteCivCoverage(mission, r, now, final);
        r.Events.Clear(); r.Damage.Clear(); r.Economy.Clear(); r.Stations.Clear(); r.OverflowEvents = 0; r.OverflowAmount = 0;
    }
}
