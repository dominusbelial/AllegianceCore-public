using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P608 — the coalesce-key CONTRACT behind the pool-mode bot "wandering"
/// regression. P606 routes both the owner's own-ship <c>SINGLE_SHIP_UPDATE(78)</c>
/// and the per-tick teammate position batch through latest-wins coalescing on
/// the SAME key (CoalesceShipStates). They MUST use DISTINCT keys: in a single
/// broadcast pass the own-ship frame is enqueued first and the teammate batch is
/// enqueued later in the same tick, so if they shared a key the batch would
/// supersede the owner's own pose IN PLACE (PeerSender.Enqueue latest-wins) —
/// the owner's own-ship state would be coalesced away before it ever reached the
/// (round-robin, SocketReady-gated) pool writer, and a bot that steers purely
/// from its own pose (TryCsPose, no local prediction) would fly on a stale pose
/// and wander. The fix gives the owner's own-ship update its OWN key
/// (CoalesceOwnShip) AND sends it GUARANTEED (native fsship.cpp:544). These tests
/// assert, deterministically (seeded world, no client timing), in BOTH broadcast
/// builders (serial and parallel), that:
///   (1) the owner's own-ship update and the teammate position stream use
///       DIFFERENT coalesce keys, and
///   (2) the owner's own-ship frame is marked GUARANTEED (never shed under
///       capacity), while the teammate batch is not.
/// </summary>
[Collection("LiveNetwork")]   // boots real in-process servers; keep serialized with other live suites
public class OwnShipCoalesceKeyTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore RepresentativeCore()
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
            new Gm { Name = "Own-Ship Key Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static short _nextFakeShipId = 31000;

    /// <summary>One deterministic world: two SAME-SIDE peers (observer + a moving
    /// mate) in the same sector, both moved after seeding so they are dirty.</summary>
    private static (List<(ConnectionId Conn, Ship Ship, Sector Sector)> Peers, short ObserverId, short MateId)
        BuildTwoSameSidePeers(Srv server, DataHullTypeIGC hull)
    {
        var sec = server.TestSectors[0];
        var observer = new Ship(_nextFakeShipId++, hull, new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        var mate = new Ship(_nextFakeShipId++, hull, new Vector3(200, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        server.TestMission!.PlaceShip(observer, sec);
        server.TestMission!.PlaceShip(mate, sec);
        var peers = new List<(ConnectionId, Ship, Sector)>
        {
            (new ConnectionId(500), observer, sec),
            (new ConnectionId(501), mate, sec),
        };
        return (peers, observer.Id, mate.Id);
    }

    /// <summary>Decode a coalesced frame into the set of SingleShipUpdate OidTargets
    /// it carries (a frame is a FedPacket of zero-or-more fmid-78 messages).</summary>
    private static HashSet<short> OidTargets(byte[] pkt)
    {
        var targets = new HashSet<short>();
        foreach (var m in FedPacket.Unpack(pkt))
        {
            ushort fmid;
            try { fmid = FedMessageHeader.Read(m).Fmid; } catch { continue; }
            if (fmid == SingleShipUpdate.Fmid)
                targets.Add(SingleShipUpdate.Decode(m).OidTarget);
        }
        return targets;
    }

    private static void AssertOwnShipKeyDistinct(Srv server, bool parallel)
    {
        var world = BuildTwoSameSidePeers(server, RepresentativeCore().HullTypes.Values.First());
        server.TestResetBroadcastState();
        server.TestSeedPeers(world.Peers);

        // Move both ships after seeding so they are dirty (and thus really sent
        // as stream frames) on this broadcast pass.
        foreach (var (_, ship, _) in world.Peers) ship.Position = new Vector3(ship.Position.X + 10, 0, 0);

        var captured = server.TestCollectBroadcastKeys(0, parallel, "shipStates");

        // Locate the observer (conn 500): its own-ship frame carries OidTarget ==
        // observer's own id; its teammate batch carries the mate id (and never its
        // own id — the broadcast excludes the recipient's own ship from posBatch).
        Assert.True(captured.ContainsKey(new ConnectionId(500)),
            $"observer conn 500 must have coalesced frames (got [{string.Join(",", captured.Keys)}])");

        int? ownKey = null; bool? ownGuaranteed = null;
        int? mateKey = null; bool? mateGuaranteed = null;
        foreach (var (pkt, key, guaranteed) in captured[new ConnectionId(500)])
        {
            var targets = OidTargets(pkt);
            if (targets.Contains(world.ObserverId)) { ownKey = key; ownGuaranteed = guaranteed; }  // the owner's own-ship update
            if (targets.Contains(world.MateId)) { mateKey = key; mateGuaranteed = guaranteed; }     // the teammate position batch
        }

        Assert.True(ownKey is not null, "the owner must receive its own-ship SingleShipUpdate as a coalesced frame");
        Assert.True(mateKey is not null, "the observer must receive the teammate position batch as a coalesced frame");

        // (1) distinct keys — the core P608 contract (was equal==1 on P606, which
        //     let the batch supersede the owner's own pose in place).
        Assert.NotEqual(ownKey, mateKey);
        // (2) own-ship is GUARANTEED (never shed); teammate batch is not.
        Assert.True(ownGuaranteed == true, "the owner's own-ship update must be sent guaranteed");
        Assert.True(mateGuaranteed == false, "the teammate position batch must be non-guaranteed");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Owners_own_ship_update_has_a_distinct_guaranteed_coalesce_key_serial_builder()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        AssertOwnShipKeyDistinct(server, parallel: false);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Owners_own_ship_update_has_a_distinct_guaranteed_coalesce_key_parallel_builder()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        AssertOwnShipKeyDistinct(server, parallel: true);
    }
}
