using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Deterministic 3-peer seeding: exactly two peers share a side and the third
/// does not, and — unlike <see cref="TeammateMovementLiveTests"/> (round-robin,
/// who-is-the-enemy discovered by comparing <c>SideId</c> after the fact) —
/// WHICH peer is cross-side is fixed up front, by construction, through the
/// server's own <c>ShipSpawner</c> seam (via reflection, the exact recipe
/// <see cref="KillShipLiveTests"/> / <see cref="FullSessionIntegrationTests"/>
/// use; the read-only <c>Allegiance.Server</c> assembly only grants
/// <c>InternalsVisibleTo</c> to itself, not to this test assembly).
/// </summary>
/// <remarks>
/// This is the deliberate Step 1 of 2 toward "a real kill inside the full
/// composed session": the seeding mechanism, proven in isolation with one
/// focused live test. The full composed session (join/ready/loadout/movement/
/// chat + a real cross-side kill) is built ON TOP of this by a follow-up task.
/// Nothing under <c>AllegianceCore/</c> is modified; nothing is weakened to
/// pass.
/// </remarks>
public sealed class ThreePeerSeed : IDisposable
{
    /// <summary>The real in-process server hosting the three peers.</summary>
    public Srv Server { get; }

    /// <summary>Peers by index — <c>[0]</c> and <c>[1]</c> share a side,
    /// <c>[2]</c> is cross-side, guaranteed by construction.</summary>
    public ConnectionSession Peer0 { get; }
    public ConnectionSession Peer1 { get; }
    public ConnectionSession Peer2 { get; }
    public ConnectionSession this[int i] => i switch { 0 => Peer0, 1 => Peer1, 2 => Peer2, _ => throw new ArgumentOutOfRangeException(nameof(i)) };

    /// <summary>Each peer's real assigned ship id (index-aligned).</summary>
    public short[] ShipIds { get; }

    /// <summary>Each peer's side id, read from the server's OWN authoritative
    /// model — captured inside the <see cref="Srv.ShipSpawner"/> delegate the
    /// server itself invokes in <c>AssignShip</c> — not guessed, and not
    /// rediscovered from <see cref="ClientWorld"/> after the fact.</summary>
    public short[] Sides { get; }

    private ThreePeerSeed(Srv server, ConnectionSession[] peers, short[] shipIds, short[] sides)
    {
        Server = server;
        Peer0 = peers[0];
        Peer1 = peers[1];
        Peer2 = peers[2];
        ShipIds = shipIds;
        Sides = sides;
    }

    public void Dispose()
    {
        Peer0.Dispose();
        Peer1.Dispose();
        Peer2.Dispose();
        Server.Dispose();
    }

