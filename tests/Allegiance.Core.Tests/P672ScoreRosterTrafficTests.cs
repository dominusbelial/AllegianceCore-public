using Allegiance.Core.Sim;
using System.Diagnostics;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;
using FltBot = Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>Repeatable loopback measurement. Also runs unchanged against the
/// pre-P672 tree to obtain actual socket egress, rather than a size estimate.</summary>
[Collection("LiveNetwork")]
public sealed class P672ScoreRosterTrafficTests(ITestOutputHelper output)
{
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Sixty_four_loopback_peers_measure_steady_refresh_and_heavy_kill_traffic()
    {
        Require.Artwork();
        using var server = new Srv();
        var saved = Console.Out;
        using var log = new StringWriter();
        var bots = new List<FltBot>();
        try
        {
            Console.SetOut(log);
            server.Boot(new ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
                Profiler = new ProfilerConfig { Enabled = true, ReportIntervalSeconds = 3600 },
                Games = [new GameConfig { Name = "P672 traffic", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }],
            });
            server.Missions[0].Stage = MissionStage.Started;
            for (int i = 0; i < 64; i++)
            {
                string name = i < 2 ? $"P672-human-{i}" : $"mcp-p672-{i:D2}";
                var bot = new FltBot(); bots.Add(bot);
                bot.Connect(server.BoundPort, name);
                bot.SendLogonRequest(new LogonRequest { CharacterName = name, FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1 });
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (bot.ShipAssignment is null && DateTime.UtcNow < deadline) { server.TickSim(); Thread.Sleep(2); }
                Assert.NotNull(bot.ShipAssignment);
            }
            Assert.Equal(64, server.Ships.Count);
            // Stabilize join traffic before opening the accounting window.
            for (int i = 0; i < 80; i++) { server.TickSim(); Thread.Sleep(2); }
            Thread.Sleep(200);
            TickProfiler.ForceReport();
            log.GetStringBuilder().Clear();
            Measure("steady", 640, combat: false);
            Measure("combat", 240, combat: true);
        }
        finally
        {
            foreach (var bot in bots) bot.Dispose();
            Console.SetOut(saved);
        }

        void Measure(string phase, int ticks, bool combat)
        {
            int kills = 0;
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < ticks; i++)
            {
                // 32 actual lethal damage events over 12 seconds. As in the
                // existing combat-score fixture, a missing pod side forces a
                // true death (rather than eject), with real AddKill/AddDeath.
                if (combat && i % 7 == 0 && kills < 32)
                {
                    var victim = server.Ships.Single(s => s.Ship.Id == bots[2 + kills].ShipAssignment!.Ship.ShipId).Ship;
                    var attacker = server.Ships.Single(s => s.Ship.Id == bots[kills % 2].ShipAssignment!.Ship.ShipId).Ship;
                    victim.SideId = 774;
                    victim.ReceiveDamage(0, 1e9f, server.SimNow, victim.Position, Vector3.Zero, attacker);
                    kills++;
                }
                server.TickSim();
                double wait = (i + 1) * 50d - timer.Elapsed.TotalMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
            }
            Thread.Sleep(200); // allow asynchronous writers to finish before draining
            TickProfiler.ForceReport();
            var window = Assert.IsType<Allegiance.Core.Transport.EgressWindow>(server.LastEgressWindow);
            var roster = window.Rows.Where(r => r.Fmid is 209 or 219).ToArray();
            Assert.Equal(roster.Sum(r => r.OfferedBytes), roster.Sum(r => r.SentBytes));
            if (combat)
            {
                Assert.Equal(32, kills);
                Assert.Equal(32, server.Ships.Sum(s => s.PlayerScore.Deaths));
                Assert.Equal(32, server.Ships.Sum(s => s.PlayerScore.Kills));
            }
            output.WriteLine($"P672 phase={phase} peers=64 humans=2 harness=62 simSeconds={ticks / 20d:0.000} wallSeconds={window.WindowSeconds:0.000} kills={kills} rosterBytes={roster.Sum(r => r.SentBytes)} rosterBytesPerSimSecond={roster.Sum(r => r.SentBytes) / (ticks / 20d):0.000} rosterBytesPerWallSecond={roster.Sum(r => r.SentBytes) / window.WindowSeconds:0.000} humanBytes={roster.Where(r => r.PeerKind == "human").Sum(r => r.SentBytes)} harnessBytes={roster.Where(r => r.PeerKind == "harness").Sum(r => r.SentBytes)}");
            output.WriteLine(log.ToString().Split('\n').Last(l => l.StartsWith("[egress-by-type] ")));
            foreach (var row in roster) output.WriteLine($"P672 fmid={row.Fmid} audience={row.PeerKind} lane={row.Lane} bytes={row.SentBytes} messages={row.SentMessages}");
            log.GetStringBuilder().Clear();
        }
    }
}
