using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

using Server = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

/// <summary>
/// P866 / SIM-0652 — the audience of the side-scoped <c>ASTEROID_DESTROYED</c>
/// the native build path emits. Native <c>BuildStation</c> calls the
/// <c>(asteroid, side, explodeF)</c> <c>KillAsteroidEvent</c> overload
/// (<c>FedSrv/FedSrv.CPP:11429-11457</c>, overload body <c>:12424-12431</c>) once
/// per side in one of two sets:
/// <list type="bullet">
/// <item>sides that already knew the rock — the builder's own side, a side with an
/// eye on it (<c>GetAsteroidCurrentEye</c>), a builder-seen side
/// (<c>GetBuilderSeenSide</c>) or a side in the <c>pbseensides</c> mask
/// (<c>:11432-11437</c>);</item>
/// <item>sides that did not know the rock but now see the STATION
/// (<c>:11445-11446</c>). Every other side is left on the stale-rock path (it
/// gets the notice only when it first spots the zone, the third overload,
/// <c>:11447-11457</c> / <c>Sector.ResolveConsumedRockVisibility</c>).</item>
/// </list>
/// Native sends that packet to <c>CFSSide::FromIGC(side)-&gt;GetGroup()</c>, so a
/// team-lobby connection (a member of the SIDE_TEAMLOBBY group, never of a real
/// side's group, <c>fsship.cpp:1073-1078</c>) can never receive it. The port's
/// retirement packet previously went to every non-owner, non-stale peer —
/// including unseated (team-lobby) peers. This test pins both edges on a real
/// booted server: informed seated sides get it, a no-eye side and both unseated
/// forms (<c>SideId == null</c> and the <c>SideTeamLobby</c> park, -2) do not.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P866EventAudienceTests
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
            new Gm
            {
                Name = "P866 Audience", Map = "XXV_simple.igc", Teams = 3,
                CivIds = [1, 3, 4], SideNames = ["Alpha", "Bravo", "Charlie"],
            },
        ],
    };

    /// <summary>The synthetic buildable rock the builder will consume. XXV_simple
    /// carries zero authored asteroids, so it is the only occluder in the sector —
    /// line of sight between the observers and this rock is therefore unconditional.</summary>
    private static DataAsteroidIGC MkRock(short id, Vector3 pos, short radius, short sectorId, short aabmCap) => new(
        Signature: 1.0f, Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: 0f, OreMax: 0f, AabmCapabilities: aabmCap,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    private static Ship SeatShip(Mission mission, short id, short? side, Vector3 position, DataHullTypeIGC hull)
    {
        var ship = new Ship(id, hull, position, Vector3.Zero, Orientation.Identity) { SideId = side };
        mission.PlaceShip(ship, mission.Sectors[0]);
        return ship;
    }

    /// <summary>Every outbound message, split per peer, as (conn, fmid).</summary>
    private static List<(ConnectionId Conn, ushort Fmid)> Capture(Server server)
    {
        var sent = new List<(ConnectionId, ushort)>();
        server.TestPublicationCapture = (peer, bytes, _, _) =>
        {
            foreach (var m in FedPacket.Unpack(bytes)) sent.Add((peer, (ushort)(m[2] | m[3] << 8)));
        };
        return sent;
    }

    /// <summary>⭐ A completed build retires the consumed rock (`ASTEROID_DESTROYED`,
    /// fmid 86) to the owner's side (with the station EXPORT) and to a side with an
    /// eye on the rock, and to NO ONE else: not to the side whose ship is 30 km away
    /// (it never knew the rock and cannot see the station), and not to a team-lobby
    /// peer in either unseated form. Native `KillAsteroidEvent(asteroid, side,
    /// false)` audience (<c>FedSrv.CPP:11429-11446,12424-12431</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Build_retires_the_rock_only_to_informed_seated_sides_never_team_lobby_peers()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;

        var core = mission.Core;
        var sector = mission.Sectors[0];
        var stationType = core.StationTypes.Values.First();
        // Deterministic signature math: every side at the identity attributes.
        foreach (var side in mission.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;
        // A long-range scanner hull for the synthetic observers.
        var observerHull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First()
            with { ScannerRange = 10_000f, Length = 10f, Signature = 1f };

        var rockPos = new Vector3(511f, 823f, 604f);
        var rock = new Asteroid(MkRock(7701, rockPos, 60, sector.Id, stationType.AabmBuild));
        mission.PlaceAsteroid(rock, sector);

        // The builder (an NPC ship, no peer) sits next to the rock.
        var builderHull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var builder = new Ship(5601, builderHull, rockPos + new Vector3(0f, 0f, 300f), Vector3.Zero, Orientation.Identity)
            { SideId = 0 };
        mission.PlaceShip(builder, sector);

        // Peers: owner-side, eye-side (2 km from the rock → in the observer's
        // scanner range), a no-eye side (30 km), and the two unseated forms.
        var ownerShip = SeatShip(mission, 9000, 0, rockPos + new Vector3(0f, 0f, 500f), observerHull);
        var eyeShip = SeatShip(mission, 9001, 1, rockPos + new Vector3(0f, 0f, 2000f), observerHull);
        var farShip = SeatShip(mission, 9002, 2, rockPos + new Vector3(0f, 0f, 30_000f), observerHull);
        var unseatedShip = SeatShip(mission, 9003, null, rockPos + new Vector3(0f, 0f, 100f), observerHull);
        var lobbyShip = SeatShip(mission, 9004, -2, rockPos + new Vector3(0f, 0f, 150f), observerHull);
        var ownerConn = new ConnectionId(900);
        var eyeConn = new ConnectionId(901);
        var farConn = new ConnectionId(902);
        var unseatedConn = new ConnectionId(903);
        var lobbyConn = new ConnectionId(904);
        server.TestSeedPeers([
            (ownerConn, ownerShip, sector),
            (eyeConn, eyeShip, sector),
            (farConn, farShip, sector),
            (unseatedConn, unseatedShip, sector),
            (lobbyConn, lobbyShip, sector),
        ]);

        // One full scanner sweep establishes the native side-visibility state the
        // retirement audience is computed from (P797 one-shot sweep).
        mission.SeedStartingVisibility();
        Assert.True(rock.GetCurrentEye(1), "the eye-side observer must have an eye on the rock");
        Assert.False(rock.GetCurrentEye(2), "the 30 km observer must have no eye on the rock");

        var sent = Capture(server);
        double start = mission.LastUpdate;
        sector.StartBuildingEffect(builder, rock, stationType, start);
        mission.Update(start + BuildingEffect.BuildDuration + 1.0);

        var station = rock.ConstructedStation;
        Assert.NotNull(station);
        Assert.Contains((short)2, station!.StaleRockSides);       // the no-eye side is on the stale path
        Assert.DoesNotContain((short)0, station.StaleRockSides);  // the builder's own side is informed
        Assert.DoesNotContain((short)1, station.StaleRockSides);  // the eye side is informed

        static bool Has(List<(ConnectionId Conn, ushort Fmid)> s, ConnectionId c, ushort fmid)
            => s.Any(x => x.Conn == c && x.Fmid == fmid);

        // Owner side: the silent rock pop rides the completion packet (destroy +
        // station EXPORT) — native loop 1.
        Assert.True(Has(sent, ownerConn, AsteroidDestroyed.Fmid), "the builder's side must receive ASTEROID_DESTROYED");
        Assert.True(Has(sent, ownerConn, ExportStation.Fmid), "the builder's side must receive the station EXPORT");
        // Eye side: the retirement packet only — native loop 1's eye branch.
        Assert.True(Has(sent, eyeConn, AsteroidDestroyed.Fmid), "a side with an eye on the rock must receive ASTEROID_DESTROYED");
        // No-eye side: nothing at build time (native leaves it stale).
        Assert.False(Has(sent, farConn, AsteroidDestroyed.Fmid), "a side that never saw the rock must NOT receive the retirement");
        // Team-lobby peers: both unseated forms are outside every side group.
        Assert.False(Has(sent, unseatedConn, AsteroidDestroyed.Fmid), "an unseated (SideId null) peer must NOT receive the retirement");
        Assert.False(Has(sent, lobbyConn, AsteroidDestroyed.Fmid), "a SIDE_TEAMLOBBY (-2) peer must NOT receive the retirement");

        Console.WriteLine($"P866-0652: build retired rock {rock.Data.AsteroidId} → owner+eye ASTEROID_DESTROYED, " +
                          $"stale side {string.Join(",", station.StaleRockSides)} quiet, both team-lobby peers quiet");
    }
}
