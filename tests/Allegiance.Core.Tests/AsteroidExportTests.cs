using System.Buffers.Binary;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
// 'Server'/'Bot' are also namespaces — alias to unambiguous identifiers (same
// pattern as MiningTests / BotFlightTests).
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// The P64 deliverable — real asteroid data reaching a wire peer. Two proofs,
/// mirroring how stations/warps were validated in P35:
/// (1) <see cref="DataAsteroidIGC.Encode"/> round-trips <c>Decode</c> exactly
/// over every corpus asteroid record (so the codec is internally consistent);
/// and (2) a loopback TCP <see cref="FltBot"/> boot on the real
/// <c>m2xspiderw.igc</c> map (134 asteroids) receives an <c>ExportAsteroid</c>
/// (<c>objecttype=5</c>, OT_asteroid) for every asteroid of every sector, each
/// byte-for-byte identical to the map's own parsed <c>DataAsteroidIGC</c> — the
/// exact static snapshot <c>UndockMinersAndStartMining</c>'s
/// <c>GetAsteroids()</c> search consumes. This is the final server-side gap
/// before the live-VM "dispatch a miner" pass.
/// </summary>
public class AsteroidExportTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";
    private const string SpiderMap = "m2xspiderw.igc";

    // ── (1) codec round-trip over the whole corpus ────────────────────────
    [Fact]
    public void DataAsteroidIGC_encode_roundtrips_across_corpus()
    {
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
    [Fact]
    public void Bot_receives_ExportAsteroid_for_every_asteroid_in_m2xspiderw()
    {
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
            for (waited = 0; waited < 3000 && bot.ShipAssignment is null; waited++)
            { server.TickSim(); Thread.Sleep(1); }
            Assert.True(waited < 3000, "bot never got a ship assignment");
            Thread.Sleep(300);

            var received = bot.AsteroidExports;
            Assert.Equal(expected.Count, received.Count);
            foreach (var ex in received)
            {
                Assert.Equal(ExportAsteroid.OtAsteroid, ex.ObjectType);
                var bytes = Convert.ToHexString(ex.Asteroid.Encode());
                Assert.True(expectedBytes.Remove(bytes),
                    $"received an unexpected asteroid (cluster {ex.Asteroid.ClusterId}, {ex.Asteroid.Name}, {expectedBytes.Count} unmatched remain)");
            }
            Assert.Empty(expectedBytes);   // every map rock accounted for, no extras
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
