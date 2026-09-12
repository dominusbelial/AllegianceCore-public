using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P94: the live loopback proof that a directly-deployed mine is broadcast as a
/// real <c>FM_S_EXPORT(26)</c>/<c>OT_mine</c> to the layer's same-side peers —
/// and, when it expires, a real <c>FM_S_MINE_DESTROYED(84)</c> naming that same
/// mine — and that a cross-side enemy receives neither. Construction mirrors
/// <see cref="MissileBroadcastTests"/> (3 bots / round-robin sides, a guaranteed
/// same-side pair plus a cross-side enemy), but the mine is triggered by calling
/// <c>Ship.DeployMine</c> directly on the deployer's server-side <c>Ship</c>
/// (the public, deliberately-not-dispatch-wired entry point this slice adds),
/// with a real <c>DataMineTypeIGC</c> pulled from the loaded <c>StaticCore</c>
/// (<c>mission.Core.MineTypes</c>). Five things are asserted for real:
/// <list type="number">
/// <item>the same-side observer receives a decoded <see cref="ExportMine"/>
/// whose <c>DataMineExport</c> fields are real (<c>p0</c> at the deployer's real
/// position, <c>sideID</c> matching the deployer's side, <c>launcherID</c> the
/// deployer, the real mine-type id and mine id, <c>fraction</c> = 1.0);</item>
/// <item>pumping ticks past the mine's decoded <c>Lifespan</c>, the observer
/// receives a real <see cref="MineDestroyed"/>(84) naming that same mine id;</item>
/// <item>the mine's position never changes between the two observations — the
/// whole simplifying premise, proved rather than assumed;</item>
/// <item>the cross-side enemy receives neither message, no matter how long ticks
/// are pumped past expiry;</item>
/// <item>deploying twice yields two distinct mine ids and two distinct destroy
/// events, not one reused/cached pair.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public class MineBroadcastTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Mine Broadcast Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 853u, DwCookie = 0x853u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 87000000000000000ul, DrmHash = "minetest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A real mine type the deployer will lay — one whose decoded
    /// <c>Lifespan</c> falls in [2 s, 8 s), comfortably long enough to pump the
    /// creation broadcast, then pump past expiry for the destruction broadcast,
    /// without either racing.</summary>
    private static DataMineTypeIGC RealMineType(StaticCore core)
    {
        var t = core.MineTypes.Values
            .Where(m => m.Lifespan >= 2.0f && m.Lifespan < 8.0f)
            .OrderBy(m => m.Lifespan)
            .FirstOrDefault();
        if (t.ExpendableTypeId == 0 && core.MineTypes.Count > 0)
            throw new InvalidOperationException("representative core has no mine with 2<=lifespan<8");
        return t;
    }

    /// <summary>⭐ A teammate must see a real, decoded EXPORT(26)/OT_mine for a
    /// mine its same-side ally deploys, and later a real MINE_DESTROYED(84)
    /// naming that same mine — while the cross-side enemy receives neither, no
    /// matter how long we pump. The mine never moves. Deploying twice yields two
    /// distinct ids / two distinct destroy events. The mines are deployed by
    /// calling <c>Ship.DeployMine</c> directly (the not-yet-dispatch-wired entry
    /// point).</summary>
    [Fact]
    public void Directly_deployed_mine_broadcasts_create_and_destroy_to_teammate_but_not_enemy()
    {
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p94_layer", "p94_observer", "p94_enemy" };
        try
        {
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i].Connect(server.BoundPort, names[i]);
                bots[i].SendLogonRequest(Request(names[i]));
            }
            PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            var ships = server.Ships.ToDictionary(s => s.Ship.Id, s => s.Ship.SideId);
            short[] ids = bots.Select(b => b.ShipAssignment!.Ship.ShipId).ToArray();

            int subject = -1, observer = -1, enemy = -1;
            for (int i = 0; i < bots.Length && observer < 0; i++)
                for (int j = 0; j < bots.Length; j++)
                    if (j != i && ships[ids[j]] == ships[ids[i]]) { subject = i; observer = j; break; }
            Assert.True(subject >= 0, "round-robin must yield a same-side pair");
            for (int k = 0; k < bots.Length; k++)
                if (k != subject && k != observer) { enemy = k; break; }
            Assert.True(enemy >= 0 && ships[ids[enemy]] != ships[ids[subject]],
                "3 bots / 2 teams must leave one bot cross-side");

            short subjectId = ids[subject];
            short subjectSide = (short)ships[ids[subject]]!;   // SideId is short?; guaranteed non-null for a placed ship
            var obsBot = bots[observer];
            var enemyBot = bots[enemy];

            var live = server.Ships.First(l => l.Ship.Id == subjectId);
            var layer = live.Ship;
            var mission = layer.Mission ?? throw new InvalidOperationException("deployer must have a mission");
            var mType = RealMineType(mission.Core);

            // Gate precondition: the observer must already hold the deployer's
            // EXPORT, else the server (correctly) withholds the teammate send.
            PumpUntil(server, () => obsBot.Exports.Any(e => e.ShipId == subjectId), 8000);
            Assert.Contains(obsBot.Exports, e => e.ShipId == subjectId);

            // Deploy TWO mines directly at the current sim time. Distinct ids
            // (NextObjectId increments per call) and, since both share `now`,
            // both expire together — yielding two distinct destroys, proving no
            // cache. Capture each returned Mine to prove it never moves.
            double now = mission.LastUpdate;
            var deployed = new List<Mine>
            {
                layer.DeployMine(mType, now),
                layer.DeployMine(mType, now),
            };
            var initialPositions = deployed.ToDictionary(m => m.Id, m => m.Position);
            Assert.True(deployed.Select(m => m.Id).Distinct().Count() == 2,
                $"two deployments must yield two distinct mine ids, got [{string.Join(", ", deployed.Select(m => m.Id))}]");

            // (1) the teammate receives two decoded ExportMine/OT_mine, each with
            // real DataMineExport fields: p0 at the layer's real position,
            // sideID matching the layer's side, launcherID the layer, the real
            // mine type id, and fraction 1.0.
            PumpUntil(server, () => obsBot.MineExports.Count >= 2, 6000);
            var created = obsBot.MineExports;
            Assert.True(created.Count >= 2, $"observer must receive ≥ 2 EXPORT(mine) messages (one per deployment), got {created.Count}");
            var p0ById = new Dictionary<short, Vector3>();
            foreach (var ex in created)
            {
                var dm = ex.Mine;
                p0ById[dm.MineId] = dm.P0;
                Assert.True(initialPositions.ContainsKey(dm.MineId), $"export {dm.MineId} must name a mine we deployed ({string.Join(",", initialPositions.Keys)})");
                Assert.Equal(mType.ExpendableTypeId, dm.MinetypeId);
                Assert.Equal(subjectId, dm.LauncherId);        // the real laying ship's id
                Assert.Equal(subjectSide, dm.SideId);          // the deployer's side
                Assert.Equal(layer.Sector?.Id, dm.ClusterId);
                Assert.True(dm.CreateNow, "a freshly-deployed mine broadcasts createNow");
                Assert.True(ex.ObjectType == ExportMine.OtMine, "objecttype must be OT_mine(3)");
                Assert.Equal(1.0f, dm.FractionValue, 1e-4f);   // armed fraction 1.0 (div-240)
                Assert.True((dm.P0 - initialPositions[dm.MineId]).Length() < 1e-4f,
                    $"export p0 {dm.P0} must be the real deployed position {initialPositions[dm.MineId]}");
            }
            Assert.Equal(2, p0ById.Count);                      // two DISTINCT mine ids, not one cached pair

            // (2) pumping past the decoded Lifespan reaches the destruction
            // broadcast: one real MineDestroyed(84) per created mine, naming the
            // same id and the same sector.
            PumpUntil(server, () => obsBot.MineDestroyeds.Count >= 2, 12000);
            var dest = obsBot.MineDestroyeds;
            Assert.True(dest.Count >= 2, $"observer must receive ≥ 2 MINE_DESTROYED (one per expired mine), got {dest.Count}");
            foreach (var d in dest)
            {
                Assert.True(p0ById.ContainsKey(d.MineId),
                    $"destroy {d.MineId} must name a mine we saw deployed ([{string.Join(", ", p0ById.Keys)}])");
                Assert.Equal(layer.Sector?.Id, d.ClusterId);
            }
            Assert.True(dest.Select(d => d.MineId).Distinct().Count() == 2,
                $"two expiries must yield two distinct destroy ids, got [{string.Join(", ", dest.Select(d => d.MineId))}]");

            // (3) the mine's position NEVER changed between the two observations
            // (the creation export's p0 and the destroy-time sim position) — the
            // whole \"no movement\" premise, proved rather than assumed.
            foreach (var d in dest)
            {
                var m = deployed.First(mm => mm.Id == d.MineId);
                Vector3 creationP0 = p0ById[d.MineId];
                Assert.True((creationP0 - m.Position).Length() < 1e-4f,
                    $"mine {d.MineId} p0 at creation {creationP0} must equal its position at expiry {m.Position}");
                Assert.True((m.Position - initialPositions[d.MineId]).Length() < 1e-4f,
                    $"mine {d.MineId} moved: deployed {initialPositions[d.MineId]} → {m.Position}");
            }

            // (4) the cross-side enemy receives NEITHER message, no matter how
            // many ticks we pump past expiry.
            PumpUntil(server, () => false, 400);   // extra ticks past expiry for luck-free confidence
            Assert.Empty(enemyBot.MineExports);
            Assert.Empty(enemyBot.MineDestroyeds);
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }
}
