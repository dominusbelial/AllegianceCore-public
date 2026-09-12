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
/// P134 — live loopback proof of the periodic per-side <c>ASTEROIDS_UPDATE</c>(179)
/// broadcast (<see cref="Server.BroadcastAsteroidsUpdate"/>, called every
/// <see cref="Srv.TickSim"/>), the asteroid sibling of P133's
/// <c>STATIONS_UPDATE</c> — the second consumer of P131/P132's real visibility,
/// extended in P134 so asteroids are scored as targets by the per-tick sweep:
/// <list type="number">
/// <item>a real asteroid visible to side A (its scanner ship sits on it) is
/// delivered in A's <c>ASTEROIDS_UPDATE</c> with the correct <c>asteroidID</c>,
/// the rock's raw <c>ore</c> short, its div-240 <b>health</b> fraction
/// <c>bpFraction</c> (full = 240 here, since mining depletes ore, not the
/// damage pool) and the identity <c>co</c>;</item>
/// <item>the <b>same</b> asteroid, not visible to side B (B's only scanner ship
/// is 30 km away, far beyond any detection radius), is <b>absent</b> from B's
/// updates — indeed B receives <b>no</b> <c>ASTEROIDS_UPDATE</c> at all,
/// because the demo map has zero asteroids and the only rock is invisible to
/// it — the per-side batching proof (each side receives only what it can see,
/// not the whole mission);</item>
/// <item>a real P102/P55 mining depletion (the side-A miner parked on the rock
/// drains it each tick) is reflected in the <b>next</b> <c>ASTEROIDS_UPDATE</c>:
/// the <c>ore</c> short declines while <c>bpFraction</c> stays 240 — the two
/// fields are genuinely non-redundant, exactly the finding resolved at
/// <c>fsship.cpp:909,911</c>.</item>
/// </list>
/// Construction mirrors P133/P120's 3-bot / 2-side harness: a synthetic He3
/// rock at the origin (the demo map has zero asteroids, so it is the only rock
/// in the universe), side A's miner parked on it, side B's scanner-only ship
/// 30 km away (never in detection range), and a side-A teammate nearby. No
/// Windows VM, no native client.
/// </summary>
[Collection("LiveNetwork")]
public sealed class AsteroidsUpdateLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private const short RockId = 6721;
    private const float InitialOre = 620f;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static DataAsteroidIGC MkAsteroid(Vector3 pos, short sectorId)
    {
        return new DataAsteroidIGC(
            Signature: 1.0f,
            Position: [pos.X, pos.Y, pos.Z],
            Up: [0f, 1f, 0f],
            Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f],
            Ore: InitialOre,
            OreMax: InitialOre,
            AabmCapabilities: Asteroid.CapMineHe3,
            AsteroidId: RockId,
            HitPoints: 0f,
            Radius: 60,
            ModelName: "roid.mdl",
            TextureName: "",
            IconName: "",
            ClusterId: sectorId,
            Name: "SYN_ROID",
            Fraction: 0);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Asteroids Update Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 779u, DwCookie = 0x779u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 78900000000000000ul, DrmHash = "asteroidsupdate",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static byte Percent(float f) => (byte)(f * 240.0f + 0.5);   // div-240 BytePercentage (Utility.h:1307-1312)

    /// <summary>A booted 3-bot / 2-side live server plus the resolved roles and
    /// the single synthetic He3 rock at the origin placed by the ShipSpawner.
    /// Slot 0 = <b>side A</b> MINER, co-located on the rock (sees + mines it);
    /// slot 1 = <b>side B</b>, 30 km away on +Z (its only scanner — never in
    /// detection range of the rock); slot 2 = <b>side A</b> teammate near the
    /// rock (also sees it). A scanner hull (largest <c>ScannerRange</c>) is used
    /// so detection is robust — an asteroid has no own-side rule, so side A
    /// sees the rock purely via its scanner in range, side B does not.</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship Miner = null!, CrossSide = null!, Teammate = null!;
        public FltBot MinerBot = null!, CrossSideBot = null!, TeammateBot = null!;
        public Asteroid Rock = null!;
        public Sector Sector = null!;

        public void Dispose()
        {
            foreach (var b in new[] { MinerBot, CrossSideBot, TeammateBot }) b?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatThree()
    {
        var seat = new Seated();
        seat.Server = new Srv();

        Vector3 apos = Vector3.Zero;
        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            if (seat.Rock is null)
            {
                seat.Rock = new Asteroid(MkAsteroid(apos, sector.Id));
                mission.PlaceAsteroid(seat.Rock, sector);
            }
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = placed switch
            {
                0 => new Ship(id, hull, apos, Vector3.Zero, Orientation.Identity),                                          // side A miner on the rock
                1 => new Ship(id, hull, new Vector3(0f, 0f, 30000f), Vector3.Zero, Orientation.Identity),                   // side B, 30 km away
                _ => new Ship(id, hull, new Vector3(200f, 0f, 100f), Vector3.Zero, Orientation.Identity),                   // side A teammate near the rock
            };
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B,A
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p134_a", "p134_b", "p134_team" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            foreach (var bot in bots)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                if (ship.Position.Length() < 1f) { seat.Miner = ship; seat.MinerBot = bot; }
                else if (ship.Position.Z > 1000f) { seat.CrossSide = ship; seat.CrossSideBot = bot; }
                else { seat.Teammate = ship; seat.TeammateBot = bot; }
            }
            Assert.NotNull(seat.Miner); Assert.NotNull(seat.CrossSide); Assert.NotNull(seat.Teammate);
            Assert.True(seat.Miner.SideId != seat.CrossSide.SideId, "cross-side peer must be on the other side from the miner");
            Assert.True(seat.Miner.SideId == seat.Teammate.SideId, "teammate must be same-side as the miner");
            seat.Miner.Velocity = Vector3.Zero;
            seat.Teammate.Velocity = Vector3.Zero;
            seat.CrossSide.Velocity = Vector3.Zero;
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    private static AsteroidStateEntry EntryOf(AsteroidsUpdate up, short asteroidId)
        => up.Entries.FirstOrDefault(e => e.AsteroidId == asteroidId);

    private static bool HasEntry(AsteroidsUpdate up, short asteroidId)
        => up.Entries.Any(e => e.AsteroidId == asteroidId);

    /// <summary>⭐ An asteroid visible to a side is delivered in that side's
    /// <c>ASTEROIDS_UPDATE</c> with the correct id, raw ore, div-240 health
    /// fraction and identity <c>co</c>; and the SAME asteroid is absent from a
    /// different side's updates (per-side batching — no whole-mission leak).
    /// A's update carries the rock; B (30 km away, sees nothing) receives no
    /// <c>ASTEROIDS_UPDATE</c> at all.</summary>
    [Fact]
    public void Visible_asteroid_received_correct_fields_absent_from_unseeing_side()
    {
        using var seat = SeatThree();
        var rock = seat.Rock;
        Assert.True(rock.SeenBySide(seat.Miner.SideId!.Value), "the miner's side must see the co-located rock (real sweep)");

        // Start counting afresh so every accumulated update below is post-seat.
        seat.MinerBot.ResetAsteroidsUpdates();
        seat.CrossSideBot.ResetAsteroidsUpdates();

        // Side A sees exactly one asteroid (the synthetic rock), so its periodic
        // update flows every tick; pump until it has at least one.
        PumpUntil(seat.Server, () => seat.MinerBot.AsteroidsUpdates.Count > 0, 2000);
        Assert.True(seat.MinerBot.AsteroidsUpdates.Count > 0, "side A must receive ASTEROIDS_UPDATE (it sees the rock)");

        var aUp = seat.MinerBot.AsteroidsUpdates[0];
        Assert.True(HasEntry(aUp, rock.Data.AsteroidId),
            $"A's update must contain the rock {rock.Data.AsteroidId}; got {string.Join(",", aUp.Entries.Select(e => e.AsteroidId))}");
        var e = EntryOf(aUp, rock.Data.AsteroidId);
        Assert.InRange(e.Ore, (short)0, (short)InitialOre);      // raw ore short within the live pool bounds
        Assert.Equal(Percent(rock.Fraction), e.BpFraction);       // health fraction (undamaged rock -> 240)
        Assert.Equal((byte)240, e.BpFraction);                    // full-health sanity
        Assert.Equal(CompactOrientation.Identity, e.Co);          // identity co (cited simplification)

        // B's side never sees the rock (its only scanner is 30 km away), and the
        // demo map has no other asteroids, so B receives NO ASTEROIDS_UPDATE at all.
        PumpUntil(seat.Server, () => seat.CrossSideBot.AsteroidsUpdates.Count > 0, 500);
        Assert.True(seat.CrossSideBot.AsteroidsUpdates.Count == 0,
            "side B must receive NO ASTEROIDS_UPDATE (it sees nothing)");

        Console.WriteLine($"P134-VISIBLE: rock {rock.Data.AsteroidId} (ore {e.Ore}/{InitialOre:0}, bp {e.BpFraction}/240, co identity) " +
                          $"-> A's ASTEROIDS_UPDATE(179); B gets none [PER SIDE]");
    }

    /// <summary>⭐ A real P102/P55 mining depletion (the side-A miner parked on
    /// the rock) is reflected in the NEXT <c>ASTEROIDS_UPDATE</c>: the <c>ore</c>
    /// short declines while <c>bpFraction</c> stays 240 — mining depletes the
    /// ore pool, not the damage/health pool, proving the two fields are
    /// genuinely non-redundant (fsship.cpp:909 ore=GetOre() vs :911
    /// bpFraction=GetFraction()).</summary>
    [Fact]
    public void Mining_depletion_reflected_in_next_update_while_health_fraction_stays_full()
    {
        using var seat = SeatThree();
        var rock = seat.Rock;

        seat.MinerBot.ResetAsteroidsUpdates();
        PumpUntil(seat.Server, () => seat.MinerBot.AsteroidsUpdates.Count >= 2, 2000);
        Assert.True(seat.MinerBot.AsteroidsUpdates.Count >= 2, "side A must receive a few updates while mining");

        // The FIRST received update carries a near-full ore; later ones must show
        // strictly less (mining drains ~5/tick, regen 0.025 is negligible).
        var first = EntryOf(seat.MinerBot.AsteroidsUpdates[0], rock.Data.AsteroidId);
        var last = EntryOf(seat.MinerBot.AsteroidsUpdates[^1], rock.Data.AsteroidId);
        Assert.True(first.Ore > last.Ore,
            $"ore must decline with real mining; first {first.Ore} -> last {last.Ore}");
        Assert.True(last.Ore < (short)InitialOre,
            $"last ore {last.Ore} must be below the initial {InitialOre:0}");
        Assert.Equal((byte)240, first.BpFraction);
        Assert.Equal((byte)240, last.BpFraction);   // health fraction unaffected by mining — not the ore fraction

        Console.WriteLine($"P134-ORE: rock {rock.Data.AsteroidId} ore {first.Ore}->{last.Ore} (of {InitialOre:0}) as the miner drains it; " +
                          $"bpFraction stays {last.BpFraction}/240 [ore != bpFraction CONFIRMED]");
    }

    /// <summary>⭐ Deterministic codec round-trip: <c>ASTEROIDS_UPDATE</c>(179)
    /// with several <see cref="AsteroidStateEntry"/>s survives Encode/Decode with
    /// the exact wire layout (fmid 179, one ib/cb var pair, count implicit in the
    /// blob length over the 13-byte pack(1) element stride).</summary>
    [Fact]
    public void AsteroidsUpdate_codec_round_trips_multiple_entries()
    {
        var msg = new AsteroidsUpdate
        {
            Entries =
            [
                new AsteroidStateEntry((short)6721, CompactOrientation.Identity, (short)619, (byte)240),
                new AsteroidStateEntry((short)6730, new CompactOrientation(65535, 4567, -2345, 4321), (short)8123, (byte)137),
                new AsteroidStateEntry((short)6745, new CompactOrientation(0, 0, 0, 631), (short)-5, (byte)96),
            ],
        };

        byte[] wire = msg.Encode();
        Assert.Equal(8 + 3 * 13, wire.Length);         // FixedSize 8 + 3 entries × 13
        Assert.Equal((ushort)179, BitConverter.ToUInt16(wire, 2));   // fmid
        Assert.Equal((ushort)8, BitConverter.ToUInt16(wire, 4));     // ib = blob at the fixed-region end
        Assert.Equal((ushort)39, BitConverter.ToUInt16(wire, 6));    // cb = 3 × 13
        Assert.Equal((ushort)wire.Length, BitConverter.ToUInt16(wire, 0));   // cbmsg

        var dec = AsteroidsUpdate.Decode(wire);
        Assert.Equal(3, dec.Entries.Length);
        Assert.Equal((6721, CompactOrientation.Identity, (short)619, (byte)240),
            (dec.Entries[0].AsteroidId, dec.Entries[0].Co, dec.Entries[0].Ore, dec.Entries[0].BpFraction));
        Assert.Equal((6730, new CompactOrientation(65535, 4567, -2345, 4321), (short)8123, (byte)137),
            (dec.Entries[1].AsteroidId, dec.Entries[1].Co, dec.Entries[1].Ore, dec.Entries[1].BpFraction));
        Assert.Equal((6745, new CompactOrientation(0, 0, 0, 631), (short)-5, (byte)96),
            (dec.Entries[2].AsteroidId, dec.Entries[2].Co, dec.Entries[2].Ore, dec.Entries[2].BpFraction));

        Console.WriteLine($"P134-CODEC: ASTEROIDS_UPDATE(179) {msg.Entries.Length} entries, wire {wire.Length} B, stride 13, round-tripped");
    }
}
