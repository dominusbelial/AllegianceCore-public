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
/// P99: the live loopback proof that a directly-deployed probe is broadcast as
/// a real <c>FM_S_EXPORT(26)</c>/<c>OT_probe</c> to the deployer's same-side
/// peers — and, when it expires, a real <c>FM_S_PROBE_DESTROYED(85)</c> naming
/// that same probe — and that a cross-side enemy receives neither. Construction
/// mirrors <see cref="MineBroadcastTests"/> (3 bots / round-robin sides, a
/// guaranteed same-side pair plus a cross-side enemy), but the probe is
/// triggered by calling <c>Ship.DeployProbe</c> directly on the deployer's
/// server-side <c>Ship</c> (the public, deliberately-not-dispatch-wired entry
/// point this slice adds), with a real <c>DataProbeTypeIGC</c> pulled from the
/// loaded <c>StaticCore</c> (<c>mission.Core.ProbeTypes</c>). Five things are
/// asserted for real:
/// <list type="number">
/// <item>the same-side observer receives a decoded <see cref="ExportProbe"/>
/// whose <c>DataProbeExport</c> fields are real (<c>p0</c> at the deployer's real
/// position, <c>sideID</c> matching the deployer's side, <c>shipID</c> the
/// deployer, the real probe-type id and probe id, and the no-target
/// <c>otTarget</c>/<c>oidTarget</c> = NA/-1);</item>
/// <item>pumping ticks past the probe's decoded <c>Lifespan</c>, the observer
/// receives a real <see cref="ProbeDestroyed"/>(85) naming that same probe
/// id;</item>
/// <item>the probe's position never changes between the two observations — the
/// whole simplifying premise, proved rather than assumed;</item>
/// <item>the cross-side enemy receives neither message, no matter how long ticks
/// are pumped past expiry;</item>
/// <item>deploying twice yields two distinct probe ids and two distinct destroy
/// events, not one reused/cached pair.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public class ProbeBroadcastTests
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
            new Gm { Name = "Probe Broadcast Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 873u, DwCookie = 0x873u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 88000000000000000ul, DrmHash = "probetest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A real probe type the deployer will lay — one whose decoded
    /// <c>Lifespan</c> falls in [2 s, 8 s), comfortably long enough to pump the
    /// creation broadcast, then pump past expiry for the destruction broadcast,
    /// without either racing.</summary>
    private static DataProbeTypeIGC RealProbeType(StaticCore core)
    {
        var t = core.ProbeTypes.Values
            .Where(pt => pt.Lifespan >= 2.0f && pt.Lifespan < 8.0f)
            .OrderBy(pt => pt.Lifespan)
            .FirstOrDefault();
        if (t.ExpendableTypeId == 0 && core.ProbeTypes.Count > 0)
            throw new InvalidOperationException("representative core has no probe with 2<=lifespan<8");
        return t;
    }

    /// <summary>⭐ A teammate must see a real, decoded EXPORT(26)/OT_probe for a
    /// probe its same-side ally deploys, and later a real PROBE_DESTROYED(85)
    /// naming that same probe — while the cross-side enemy receives neither, no
    /// matter how long we pump. The probe never moves. Deploying twice yields two
    /// distinct ids / two distinct destroy events. The probes are deployed by
    /// calling <c>Ship.DeployProbe</c> directly (the not-yet-dispatch-wired entry
    /// point).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Directly_deployed_probe_broadcasts_create_and_destroy_to_teammate_but_not_enemy()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p99_deployer", "p99_observer", "p99_enemy" };
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
            var deployer = live.Ship;
            var mission = deployer.Mission ?? throw new InvalidOperationException("deployer must have a mission");
            var pType = RealProbeType(mission.Core);

            // Gate precondition: the observer must already hold the deployer's
            // EXPORT, else the server (correctly) withholds the teammate send.
            PumpUntil(server, () => obsBot.Exports.Any(e => e.ShipId == subjectId), 8000);
            Assert.Contains(obsBot.Exports, e => e.ShipId == subjectId);

            // Fresh default seats now start in the hangar. Exercise emissions
            // from a genuinely flying source through the real undock wire path.
            bots[subject].SendDocked();
            PumpUntil(server, () => deployer.StationId is null, 4000);
            Assert.Null(deployer.StationId);
            Assert.True(deployer.Fraction > 0);

            // Deploy TWO probes directly at the current sim time. Distinct ids
            // (NextObjectId increments per call) and, since both share `now`,
            // both expire together — yielding two distinct destroys, proving no
            // cache. Capture each returned Probe to prove it never moves.
            double now = mission.LastUpdate;
            var deployed = new List<Probe>
            {
                deployer.DeployProbe(pType, now)!,
                deployer.DeployProbe(pType, now)!,
            };
            var initialPositions = deployed.ToDictionary(p => p.Id, p => p.Position);
            Assert.True(deployed.Select(p => p.Id).Distinct().Count() == 2,
                $"two deployments must yield two distinct probe ids, got [{string.Join(", ", deployed.Select(p => p.Id))}]");

            // (1) the teammate receives two decoded ExportProbe/OT_probe, each
            // with real DataProbeExport fields: p0 at the deployer's real
            // position, sideID matching the deployer's side, shipID the
            // deployer, the real probe type id, the no-target
            // otTarget/oidTarget = NA/-1, and createNow true.
            PumpUntil(server, () => obsBot.ProbeExports.Count >= 2, 6000);
            var created = obsBot.ProbeExports;
            Assert.True(created.Count >= 2, $"observer must receive ≥ 2 EXPORT(probe) messages (one per deployment), got {created.Count}");
            var p0ById = new Dictionary<short, Vector3>();
            foreach (var ex in created)
            {
                var dp = ex.Probe;
                p0ById[dp.ProbeId] = dp.P0;
                Assert.True(initialPositions.ContainsKey(dp.ProbeId), $"export {dp.ProbeId} must name a probe we deployed ({string.Join(",", initialPositions.Keys)})");
                Assert.Equal(pType.ExpendableTypeId, dp.ProbetypeId);
                Assert.Equal(subjectId, dp.ShipId);          // the real deploying ship's id
                Assert.Equal(subjectSide, dp.SideId);        // the deployer's side
                Assert.Equal(deployer.Sector?.Id, dp.ClusterId);
                Assert.True(dp.CreateNow, "a freshly-deployed probe broadcasts createNow");
                Assert.True(ex.ObjectType == ExportProbe.OtProbe, "objecttype must be OT_probe(4)");
                Assert.Equal(-1, dp.OtTarget);               // no target this slice — NA
                Assert.Equal(-1, dp.OidTarget);              // no target this slice — NA
                Assert.True(dp.ExportF, "a fresh export sets exportF");
                Assert.True((dp.P0 - initialPositions[dp.ProbeId]).Length() < 1e-4f,
                    $"export p0 {dp.P0} must be the real deployed position {initialPositions[dp.ProbeId]}");
            }
            Assert.Equal(2, p0ById.Count);                      // two DISTINCT probe ids, not one cached pair

            // (2) pumping past the decoded Lifespan reaches the destruction
            // broadcast: one real ProbeDestroyed(85) per created probe, naming
            // the same id and the same sector.
            PumpUntil(server, () => obsBot.ProbeDestroyeds.Count >= 2, 12000);
            var dest = obsBot.ProbeDestroyeds;
            Assert.True(dest.Count >= 2, $"observer must receive ≥ 2 PROBE_DESTROYED (one per expired probe), got {dest.Count}");
            foreach (var d in dest)
            {
                Assert.True(p0ById.ContainsKey(d.ProbeId),
                    $"destroy {d.ProbeId} must name a probe we saw deployed ([{string.Join(", ", p0ById.Keys)}])");
                Assert.Equal(deployer.Sector?.Id, d.ClusterId);
            }
            Assert.True(dest.Select(d => d.ProbeId).Distinct().Count() == 2,
                $"two expiries must yield two distinct destroy ids, got [{string.Join(", ", dest.Select(d => d.ProbeId))}]");

            // (3) the probe's position NEVER changed between the two observations
            // (the creation export's p0 and the destroy-time sim position) — the
            // whole "no movement" premise, proved rather than assumed.
            foreach (var d in dest)
            {
                var pr = deployed.First(pp => pp.Id == d.ProbeId);
                Vector3 creationP0 = p0ById[d.ProbeId];
                Assert.True((creationP0 - pr.Position).Length() < 1e-4f,
                    $"probe {d.ProbeId} p0 at creation {creationP0} must equal its position at expiry {pr.Position}");
                Assert.True((pr.Position - initialPositions[d.ProbeId]).Length() < 1e-4f,
                    $"probe {d.ProbeId} moved: deployed {initialPositions[d.ProbeId]} → {pr.Position}");
            }

            // (4) the cross-side enemy receives NEITHER message, no matter how
            // many ticks we pump past expiry.
            PumpUntil(server, () => false, 400);   // extra ticks past expiry for luck-free confidence
            Assert.Empty(enemyBot.ProbeExports);
            Assert.Empty(enemyBot.ProbeDestroyeds);
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }
}
