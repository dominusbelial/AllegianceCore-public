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
/// P485 — LIVE proof of the "fresh combat bots freeze on normal maps" root
/// cause and its fix. A NON-debug (<c>debugMode=false</c>) map never auto-starts
/// its mission: unlike debug mode (which <c>TryAutoStart</c>s at boot with no
/// players), a normal map holds <see cref="MissionStage.NotStarted"/> until every
/// occupied side's connected players ready up — and <c>Mission.Update</c>
/// early-returns on NotStarted, so the whole sim is frozen. The MCP bot-spawner
/// seats its peers but never sends <c>PLAYER_READY</c>, so a freshly-seated
/// combat bot is launched into a frozen world and never moves (the P429/P474
/// "combat bots freeze"). vizfight bypasses the lobby either in-process
/// (<c>m.Stage = Started</c>, impossible for an out-of-process wire client) or
/// over the wire (<c>SendPlayerReady(true,…)</c>, Showcase ReadyUp phase); the
/// MCP layer's only legal equivalent is to ready up its OWN peers over the wire.
///
/// This test boots a normal-map in-process server and drives a real
/// <see cref="BotManager"/> against it. It asserts the mission reaches
/// <see cref="MissionStage.Started"/> <b>purely from the harness's ready-up</b>
/// (no manual stage forcing), then spawns a combat bot on the side opposing the
/// probe and asserts its live position CHANGES — the once-frozen bot now moves.
/// On the unfixed harness (no ready-up) the mission stays NotStarted and the
/// assertion fails: the test is red before the fix, green after.
/// </summary>
[Collection("LiveNetwork")]
public sealed class ClientReadyUpUnfreezesNormalMapLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg NormalConfig() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = new() { DebugMode = false },   // a NORMAL map: no debug auto-start
        Games =
        [
            new Gm { Name = "P485 Normal Freeze", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static IEnumerable<Ship> Ships(Srv srv) =>
        srv.Missions.SelectMany(m => m.Sectors.SelectMany(s => s.Models.OfType<Ship>()));

    private static bool Wait(Func<bool> done, int maxMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(maxMs);
        while (!done() && DateTime.UtcNow < deadline) Thread.Sleep(10);
        return done();
    }

    /// <summary>A background thread that keeps the in-process server's sim ticking
    /// (≈ every 5 ms) so real client connections progress without the test thread
    /// driving every tick — the way a production server runs its own sim loop.
    /// This is what lets <see cref="BotManager"/>'s blocking probe
    /// connect/logon/seat round-trips complete while it awaits them.</summary>
    private sealed class ServerTicker : IDisposable
    {
        private readonly Srv _server;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;

        public ServerTicker(Srv server)
        {
            _server = server;
            _thread = new Thread(Loop) { IsBackground = true, Name = "p485-server-ticker" };
            _thread.Start();
        }

        private void Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try { _server.TickSim(); }
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

    /// <summary>⭐ The live root-cause + fix proof.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Harness_ready_up_starts_normal_map_mission_and_opposing_combat_bot_moves()
    {
        Require.Artwork();
        var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;
        server.Boot(NormalConfig());
        var mission = server.Missions[0];
        Assert.Equal(MissionStage.NotStarted, mission.Stage);   // a normal map holds NotStarted (debug off)

        using var ticker = new ServerTicker(server);
        using var mgr = new BotManager("127.0.0.1", server.BoundPort);
        Assert.True(mgr.ServerReady, "the BotManager probe must validate against the in-process server");

        // ⭐ THE FIX UNDER TEST: the harness readies its OWN probe (and every bot),
        // so the normal-map lobby ready-up auto-start fires — the sim stops being
        // frozen at NotStarted. Nothing here forces m.Stage manually.
        bool started = Wait(() => mission.Stage == MissionStage.Started, 40000);
        Assert.True(started, $"normal-map mission never auto-started from the harness ready-up (stage={mission.Stage}) — the freeze is unfixed");
        Console.WriteLine($"P485-LIVE: normal map reached {mission.Stage} purely from the harness ready-up");

        // Now the once-frozen case: a freshly-seated combat bot on the side OPPOSING
        // the probe. It must actually move (the sim is no longer frozen).
        var sides = mgr.ValidSides();
        short sideB = sides[1];                       // opposing the probe (probe seats sides[0])
        var hull = Catalog.ResolveHull("Adv Scout");
        var spawn = mgr.SpawnBot(hull.Id, hull.Name, sideB, Catalog.DefaultParts(), "combat");
        Ship bot = Ships(server).Single(s => !s.IsNpc && s.Id == spawn.ShipId);
        var startPos = bot.Position;
        Console.WriteLine($"P485-LIVE: opposing combat bot {bot.Id} on side {sideB} start pos {startPos:F0}");

        bool moved = Wait(() => (bot.Position - startPos).Length() > 1.0f, 40000);
        Console.WriteLine($"P485-LIVE: opposing combat bot {bot.Id} now {bot.Position:F0} (start {startPos:F0}) moved={moved} mission={mission.Stage}");
        Assert.True(moved, $"opposing combat bot on a normal map stayed frozen at {startPos:F0} (mission={mission.Stage}) — freeze unfixed");
    }
}
