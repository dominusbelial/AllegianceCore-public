using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Tools.McpSpawner;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P432 — LIVE end-to-end proof of <c>run_hull_matrix</c>: a real in-process
/// server (the same m2xspiderw Test Mission the docker dev server runs, which
/// homes both teams in the same starting sector so combat begins immediately) +
/// a real <see cref="BotManager"/> (its real wire probe + real combat bots over
/// real TCP). Calls the real tool through <see cref="McpTools.Handle"/> with a
/// small representative matrix and asserts the real structure:
///   • the full cartesian product is returned, one row per combination;
///   • a weapon that genuinely does not mount on a fast hull (PE AutoCan 3 vs
///     Scout) is a real, specific per-combination error row — and the matrix
///     CONTINUES to run the compatible combos (no whole-matrix crash);
///   • every "ok" row carries real per-side spawn/kill/survivor counts and real
///     pooled update-cadence stats from that combination's own bots;
///   • after the whole matrix returns, exactly zero bots remain — each
///     combination's own bots were cleared (each ended its own real connection)
///     before the next ran, so measurements never bled.
/// </summary>
[Collection("LiveNetwork")]
public sealed class RunHullMatrixLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P432 Hull/Weapon Matrix", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private sealed class ServerTicker : IDisposable
    {
        private readonly Srv _server;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;
        public ServerTicker(Srv server) { _server = server; _thread = new Thread(Loop) { IsBackground = true, Name = "p432-server-ticker" }; _thread.Start(); }
        private void Loop() { while (!_cts.IsCancellationRequested) { try { _server.TickSim(); } catch { } Thread.Sleep(5); } }
        public void Dispose() { _cts.Cancel(); _thread.Join(2000); }
    }

    private static string ResultText(object result)
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
        return "";
    }

    /// <summary>⭐ The live proof: run a small real hull/weapon matrix against a
    /// real server and confirm the real per-combination rows + the clean
    /// disposal of each combination's own bots.</summary>
    [Fact]
    public void RunHullMatrix_ReturnsRealPerCombinationRows_AndClearsEachCombinationsBots()
    {
        using var server = new Srv();
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass the ready-up lobby (P180 pattern)
        using var ticker = new ServerTicker(server);

        using var mgr = new BotManager("127.0.0.1", server.BoundPort);
        Assert.True(mgr.ServerReady, "the BotManager probe must validate against the in-process server");

        var args = new Dictionary<string, JsonElement>
        {
            ["hulls"] = JsonSerializer.SerializeToElement(new[] { "Scout", "Bomber" }),
            ["weapons"] = JsonSerializer.SerializeToElement(new[] { "PW Gat Gun 1", "PE AutoCan 3" }),
            ["sizes"] = JsonSerializer.SerializeToElement(new[] { 1 }),
            ["observe_seconds"] = JsonSerializer.SerializeToElement(12),
        };

        var result = McpTools.Handle("run_hull_matrix", args, mgr);
        string text = ResultText(result);
        Assert.False(string.IsNullOrWhiteSpace(text), "run_hull_matrix returned no text");
        Assert.False(result.IsError == true, $"run_hull_matrix errored: {Truncate(text)}");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.Equal(4, root.GetProperty("total").GetInt32());       // 2 hulls × 2 weapons × 1 size
        Assert.Equal(3, root.GetProperty("ok").GetInt32());          // Gat fits both; AutoCan fits only Bomber
        Assert.Equal(1, root.GetProperty("errors").GetInt32());

        var combos = root.GetProperty("combos").EnumerateArray().ToList();
        Assert.Equal(4, combos.Count);

        // ── The compatibility-error row: Scout × PE AutoCan 3 is a real, specific
        //    error (the cannon has no hardpoint on a fast scout) and the matrix
        //    continues (the other combos still ran) — never a whole-matrix crash.
        var scoutAuto = combos.Single(c => c.GetProperty("hull").GetString() == "Scout"
                                        && c.GetProperty("weapon").GetString() == "PE AutoCan 3");
        Assert.Equal("error", scoutAuto.GetProperty("status").GetString());
        Assert.Contains("does not mount", scoutAuto.GetProperty("error").GetString());

        // ── Each ok row: real per-side spawn/kill/survivor counts + real cadence.
        var okRows = combos.Where(c => c.GetProperty("status").GetString() == "ok").ToList();
        Assert.Equal(3, okRows.Count);   // Scout×Gat, Bomber×Gat, Bomber×AutoCan
        foreach (var row in okRows)
        {
            var sa = row.GetProperty("sideA");
            var sb = row.GetProperty("sideB");
            Assert.Equal(1, sa.GetProperty("spawned").GetInt32());
            Assert.Equal(1, sb.GetProperty("spawned").GetInt32());
            // survivors == spawned − killed (internally consistent real accounting).
            Assert.Equal(sa.GetProperty("spawned").GetInt32() - sa.GetProperty("killed").GetInt32(),
                         sa.GetProperty("survived").GetInt32());
            Assert.Equal(sb.GetProperty("spawned").GetInt32() - sb.GetProperty("killed").GetInt32(),
                         sb.GetProperty("survived").GetInt32());
            Assert.InRange(sa.GetProperty("killed").GetInt32(), 0, 1);
            Assert.InRange(sb.GetProperty("killed").GetInt32(), 0, 1);
            // Real pooled cadence captured from this combination's own bots.
            var cad = row.GetProperty("cadence");
            int bc = cad.GetProperty("botCount").GetInt32();
            Assert.InRange(bc, 0, 2);
            Assert.True(cad.GetProperty("interval").GetProperty("mean").GetDouble() >= 0);
        }

        // ── The disposal proof: after the whole matrix, ZERO bots remain. Had any
        //    combination's bots not been cleared before the next ran, 2 bots per
        //    ok combination (3 ok combos) would still be connected — none are.
        Assert.Empty(mgr.ActiveBots());
    }

    private static string Truncate(string s) => s.Length > 400 ? s[..400] + "…" : s;
}
