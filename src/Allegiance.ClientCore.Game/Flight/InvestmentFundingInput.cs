using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>Native team gifts and standing donations, WinTrek/teampane.cpp:1141-1241.
/// Uses authoritative wallets; the server acknowledges all money changes.</summary>
internal sealed class InvestmentFundingInput
{
    private readonly ActionPressEdges _edges = new();
    private short? _recipient;
    internal readonly record struct Result(PlayerDonate? Gift = null, AutoDonate? Auto = null);
    private static readonly string[] Actions = ["InvestNextPilot", "InvestDonate", "InvestDonateLarge", "InvestAutoDonate", "InvestToggleDonate", "InvestKeepIncome"];
    internal short? Recipient => _recipient;

    internal Result Step(KeyboardState keys, ActionMap map, ClientWorld world, bool active)
    {
        bool Edge(string action) => _edges.Observe(action, map.IsDownMostSpecific(keys, action), !active);
        bool next = Edge(Actions[0]), small = Edge(Actions[1]), large = Edge(Actions[2]),
            auto = Edge(Actions[3]), toggle = Edge(Actions[4]), keep = Edge(Actions[5]);
        var pilots = world.Ships.Values.Where(s => s.SideId == world.MySideId && s.ShipId != world.MyShipId
            && !world.ResearchProduction.ContainsKey(s.ShipId) && world.ScoreRoster.Any(p => p.CharacterName == s.CharacterName))
            .OrderBy(s => s.ShipId).ToArray();
        int index = Array.FindIndex(pilots, s => s.ShipId == _recipient);
        if (next) index = (index + 1) % Math.Max(1, pilots.Length);
        _recipient = pilots.Length == 0 ? null : pilots[Math.Max(0, index)].ShipId;
        if (!active || world.MySideId is null) return default;
        if (keep) return new(Auto: new AutoDonate { SidDonateTo = -1, Amount = 0 });
        if (_recipient is not { } id) return default;
        int money = Math.Max(0, world.MyMoney ?? 0);
        if ((small || large) && money > 0)
            return new(new PlayerDonate { ShipID = id, MoneyGiven = Math.Min(money, large ? 500 : 100) });
        if (toggle && world.IsTeamLeader) return new(Auto: new AutoDonate { SidDonateTo = id, Amount = -1 });
        if (auto) return new(Auto: new AutoDonate { SidDonateTo = id, Amount = money });
        return default;
    }

    internal string Status(ClientWorld world)
    {
        if (_recipient is not { } id || !world.Ships.TryGetValue(id, out var ship)) return "Pilot funding: waiting for team roster";
        var donor = world.AutoDonateTargetByShipId.TryGetValue(id, out var target) && world.Ships.TryGetValue(target, out var to)
            ? to.CharacterName : "self";
        return ShopLayout.AsciiSafe($"Pilot: {ship.CharacterName} | income to: {donor} | TAB next  D give $100  E give $500  A auto  C keep income  X toggle (leader)");
    }
}
