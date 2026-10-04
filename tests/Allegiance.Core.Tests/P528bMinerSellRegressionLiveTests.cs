using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P528b — REGRESSION: after P528 moved He3 mining to native values (rate ≈ 1
/// ore/s instead of 100×, capacity = FloatConstants[7] × GA, price =
/// FloatConstants[8] × GA), a miner that filled its bag over the now-slow
/// (80–112 s) dwell never began the sell leg — it sat beside its (huge) rock
/// forever, never warping home to sell. Reproduced live on the 221 test server
/// (m2xspiderw, IC vs GT).
///
/// Root cause (fixed in P528b): the miner cycle clears
/// <see cref="Ship.MiningAsteroidTarget"/> the moment the bag is full
/// (Ship.cs:6876) so the sell leg can decide, but the P168 dodge scan exempts
/// <see cref="Ship.MiningAsteroidTarget"/> from its obstacle set (Ship.cs:4972).
/// With the target cleared, the (usually huge) rock the ship is parked against
/// becomes an "imminent collision", so <see cref="Ship.TryComputeDodgeGoal"/>
/// overrides the sell-leg cross-cluster steering every tick — a silent latch
/// that left the full miner orbiting its rock, never warping home. The fix
/// remembers the rock being departed (<see cref="Ship._departingMineAsteroid"/>)
/// so the dodge scan keeps ignoring it until the ship has visibly left.
///
/// This test boots the REAL m2xspiderw map with IC vs GT (civIds [25,3]) — the
/// exact 221 server shape — lets the auto-spawned initial miners work at NATIVE
/// rates (cross-sector sell leg, 2–3 hops home), and asserts EACH initial miner
/// completes TWO full mine→sell cycles with the P528 amounts. It FAILS against
/// the pre-fix code (942e1dd4): the IC miner (8096, cap 67.5) fills and never
/// sells, so the two-cycle assertion times out.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P528bMinerSellRegressionLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P528b Miner Sell Regression", Map = "m2xspiderw.igc", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; }
        return n;
    }

    private static List<Ship> Miners(Srv server) =>
        server.Missions[0].Sectors
            .SelectMany(s => s.Models.OfType<Ship>())
            .Where(s => s.IsNpc && s.PilotType == Ship.PilotTypeMiner)
            .ToList();

    /// <summary>⭐ The regression proof: each initial miner autonomously completes
    /// TWO full mine→sell cycles on the real m2xspiderw map at native rates —
    /// began mining → full → cross-sector warp(s) home → [mine-sell] at the P528
    /// amounts → relaunch → began again. Fails (times out, no second sale) on the
    /// pre-P528b code where a full miner is trapped beside its rock.</summary>
    [Fact]
    public void Each_initial_miner_completes_two_full_mine_sell_cycles_at_native_rates()
    {
        using var server = new Srv();
        server.Boot(Config());
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;   // P180: decision loops need a started mission

        var miners = Miners(server);
        Assert.Equal(2, miners.Count);

        // Phase 1: both initial miners undock and acquire an Alderan (1052) He3 target.
        int t1 = Pump(server, () => miners.All(m => m.StationId is null && m.MiningAsteroidTarget is not null), 12000);
        Assert.True(t1 < 12000, $"initial miners never undocked+targeted (ticks {t1})");
        foreach (var m in miners)
            Assert.Equal((short)1052, m.MiningAsteroidTarget!.Data.ClusterId);   // Alderan mining field

        // Phase 2: watch for sell completions. A sale is the Ore→0 transition while
        // undocked (the dock-sell path zeroes Ore). Each ship must sell twice.
        // Record the pre-sale ore so the P528 amounts can be asserted.
        var sells = miners.ToDictionary(m => m.Id, _ => new List<float>());
        var prevOre = miners.ToDictionary(m => m.Id, m => m.Ore);
        int budget = 85000;   // 4250 s: ample for travel + 2 native fills + 2 warp legs
        bool bothTwice = false;
        for (int i = 0; i < budget && !bothTwice; i++)
        {
            server.TickSim();
            foreach (var m in miners)
            {
                float po = prevOre[m.Id];
                if (po > 0f && m.Ore <= 0f && m.StationId is not null)   // docked just now → sold
                    sells[m.Id].Add(po);
                prevOre[m.Id] = m.Ore;
            }
            bothTwice = miners.All(m => sells[m.Id].Count >= 2);
        }

        Assert.True(bothTwice,
            $"each initial miner must complete TWO full sell cycles in {budget} ticks; got: "
            + string.Join(", ", miners.Select(m =>
                $"side{m.SideId}[{m.Id}] cap {m.EffectiveMineCapacity():F1} sells={sells[m.Id].Count} (last ore {prevOre[m.Id]:F2}, docked {m.StationId})")));

        // Phase 3: the sold amounts match P528's native values — IC (side 0) sells
        // its 67.5-ore bag @ 80/u = 5400; GT (side 1) sells its 90-ore bag @ 80/u = 7200.
        foreach (var m in miners)
        {
            var mySells = sells[m.Id];
            Assert.All(mySells, sold => Assert.True(
                Math.Abs(sold - m.EffectiveMineCapacity()) < 1e-3f,
                $"ship {m.Id} (side {m.SideId}) sold {sold} ore ≠ its full capacity {m.EffectiveMineCapacity():F1}"));
            Console.WriteLine($"[P528B-LIVE] side {m.SideId} miner {m.Id}: {mySells.Count} full cycles, each {m.EffectiveMineCapacity():F1} ore @ {m.EffectiveOreCreditPerUnit():F0}/u = {mySells[^1] * m.EffectiveOreCreditPerUnit():F0} cr");
        }
    }
}
