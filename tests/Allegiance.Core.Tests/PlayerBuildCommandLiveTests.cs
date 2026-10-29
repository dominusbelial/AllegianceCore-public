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
/// P422 — the real player-issuable Build command (<c>c_cidBuild</c> =
/// <see cref="OrderChange.CidBuild"/>, the native constant this port never had —
/// cf. Ship.cs:1542-1544). A real Builder-hulled peer sends the Build order over
/// the real wire (<c>FM_CS_ORDER_CHANGE</c> with an <c>OT_asteroid</c> target);
/// the server validates it against the native <c>LegalCommand</c> c_cidBuild gate
/// (Builder-role pilot + buildable asteroid capability, shipIGC.h:1695-1707) and,
/// when legal, starts the timed construction immediately (the port's established
/// no-travel-before-build gate, P163-165). Live-loopback proofs:
/// <list type="number">
/// <item><b>Legal full arc</b> — a real Builder hull (c_habmBuilder) with a
/// configured station type issues a legal Build; the server validates it
/// (native LegalCommand gates) and starts the timed 40 s construction
/// (<see cref="Model.Sector.StartBuildingEffect"/>) immediately (the port's
/// established no-travel-before-build gate); tick to completion → the rock
/// is replaced by a real station of the configured type, the Builder ship is
/// consumed, and a friendly ship can dock at the new station.</item>
/// <item><b>Non-Builder-hull refused</b> — the gate that only a Builder-role
/// pilot may build.</item>
/// <item><b>Non-asteroid target refused</b> — the OT_asteroid-only gate.</item>
/// <item><b>Capability-mismatch asteroid refused</b> — not every rock is
/// buildable; an asteroid lacking the required buildable bit is refused.</item>
/// <item><b>No configured station type refused</b> — a Builder with nothing to
/// build cannot build.</item>
/// </list>
/// Harness mirrors P285/P165 (booted in-process server + seated bot).
/// </summary>
[Collection("LiveNetwork")]
public sealed class PlayerBuildCommandLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    /// <summary>Codec: an <see cref="OrderChange"/> carrying the new
    /// <c>c_cidBuild</c> command id (<see cref="OrderChange.CidBuild"/>=9,
    /// igc.h:910) round-trips its full shape — the wire path the live tests
    /// drive sends exactly this and the server decodes it in
    /// <c>HandleOrderChange</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void OrderChange_build_roundtrips_cidBuild_constant()
    {
        Require.Artwork();
        var msg = new OrderChange
        {
            ShipID = 12,
            CommandId = OrderChange.CidBuild,
            Command = OrderChange.CmdAccepted,
            ObjectType = IgcObjectType.Asteroid,
            ObjectID = 6155,
        };
        var bytes = msg.Encode();
        Assert.Equal(OrderChange.FixedSize, bytes.Length);
        var dec = OrderChange.Decode(bytes);
        Assert.Equal(msg.ShipID, dec.ShipID);
        Assert.Equal(OrderChange.CidBuild, dec.CommandId);
        Assert.Equal(OrderChange.CmdAccepted, dec.Command);
        Assert.Equal(IgcObjectType.Asteroid, dec.ObjectType);
        Assert.Equal((short)6155, dec.ObjectID);
        Console.WriteLine("P422-CODEC: ORDER_CHANGE with commandID=c_cidBuild(9) + OT_asteroid target round-tripped");
    }

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
            new Gm { Name = "P422 Player Build Command Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 762u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 66400000000000002ul, DrmHash = "p422build",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, short radius, short sectorId, short aabmCap) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: 0f, OreMax: 0f,
        AabmCapabilities: aabmCap,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    private static bool PositionsMatch(float[] a, Vector3 b) =>
        a is { Length: 3 } &&
        Math.Abs(a[0] - b.X) < 0.01f && Math.Abs(a[1] - b.Y) < 0.01f && Math.Abs(a[2] - b.Z) < 0.01f;

    private static Vector3 RockPos(Asteroid rock) =>
        new(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]);

    /// <summary>Boot the in-process server + seated bot (the "player" peer on
    /// side 0) and assemble the single-sector fixture: the peer's real ship, a
    /// real Builder/Constructor hull (<c>c_habmBuilder</c>) + a real non-Builder
    /// hull for the negative gate, a real buildable techbase station type, and
    /// BOTH a compatible rock and a wrong-type (capability-mismatch) rock.</summary>
    private static (Srv Server, Mission Mission, Sector Sector, Ship Peer, DataHullTypeIGC BuilderHull, DataHullTypeIGC NonBuilderHull, DataStationTypeIGC StationType, Asteroid Rock, Asteroid WrongRock, short PeerSide)
        BootFixture(out FltBot bot)
    {
        var server = new Srv();
        var core = Core();
        Mission? mission = null;
        Sector? sector = null;
        Ship? peer = null;
        DataHullTypeIGC? builderHull = null;
        DataHullTypeIGC? nonBuilderHull = null;
        DataStationTypeIGC? stationType = null;
        Asteroid? rock = null;
        Asteroid? wrongRock = null;
        short peerSide = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            peerSide = m.Sides[0].Id;

            // Real Constructor/Builder hull (c_habmBuilder = 0x8000).
            builderHull = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmBuilder));
            // A real hull that is NOT a Builder, for the negative gate.
            nonBuilderHull = core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmBuilder));

            // A real buildable techbase (Garrison-class, AabmBuild buildable).
            stationType = core.StationTypes.Values.First(s =>
                s.ClassId == 0 && (s.AabmBuild & DataAsteroidIGC.AabmBuildable) != 0);

            // A compatible rock (carries the station's AabmBuild) + a wrong-type
            // rock whose capability shares no bit with it (provably not buildable).
            rock = new Asteroid(MkAsteroid(6221, new Vector3(602f, 431f, 465f), 60, sector.Id, stationType!.Value.AabmBuild));
            short wrongCap = stationType.Value.AabmBuild switch
            {
                DataAsteroidIGC.AabmBuildable => (short)(DataAsteroidIGC.AabmSpecial << 2),  // carbon 0x40
                (short)(DataAsteroidIGC.AabmSpecial << 2) => DataAsteroidIGC.AabmBuildable,
                (short)(DataAsteroidIGC.AabmSpecial << 0) => (short)(DataAsteroidIGC.AabmSpecial << 1),
                (short)(DataAsteroidIGC.AabmSpecial << 1) => (short)(DataAsteroidIGC.AabmSpecial << 0),
                _ => DataAsteroidIGC.AabmBuildable,
            };
            Assert.Equal(0, stationType.Value.AabmBuild & wrongCap);   // provably disjoint
            wrongRock = new Asteroid(MkAsteroid(6222, new Vector3(610f, 428f, 461f), 60, sector.Id, wrongCap));
            m.PlaceAsteroid(rock, sector);
            m.PlaceAsteroid(wrongRock, sector);

            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            peer = new Ship(id, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peer, sector);
            return (peer, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby (P180 pattern)
        bot = new FltBot();
        var b = bot;                       // `bot` is an out param — unusable inside a lambda
        b.Connect(server.BoundPort, "p422_build");
        b.SendLogonRequest(Request("p422_build"));
        Pump(server, () => mission is not null && b.ShipAssignment is not null, 4000);

        Assert.NotNull(mission); Assert.NotNull(sector); Assert.NotNull(peer);
        Assert.NotNull(builderHull); Assert.NotNull(nonBuilderHull);
        Assert.NotNull(stationType); Assert.NotNull(rock); Assert.NotNull(wrongRock);
        return (server!, mission!, sector!, peer!, builderHull!.Value, nonBuilderHull!.Value, stationType!.Value, rock!, wrongRock!, peerSide);
    }

    /// <summary>Give the peer a real Builder hull + a configured station type
    /// (P470: the P285 purchase-time wire is gone, so set the plain-settable
    /// field directly — the same value a bucket-spawned drone gets at spawn),
    /// returning the configured type. Guard: the peer's stock hull must not
    /// already be a Builder, so the non-Builder refusal test can rely on the
    /// stock hull.</summary>
    private static DataStationTypeIGC MakeBuilderPeer(Ship peer, DataHullTypeIGC builderHull, DataStationTypeIGC stationType)
    {
        Assert.False(peer.HullType.HasCapability(DataHullTypeIGC.HabmBuilder),
            "fixture peer's stock hull must not be a Builder hull");
        peer.ChangeHull(builderHull);                      // real Constructor hull in place (Ship.ChangeHull = SetBaseHullType)
        Assert.True(peer.HullType.HasCapability(DataHullTypeIGC.HabmBuilder));
        peer.BuilderStationType = stationType;
        Assert.NotNull(peer.BuilderStationType);
        return peer.BuilderStationType!.Value;
    }

    /// <summary>⭐ Primary leg: the real Builder-hulled peer issues a legal Build
    /// command against the compatible rock over the real wire; the server
    /// validates it (native LegalCommand gates), starts the timed 40 s
    /// construction immediately (the port's established no-travel-before-build
    /// gate), and ticking to completion the rock → station transformation fires,
    /// the Builder ship is consumed, and a friendly ship docks at the new
    /// station. The full native construction arc, live.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Legal_build_starts_construction_and_creates_dockable_station()
    {
        Require.Artwork();
        var (srv, mission, sector, peer, builderHull, _, stationType, rock, _, peerSide) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            var cfg = MakeBuilderPeer(peer, builderHull, stationType);
            Assert.False(peer.IsBuilding);
            Assert.Empty(sector.BuildingEffects);

            // ── Issue the Build command over the real wire ───────────────────
            // command=c_cmdAccepted (SetAccepted), commandID=c_cidBuild,
            // objectType=OT_asteroid, objectID=the compatible rock.
            bot.SendOrderChange(peer.Id, OrderChange.CidBuild, OrderChange.CmdAccepted, IgcObjectType.Asteroid, rock.Data.AsteroidId);
            Pump(srv, () => sector.BuildingEffects.Count > 0, 2000);
            var effect = Assert.Single(sector.BuildingEffects);
            Assert.Same(rock, effect.Target);                 // built at the commanded rock
            Assert.Equal(cfg, effect.StationType);            // builds the configured station type
            Assert.True(peer.IsBuilding);                     // construction underway
            Console.WriteLine($"P422-START: peer {peer.Id} (Builder hull '{peer.HullType.Name}') BUILD accepted → construction of '{cfg.Name}' started at asteroid {rock.Data.AsteroidId} (effect completes @+{effect.CompletionTime - mission.LastUpdate:0}s)");

            // ── Tick to completion (~40 s) → rock → station, builder consumed ──
            int tc = 0;
            const int MaxTicks = 4600;
            while (tc < MaxTicks && sector.BuildingEffects.Count > 0) { srv.TickSim(); tc++; Thread.Sleep(1); }
            Assert.True(tc < MaxTicks, $"build must complete within {MaxTicks} ticks (elapsed {tc})");

            var rp = RockPos(rock);
            Assert.DoesNotContain(rock, sector.Mineable);     // the rock became a station
            Assert.DoesNotContain(peer, sector.Models);       // the Builder consumed itself
            Assert.Empty(sector.BuildingEffects);
            Assert.False(peer.IsBuilding);

            var built = sector.StationRuntimes.Where(s => PositionsMatch(s.Data.Position, rp)).ToList();
            var builtStation = Assert.Single(built);
            Assert.Equal(cfg.StationTypeId, builtStation.Type.StationTypeId);
            Assert.Equal(cfg.StationTypeId, builtStation.Data.StationTypeId);
            Assert.Equal(peerSide, builtStation.SideId);      // the Builder's side
            Assert.Contains(builtStation.Data, sector.Stations);   // static record registered = in the dock-pass subject set
            Console.WriteLine($"P422-BUILD: completed → rock {rock.Data.AsteroidId} gone, station {builtStation.Data.StationId} '{builtStation.Data.Name}' (type {cfg.StationTypeId}, side {peerSide}) at {rp}, builder {peer.Id} consumed");

            // ── Dockable: a friendly ship can dock at the new station ────────
            // ResolveDockCollisions iterates sector.Stations (the built station
            // IS in it) and docks a same-side ship within DockRadius (200).
            var dockShip = srv.SpawnNpcShip(mission, sector, peerSide, new Vector3(rp.X, rp.Y, rp.Z + 2f), null);
            // Park it inside the new station's landing bay (same side → friendly).
            dockShip.Position = DockTestPlacement.Inside(mission, builtStation.Data, dockShip.HullType);
            Assert.Null(dockShip.StationId);
            Pump(srv, () => dockShip.StationId == builtStation.Data.StationId, 2000);
            Assert.Equal(builtStation.Data.StationId, dockShip.StationId);   // ★ docked at the freshly-built station
            Console.WriteLine($"P422-DOCK: friendly ship {dockShip.Id} docked at newly-built station {builtStation.Data.StationId} '{builtStation.Data.Name}' — dockable confirmed");
        }
    }

    /// <summary>⭐ Negative: a Build command from a ship whose hull is NOT a
    /// Builder (no c_habmBuilder) is refused exactly as native's
    /// LegalCommand rejects a non-c_ptBuilder pilot (shipIGC.h:1699-1702) — no
    /// commitment, no effect.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Build_from_non_builder_hull_is_refused()
    {
        Require.Artwork();
        var (srv, _, sector, peer, builderHull, nonBuilderHull, stationType, rock, _, _) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            // Configure a Builder target so the ONLY failing gate is
            // the Builder-role one — the discriminating test.
            peer.ChangeHull(builderHull);
            peer.BuilderStationType = stationType;
            Assert.NotNull(peer.BuilderStationType);

            // Now swap back to a NON-Builder hull — the hull must not be a Builder.
            peer.ChangeHull(nonBuilderHull);
            Assert.False(peer.HullType.HasCapability(DataHullTypeIGC.HabmBuilder));

            bot.SendOrderChange(peer.Id, OrderChange.CidBuild, OrderChange.CmdAccepted, IgcObjectType.Asteroid, rock.Data.AsteroidId);
            Pump(srv, () => sector.BuildingEffects.Count > 0, 1500);

            Assert.Empty(sector.BuildingEffects);   // refused — not a Builder-role ship
            Assert.False(peer.IsBuilding);          // never committed
            Console.WriteLine($"P422-REJECT: BUILD from non-Builder hull '{nonBuilderHull.Name}' refused — no construction");
        }
    }

    /// <summary>⭐ Negative: a Build command targeting something that is not an
    /// <c>OT_asteroid</c> is refused (native LegalCommand is legal only against
    /// an OT_asteroid, shipIGC.h:1699-1701).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Build_against_non_asteroid_target_is_refused()
    {
        Require.Artwork();
        var (srv, _, sector, peer, builderHull, _, stationType, rock, _, _) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            MakeBuilderPeer(peer, builderHull, stationType);

            // Target a SHIP (not an asteroid) with a Build command.
            bot.SendOrderChange(peer.Id, OrderChange.CidBuild, OrderChange.CmdAccepted, IgcObjectType.Ship, peer.Id);
            Pump(srv, () => sector.BuildingEffects.Count > 0, 1500);

            Assert.Empty(sector.BuildingEffects);   // refused — non-asteroid target
            Assert.False(peer.IsBuilding);
            Console.WriteLine("P422-REJECT: BUILD against a non-OT_asteroid target refused");
        }
    }

    /// <summary>⭐ Negative: a Build command against an asteroid whose capability
    /// does not match the configured station type (not every rock is buildable)
    /// is refused — native HasCapability(m_abmOrders), shipIGC.h:1702.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Build_against_capability_mismatch_asteroid_is_refused()
    {
        Require.Artwork();
        var (srv, _, sector, peer, builderHull, _, stationType, _, wrongRock, _) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            MakeBuilderPeer(peer, builderHull, stationType);

            // The wrong-type rock shares no capability bit with the station's
            // AabmBuild (asserted disjoint in the fixture).
            bot.SendOrderChange(peer.Id, OrderChange.CidBuild, OrderChange.CmdAccepted, IgcObjectType.Asteroid, wrongRock.Data.AsteroidId);
            Pump(srv, () => sector.BuildingEffects.Count > 0, 1500);

            Assert.Empty(sector.BuildingEffects);          // refused — capability mismatch
            Assert.False(peer.IsBuilding);
            Assert.Contains(wrongRock, sector.Mineable);   // wrong-type rock untouched
            Console.WriteLine($"P422-REJECT: BUILD of '{stationType.Name}' at wrong-type rock {wrongRock.Data.AsteroidId} (cap 0x{wrongRock.Data.AabmCapabilities:x}) refused — capability mismatch");
        }
    }

    /// <summary>⭐ Negative: a Builder with no configured station type (nothing
    /// to build) is refused — native's unset m_abmOrders has no build AABM to
    /// match, so LegalCommand cannot be satisfied.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Build_without_configured_station_type_is_refused()
    {
        Require.Artwork();
        var (srv, _, sector, peer, builderHull, _, _, rock, _, _) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            peer.ChangeHull(builderHull);                 // Builder hull…
            Assert.Null(peer.BuilderStationType);         // …but NO station type configured

            bot.SendOrderChange(peer.Id, OrderChange.CidBuild, OrderChange.CmdAccepted, IgcObjectType.Asteroid, rock.Data.AsteroidId);
            Pump(srv, () => sector.BuildingEffects.Count > 0, 1500);

            Assert.Empty(sector.BuildingEffects);   // refused — nothing to build
            Assert.False(peer.IsBuilding);
            Console.WriteLine($"P422-REJECT: BUILD with no configured BuilderStationType refused");
        }
    }
}
