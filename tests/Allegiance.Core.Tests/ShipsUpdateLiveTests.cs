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
/// P139 — live loopback proof of the periodic <b>per-player</b>
/// <c>LIGHT_SHIPS_UPDATE</c>(175)/<c>HEAVY_SHIPS_UPDATE</c>(176) broadcast
/// (<see cref="Srv.BroadcastShipsUpdate"/>, called every <see cref="Srv.TickSim"/>),
/// the consumer of P138's per-cluster candidate list
/// (<see cref="Sector.GetShipUpdateCandidates"/>). This is the task that makes
/// "see other ships" real in this port for the first time. The verification
/// requirements:
/// <list type="number">
/// <item>two real ships in the same cluster on <b>different sides</b> each
/// receive a HEAVY_SHIPS_UPDATE containing the <b>other</b> ship — the
/// cluster-membership, side-agnostic selection P138 proved in isolation;</item>
/// <item>a player with a real locked command target (<see cref="Ship.CommandTarget"/>)
/// among the candidates has that target in the <b>first heavy slot</b> and its
/// real hull/shield fractions in the <c>bpTargetHull</c>/<c>bpTargetShield</c>
/// scalars;</item>
/// <item>a ship leaving the cluster is absent from the next update;</item>
/// <item>no player receives their <b>own</b> ship in this message (their own
/// state rides SingleShipUpdate already — no double-send).</item>
/// </list>
/// Construction mirrors P133-135's booted 2-bot / 2-side harness (no Windows VM,
/// no native client). Every ship in the sector is a candidate regardless of side
/// or proximity, so both peers sit in <c>Sectors[0]</c>.
/// </summary>
[Collection("LiveNetwork")]
public sealed class ShipsUpdateLiveTests
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
            new Gm { Name = "Ships Update Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 992u, DwCookie = 0x992u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99000000000000001ul, DrmHash = "shipsupdate",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>div-240 BytePercentage (Utility.h:1307), matching the server's
    /// <c>BroadcastShipsUpdate</c> Percent helper exactly.</summary>
    private static byte Percent(float f)
    {
        double v = (double)f * 240.0 + 0.5;
        if (v < 0.0) return 0;
        if (v >= 241.0) return 240;
        return (byte)v;
    }

    /// <summary>A booted 2-bot / 2-side live server. Slot 0 = <b>side A</b> at
    /// the origin; slot 1 = <b>side B</b> a little way off — both in
    /// <c>Sectors[0]</c>, both flying, so each is a candidate to the other
    /// regardless of side (P138: cluster-membership only).</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship A = null!, B = null!;
        public FltBot ABot = null!, BBot = null!;
        public Sector Sector = null!;

        public void Dispose()
        {
            ABot?.Dispose();
            BBot?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatTwo()
    {
        var seat = new Seated();
        seat.Server = new Srv();

        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = placed switch
            {
                0 => new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity),                     // side A at origin
                _ => new Ship(id, hull, new Vector3(500f, 0f, 0f), Vector3.Zero, Orientation.Identity),        // side B, same cluster
            };
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot() };
        string[] names = { "p139_a", "p139_b" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "both bots must be assigned a ship");

            for (int i = 0; i < bots.Length; i++)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bots[i].ShipAssignment!.Ship.ShipId).Ship;
                if (i == 0) { seat.A = ship; seat.ABot = bots[i]; }
                else { seat.B = ship; seat.BBot = bots[i]; }
            }
            Assert.NotNull(seat.A); Assert.NotNull(seat.B);
            Assert.True(seat.A.SideId != seat.B.SideId, "A and B must be on different sides (cross-side visibility)");
            Assert.Equal(seat.A.Sector, seat.B.Sector);          // same cluster
            seat.A.Velocity = Vector3.Zero;
            seat.B.Velocity = Vector3.Zero;
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    private static bool HasEntry(HeavyShipsUpdate up, short shipId)
        => up.HeavyShipEntries.Any(e => e.ShipId == shipId);

    /// <summary>⭐ Two real ships in the same cluster on different sides each
    /// receive a <c>HEAVY_SHIPS_UPDATE</c> containing the OTHER ship —
    /// cluster-membership (P138), side-agnostic. Also the self-exclusion check:
    /// neither player's message contains their own ship (their own state rides
    /// SingleShipUpdate, so no double-send). With exactly two candidates in the
    /// sector, each player's heavy list is exactly one entry = the other ship.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cross_side_ships_see_each_other_and_not_self()
    {
        Require.Artwork();
        using var seat = SeatTwo();
        seat.ABot.ResetHeavyShipsUpdates();
        seat.BBot.ResetHeavyShipsUpdates();

        // Start fresh after seating; each peer should accrue one HEAVY per tick.
        PumpUntil(seat.Server, () => seat.ABot.HeavyShipsUpdates.Any(u => HasEntry(u, seat.B.Id))
            && seat.BBot.HeavyShipsUpdates.Any(u => HasEntry(u, seat.A.Id)), 20000);
        Assert.True(seat.ABot.HeavyShipsUpdates.Count > 0, "A must receive HEAVY_SHIPS_UPDATE");
        Assert.True(seat.BBot.HeavyShipsUpdates.Count > 0, "B must receive HEAVY_SHIPS_UPDATE");

        var aUp = seat.ABot.HeavyShipsUpdates.First(u => HasEntry(u, seat.B.Id));
        var bUp = seat.BBot.HeavyShipsUpdates.First(u => HasEntry(u, seat.A.Id));

        // Cross-side, cluster-membership: each sees the other.
        Assert.True(HasEntry(aUp, seat.B.Id),
            $"A's update must contain B({seat.B.Id}); got {string.Join(",", aUp.HeavyShipEntries.Select(e => e.ShipId))}");
        Assert.True(HasEntry(bUp, seat.A.Id),
            $"B's update must contain A({seat.A.Id}); got {string.Join(",", bUp.HeavyShipEntries.Select(e => e.ShipId))}");

        // Self-exclusion: no double-send of the player's own ship.
        Assert.False(HasEntry(aUp, seat.A.Id), "A must NOT receive its own ship in HEAVY_SHIPS_UPDATE");
        Assert.False(HasEntry(bUp, seat.B.Id), "B must NOT receive its own ship in HEAVY_SHIPS_UPDATE");

        // The every-candidate-goes-heavy decision leaves the LIGHT tier empty and
        // unsent (native forwards LIGHT only when non-empty).
        Assert.True(seat.ABot.LightShipsUpdates.Count == 0, "no LIGHT_SHIPS_UPDATE is emitted (light tier unpopulated)");

        Console.WriteLine($"P139-XSIDE: A({seat.A.Id}, side {seat.A.SideId}) sees B({seat.B.Id}, side {seat.B.SideId}) " +
                          $"and vice versa in HEAVY_SHIPS_UPDATE(176), no self, per-player [PER PLAYER]");
    }

    /// <summary>⭐ A player with a real locked command target among the
    /// candidates (B.CommandTarget = A, the port's <c>m_commandTargets[c_cmdCurrent]</c>)
    /// has that target in the FIRST heavy slot, and the <c>bpTargetHull</c>/
    /// <c>bpTargetShield</c> scalars reflect A's real hull/shield fractions
    /// (FedSrv.CPP:4075-4104). The target's own ship is not duplicated in the
    /// rest of the list, and B's own ship is still excluded.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Locked_target_fills_first_heavy_slot_with_real_fractions()
    {
        Require.Artwork();
        using var seat = SeatTwo();
        seat.B.CommandTarget = seat.A;   // SetCommandTarget: B locks A
        seat.BBot.ResetHeavyShipsUpdates();

        // The first update after locking can be an in-flight one generated
        // BEFORE the command target was set (the server had sent 255 'no
        // target'); condition-wait for an update that actually CARRIES the
        // locked target (ship A in the first heavy slot, with A's real
        // hull/shield fractions) instead of blindly grabbing index 0.
        bool HasTargetUpdate() =>
            seat.BBot.HeavyShipsUpdates.Any(u => u.HeavyShipEntries.Length >= 1
                && u.HeavyShipEntries[0].ShipId == seat.A.Id
                && u.BpTargetHull == Percent(seat.A.Fraction)
                && u.BpTargetShield == Percent(seat.A.ShieldFraction));

        PumpUntil(seat.Server, HasTargetUpdate, 20000);
        Assert.True(HasTargetUpdate(), "B must receive a TARGET-carrying HEAVY_SHIPS_UPDATE after locking a target");

        var up = seat.BBot.HeavyShipsUpdates.First(u => u.HeavyShipEntries.Length >= 1
            && u.HeavyShipEntries[0].ShipId == seat.A.Id
            && u.BpTargetHull == Percent(seat.A.Fraction)
            && u.BpTargetShield == Percent(seat.A.ShieldFraction));
        Assert.True(up.HeavyShipEntries.Length >= 1, "B's update must contain the locked target in slot 0");
        Assert.Equal(seat.A.Id, up.HeavyShipEntries[0].ShipId);   // first heavy slot = target

        // Target appears exactly once (slot 0) — not duplicated in the remainder.
        Assert.Equal(1, up.HeavyShipEntries.Count(e => e.ShipId == seat.A.Id));

        // Scalar region reflects the target's REAL fractions (div-240).
        Assert.Equal(Percent(seat.A.Fraction), up.BpTargetHull);
        Assert.Equal(Percent(seat.A.ShieldFraction), up.BpTargetShield);

        // Still no self double-send, and the target ship rides its real entry.
        Assert.False(HasEntry(up, seat.B.Id), "B must NOT receive its own ship in HEAVY_SHIPS_UPDATE");

        Console.WriteLine($"P139-TARGET: B targets A({seat.A.Id}) frac {seat.A.Fraction:F3}/{seat.A.ShieldFraction:F3} " +
                          $"-> first heavy slot + bpTargetHull {up.BpTargetHull}/240 bpTargetShield {up.BpTargetShield}/240");
    }

    /// <summary>⭐ A ship leaving the cluster (removed from its sector's model
    /// list — the port's ship-removal idiom at Server.cs:1066-1067, the same
    /// one <c>ReleasePeer</c> uses) is absent from the NEXT update to the peer
    /// that remains. Proves the per-cluster candidate list tracks membership
    /// live, not a one-time snapshot.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ship_leaving_cluster_disappears_from_next_update()
    {
        Require.Artwork();
        using var seat = SeatTwo();
        seat.ABot.ResetHeavyShipsUpdates();

        // Baseline: A sees B while both are in the cluster.
        PumpUntil(seat.Server, () => seat.ABot.HeavyShipsUpdates.Any(u => HasEntry(u, seat.B.Id)), 20000);
        Assert.Contains(seat.ABot.HeavyShipsUpdates, u => HasEntry(u, seat.B.Id));

        // B leaves the cluster (the port's removal idiom, Server.cs:1066-1067).
        seat.Sector.Remove(seat.B);
        seat.B.Sector = null;

        seat.ABot.ResetHeavyShipsUpdates();
        seat.Server.TickSim();
        uint removalTick = (uint)seat.Server.LoopIterations;
        Assert.True(SpinWait.SpinUntil(() => seat.ABot.HeavyShipsUpdates.Any(u => u.TimeReference == removalTick),
            TimeSpan.FromSeconds(30)), "A must decode the next generated update after B leaves");
        var up = Assert.Single(seat.ABot.HeavyShipsUpdates, u => u.TimeReference == removalTick);
        Assert.False(HasEntry(up, seat.B.Id),
            $"after B leaves the cluster A must NOT see B; got {string.Join(",", up.HeavyShipEntries.Select(e => e.ShipId))}");
        Assert.False(HasEntry(up, seat.A.Id), "A still never sees its own ship");

        Console.WriteLine($"P139-LEFT: B({seat.B.Id}) removed from cluster -> A's entries now " +
                          $"{string.Join(",", up.HeavyShipEntries.Select(e => e.ShipId))} " +
                          $"(expect none); A no longer sees B in HEAVY_SHIPS_UPDATE(176)");
    }
}
