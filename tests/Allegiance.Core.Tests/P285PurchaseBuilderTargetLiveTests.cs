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
/// P285 — the live proof that a player's purchase-time Builder target choice
/// really flows wire → field → construction:
/// <list type="number">
/// <item><b>Wire → field:</b> the player's peer (a real ConnectionSession /
/// bot over the network) sends the new <c>SET_BUILDER_TARGET</c>(201) naming a
/// real Builder/Constructor hull + a chosen station type; the server sets the
/// peer ship's <see cref="Ship.BuilderStationType"/> to that exact choice (not
/// a default — it is null before).</item>
/// <item><b>Field → construction:</b> reusing P163-165's existing
/// <see cref="Sector.StartBuildingEffect"/> construction flow with that
/// configured field, the mission ticks the full 40 s build through and the
/// station that gets placed is of exactly the chosen station type — the brief's
/// "the built station matches what was chosen", evidenced by real state.</item>
/// </list>
/// Negatives: a <c>SET_BUILDER_TARGET</c> naming a NON-Builder hull (no
/// <c>c_habmBuilder</c>) or an unknown station type is soft-refused (field
/// stays null) — the server only honours a Builder-class hull purchase.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P285PurchaseBuilderTargetLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P285 Builder Target Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 463u, DwCookie = 0x463u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 66200000000000006ul, DrmHash = "p285builder",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A synthetic rock the Builder builds at, carrying the given
    /// buildability capability mask (default <c>0x08</c>, the generic/buildable
    /// bit). Identical to P165's synthetic build rock; XXV_simple has zero map
    /// asteroids, so this is the only rock reachable.</summary>
    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, short radius, short sectorId, short aabmCap = DataAsteroidIGC.AabmBuildable) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: 0f, OreMax: 0f,
        AabmCapabilities: aabmCap,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    /// <summary>Boot the in-process server + seated bot (the "player" peer) and
    /// assemble the single-sector fixture: the peer's real ship, ONE neutral
    /// asteroid the peer's builder will transform, a real Builder/Constructor
    /// hull from the core (<c>c_habmBuilder</c>), and a real buildable techbase
    /// station type (a Garrison-class, AabmBuild buildable) to assign.</summary>
    private static (Srv Server, Mission Mission, Sector Sector, Ship PeerShip, Asteroid Rock, DataHullTypeIGC BuilderHull, DataStationTypeIGC StationType)
        BootFixture(out FltBot bot)
    {
        var server = new Srv();
        var core = Core();
        Mission? mission = null;
        Sector? sector = null;
        Ship? peer = null;
        Asteroid? rock = null;
        DataHullTypeIGC? builderHull = null;
        DataStationTypeIGC? stationType = null;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            short peerSide = m.Sides[0].Id;

            // A real Builder/Constructor hull straight from the core
            // (c_habmBuilder = 0x8000) — the hull the player buys in the shop.
            builderHull = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmBuilder));

            // A real buildable techbase to assign: a Garrison-class station
            // type whose AabmBuild the generic/buildable rock satisfies (P280).
            stationType = core.StationTypes.Values.First(s =>
                s.ClassId == 0 && (s.AabmBuild & DataAsteroidIGC.AabmBuildable) != 0);

            rock = new Asteroid(MkAsteroid(6155, new Vector3(602f, 431f, 465f), 60, sector.Id, stationType!.Value.AabmBuild));
            m.PlaceAsteroid(rock, sector);

            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            peer = new Ship(id, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peer, sector);
            return (peer, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby (P180 pattern)
        bot = new FltBot();
        var b = bot;                       // `bot` is an out param — unusable inside a lambda
        b.Connect(server.BoundPort, "p285_builder");
        b.SendLogonRequest(Request("p285_builder"));
        Pump(server, () => mission is not null && b.ShipAssignment is not null, 4000);

        Assert.NotNull(mission); Assert.NotNull(sector); Assert.NotNull(peer);
        Assert.NotNull(rock); Assert.NotNull(builderHull); Assert.NotNull(stationType);
        return (server!, mission!, sector!, peer!, rock!, builderHull!.Value, stationType!.Value);
    }

    /// <summary>⭐ Primary leg: the player (peer) sends SET_BUILDER_TARGET for a
    /// real Builder hull + chosen station type over the real wire; the server
    /// sets the ship's BuilderStationType to that choice; then, reusing the
    /// existing P163-165 construction flow with that configured field, the
    /// mission ticks the 40 s build and the placed station is of the chosen
    /// type — choice → spawn → construction → match, evidenced by real state.</summary>
    [Fact]
    public void Purchased_builder_wire_target_sets_field_and_builds_chosen_station()
    {
        var (srv, mission, sector, peer, rock, builderHull, stationType) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            // The peer ship must not carry a Builder target yet — proving the
            // value that arrives is the player's CHOICE, not a preset/default.
            Assert.Null(peer.BuilderStationType);

            // Wire → field: the player's real connection sends the choice.
            bot.SendSetBuilderTarget(builderHull.HullId, stationType.StationTypeId);
            Pump(srv, () => peer.BuilderStationType is not null, 2000);

            Assert.NotNull(peer.BuilderStationType);
            Assert.Equal(stationType.StationTypeId, peer.BuilderStationType!.Value.StationTypeId);
            Assert.Equal(stationType.Name, peer.BuilderStationType!.Value.Name);
            Console.WriteLine($"P285-WIRE: peer ship {peer.Id} <- SET_BUILDER_TARGET hull {builderHull.HullId} '{builderHull.Name}' (c_habmBuilder) -> BuilderStationType '{peer.BuilderStationType.Value.Name}' (type {peer.BuilderStationType.Value.StationTypeId})");

            // Field → construction: reuse the EXISTING P163-165 construction
            // flow with the configured field, and confirm the built station
            // matches the choice.
            var cfg = peer.BuilderStationType!.Value;
            sector.StartBuildingEffect(peer, rock, cfg, sector.LastUpdate);
            var effect = Assert.Single(sector.BuildingEffects);
            Assert.Equal(cfg.StationTypeId, effect.StationType.StationTypeId);

            int tc = 0;
            const int MaxTicks = 4600;
            while (tc < MaxTicks && sector.BuildingEffects.Count > 0) { srv.TickSim(); tc++; Thread.Sleep(1); }
            Assert.True(tc < MaxTicks, $"build must complete within {MaxTicks} ticks (elapsed {tc})");

            Assert.DoesNotContain(rock, sector.Mineable);          // the rock became a station
            var built = sector.StationRuntimes.Where(s => PositionsMatch(s.Data.Position, RockPos(rock))).ToList();
            var builtStation = Assert.Single(built);
            Assert.Equal(cfg.StationTypeId, builtStation.Type.StationTypeId);
            Assert.Equal(cfg.StationTypeId, builtStation.Data.StationTypeId);
            Assert.Equal(stationType.Name, builtStation.Data.Name);
            Console.WriteLine($"P285-BUILD: completed -> station {builtStation.Data.StationId} '{builtStation.Data.Name}' (type {builtStation.Data.StationTypeId}) = the player's chosen target");
        }
    }

    /// <summary>⭐ Negative: a SET_BUILDER_TARGET naming a NON-Builder hull
    /// (no <c>c_habmBuilder</c>) is soft-refused — the field stays null (the
    /// server only honours a Builder-class hull purchase).</summary>
    [Fact]
    public void Non_builder_hull_target_is_ignored()
    {
        var (srv, mission, _, peer, _, _, stationType) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            var core = mission.Core;
            var nonBuilder = core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmBuilder));
            Assert.False(nonBuilder.HasCapability(DataHullTypeIGC.HabmBuilder));

            bot.SendSetBuilderTarget(nonBuilder.HullId, stationType.StationTypeId);
            Pump(srv, () => peer.BuilderStationType is not null, 1500);

            Assert.Null(peer.BuilderStationType);   // refused — not a Builder hull
            Console.WriteLine($"P285-REJECT: SET_BUILDER_TARGET for non-builder hull {nonBuilder.HullId} '{nonBuilder.Name}' ignored — BuilderStationType stays null");
        }
    }

    /// <summary>⭐ Negative: a SET_BUILDER_TARGET naming an unknown station type
    /// is soft-refused — the field stays null.</summary>
    [Fact]
    public void Unknown_station_type_target_is_ignored()
    {
        var (srv, _, _, peer, _, builderHull, _) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            const short bogusStationType = 6666;   // not in the core
            bot.SendSetBuilderTarget(builderHull.HullId, bogusStationType);
            Pump(srv, () => peer.BuilderStationType is not null, 1500);

            Assert.Null(peer.BuilderStationType);
            Console.WriteLine($"P285-REJECT: SET_BUILDER_TARGET for unknown station type {bogusStationType} ignored — BuilderStationType stays null");
        }
    }

    private static bool PositionsMatch(float[] a, Vector3 b) =>
        a is { Length: 3 } &&
        Math.Abs(a[0] - b.X) < 0.01f && Math.Abs(a[1] - b.Y) < 0.01f && Math.Abs(a[2] - b.Z) < 0.01f;

    private static Vector3 RockPos(Asteroid rock) =>
        new(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]);
}
