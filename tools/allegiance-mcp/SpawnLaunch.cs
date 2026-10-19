using System.Diagnostics;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Wire;
using BotClass = global::Allegiance.Bot.Bot;

namespace Allegiance.Tools.McpSpawner;

/// <summary>A purchase and launch must be confirmed by the purchasing peer.</summary>
internal static class SpawnLaunch
{
    public static int TimeoutMs => int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_MCP_SPAWN_TIMEOUT_MS"), out int ms)
        && ms > 0 && ms <= 120000 ? ms : 15000;

    // Capture before the first purchase, not before each retry: a late first
    // response must still count. A seat's old SET_CLUSTER is not launch proof.
    public static Func<bool> Observe(ConnectionSession cs, short shipId)
    {
        lock (cs.Gate)
        {
            cs.World.Ships.TryGetValue(shipId, out var initial);
            var pose = initial?.Latest;
            int station = initial?.StationId ?? -1;
            uint stateTime = initial?.StateTime ?? 0;
            byte state = initial?.State ?? 0;
            return () =>
            {
                lock (cs.Gate)
                    return cs.World.MyShipId == shipId && cs.World.Ships.TryGetValue(shipId, out var own)
                        && own.StationId < 0 && own.SectorId >= 0 && !own.IsDead && !own.IsGone
                        && (!ReferenceEquals(pose, own.Latest) || station >= 0
                            || stateTime != own.StateTime || state != own.State);
            };
        }
    }

    // BotClass has no ClientWorld. Its locked, own-connection wire snapshots
    // provide the same SET_CLUSTER / undock / own SHIP_STATUS confirmation.
    public static Func<bool> Observe(BotClass bot, short shipId)
    {
        var initial = bot.OwnLaunchState;
        int observations = bot.Observations.Count;
        return () =>
        {
            var current = bot.OwnLaunchState;
            if (current.ShipId != shipId) return false;
            // A late seat DOCKED must not mask a later flying SHIP_STATUS.
            // Apply signals in receive order, as native does for SET_CLUSTER /
            // DOCKED (clintlib/appmsg.cpp:991-1115) and status (:2711-2736).
            if (current.Revision != initial.Revision) return current.Flying;
            return initial.Flying && bot.Observations.Skip(observations).Any(o => o.ShipId == shipId);
        };
    }

    public static BuyLoadoutAck Confirm(string name, Func<bool> connected, Func<bool> ownLaunched,
        Action send, Func<IReadOnlyList<BuyLoadoutAck>> acks, Action<BuyLoadoutAck> validate,
        int timeoutMs, Func<Func<bool>, int, bool>? wait = null, Func<BuyLoadoutAck, bool>? matches = null)
    {
        wait ??= Wait;
        BuyLoadoutAck? granted = null;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            if (!connected()) throw new InvalidOperationException($"spawn_bot: bot '{name}' disconnected during launch");
            send();
            bool Confirmed()
            {
                if (!connected()) throw new InvalidOperationException($"spawn_bot: bot '{name}' disconnected during launch");
                var ack = acks().LastOrDefault(a => matches?.Invoke(a) ?? true);
                if (ack is not null)
                {
                    if (!ReferenceEquals(granted, ack)) validate(ack);
                    granted = ack;
                }
                return granted is not null && ownLaunched();
            }
            if (wait(Confirmed, timeoutMs)) return granted!;
            // A late join-burst/default-hull ack must not mask the requested
            // purchase. If no matching ack arrives, evaluate the last answer
            // for the existing specific refusal reason before retrying.
            if (granted is null && acks().LastOrDefault() is { } unmatched) validate(unmatched);
        }
        throw new InvalidOperationException($"spawn_bot: bot '{name}' timed out after 2 buy/launch attempts ({timeoutMs} ms each): "
            + (granted is null ? "no BUY_LOADOUT_ACK from its own session" : "own session never confirmed launch (SET_CLUSTER / undock / own ship status)"));
    }

    private static bool Wait(Func<bool> done, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < timeoutMs) Thread.Sleep(20);
        return done();
    }

    public static void LogFailure(int? side, string hull, string reason) =>
        Console.Error.WriteLine($"[p657] spawn failed side={side?.ToString() ?? "auto"} hull='{OneLine(hull)}': {OneLine(reason)}");
    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}
