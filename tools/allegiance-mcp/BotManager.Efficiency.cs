using Allegiance.ClientCore.State;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private readonly Dictionary<short, SharedBotWorlds> _sideWorlds = new();
    private readonly bool _shareWorlds = Environment.GetEnvironmentVariable("ALLEGIANCE_BOT_SHARED_WORLD") != "off";
    private readonly bool _lodEnabled = Environment.GetEnvironmentVariable("ALLEGIANCE_BOT_LOD") == "on";
    private readonly double _distantInterval = BotLodPolicy.ReadInterval(Environment.GetEnvironmentVariable("ALLEGIANCE_BOT_DISTANT_HZ"));
    private int _focusSector = -1;
    private long _focusReceived;
    private SharedBotWorlds? BotWorlds(short side)
    {
        if (!_shareWorlds) return null;
        lock (_sideWorlds)
        {
            if (!_sideWorlds.TryGetValue(side, out var worlds)) _sideWorlds[side] = worlds = new(side);
            return worlds;
        }
    }
    internal void SetFocusSector(short? sector)
    {
        Volatile.Write(ref _focusSector, sector ?? -1);
        Interlocked.Exchange(ref _focusReceived, Environment.TickCount64);
    }
    private bool[] DecisionDue(ActiveBot[] bots, double now)
    {
        if (!_lodEnabled) return Enumerable.Repeat(true, bots.Length).ToArray();
        var humanSectors = new HashSet<short>();
        int focus = Volatile.Read(ref _focusSector);
        bool local = Environment.GetEnvironmentVariable("ALLEGIANCE_LOCAL_PILOT") is not null;
        bool focusFresh = Environment.TickCount64 - Interlocked.Read(ref _focusReceived) < 6000;
        if (focus >= 0 && focusFresh) humanSectors.Add((short)focus);
        // Scan each side table once. Human identities arrive through ordinary
        // PLAYERINFO; managed pilots and research controllers are excluded.
        foreach (var group in bots.Where(b => b.Cs is not null).GroupBy(b => b.Side))
        {
            var cs = group.First().Cs!;
            lock (cs.Gate)
                foreach (var ship in cs.World.Ships.Values)
                    if (ship.Player is { PilotType: Allegiance.Core.Wire.PlayerInfo.PilotTypePlayer } && ship.SideId >= 0 && !ship.IsDead
                        && BotLodPolicy.IsHumanName(ship.CharacterName))
                        humanSectors.Add(ship.SectorId);
        }
        var due = new bool[bots.Length];
        for (int i = 0; i < bots.Length; i++)
        {
            var bot = bots[i];
            short sector = bot.Cs is { } cs ? CsSector(cs) : (short)-1;
            // Stale/missing local camera information conservatively retains full rate.
            bool near = humanSectors.Contains(sector) || (local && !focusFresh);
            if (BotLodPolicy.Due(_lodEnabled, near, now, bot.LastAiDecision, _distantInterval))
            { due[i] = true; bot.LastAiDecision = now; }
            else if (bot.TargetId is { } target)
                _tickEngagers[target] = _tickEngagers.GetValueOrDefault(target) + 1;
        }
        return due;
    }
}

internal static class BotLodPolicy
{
    internal static bool IsHumanName(string name) =>
        !name.StartsWith("mcp-", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("research-cmd", StringComparison.OrdinalIgnoreCase);
    internal static double ReadInterval(string? hz) => double.TryParse(hz, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out double rate) && double.IsFinite(rate) && rate is >= .5 and <= 20 ? 1 / rate : .5;
    internal static bool Due(bool enabled, bool nearHuman, double now, double previous, double distantInterval) =>
        !enabled || nearHuman || now < previous || now - previous >= distantInterval;
}
