using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>Real loopback peers, fixed showcase inputs and clock. Network handshake is outside
/// the transcript; every frame in the seeded combat/research tape is compared without filtering.</summary>
[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P1000CivStrategyLoopbackTests(ITestOutputHelper output)
{
    private const string BaseDigest = "06BAB376D13E480CFAECDC83A2D4CB38F28F400588FE705D186CED0C7FCD4611";
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed record ReplayResult(string Digest, int Frames, long Bytes, string Plans, string Log, int Hits);

    private ReplayResult Replay(bool enabled)
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter(); Console.SetOut(TextWriter.Synchronized(log));
        try
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false, SimulationRandom = new Random(1000) };
            var config = new ServerConfig
            {
                ArtPath = Catalog.ArtRoot, Port = 0, BenchmarkSeed = 1000, SideSelectAutoSeatSeconds = 30,
                Showcase = new() { TechMode = "research", ResearchTimeScale = 16 },
                LiveSettings = new() { TestUnlock = new() { Enabled = false }, TestHarness = new() { Enabled = false } },
                Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = false, ShipUpdateModel = "native" },
                Games = [new() { Name = "P1000 seeded showcase", Map = "XXV_simple.igc", Teams = 2,
                    CivIds = [18, 4], CivSeed = 1000, SideNames = ["Alpha", "Bravo"], InitialMinersPerTeam = 0,
                    MapGen = new() { Type = "SingleRing", Seed = 1000 },
                    ResearchPlans = new() { [0] = "generated", [1] = "generated" } }]
            };
            // This reflection keeps the OFF replay source executable on the base revision.
            var bots = typeof(ServerConfig).GetProperty("Bots")?.GetValue(config);
            if (enabled) { Assert.NotNull(bots); bots.GetType().GetProperty("CivStrategy")!.SetValue(bots, true); }
            double now = 0; server.TestUpdateCadenceClock = () => now;
            server.Boot(config);
            using var a = P660NextMatchLiveTests.Connect(server, "P1000-A", 100001);
            using var b = P660NextMatchLiveTests.Connect(server, "P1000-B", 100002);
            var connections = ((IDictionary)typeof(Srv).GetField("_peerShips", Private)!.GetValue(server)!).Keys
                .Cast<ConnectionId>().OrderBy(c => c.Value).ToArray();
            Assert.Equal(2, connections.Length);
            var mission = server.Missions[0];
            // The socket admission pump advances a variable number of NOTSTARTED ticks.
            // No game simulation has run; reset its idle clock before the fixed tape.
            Assert.Equal(MissionStage.NotStarted, mission.Stage);
            typeof(Mission).GetProperty("LastUpdate")!.SetValue(mission, 0d);
            typeof(Srv).GetField("_simNow", Private)!.SetValue(server, 0d);
            foreach (var old in server.Ships.ToArray()) old.Ship.Sector!.Terminate(old.Ship);
            var population = new List<(ConnectionId Conn, Ship Ship, Sector Sector)>();
            var sector = mission.Sectors[0];
            for (short side = 0; side < 2; side++)
            {
                var owner = mission.Sides[side];
                var selected = TechLoadoutSelector.Select(mission.Core, owner.Civilization.CivilizationId,
                    Showcase.HullRole.Fighter, owner.Techs, owner.Civilization.InitialStationTypeId)
                    ?? TechLoadoutSelector.Select(mission.Core, owner.Civilization.CivilizationId,
                        Showcase.HullRole.Scout, owner.Techs, owner.Civilization.InitialStationTypeId);
                Assert.NotNull(selected);
                var orientation = Orientation.Identity; if (side == 1) orientation.Yaw(MathF.PI);
                var ship = new Ship((short)(10000 + side), mission.Core.HullTypes[selected.HullId],
                    new(50000, 0, side == 0 ? 200 : -200), Vector3.Zero, orientation) { SideId = side };
                mission.PlaceShip(ship, sector);
                foreach (var part in selected.Parts)
                {
                    var type = new ResearchTree(mission.Core, owner.Civilization.CivilizationId).Parts.Single(p => p.PartId == part.PartId);
                    ship.MountPart(type, part.Amount, unchecked((byte)part.MountId), 255);
                }
                ship.StateM = ShipControlState.AllWeapons;
                population.Add((connections[side], ship, sector));
            }
            mission.Random = new Random(1000);
            server.TestResetSnapshotReplay(); server.TestSeedPeers(population);
            mission.Stage = MissionStage.Started;
            string plans = JsonSerializer.Serialize(mission.ResearchPlans);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int frames = 0, hits = 0; long bytes = 0;
            server.TestPublicationCapture = (peer, packet, key, guaranteed) =>
            {
                hash.AppendData(BitConverter.GetBytes(peer.Value)); hash.AppendData(BitConverter.GetBytes(key));
                hash.AppendData(BitConverter.GetBytes(guaranteed)); hash.AppendData(BitConverter.GetBytes(packet.Length));
                hash.AppendData(packet); frames++; bytes += packet.Length;
                foreach (var frame in FedPacket.Unpack(packet))
                {
                    int fmid = FedMessageHeader.Read(frame).Fmid;
                    if (fmid == ProjectileHullHit.Fmid) hits++;
                    else if (fmid == GunfireBatch.Fmid) hits += GunfireBatch.Decode(frame).Records.OfType<ProjectileHullHit>().Count();
                }
            };
            try
            {
                for (uint tick = 0; tick < 1400; tick++)
                {
                    now = (tick + 1) * .05;
                    typeof(Srv).GetField("_simNow", Private)!.SetValue(server, now);
                    hash.AppendData(BitConverter.GetBytes(tick));
                    // Scripted commander funding and pilot replenishment are identical inputs.
                    // No flag-dependent selection or hidden opponent state enters the tape.
                    if (tick == 0)
                        foreach (var side in mission.Sides)
                        {
                            var first = mission.ResearchPlans[side.Id].Steps[0];
                            var bucket = side.Buckets.Single(b => b.Development?.DevelopmentId == first.Id);
                            bucket.AddMoney(bucket.Price);
                        }
                    foreach (var (_, ship, _) in population)
                    { ship.SetFraction(1); ship.SetShieldFraction(1); ship.Ammo = ship.HullType.MaxAmmo; ship.Energy = ship.EffectiveMaxEnergy(); }
                    server.TestProjectRecordedTick(tick, true, surge: () => mission.Update(now));
                    typeof(Srv).GetMethod("FlushCivStrategy", Private)?.Invoke(server, [mission, now, false]);
                    server.FlushBroadcastSnapshots();
                }
                typeof(Srv).GetMethod("FlushCivStrategy", Private)?.Invoke(server, [mission, now, true]);
            }
            finally { server.TestPublicationCapture = null; }
            Assert.True(a.Connected && b.Connected);
            Assert.Equal(0, server.SimPassStageFailures);
            string digest = Convert.ToHexString(hash.GetHashAndReset());
            output.WriteLine($"enabled={enabled} digest={digest} frames={frames} bytes={bytes} hits={hits}");
            return new(digest, frames, bytes, plans, log.ToString(), hits);
        }
        finally { Console.SetOut(saved); }
    }

    [SkippableFact]
    public void Seeded_loopback_off_is_pinned_to_base()
    {
        var off = Replay(false);
        Assert.DoesNotContain("[civ-strategy]", off.Log);
        Assert.True(off.Hits > 0);
        Assert.Equal(BaseDigest, off.Digest);
    }

    [SkippableFact]
    public void On_and_off_have_identical_plans_and_every_combat_frame_with_receipts()
    {
        var off = Replay(false); var on = Replay(true);
        Assert.Equal(off.Plans, on.Plans);
        Assert.Equal(off.Frames, on.Frames); Assert.Equal(off.Bytes, on.Bytes); Assert.Equal(off.Digest, on.Digest);
        Assert.True(on.Hits > 0);
        var receipts = on.Log.Split('\n').Where(l => l.StartsWith("[civ-strategy] "))
            .Select(l => JsonDocument.Parse(l[15..]).RootElement.Clone()).ToArray();
        Assert.Equal(2, receipts.Count(r => r.GetProperty("kind").GetString() == "choice"));
        Assert.Contains(receipts, r => r.GetProperty("kind").GetString() == "research");
        var windows = receipts.Where(r => r.GetProperty("kind").GetString() == "window").ToArray();
        Assert.Equal(2, windows.Length);
        Assert.Contains(windows, w => w.GetProperty("damage").EnumerateArray().Any(d => d.GetProperty("source").GetInt32() >= 0));
        Assert.Contains(windows, w => w.GetProperty("grants").GetArrayLength() > 0);
        Assert.Equal(2, windows.SelectMany(w => w.GetProperty("sides").EnumerateArray()).Sum(s => s.GetProperty("paydays").GetInt64()));
        Assert.All(receipts, r => Assert.True(r.GetProperty("match").GetInt64() > 0));
        string evidence = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.scratch/p1000"));
        Directory.CreateDirectory(evidence); File.WriteAllText(Path.Combine(evidence, "loopback-receipts.log"), on.Log);
    }
}
