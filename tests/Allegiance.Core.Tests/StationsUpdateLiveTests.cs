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
/// P133 — live loopback proof of the periodic per-side <c>STATIONS_UPDATE</c>(57)
/// broadcast (<see cref="Server.BroadcastStationsUpdate"/>, called every
/// <see cref="Srv.TickSim"/>), the first consumer of P131/P132's now-real
/// <see cref="Station.SeenBySide"/>:
/// <list type="number">
/// <item>a station owned by side A and visible to it is delivered in A's
/// <c>STATIONS_UPDATE</c> with the correct <c>stationID</c> and full div-240
/// hull/shield fractions;</item>
/// <item>the <b>same</b> station, not visible to side B (no B scanner in range),
/// is <b>absent</b> from B's update — per-side batching, each side receives only
/// what it can see, not the whole mission;</item>
/// <item>a real P125 <see cref="Station.ReceiveDamage"/> shield hit lowers
/// <see cref="Station.ShieldFraction"/>, and the side's <b>next</b>
/// <c>STATIONS_UPDATE</c> carries the new (lower) div-240 shield byte while the
/// hull stays full.</item>
/// </list>
/// Construction: a 3-bot / 2-side live server (round-robin A,B,A) with TWO
/// synthetic stations far apart on the z-axis — SA (id 9401) owned by side A at
/// z = −20000, SB (id 9501) owned by side B at z = +20000 — while all three ships
/// cluster near the origin, 20 km from either station. Nothing on side B has a
/// scanner in range of SA (nor side A of SB): every side-owned station is visible
/// only to its own side, so side A's update carries exactly its own base (with SA
/// present / SB absent) and side B's carries exactly its own (SB present / SA
/// absent) — the clean per-side-batching proof. The stations are placed via
/// <see cref="Mission.PlaceStation"/> in the ShipSpawner with a real core station
/// type <c>with</c>-modified to a chosen (shield-defense, capacity, regen) combo
/// so the shield-damage math is exact, exactly the StationCombatLive (P125) seam.
/// No Windows VM, no native client.
/// </summary>
[Collection("LiveNetwork")]
public sealed class StationsUpdateLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static float Dtm(StaticCore core, byte dmg, byte def) => core.Constants.DamageConstants[dmg][def];

    /// <summary>A (damage-type, shield-defense-index, dtmShield) triple where the
    /// damage type has a STRICTLY POSITIVE matrix cell for the shield defense
    /// index, so a real, non-degenerate shield-absorb resolves (borrowed from the
    /// StationCombatLive P125 harness, which needs the same property).</summary>
    private static readonly Lazy<(byte DmgType, byte DefShield, float Ds)> ShieldCombo = new(() =>
    {
        var core = Core();
        foreach (var dmg in Enumerable.Range(0, DataConstantsIGC.DmgidMax))
            for (byte def = 0; def < DataConstantsIGC.DefidMax; def++)
                if (Dtm(core, (byte)dmg, def) > 0f)
                    return ((byte)dmg, def, Dtm(core, (byte)dmg, def));
        throw new InvalidOperationException("no positive (damage-type, shield-defense) matrix cell in representative core");
    });

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Stations Update Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 793u, DwCookie = 0x793u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 78200000000000000ul, DrmHash = "stationsupdate",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static byte Percent(float f) => (byte)(f * 240.0f + 0.5);   // div-240 BytePercentage (Utility.h:1307-1312)

    /// <summary>A booted 3-bot / 2-side live server with the two far-apart
    /// synthetic stations. Slot 0 = <b>side A</b> near the origin, slot 1 =
    /// <b>side B</b> near the origin, slot 2 = <b>side A</b> near the origin —
    /// all ~20 km from both stations, so no side has a scanner in range of the
    /// OTHER side's station.</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship A = null!, B = null!, Teammate = null!;
        public FltBot ABot = null!, BBot = null!, TeammateBot = null!;
        public Station Sa = null!, Sb = null!;
        public Sector Sector = null!;

        public void Dispose()
        {
            foreach (var b in new[] { ABot, BBot, TeammateBot }) b?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatTwoBases(float maxShield, short sideAStationId, short sideBStationId)
    {
        var (dmgType, defShield, _) = ShieldCombo.Value;
        var seat = new Seated();
        var core = Core();
        seat.Server = new Srv();

        var saSpot = new Vector3(0f, 0f, -20000f);
        var sbSpot = new Vector3(0f, 0f, +20000f);
        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            if (seat.Sa is null)
            {
                var baseType = core.StationTypes.Values.First();
                // A shield-up, no-regen type on the chosen shield defense index,
                // with scanner range forced to 0 so neither synthetic station can
                // ever see ACROSS to the other side's base — only its own side.
                var type = baseType with
                {
                    MaxArmorHitPoints = 5000f,
                    MaxShieldHitPoints = maxShield,
                    DefenseTypeArmor = defShield,
                    DefenseTypeShield = defShield,
                    ShieldRegeneration = 0f,
                    ArmorRegeneration = 0f,
                    ScannerRange = 0f,
                    Radius = 40,
                };
                var saData = new DataStationIGC(
                    Position: [saSpot.X, saSpot.Y, saSpot.Z],
                    Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
                    ClusterId: sector.Id, SideId: 0, StationId: sideAStationId,
                    StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: "SYN_SA");
                seat.Sa = new Station(saData, type);
                mission.PlaceStation(seat.Sa, sector);

                var sbData = new DataStationIGC(
                    Position: [sbSpot.X, sbSpot.Y, sbSpot.Z],
                    Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
                    ClusterId: sector.Id, SideId: 1, StationId: sideBStationId,
                    StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: "SYN_SB");
                seat.Sb = new Station(sbData, type);
                mission.PlaceStation(seat.Sb, sector);
            }
            var hull = core.HullTypes.Values.OrderBy(h => h.HitPoints).First();
            var ship = placed switch
            {
                0 => new Ship(id, hull, new Vector3(0f, 400f, 0f), Vector3.Zero, Orientation.Identity),    // side A
                1 => new Ship(id, hull, new Vector3(0f, -400f, 0f), Vector3.Zero, Orientation.Identity),   // side B
                _ => new Ship(id, hull, new Vector3(0f, 0f, 860f), Vector3.Zero, Orientation.Identity),    // side A teammate
            };
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B,A
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable mission; bypass ready-up
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p133_a", "p133_b", "p133_team" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            foreach (var bot in bots)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                if (ship.Position.Y > 200f) { seat.A = ship; seat.ABot = bot; }
                else if (ship.Position.Y < -200f) { seat.B = ship; seat.BBot = bot; }
                else { seat.Teammate = ship; seat.TeammateBot = bot; }
            }
            Assert.NotNull(seat.A); Assert.NotNull(seat.B); Assert.NotNull(seat.Teammate);
            Assert.True(seat.A.SideId != seat.B.SideId, "B must be cross-side to A");
            Assert.True(seat.A.SideId == seat.Teammate.SideId, "teammate must be same-side as A");
            // P324 (M2): the visibility sweep runs every 10 sector ticks, so wait for
            // a sweep before asserting the own-side-rule preconditions (seating alone
            // may land between sweeps).
            PumpUntil(seat.Server, () => seat.Sa.SeenBySide(seat.A.SideId!.Value) && seat.Sb.SeenBySide(seat.B.SideId!.Value), 2000);
            Assert.True(seat.Sa.SeenBySide(seat.A.SideId!.Value), "SA must be visible to its owner side A (own-side rule)");
            Assert.True(seat.Sb.SeenBySide(seat.B.SideId!.Value), "SB must be visible to its owner side B (own-side rule)");
            seat.A.Velocity = Vector3.Zero;
            seat.B.Velocity = Vector3.Zero;
            seat.Teammate.Velocity = Vector3.Zero;
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    private static StationsUpdateEntry EntryOf(StationsUpdate up, short stationId)
        => up.Entries.FirstOrDefault(e => e.StationId == stationId);

    private static bool HasEntry(StationsUpdate up, short stationId)
        => up.Entries.Any(e => e.StationId == stationId);

    /// <summary>⭐ A station visible to a side is delivered in that side's
    /// <c>STATIONS_UPDATE</c> with the correct id and full div-240 fractions, and
    /// the SAME station is ABSENT from a different side's update (per-side
    /// batching — no whole-mission leak). A's latest update carries SA (not SB);
    /// B's latest carries SB (not SA).</summary>
    [Fact]
    public void Visible_station_received_correct_fields_absent_from_unseeing_side()
    {
        using var seat = SeatTwoBases(maxShield: 1000f, sideAStationId: 9401, sideBStationId: 9501);

        // Start counting afresh so every accumulated update below is post-seat.
        seat.ABot.ResetStationsUpdates();
        seat.BBot.ResetStationsUpdates();

        // Each side sees exactly one station (its own), so both sides' periodic
        // updates flow every tick; pump until each has at least one.
        PumpUntil(seat.Server,
            () => seat.ABot.StationsUpdates.Count > 0 && seat.BBot.StationsUpdates.Count > 0,
            2000);
        Assert.True(seat.ABot.StationsUpdates.Count > 0, "side A must receive STATIONS_UPDATE");
        Assert.True(seat.BBot.StationsUpdates.Count > 0, "side B must receive STATIONS_UPDATE");

        // Latest batch on each side: A's has SA (correct id + full fractions) and
        // NOT SB; B's has SB and NOT SA — the per-side-batching proof.
        var aUp = seat.ABot.StationsUpdates[^1];
        var bUp = seat.BBot.StationsUpdates[^1];
        Assert.True(HasEntry(aUp, seat.Sa.StationId), $"A's update must contain SA {seat.Sa.StationId}; got {string.Join(",", aUp.Entries.Select(e => e.StationId))}");
        Assert.False(HasEntry(aUp, seat.Sb.StationId), $"A's update must NOT contain the unseeing-side station SB {seat.Sb.StationId}");
        Assert.True(HasEntry(bUp, seat.Sb.StationId), $"B's update must contain SB {seat.Sb.StationId}");
        Assert.False(HasEntry(bUp, seat.Sa.StationId), $"B's update must NOT contain SA {seat.Sa.StationId} (no B scanner in range)");

        var sa = EntryOf(aUp, seat.Sa.StationId);
        Assert.Equal(Percent(seat.Sa.ArmorFraction), sa.BpHullFraction);
        Assert.Equal(Percent(seat.Sa.ShieldFraction), sa.BpShieldFraction);
        Assert.Equal((byte)240, sa.BpHullFraction);     // full-health sanity
        Assert.Equal((byte)240, sa.BpShieldFraction);

        Console.WriteLine($"P133-VISIBLE: SA {seat.Sa.StationId} (owner side {seat.A.SideId}) -> A's STATIONS_UPDATE " +
                          $"hull {sa.BpHullFraction}/240 shield {sa.BpShieldFraction}/240; B's update has SB only [PER SIDE]");
    }

    /// <summary>⭐ A real P125 <see cref="Station.ReceiveDamage"/> shield hit lowers
    /// <see cref="Station.ShieldFraction"/> and the side's NEXT <c>STATIONS_UPDATE</c>
    /// carries the new div-240 shield byte while the hull stays full.</summary>
    [Fact]
    public void Damaged_station_fraction_reflected_in_next_update()
    {
        var (dmgType, defShield, ds) = ShieldCombo.Value;
        // Size maxShield so the hit absorbs a comfortable, survivable shield chunk.
        float maxShield = MathF.Max(300f, MathF.Round(1000f * ds / 0.4f));
        using var seat = SeatTwoBases(maxShield, sideAStationId: 9411, sideBStationId: 9511);
        var bas = seat.Sa;

        // Only side A's bot matters here (it owns SA); start counting afresh.
        seat.ABot.ResetStationsUpdates();
        PumpUntil(seat.Server, () => seat.ABot.StationsUpdates.Count > 0, 2000);
        Assert.True(seat.ABot.StationsUpdates.Count > 0, "side A must receive STATIONS_UPDATE before damage");

        // A real cross-side hit through the P125 damage path (the launcher is the
        // side-B ship so the friendly-fire gate passes). Absorbed = P·ds/maxShield.
        float power = 1000f;
        float absorbed = power * ds / maxShield;
        Assert.True(absorbed > 0.05f && absorbed < 0.9f, $"shield absorb must be survivable; got {absorbed:F3}");
        DamageResult r = bas.ReceiveDamage(dmgType, power, 0.0, Vector3.Zero, Vector3.Zero, seat.B);
        Assert.NotEqual(DamageResult.NoDamage, r);
        Assert.NotEqual(DamageResult.Killed, r);
        float newShield = bas.ShieldFraction;
        Assert.True(newShield < 1f, "the shield must actually be lowered by the hit");
        Assert.Equal(1f, bas.ArmorFraction, 4);          // hull untouched

        // Pump until side A receives a message reflecting the changed shield byte.
        int got = PumpUntil(seat.Server,
            () => seat.ABot.StationsUpdates.Count > 0 && EntryOf(seat.ABot.StationsUpdates[^1], bas.StationId).BpShieldFraction != 240,
            3000);
        Assert.True(got < 3000, "side A's update must eventually reflect the lowered shield fraction");
        var entry = EntryOf(seat.ABot.StationsUpdates[^1], bas.StationId);
        Assert.Equal(Percent(newShield), entry.BpShieldFraction);
        Assert.Equal(Percent(bas.ArmorFraction), entry.BpHullFraction);
        Assert.Equal((byte)240, entry.BpHullFraction);   // hull stayed full

        Console.WriteLine($"P133-DAMAGE: SA {bas.StationId} shield {1f}->{newShield:0.###} " +
                          $"(dtmShield {ds:F3}, maxShield {maxShield:F0}) => next STATIONS_UPDATE shield byte {entry.BpShieldFraction}/240 [PER SIDE]");
    }

    /// <summary>⭐ Deterministic codec round-trip: <c>STATIONS_UPDATE</c>(57) with
    /// several <see cref="StationsUpdateEntry"/>s survives Encode/Decode with the
    /// exact wire layout (fmid 57, one ib/cb var pair, count implicit in the blob
    /// length over the 4-byte element stride).</summary>
    [Fact]
    public void StationsUpdate_codec_round_trips_multiple_entries()
    {
        var msg = new StationsUpdate
        {
            Entries =
            [
                new StationsUpdateEntry(9401, 240, 240),
                new StationsUpdateEntry(9520, 137, 96),
                new StationsUpdateEntry(9633, 0, 240),
            ],
        };

        byte[] wire = msg.Encode();
        Assert.Equal(8 + 3 * 4, wire.Length);          // FixedSize 8 + 3 entries × 4
        Assert.Equal((ushort)57, BitConverter.ToUInt16(wire, 2));   // fmid
        Assert.Equal((ushort)8, BitConverter.ToUInt16(wire, 4));    // ib = blob at the fixed-region end
        Assert.Equal((ushort)12, BitConverter.ToUInt16(wire, 6));   // cb = 3 × 4
        Assert.Equal((ushort)wire.Length, BitConverter.ToUInt16(wire, 0));   // cbmsg

        var dec = StationsUpdate.Decode(wire);
        Assert.Equal(3, dec.Entries.Length);
        Assert.Equal((9401, 240, 240), (dec.Entries[0].StationId, dec.Entries[0].BpHullFraction, dec.Entries[0].BpShieldFraction));
        Assert.Equal((9520, 137, 96), (dec.Entries[1].StationId, dec.Entries[1].BpHullFraction, dec.Entries[1].BpShieldFraction));
        Assert.Equal((9633, 0, 240), (dec.Entries[2].StationId, dec.Entries[2].BpHullFraction, dec.Entries[2].BpShieldFraction));

        Console.WriteLine($"P133-CODEC: STATIONS_UPDATE(57) {msg.Entries.Length} entries, wire {wire.Length} B round-tripped");
    }
}