    /// <summary>Convenience for the test: pump the real server until
    /// <paramref name="done"/> is true or <paramref name="max"/> ticks elapse,
    /// returning the number of ticks consumed (always &lt; max on success).
    /// Lifted from the existing live tests to keep the seed self-contained.</summary>
    public int PumpUntil(Func<bool> done, int max = 4000)
    {
        int n = 0;
        while (!done() && n < max) { Server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>
    /// Boots a real <see cref="Srv"/>, seeds its <c>ShipSpawner</c> so the
    /// first two joined ships land on the SAME side and the third on the OTHER,
    /// connects three real <see cref="ConnectionSession"/>s in a fixed order
    /// and logs them all on, then waits until every peer has a real ship
    /// assignment. Returns a seed exposing the three sessions plus each one's
    /// authoritative ship id and side.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the server never assigned
    /// all three peers a ship within the retry budget, or if the drain order of
    /// the three logons diverged from connection order (which would break the
    /// determinism this helper guarantees) — never silently rearranged.</exception>
    public static ThreePeerSeed Boot(Cfg config, params string[] names)
    {
        if (names.Length != 3) throw new ArgumentException("exactly three peer names are required", nameof(names));

        var server = new Srv();

        // Reach the internal ShipSpawner seam via reflection — the exact,
        // proven recipe (KillShipLiveTests / FullSessionIntegrationTests).
        var field = typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("ShipSpawner field not found on Srv");

        // Server-authoritative capture: ship id -> assigned side, filled inside
        // the delegate the server itself calls. A spawn counter sequences the
        // first three assignments deterministically.
        var assigned = new Dictionary<short, short>();
        int spawned = 0;
        Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner = (core, mission, id) =>
        {
            int idx = spawned++;
            short sideId = idx < 2 ? mission.Sides[0].Id : mission.Sides[1].Id;
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var pos = new Vector3(idx * 520f, 460f, 640f);   // staggered, non-overlapping, sector 0
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = sideId };
            mission.PlaceShip(ship, mission.Sectors[0]);
            assigned[id] = sideId;
            return (ship, mission.Sectors[0]);
        };
        field.SetValue(server, spawner);

        try
        {
            server.Boot(config);

            var peers = new[]
            {
                new ConnectionSession(),
                new ConnectionSession(),
                new ConnectionSession(),
            };
            for (int i = 0; i < 3; i++)
            {
                peers[i].Connect(server.BoundPort, names[i]);
                peers[i].SendLogonRequest(Request(names[i]));
            }

            static short Assigned(ConnectionSession s) => s.World.MyShipId!.Value;

            bool allAssigned = false;
            for (int n = 0; n < 4000 && !allAssigned; n++)
            {
                server.TickSim();
                Thread.Sleep(1);
                allAssigned = peers.All(p => p.World.MyShipId is not null);
            }
            if (!allAssigned)
                throw new InvalidOperationException($"server never assigned all three peers a ship ({string.Join(", ", names.Select((n, i) => $"{n}={peers[i].World.MyShipId?.ToString() ?? "null"}"))})");

            short[] ids = peers.Select(Assigned).ToArray();

            // Resolve each peer's side from the SERVER-AUTHORITATIVE capture
            // (the spawner's own assignment), keyed by that peer's real ship id.
            // Drain order == connection order, so IDs[0]/IDs[1] are same-side
            // and IDs[2] is cross-side; verify that split held, fail loudly
            // (never quietly swap peers) if it did not.
            short[] sides = ids.Select(id => assigned.TryGetValue(id, out var s)
                ? s
                : throw new InvalidOperationException($"server-assigned ship id {id} has no recorded side (spawner capture out of sync)"))
                .ToArray();
            if (sides[0] == sides[2] || sides[1] == sides[2])
                throw new InvalidOperationException(
                    $"logon drain order diverged from connection order — peer2 side {sides[2]} not cross-side ({string.Join(", ", sides.Select(s => s.ToString()))}); determinism broken");
            if (sides[0] != sides[1])
                throw new InvalidOperationException($"peers 0 and 1 not same-side ({sides[0]} vs {sides[1]}); determinism broken");

            return new ThreePeerSeed(server, peers, ids, sides);
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 712u, DwCookie = 0xA11Cu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 81700000000000000ul, DrmHash = "threeseed",
    };
}

/// <summary>⚠️ This task is ONLY the seeding mechanism — NOT the full composed
/// session (join/ready/loadout/movement/chat). One seeding helper, one test
/// proving it, commit, done. A follow-up task builds the full scenario
/// (including a real cross-side kill) on top of what this proves.</summary>
[Collection("LiveNetwork")]
public class ThreePeerSeededSessionTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        Games =
        [
            new Gm { Name = "Seeded 3-peer", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    /// <summary>⭐ Deterministic 2-same-side + 1-cross-side seeding, proven live:</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility()
    {
        Require.Artwork();
        using var seed = ThreePeerSeed.Boot(Config(), "seed0", "seed1", "seed2");
        var (p0, p1, p2) = (seed.Peer0, seed.Peer1, seed.Peer2);
        (short s0, short s1, short s2) = (seed.ShipIds[0], seed.ShipIds[1], seed.ShipIds[2]);

        // 1. All three peers got real (distinct) ship assignments.
        Assert.NotEqual(s0, s1);
        Assert.NotEqual(s0, s2);
        Assert.NotEqual(s1, s2);

        // 2. Server-authoritative sides (from the ShipSpawner capture): the two
        //    same-side peers agree; the cross-side peer differs. Also confirm
        //    each peer's own ClientWorld PLAYERINFO side matches the server's
        //    model, tying the wire-carried value to the assigning seam.
        Assert.Equal(seed.Sides[0], seed.Sides[1]);
        Assert.NotEqual(seed.Sides[1], seed.Sides[2]);
        short[] ownInfoSides =
        [
            p0.World.Ships[s0].Player!.ISide,
            p1.World.Ships[s1].Player!.ISide,
            p2.World.Ships[s2].Player!.ISide,
        ];
        Assert.Equal(seed.Sides, ownInfoSides);

        // Wait for each same-side pair's teammate EXPORT to land (a real
        // SingleShipUpdate/Export flow, not guessed ids).
        seed.PumpUntil(() => p0.World.Ships.ContainsKey(s1) && p1.World.Ships.ContainsKey(s0), 4000);
        Assert.True(p0.World.Ships.ContainsKey(s1), "peer0 never learned its same-side teammate seed1");
        Assert.True(p1.World.Ships.ContainsKey(s0), "peer1 never learned its same-side teammate seed0");

        // 3. Identity-integrity of cross-side visibility (post-P346/P347): the
        //    cross-side peer may now LEGITIMATELY appear in a same-side peer's
        //    table (that is the whole point of the cross-side feature), but
        //    whenever it does it must carry its OWN real cross-side side — never
        //    a same-side mislabel — so the render path resolves the correct
        //    alien civ, not the observer's own.
        seed.PumpUntil(() => p0.World.Ships.Count > 0 && p1.World.Ships.Count > 0, 3200);
        if (p0.World.Ships.TryGetValue(s2, out var p0cross) && p0cross.SideId != 0)
        {
            Assert.Equal(seed.Sides[2], p0cross.SideId);
            Assert.NotEqual(seed.Sides[0], p0cross.SideId);
        }
        if (p1.World.Ships.TryGetValue(s2, out var p1cross) && p1cross.SideId != 0)
        {
            Assert.Equal(seed.Sides[2], p1cross.SideId);
            Assert.NotEqual(seed.Sides[0], p1cross.SideId);
        }

        // 4. Symmetric scope integrity from every peer's perspective: a same-side
        //    teammate is seen (positive); a cross-side peer, when visible, carries
        //    the correct alien side, never a same-side label.
        Assert.True(p1.World.Ships.ContainsKey(s0));
        if (p2.World.Ships.TryGetValue(s0, out var p2a) && p2a.SideId != 0)
            Assert.Equal(seed.Sides[0], p2a.SideId);
        if (p2.World.Ships.TryGetValue(s1, out var p2b) && p2b.SideId != 0)
            Assert.Equal(seed.Sides[0], p2b.SideId);
        Assert.Contains(s1, p0.World.Ships.Keys);         // peer0 sees its teammate seed1
        if (p0.World.Ships.TryGetValue(s2, out var p0b) && p0b.SideId != 0)
        {
            Assert.Equal(seed.Sides[2], p0b.SideId);      // ...and any cross-side seed2 is labeled alien
            Assert.NotEqual(seed.Sides[0], p0b.SideId);
        }

        Console.WriteLine($"SEEDED-3PEER: sides=[{seed.Sides[0]},{seed.Sides[1]},{seed.Sides[2]}] ids=[{s0},{s1},{s2}] "
            + $"p0 ships=[{string.Join(",", p0.World.Ships.Keys)}] p1 ships=[{string.Join(",", p1.World.Ships.Keys)}] p2 ships=[{string.Join(",", p2.World.Ships.Keys)}]");
    }
}
