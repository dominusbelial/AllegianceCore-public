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
/// P147 Stage 1 — live loopback proof that an <b>ownerless</b> NPC/drone ship
/// can exist and be seen by a real connected peer with <b>zero new broadcast
/// code</b>. Construction mirrors P138/P139's harness (booted bot peer, no
/// Windows VM): one connected bot is seated in <c>Sectors[0]</c>, then an NPC
/// ship with <b>no</b> <c>_peerShips</c> entry is spawned into the SAME sector
/// via the new <see cref="Srv.SpawnNpcShip"/> helper. The claim under test is
/// that <c>Sector.GetShipUpdateCandidates</c> (cluster-membership only — no
/// peer-ownership gate) plus <c>BroadcastShipsUpdate</c> (sends every
/// candidate in the sector to every peer, per-player) put the NPC into the
/// real peer's <c>HEAVY_SHIPS_UPDATE</c>(176) stream with no extra wiring.
///
/// If this held, the NPC appears in the peer's stream with its real placed
/// position (within the port's standard ±1.0 unit CompactPosition tolerance)
/// and no <c>ExportShip</c>/<c>PilotType</c> involvement (the heavy update
/// carries no pilot type at all — HeavyShipsUpdate.cs:50-178).
/// </summary>
[Collection("LiveNetwork")]
public sealed class NpcShipFoundationLiveTests
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
            new Gm { Name = "NPC Foundation Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 993u, DwCookie = 0x993u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99100000000000002ul, DrmHash = "npcfoundation",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A booted single-bot live server in <c>Sectors[0]</c>, with the
    /// mission/sector captured so the test can later spawn an NPC alongside.</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Mission Mission = null!;
        public Sector Sector = null!;
        public Ship Peer = null!;
        public FltBot Bot = null!;
        public short PeerSide;

        public void Dispose()
        {
            Bot?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatOne()
    {
        var seat = new Seated();
        seat.Server = new Srv();
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            seat.Mission = mission;
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);  // peer at origin
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        var port = seat.Server.BoundPort;

        var bot = new FltBot();
        try
        {
            bot.Connect(port, "p147_npc");
            bot.SendLogonRequest(Request("p147_npc"));
            PumpUntil(seat.Server, () => bot.ShipAssignment is not null, 4000);
            Assert.True(bot.ShipAssignment is not null, "bot must be assigned a ship");
            var peer = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
            peer.Velocity = Vector3.Zero;
            seat.Peer = peer;
            seat.PeerSide = peer.SideId ?? throw new InvalidOperationException("peer must have a side");
            seat.Bot = bot;
            return seat;
        }
        catch
        {
            bot.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    private static bool HasEntry(HeavyShipsUpdate up, short shipId)
        => up.HeavyShipEntries.Any(e => e.ShipId == shipId);

    /// <summary>⭐ THE P147 proof. An ownerless NPC ship spawned into the same
    /// sector as a connected bot appears in the bot's live
    /// <c>HEAVY_SHIPS_UPDATE</c>(176) stream with its real placed position —
    /// with zero new broadcast code. The NPC has no <c>_peerShips</c> entry
    /// (verified) and never goes through <c>ExportShip</c>; it is visible purely
    /// because <c>GetShipUpdateCandidates</c> is cluster-membership-only and
    /// <c>BroadcastShipsUpdate</c> relays every candidate to every peer.</summary>
    [Fact]
    public void Ownerless_npc_ship_appears_in_peer_heavy_updates_with_position()
    {
        using var seat = SeatOne();

        // The NPC sits at a position far from the origin peer, at a real side id.
        var npcPos = new Vector3(950f, 740f, 880f);
        var npc = seat.Server.SpawnNpcShip(seat.Mission, seat.Sector, seat.PeerSide, npcPos);

        // Assert ownerlessness: the NPC must have NO _peerShips entry.
        Assert.False(seat.Server.Ships.Any(s => s.Ship.Id == npc.Id),
            $"NPC ship {npc.Id} must have no peer-connection entry (ownerless)");
        // It shares the ShipID namespace with players and is in the same sector.
        Assert.NotEqual(seat.Peer.Id, npc.Id);
        Assert.Same(seat.Sector, npc.Sector);

        seat.Bot.ResetHeavyShipsUpdates();

        // Run enough ticks for at least one full broadcast cycle.
        PumpUntil(seat.Server, () => seat.Bot.HeavyShipsUpdates.Count > 0, 1000);
        Assert.True(seat.Bot.HeavyShipsUpdates.Count > 0, "peer must receive HEAVY_SHIPS_UPDATE after NPC spawn");

        var up = seat.Bot.HeavyShipsUpdates[0];

        // The ownerless NPC appears in the real peer's heavy stream.
        Assert.True(HasEntry(up, npc.Id),
            $"peer's heavy update must contain NPC({npc.Id}); got {string.Join(",", up.HeavyShipEntries.Select(e => e.ShipId))}");

        // Its position matches where it was placed, within the port's standard
        // ±1.0 unit CompactPosition tolerance (LightHeavyShipsUpdateCodecTests.cs:129).
        // Compare against the authoritative current npc.Position (sits still).
        var entry = up.HeavyShipEntries.First(e => e.ShipId == npc.Id);
        var decoded = entry.Position(up.PositionReference);
        Assert.Equal(npc.Position.X, decoded.X, 1.0f);
        Assert.Equal(npc.Position.Y, decoded.Y, 1.0f);
        Assert.Equal(npc.Position.Z, decoded.Z, 1.0f);

        // The peer still never sees its own ship in the heavy tier.
        Assert.False(HasEntry(up, seat.Peer.Id), "peer still never receives its own ship in HEAVY_SHIPS_UPDATE");

        Console.WriteLine($"P147-NPC: ownerless NPC {npc.Id} side {npc.SideId} @ {npcPos} appeared in peer " +
                          $"{seat.Peer.Id}(side {seat.PeerSide}) HEAVY_SHIPS_UPDATE(176) at {decoded} " +
                          $"(≤1u tolerance), no _peerShips entry, no ExportShip — ZERO new broadcast code");
    }
}
