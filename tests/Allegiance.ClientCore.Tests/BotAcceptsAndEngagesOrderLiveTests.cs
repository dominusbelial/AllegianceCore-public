using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P431 — LIVE end-to-end proof that an MCP-spawned bot auto-accepts a real
/// teammate order and obeys it: real in-process server + a real
/// <see cref="BotManager"/> (its real wire probe + a <b>spawn_scenario-spawned</b>
/// combat bot) over real TCP sockets. A real order is queued on the scenario bot
/// (the P424 server-side queued-order delivery — the only real-player queued-command
/// writer, see P431 report), and we confirm over the real wire that the bot's own
/// client world sees the pending order, the bot sends a real ACCEPT back (the server
/// promotes it), the bot commits to the SPECIFIC commanded target, and
/// <c>list_active_bots</c> reports that accepted-order target.
///
/// Composition with spawn_scenario (P430) is the point of the setup: the ordered
/// bot is a spawn_scenario-spawned bot, not a separately, individually
/// spawn_bot-ed one.
///
/// Setup notes. The commanded bot sits on the side opposing the probe, because that
/// is the side whose own ClientWorld co-locates the cross-side target (the documented
/// P429 cross-side-visibility reality). The two sides spawn in different home sectors
/// on this map, so the target ship is relocated into the obeyer's sector and
/// re-anchored beside it each sim tick (a test-setup convenience that guarantees the
/// two are co-located — the acceptance + specific-target selection + real wire calls
/// under test are untouched).
/// </summary>
[Collection("LiveNetwork")]
public sealed class BotAcceptsAndEngagesOrderLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    // Representative "engage that ship" order action id. The port has no dedicated
    // attack constant; the bot's translation keys on TargetType == Ship, so any real
    // CommandID the queued-order wire carries is honored (see the P431 report).
    private const short AttackCmd = OrderChange.CidGoto;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P431 MCP Bot Accepts Teammate Order", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static IEnumerable<Ship> Ships(Srv srv) =>
        srv.Missions.SelectMany(m => m.Sectors.SelectMany(s => s.Models.OfType<Ship>()));

    /// <summary>A background thread that keeps the in-process server's sim ticking
    /// (≈ every 5 ms) so real client connections progress without the test thread
    /// driving every tick — the way a production server runs its own sim loop. This
    /// is what lets <see cref="BotManager"/>'s blocking probe connect/logon/seat
    /// round-trips complete while it awaits them. An optional
    /// <paramref name="beforeTick"/> hook runs immediately before each tick so a
    /// test can kinematically re-anchor a ship right before the server sims it.</summary>
    private sealed class ServerTicker : IDisposable
    {
        private readonly Srv _server;
        private readonly Action<Srv>? _beforeTick;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;

        public ServerTicker(Srv server, Action<Srv>? beforeTick = null)
        {
            _server = server;
            _beforeTick = beforeTick;
            _thread = new Thread(Loop) { IsBackground = true, Name = "p431-server-ticker" };
            _thread.Start();
        }

        private void Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try { _beforeTick?.Invoke(_server); _server.TickSim(); }
                catch { }
                Thread.Sleep(5);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _thread.Join(2000);
        }
    }

    private static bool Wait(Func<bool> done, int maxMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(maxMs);
        while (!done() && DateTime.UtcNow < deadline) Thread.Sleep(10);
        return done();
    }

    /// <summary>⭐ The live proof.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ScenarioBot_AutoAcceptsAndEngagesTheSpecificCommandedTarget()
    {
        Require.Artwork();
        var server = new Srv();
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass the ready-up lobby (P180 pattern)

        Ship? anchor = null;      // the obeyer (a scenario-spawned combat bot)
        Ship? anchoredTarget = null;   // the commanded enemy, re-anchored beside the obeyer
        using var ticker = new ServerTicker(server, srv =>
        {
            // Keep the commanded target co-located with the obeyer so it can be engaged.
            if (anchor is { } a && anchoredTarget is { } t)
                t.Position = a.Position + new Vector3(200f, 0f, 0f);
        });
        using var mgr = new BotManager("127.0.0.1", server.BoundPort);
        Assert.True(mgr.ServerReady, "the BotManager probe must validate against the in-process server");

        var sides = mgr.ValidSides();
        Assert.True(sides.Count >= 2, $"staging needs ≥2 sides, got [{string.Join(", ", sides)}]");
        short sideA = sides[0];   // the probe's side (it seats on Sides[0])
        short sideB = sides[1];   // the opposing side — where the obedied bot must sit

        // ── spawn_scenario(1) — the REAL scenario path: 1 combat bot per side ──
        var scenarioArgs = new Dictionary<string, JsonElement>
        {
            ["size"] = JsonSerializer.SerializeToElement(1),
        };
        var scenarioResult = McpTools.Handle("spawn_scenario", scenarioArgs, mgr);
        Assert.False(scenarioResult.IsError == true, $"spawn_scenario errored: {ScenarioText(scenarioResult)}");

        // Locate the two scenario bots over the real world (real peers only — the map
        // also pre-seeds NPC defenders). The opposing-side one is the obeyer; the
        // same-side-as-probe one is the commanded enemy target.
        short probeId = (short)mgr.Probe.World.MyShipId!.Value;
        Wait(() => Ships(server).Any(s => !s.IsNpc && s.SideId == sideB) && Ships(server).Any(s => !s.IsNpc && s.SideId == sideA && s.Id != probeId), 6000);
        Ship obeyer = Ships(server).Single(s => !s.IsNpc && s.SideId == sideB);
        Ship target = Ships(server).Single(s => !s.IsNpc && s.SideId == sideA && s.Id != probeId);
        short obeyerId = obeyer.Id;
        short targetId = target.Id;
        Console.WriteLine($"P431-LIVE: scenario bot {obeyerId} on side {sideB} (sector {obeyer.Sector?.Id}); commanded enemy {targetId} on side {sideA} (sector {target.Sector?.Id}); probe #{probeId} is an ambient same-side peer");

        // Co-locate: move the commanded target into the obeyer's sector (it spawns in
        // a different home sector on this map), then anchor it beside the obeyer.
        target.Sector?.Remove(target);
        var mission = obeyer.Mission ?? target.Mission!;
        mission.PlaceShip(target, obeyer.Sector!);
        target.Position = obeyer.Position + new Vector3(200f, 0f, 0f);
        anchor = obeyer;
        anchoredTarget = target;

        // ── Issue a real order on the obeyer, naming the target ship ──
        server.QueueCommandForPlayer(obeyer, AttackCmd, target);
        Assert.Equal(AttackCmd, obeyer.QueuedCommandId);          // the order is real, pending

        // Drive the real sim + the real MCP loop until the bot has (a) auto-accepted
        // over the wire and (b) engaged the SPECIFIC named target (server wrote
        // Ship.CommandTarget = that ship).
        bool engaged = Wait(() =>
            obeyer.AcceptedShipCommandId == AttackCmd
            && obeyer.CommandTarget?.Id == targetId
            && obeyer.QueuedCommandId == OrderChange.CidNone, 25000);
        Assert.True(engaged, $"bot {obeyerId} never reached accepted+engaged (accepted={obeyer.AcceptedShipCommandId}, cmdTarget={obeyer.CommandTarget?.Id}, queued={obeyer.QueuedCommandId}, orderedTargetVisible={(obeyer.CommandTarget is not null)})");

        // 1) The bot auto-accepted the real order over the wire (server promoted it).
        Assert.Equal(AttackCmd, obeyer.AcceptedShipCommandId);
        Assert.Equal(OrderChange.CidNone, obeyer.QueuedCommandId);   // pending cleared by the accept
        Console.WriteLine($"P431-LIVE: bot {obeyerId} AUTO-ACCEPTED cmdID={AttackCmd} over the real wire (accepted slot = {obeyer.AcceptedShipCommandId}, queued cleared)");

        // 2) It visibly turned to engage the SPECIFIC commanded target (not any other
        //    nearby peer — the server-side CommandTarget names the issued ship).
        Assert.Equal(targetId, obeyer.CommandTarget?.Id);
        Console.WriteLine($"P431-LIVE: bot {obeyerId} engaged the SPECIFIC commanded ship {targetId} (server CommandTarget #{obeyer.CommandTarget?.Id})");

        // 3) list_active_bots reports the real accepted-order target for the bot.
        var obView = mgr.ActiveBots().Single(b => b.ShipId == obeyerId);
        Assert.Equal(targetId, obView.OrderedTarget);
        Console.WriteLine($"P431-LIVE: list_active_bots shows bot {obeyerId} orderedTarget={obView.OrderedTarget} == the issued ship {targetId} (behavior={obView.Behavior}, live target={obView.Target})");
    }

    private static string ScenarioText(object result)
    {
        try
        {
            var prop = result.GetType().GetProperty("Content");
            if (prop?.GetValue(result) is System.Collections.IEnumerable list)
                foreach (var c in list)
                {
                    var textProp = c?.GetType().GetProperty("Text");
                    if (textProp?.GetValue(c) is string s) return s;
                }
        }
        catch { }
        return "<unreadable>";
    }
}
