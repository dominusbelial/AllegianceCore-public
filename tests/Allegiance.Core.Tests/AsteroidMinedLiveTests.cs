using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P120 — live loopback proof that the <c>ASTEROID_MINED</c>(198) seam the port
/// already wires (<see cref="ServerHost.MineAsteroidEvent"/> raised from
/// <see cref="Sector.ResolveMining"/> at the native &gt;3.0 ore-change threshold,
/// <c>asteroidigc.h:246-251</c>) now also <b>broadcasts</b>, not just logs
/// (<c>Server.OnMine</c>):
/// <list type="number">
/// <item>a ship parked on a real He3 rock mines it past the threshold and a real
/// <see cref="AsteroidMined"/>(198) naming that asteroid id, its sector and its
/// remaining ore (div-240 byte) is delivered to the WHOLE MISSION — the
/// cross-side enemy bot receives it exactly like the same-side peer, the same
/// <c>GetGroupRealSides()</c> audience (<c>FedSrv.CPP:12418</c>) and settlement
/// P102 reached for <c>ASTEROID_DESTROYED</c>(86);</item>
/// <item>once the miner's bag hits capacity, accrual halts and <b>no</b> further
/// <c>ASTEROID_MINED</c> fires on subsequent ticks — the existing
/// <see cref="Asteroid.MineOre"/> reporting throttle still gates the new
/// broadcast exactly as it gates the log.</item>
/// </list>
/// Construction mirrors <see cref="AsteroidCombatLiveTests"/>' 3-bot / 2-side
/// harness (a guaranteed same-side pair plus a cross-side peer) with the rock and
/// the miner co-located at the origin spawn so only the miner is in mining range;
/// the demo map has zero asteroids, so the synthetic rock is the only one in the
/// universe (<see cref="MiningTests"/>' seam). A separate deterministic unit test
/// pins the &gt;3.0 reporting threshold itself. No Windows VM, no native client.
/// </summary>
[Collection("LiveNetwork")]
public sealed class AsteroidMinedLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, short radius, short sectorId)
    {
        return new DataAsteroidIGC(
            Signature: 1.0f,
            Position: [pos.X, pos.Y, pos.Z],
            Up: [0f, 1f, 0f],
            Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f],
            Ore: oreMax,
            OreMax: oreMax,
            AabmCapabilities: Asteroid.CapMineHe3,
            AsteroidId: id,
            HitPoints: 0f,
            Radius: radius,
            ModelName: "roid.mdl",
            TextureName: "",
            IconName: "",
            ClusterId: sectorId,
            Name: $"ROID_{id}",
            Fraction: 0);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Asteroid Mined Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 821u, DwCookie = 0x821u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 82200000000000000ul, DrmHash = "asteroidmined",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static float Frac(AsteroidMined m) => m.BpOreFraction / 240f;   // div-240 retrieve (Utility.h:1312)

    /// <summary>A booted 3-bot / 2-side live server plus the resolved roles and the
    /// single synthetic He3 rock (id 8301) placed at the origin spawn by the
    /// ShipSpawner. Slot 0 = <b>side A</b> MINER, co-located with the rock so it
    /// mines it; slot 1 = <b>side B</b> cross-side observer; slot 2 = <b>side A</b>
    /// same-side observer. Both observers sit far (&gt; ~700 m) from the rock, so
    /// they never mine and never generate broadcasts of their own — every
    /// <c>ASTEROID_MINED</c> they receive is provably the miner's, delivered
    /// whole-mission.</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship Miner = null!, CrossSide = null!, Teammate = null!;
        public FltBot MinerBot = null!, CrossSideBot = null!, TeammateBot = null!;
        public Asteroid Rock = null!;
        public Sector Sector = null!;

        public void Dispose()
        {
            foreach (var b in new[] { MinerBot, CrossSideBot, TeammateBot }) b?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatThree()
    {
        var seat = new Seated();
        seat.Server = new Srv();

        // Rock + miner at the origin; observers far away (outside the mining
        // proximity envelope radius+shipHalfLen+30 for a radius-60 rock).
        Vector3 apos = Vector3.Zero;
        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            if (seat.Rock is null)
            {
                seat.Rock = new Asteroid(MkAsteroid(8301, apos, oreMax: 480f, radius: 60, sectorId: sector.Id));
                mission.PlaceAsteroid(seat.Rock, sector);
            }
            var ship = placed switch
            {
                0 => new Ship(id, mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First(), apos, Vector3.Zero, Orientation.Identity),   // miner on the rock
                1 => new Ship(id, mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First(), new Vector3(0f, 580f, 980f), Vector3.Zero, Orientation.Identity),   // cross-side observer
                _ => new Ship(id, mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First(), new Vector3(680f, -400f, 840f), Vector3.Zero, Orientation.Identity),  // same-side observer
            };
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B,A
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        // Keep mining paused until all three roles have joined. Reader scheduling
        // must not consume ore or generate a partial-audience pre-test history.
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p120_miner", "p120_cross", "p120_teammate" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            for (int i = 0; i < bots.Length; i++)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bots[i].ShipAssignment!.Ship.ShipId).Ship;
                if (ship.Position.Length() < 1f) { seat.Miner = ship; seat.MinerBot = bots[i]; }
                else if (ship.Position.X > 500f) { seat.Teammate = ship; seat.TeammateBot = bots[i]; }   // teammate at (680,−400,840)
                else { seat.CrossSide = ship; seat.CrossSideBot = bots[i]; }                              // cross-side at (0,580,980)
            }
            Assert.NotNull(seat.Miner); Assert.NotNull(seat.CrossSide); Assert.NotNull(seat.Teammate);
            Assert.True(seat.Miner.SideId != seat.CrossSide.SideId, "cross-side peer must be on the other side from the miner");
            Assert.True(seat.Miner.SideId == seat.Teammate.SideId, "teammate must be same-side as the miner");
            seat.Miner.Velocity = Vector3.Zero;
            // P528: pin the miner's rate base so the fill (native ~1 ore/s × the
            // civ's GA) completes fast; the mining behaviour under test is
            // timing-independent, so this keeps the live proof quick.
            seat.Miner.MiningRateOverridePerSecond = 100f;
            foreach (var mission in seat.Server.Missions) mission.Stage = MissionStage.Started;
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    private static void ReceiveBarrier(Seated seat, uint marker)
    {
        var peers = new[] { seat.MinerBot, seat.CrossSideBot, seat.TeammateBot };
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        long nextProbe = 0;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            // A second phase may begin within the normal reply throttle. Retry
            // missing echoes at its real cadence; keep the production limit intact.
            if (elapsed.ElapsedMilliseconds >= nextProbe)
            {
                foreach (var peer in peers)
                    if (!peer.Pings.Any(ping => ping.TimeClient == marker)) peer.SendPing(marker);
                nextProbe = elapsed.ElapsedMilliseconds + global::Allegiance.Server.ServerHost.MinPingReplyIntervalMs + 50;
            }
            seat.Server.TickSim();
            return peers.All(p => p.Pings.Any(ping => ping.TimeClient == marker));
        }, TimeSpan.FromSeconds(30)), "all mining recipients must decode the ordered phase barrier");
    }

    /// <summary>⭐ A ship parked on a real He3 rock mines it past the &gt;3.0
    /// threshold and a real <see cref="AsteroidMined"/>(198) naming the correct
    /// asteroid id + sector, with a declining div-240 ore fraction, is broadcast to
    /// the WHOLE MISSION: the same-side teammate AND the cross-side enemy both
    /// receive it. That cross-side arrival is the whole-mission proof
    /// (<c>GetGroupRealSides()</c>, <c>FedSrv.CPP:12418</c>), exactly the P102
    /// <c>ASTEROID_DESTROYED</c> settlement.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mining_broadcasts_ASTEROID_MINED_to_whole_mission_cross_side_and_same_side()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var rock = seat.Rock;
        var sector = seat.Sector;

        // Clear any pre-join notices; all three bots must be seated (in the peer
        // map) to receive the whole-mission broadcast.
        seat.MinerBot.ResetAsteroidMineds();
        seat.CrossSideBot.ResetAsteroidMineds();
        seat.TeammateBot.ResetAsteroidMineds();

        // Generate the first real threshold crossing, then freeze physics while
        // every reader decodes that same historical ore state.
        int mined = PumpUntil(seat.Server, () => rock.OreMax - rock.OreRemaining > 3f, 6000);
        Assert.True(mined < 6000, "mining must cross the native reporting threshold");
        Assert.True(SpinWait.SpinUntil(() => seat.MinerBot.AsteroidMineds.Count > 0
            && seat.CrossSideBot.AsteroidMineds.Count > 0 && seat.TeammateBot.AsteroidMineds.Count > 0,
            TimeSpan.FromSeconds(30)), "all three peers must decode the mining report");
        Assert.True(seat.MinerBot.AsteroidMineds.Count > 0, "the miner itself must receive ASTEROID_MINED");
        Assert.True(seat.CrossSideBot.AsteroidMineds.Count > 0, "cross-side enemy must receive ASTEROID_MINED (whole mission)");
        Assert.True(seat.TeammateBot.AsteroidMineds.Count > 0, "same-side teammate must receive ASTEROID_MINED (whole mission)");

        // Every message names the RIGHT rock and sector, and carries a declining
        // (non-increasing) ore fraction — the rock only drains between reports.
        foreach (var bot in new[] { seat.MinerBot, seat.CrossSideBot, seat.TeammateBot })
        {
            var msgs = bot.AsteroidMineds;
            foreach (var m in msgs)
            {
                Assert.Equal(rock.Data.AsteroidId, m.AsteroidId);
                Assert.Equal(sector.Id, m.ClusterId);
                Assert.InRange(m.BpOreFraction, (byte)0, (byte)240);
            }
            for (int i = 1; i < msgs.Count; i++)
                Assert.True(msgs[i].BpOreFraction <= msgs[i - 1].BpOreFraction,
                    "ore fraction must not increase between mining reports (rock only drains)");
        }

        // The broadcasts started ~full and ended tracking the rock's drained
        // state: the last fraction each peer got equals (oreMax − ship.Ore)/oreMax
        // — the rock's net draw equals the miner's carried ore (self-consistent
        // with the server's own numbers), within regen drift + timing slack.
        float expectedStop = (rock.OreMax - seat.Miner.Ore) / rock.OreMax;
        foreach (var bot in new[] { seat.MinerBot, seat.CrossSideBot, seat.TeammateBot })
        {
            var msgs = bot.AsteroidMineds;
            float last = Frac(msgs[^1]);
            Assert.True(Math.Abs(last - expectedStop) < 0.065f,
                $"final reported fraction {last:0.###} should track the rock's drained state ~{expectedStop:0.###}");
        }

        Console.WriteLine($"P120-MINED: asteroid {rock.Data.AsteroidId} '{rock.Data.Name}' mined to ore fraction {Frac(seat.TeammateBot.AsteroidMineds[^1]):0.###} " +
                          $"=> ASTEROID_MINED(198) at sector {sector.Id} -> miner + same-side + cross-side peer(s) [WHOLE MISSION]");
    }

    /// <summary>⭐ No-regression of the throttle: once the miner's bag reaches its
    /// flat capacity (<see cref="Ship.MineCapacity"/>), accrual halts and <b>no</b>
    /// further <c>ASTEROID_MINED</c> fires on subsequent ticks — the existing
    /// <see cref="Asteroid.MineOre"/> reporting gate (which only raises the seam on
    /// a &gt;3.0 ore change) holds exactly the same with the new broadcast wired in.
    /// The observer peers' message counts must stay frozen while the server ticks.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void No_broadcast_fires_once_miner_hits_capacity()
    {
        Require.Artwork();
        using var seat = SeatThree();

        // Let the miner fill to its (GA-scaled) capacity — the miner is on
        // side 0 (civ 1 Dreghklar, c_gaMiningCapacity base 0.95), so
        // EffectiveMineCapacity = core[7] (90) × 0.95 = 85.5 (P477/P528), not
        // the retired flat 150.
        float cap = seat.Miner.EffectiveMineCapacity();
        PumpUntil(seat.Server,
            () => seat.Miner.Ore >= cap
               && seat.MinerBot.AsteroidMineds.Count > 0
               && seat.CrossSideBot.AsteroidMineds.Count > 0
               && seat.TeammateBot.AsteroidMineds.Count > 0,
            20000);
        Assert.True(seat.Miner.Ore >= cap - 0.5f, "miner should reach its ore capacity");
        // Being full does not mean the earlier reports have left each TCP reader.
        ReceiveBarrier(seat, 0x120a);

        int mCount = seat.MinerBot.AsteroidMineds.Count;
        int cCount = seat.CrossSideBot.AsteroidMineds.Count;
        int tCount = seat.TeammateBot.AsteroidMineds.Count;
        Assert.True(mCount > 0 && cCount > 0 && tCount > 0, "broadcasts should have flowed on the way to capacity");

        // Pump many more ticks — with the miner parked full, nothing mines, so no
        // MineAsteroidEvent (and therefore no ASTEROID_MINED) may be emitted.
        PumpUntil(seat.Server, () => false, 150);
        ReceiveBarrier(seat, 0x120b);
        Assert.Equal(mCount, seat.MinerBot.AsteroidMineds.Count);
        Assert.Equal(cCount, seat.CrossSideBot.AsteroidMineds.Count);
        Assert.Equal(tCount, seat.TeammateBot.AsteroidMineds.Count);

        Console.WriteLine($"P120-HALT: miner full at {seat.Miner.Ore:0.#}/{cap:0.#}; {mCount}+{cCount}+{tCount} " +
                          $"ASTEROID_MINED seen, zero further broadcasts across 150 idle ticks [THROTTLE HOLDS]");
    }

    /// <summary>⭐ Deterministic unit pin of the &gt;3.0 reporting threshold the
    /// broadcast rides on (<c>asteroidigc.h:246-251</c>): small ore movements that
    /// never cross 3.0 cumulative produce <b>no</b> pending report, the crossing
    /// movement raises exactly one, and a sub-threshold movement afterwards stays
    /// quiet again. This is the same gate that decides whether <c>OnMine</c> (and
    /// thus the new <c>ASTEROID_MINED</c> broadcast) fires at all.</summary>
    [Fact]
    public void MineOre_threshold_gates_the_pending_report()
    {
        var rock = new Asteroid(MkAsteroid(8399, Vector3.Zero, oreMax: 10000f, radius: 60, sectorId: 913));

        // Three 1.0 draws: cumulative change 1 → 2 → 3, never > 3.0 → no report.
        rock.MineOre(1.0f);
        Assert.False(rock.HasPendingReport, "Δ1 ≤ 3 must not report");
        rock.MineOre(1.0f);
        Assert.False(rock.HasPendingReport, "Δ2 ≤ 3 must not report");
        rock.MineOre(1.0f);
        Assert.False(rock.HasPendingReport, "Δ3 ≤ 3 must not report (strictly greater than 3.0)");

        // Fourth 1.0 draw: cumulative change now 4.0 > 3.0 → exactly one report,
        // carrying the current ore fraction.
        rock.MineOre(1.0f);
        Assert.True(rock.HasPendingReport, "cumulative Δ4 > 3.0 must report");
        Assert.True(Math.Abs(rock.PendingReport - (9996f / 10000f)) < 0.0001f,
            $"pending report should carry ore fraction, got {rock.PendingReport}");
        rock.ClearPendingReport();

        // A further small draw stays quiet (watermark moved to the report point).
        rock.MineOre(0.5f);
        Assert.False(rock.HasPendingReport, "Δ0.5 after the report must not re-report");
    }
}
