using System.Buffers.Binary;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
// 'Server'/'Bot' are also namespaces — alias to unambiguous identifiers (same
// pattern as MiningTests / BotFlightTests).
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// The P64 deliverable — real asteroid data reaching a wire peer. Two proofs,
/// mirroring how stations/warps were validated in P35:
/// (1) <see cref="DataAsteroidIGC.Encode"/> round-trips <c>Decode</c> exactly
/// over every corpus asteroid record (so the codec is internally consistent);
/// and (2) a loopback TCP <see cref="FltBot"/> boot on the real
/// <c>m2xspiderw.igc</c> map (134 asteroids) receives an <c>ExportAsteroid</c>
/// (<c>objecttype=5</c>, OT_asteroid) for the asteroids its <b>side has seen</b>
/// (P797: the join export is per side, native <c>fsmission.cpp:4206</c>), each
/// byte-for-byte identical to the map's own parsed <c>DataAsteroidIGC</c> — the
/// exact static snapshot <c>UndockMinersAndStartMining</c>'s
/// <c>GetAsteroids()</c> search consumes. This is the final server-side gap
/// before the live-VM "dispatch a miner" pass.
/// </summary>
public class AsteroidExportTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private const string SpiderMap = "m2xspiderw.igc";

    // ── (1) codec round-trip over the whole corpus ────────────────────────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void DataAsteroidIGC_encode_roundtrips_across_corpus()
    {
        Require.Artwork();
        int n = 0;
        foreach (var (file, v) in PayloadDecodeTests.Asteroids())
        {
            n++;
            var encoded = v.Encode();
            Assert.Equal(DataAsteroidIGC.Size, encoded.Length);
            var back = DataAsteroidIGC.Decode(encoded);
            // record-struct equality compares float[] by reference — prove the
            // round-trip component-wise instead, covering every payload byte.
            Assert.Equal(v.Signature, back.Signature);
            Assert.Equal(v.Position, back.Position);
            Assert.Equal(v.Up, back.Up);
            Assert.Equal(v.Forward, back.Forward);
            Assert.Equal(v.Rotation, back.Rotation);
            Assert.Equal(v.Ore, back.Ore);
            Assert.Equal(v.OreMax, back.OreMax);
            Assert.Equal(v.AabmCapabilities, back.AabmCapabilities);
            Assert.Equal(v.AsteroidId, back.AsteroidId);
            Assert.Equal(v.HitPoints, back.HitPoints);
            Assert.Equal(v.Radius, back.Radius);
            Assert.Equal(v.ModelName, back.ModelName);
            Assert.Equal(v.TextureName, back.TextureName);
            Assert.Equal(v.IconName, back.IconName);
            Assert.Equal(v.ClusterId, back.ClusterId);
            Assert.Equal(v.Name, back.Name);
            Assert.Equal(v.Fraction, back.Fraction);
        }
        Assert.True(n >= 965, $"expected ~973 corpus asteroid records to round-trip, saw {n}");
    }

    // ── (2) real map reaches a wire peer over loopback TCP ───────────────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Bot_receives_ExportAsteroid_for_every_asteroid_in_m2xspiderw()
    {
        Require.Artwork();
        // Parse the real map independently (no server internals) to know the
        // expected set of asteroids the server must send.
        var map = MissionMap.Load(IgcReader.ParseFile(
            Path.Combine(ArtRoot, SpiderMap)));
        var expected = map.Asteroids;
        Assert.True(expected.Count >= 100, $"m2xspiderw should carry ~134 asteroids, saw {expected.Count}");
        Assert.Contains(expected, a => a.Name.StartsWith("He"));
        // every expected rock's encoded bytes (bijection key; Decode-trimmed
        // names re-encode identically, so bytes are stable).
        var expectedBytes = expected.ToDictionary(
            a => Convert.ToHexString(a.Encode()), _ => true);

        using var server = new Srv();
        // P797 — the ship must be SEATED (native exports map objects per side, and
        // an unseated peer has no side to gate on) and must actually scan the
        // sector's rocks. Place it, on side 0, inside a rock-bearing sector next
        // to its first few asteroids, with a hull that has a real scanner range.
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var sector = mission.Sectors.First(x => x.Mineable.Count > 0);
            var rock = sector.Mineable.First(a => a.Data.Position is { Length: 3 });
            var rp = rock.Data.Position!;
            var hull = core.HullTypes.Values.Where(h => h.ScannerRange >= 3000f)
                .OrderBy(h => h.HitPoints).First();
            var ship = new Ship(shipId, hull,
                new Vector3(rp[0] + 200f, rp[1], rp[2]), Vector3.Zero, Orientation.Identity)
            { SideId = mission.Sides[0].Id };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(new Cfg
        {
            ArtPath = ArtRoot,
            StaticCore = "PCore001.igc",
            Port = 0,
            UpdatesPerSecond = 20,
            Games =
            [
                new Gm
                {
                    Name = "Spider Mission", Map = SpiderMap, Teams = 2,
                    CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
                },
            ],
        });

        var bot = new FltBot();
        try
        {
            bot.Connect(server.BoundPort, "ASTROID");
            bot.SendLogonRequest(SampleRequest("ASTROID"));

            // pump until the bot is assigned a ship (post join-burst), then let
            // it drain the exports that arrived with SET_CLUSTER.
            int waited;
            for (waited = 0; waited < 20000 && bot.ShipAssignment is null; waited++)
            { server.TickSim(); Thread.Sleep(1); }
            Assert.True(waited < 20000, "bot never got a ship assignment");
            Thread.Sleep(300);

            // P797 — the join export is PER SIDE (native fsmission.cpp:4206
            // `if (pmdllink->data()->SeenBySide(pside))`), so a peer receives the
            // rocks its own side has seen, not all 134 of the map. What this test
            // still proves byte-for-byte is the wire path: every received export
            // is a real map record, exactly once, and the set equals the mission's
            // own latched SeenBySide answer for the peer's side.
            var received = bot.AsteroidExports;
            Assert.NotEmpty(received);
            foreach (var ex in received)
            {
                Assert.Equal(ExportAsteroid.OtAsteroid, ex.ObjectType);
                var bytes = Convert.ToHexString(ex.Asteroid.Encode());
                Assert.True(expectedBytes.Remove(bytes),
                    $"received an unexpected asteroid (cluster {ex.Asteroid.ClusterId}, {ex.Asteroid.Name}, {expectedBytes.Count} unmatched remain)");
            }
            short side = server.Ships.First(x => x.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship.SideId!.Value;
            var mission = server.Missions[0];
            var seen = mission.Sectors.SelectMany(x => x.Mineable)
                .Where(a => a.SeenBySide(side))
                .Select(a => a.Data.AsteroidId).OrderBy(x => x).ToArray();
            Assert.Equal(seen, received.Select(r => r.Asteroid.AsteroidId).Distinct().OrderBy(x => x).ToArray());
            Assert.True(received.Count < expected.Count,
                $"a fresh side must not receive all {expected.Count} map rocks (got {received.Count})");
            Assert.Contains(received, r => r.Asteroid.Name.StartsWith("He"));
        }
        finally
        {
            bot.Shutdown();
            server.Shutdown();
        }
    }

    private static LogonRequest SampleRequest(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 889u, DwCookie = 0xFACEu,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 71100000000000000ul, DrmHash = "aexp",
    };
}
