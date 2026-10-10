using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P145 — the <c>FM_C_TREASURE_ACK(163)</c>/<c>FM_S_ADD_PART(164)</c>
/// part-treasure pickup round-trip, proven over a real in-process server + real
/// TCP bots. Covers:
///   (1) a rescue-capable ship touching a part-type treasure destroys it
///       IMMEDIATELY (DESTROY_TREASURE broadcast) and sends ACQUIRE_TREASURE(162)
///       to the picking player's OWN connection only (a bystander in the same
///       sector does NOT receive it) — with the correct part-type id + amount;
///   (2) accepting with a real mount merges the amount into an existing same-type
///       part at that mount (server-side sum) and broadcasts ADD_PART(164)
///       sector-wide carrying the GRANTED amount;
///   (3) accepting into an EMPTY mount mounts a fresh part there;
///   (4) declining (MountNA) re-creates a NEW part treasure at the ship's current
///       position (not the original touch point);
///   (5) touching a second part-treasure while an offer is already pending still
///       destroys it but sends NO second ACQUIRE_TREASURE.
/// </summary>
[Collection("LiveNetwork")]
public class AddPartTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private const short FlySector = 1052;   // XXV_simple: no-station fly sector (P111 report)

    private static readonly Vector3 FarAway = new(1000f, 990f, 985f);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Add Part Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 553u, DwCookie = 0x553u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 5590000000000000000ul, DrmHash = "addparttest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static StaticCore Core() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>A real weapon part id that fits the pickup ship's fixed hardpoint 0
    /// (PartMask subset) — so both the pre-mount and the ack's CanMount hold.</summary>
    private static short WeaponForMount0(Ship pickup)
    {
        short hp0 = pickup.HullType.Hardpoints[0].PartMask;
        var w = Core().PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon && p.PartId != 0)
            .OrderBy(p => p.PartId)
            .FirstOrDefault(p => (p.PartMask & ~hp0) == 0);
        if (w.PartId == 0)
            throw new InvalidOperationException("need a weapon fitting hardpoint 0 in representative core");
        return w.PartId;
    }

    /// <summary>Spawn injection: ship 0 is the rescue-capable pickup (side 0) at
    /// <paramref name="pickupPos"/>; ships 1/2 are a same-side bystander and a
    /// cross-side outsider (side 1), both far away and non-rescue. The holder's
    /// <see cref="PickupHolder.Pickup"/> is populated by the spawner when the ship
    /// is created (during Boot), so it is read only after Boot/Seat.</summary>
    private sealed class PickupHolder { public Ship Pickup = null!; }

    private static PickupHolder InjectPickup(Srv server, Vector3 pickupPos)
    {
        var holder = new PickupHolder();
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            // A hull with >= 1 hardpoint that actually hosts some weapon, so the
            // ack's CanMount has a real target (the P123 cash-only tests used the
            // bare min hull, which has no weapon mounts — fine there, not here).
            var baseHull = core.HullTypes.Values
                .OrderBy(h => h.HullId)
                .First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord)
                         && h.Hardpoints.Length >= 1
                         && core.PartTypes.Values.Any(p => p.EquipmentType == EquipmentType.Weapon && p.PartId != 0 && (p.PartMask & ~h.Hardpoints[0].PartMask) == 0));
            Ship ship = n switch
            {
                0 => new Ship(shipId, baseHull with { HabmCapabilities = DataHullTypeIGC.HabmRescue }, pickupPos, Vector3.Zero, Orientation.Identity) { SideId = 0 },
                1 => new Ship(shipId, baseHull with { HabmCapabilities = 0 }, FarAway, Vector3.Zero, Orientation.Identity) { SideId = 0 },
                _ => new Ship(shipId, baseHull with { HabmCapabilities = 0 }, FarAway, Vector3.Zero, Orientation.Identity) { SideId = 1 },
            };
            mission.PlaceShip(ship, fly);
            if (n == 0) holder.Pickup = ship;
            n++;
            return (ship, fly);
        };
        return holder;
    }

    private static Treasure PlacePartTreasure(Srv server, Vector3 pos, short partId, short amount, short oid)
    {
        var mission = server.Missions[0];
        var fly = mission.Sectors.First(s => s.Id == FlySector);
        var data = new DataTreasureIGC(
            P0: [pos.X, pos.Y, pos.Z], V0: [0f, 0f, 0f], Lifespan: 9999f, Time0: 0u,
            ObjectId: oid, TreasureId: partId, ClusterId: FlySector, Amount: amount,
            TreasureCode: TreasureCode.Part, CreateNow: true);
        var t = new Treasure(oid, fly, data, 0.0);
        fly.Add(t);
        return t;
    }

    private static bool SectorHasTreasure(Srv server, short oid) =>
        server.Missions[0].Sectors.First(s => s.Id == FlySector).Models.OfType<Treasure>().Any(t => t.Id == oid);

    private static FltBot[] Seat(Srv server, params string[] names)
    {
        var bots = names.Select(name => { var b = new FltBot(); b.Connect(server.BoundPort, name); b.SendLogonRequest(Request(name)); return b; }).ToArray();
        PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 6000);
        return bots;
    }

    // ── Pin the ADD_PART(164) wire layout: header + shipID short + 5-byte PartData + 1 pad = 12 B ──
    [Fact]
    public void AddPart_codec_roundtrips_fixed_12_byte_layout()
    {
        var msg = new AddPart
        {
            ShipID = 4242,
            NewPartData = new PartData(PartId: 7001, Amount: 25, MountId: 3),
        };

        var encoded = msg.Encode();
        Assert.Equal(12, encoded.Length);
        Assert.Equal((ushort)12, FedMessageHeader.Read(encoded).CbMsg);
        Assert.Equal(AddPart.Fmid, FedMessageHeader.Read(encoded).Fmid);

        var again = AddPart.Decode(encoded);
        Assert.Equal((short)4242, again.ShipID);
        Assert.Equal((short)7001, again.NewPartData.PartId);
        Assert.Equal((short)25, again.NewPartData.Amount);
        Assert.Equal((byte)3, again.NewPartData.MountId);

        // Native structural layout: bytes @4/5 shipID LE, @6/7 partID LE, @8/9
        // amount LE, @10 mountID, @11 pad. (4242 = 0x1092; 7001 = 0x1B59.)
        byte[] native = [0x0C, 0x00, 0xA4, 0x00, 0x92, 0x10, 0x59, 0x1B, 0x19, 0x00, 0x03, 0x00];
        var nat = AddPart.Decode(native);
        Assert.Equal((short)4242, nat.ShipID);
        Assert.Equal((short)7001, nat.NewPartData.PartId);
        Assert.Equal((short)25, nat.NewPartData.Amount);
        Assert.Equal((byte)3, nat.NewPartData.MountId);
    }

    // ── Pin TREASURE_ACK(163): 6 B, and MountId is a REAL variable field (the
    //    "always NA" header comment is stale — see TreasureAck.cs) ──────────
    [Fact]
    public void TreasureAck_mount_id_is_a_real_variable_field()
    {
        var decl = new TreasureAck { MountId = TreasureAck.MountNA };
        var acc = new TreasureAck { MountId = 4 };

        var declEnc = decl.Encode();
        Assert.Equal(6, declEnc.Length);
        Assert.Equal((byte)TreasureAck.MountNA, declEnc[4]);
        Assert.Equal(TreasureAck.MountNA, TreasureAck.Decode(declEnc).MountId);

        var accEnc = acc.Encode();
        Assert.Equal((byte)4, accEnc[4]);
        var accDec = TreasureAck.Decode(accEnc);
        Assert.Equal((byte)4, accDec.MountId);
        Assert.NotEqual(TreasureAck.MountNA, accDec.MountId);   // a real choice is preserved, not forced to NA
    }

    // ── Scenario 1: pickup destroys immediately + offers only to the picker,
    //    then accepting into an existing mount MERGES the amount ────────────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Part_pickup_destroys_then_accept_merges_amount_into_existing_mount()
    {
        Require.Artwork();
        using var server = new Srv();
        var holder = InjectPickup(server, Vector3.Zero);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        var bots = Seat(server, "PA", "PB");
        var pickup = holder.Pickup;
        short pidW = WeaponForMount0(pickup);

        // Pre-mount weapon W at mount 0, amount 10 (for the merge case).
        pickup.MountPart(Core().PartTypes[pidW], 10, 0, ExpandedPartData.FullyMounted);
        Assert.Equal((short)10, pickup.Parts.Single(p => p.MountId == 0).Amount);

        // Place a part treasure of the SAME type, amount 5, at the ship's position.
        var treasure = PlacePartTreasure(server, pickup.Position, pidW, 5, 9930);

        foreach (var b in bots) { b.ResetDestroyTreasures(); b.ResetAcquireTreasures(); b.ResetAddParts(); }

        // The pickup destroys the treasure and offers only to the picker.
        int n = PumpUntil(server,
            () => bots[0].DestroyTreasures.Count >= 1 && bots[0].AcquireTreasures.Count >= 1
               && bots[1].DestroyTreasures.Count >= 1 && !SectorHasTreasure(server, treasure.Id),
            3000);
        Assert.True(n < 3000, "pickup did not destroy + offer in time");
        Assert.False(SectorHasTreasure(server, treasure.Id), "treasure must be destroyed on touch (KillTreasureEvent always fires)");

        // DESTROY_TREASURE reaches both peers (whole-mission).
        foreach (var b in bots) Assert.Single(b.DestroyTreasures);

        // ACQUIRE_TREASURE reaches ONLY the picker (own connection), with the part id + amount.
        Assert.Single(bots[0].AcquireTreasures);
        var at = bots[0].AcquireTreasures.Single();
        Assert.Equal((byte)TreasureCode.Part, at.TreasureCode);
        Assert.Equal(pidW, at.TreasureId);
        Assert.Equal((short)5, at.Amount);
        Assert.Empty(bots[1].AcquireTreasures);   // bystander gets NO offer

        // Offer buffered server-side until the ack.
        Assert.NotNull(pickup.PendingTreasureOffer);
        Assert.Equal(pidW, pickup.PendingTreasureOffer.Value.PartTypeId);
        Assert.Equal((short)5, pickup.PendingTreasureOffer.Value.Amount);

        // Accept into mount 0 (already holding the same type) → merge to 15.
        bots[0].SendTreasureAck(0);

        n = PumpUntil(server,
            () => pickup.PendingTreasureOffer is null
               && pickup.Parts.Single(p => p.MountId == 0).Amount == 15
               && bots[0].AddParts.Count >= 1 && bots[1].AddParts.Count >= 1,
            3000);
        Assert.True(n < 3000, "accept did not merge + broadcast ADD_PART in time");

        // Server-side merged amount = 10 + 5.
        Assert.Equal((short)15, pickup.Parts.Single(p => p.MountId == 0).Amount);
        Assert.Null(pickup.PendingTreasureOffer);

        // ADD_PART(164) sector-wide (both peers), carrying the GRANTED amount (5), not 15.
        foreach (var b in bots)
        {
            var ap = Assert.Single(b.AddParts);
            Assert.Equal(pickup.Id, ap.ShipID);
            Assert.Equal(pidW, ap.NewPartData.PartId);
            Assert.Equal((short)5, ap.NewPartData.Amount);   // granted, not merged
            Assert.Equal((byte)0, ap.NewPartData.MountId);
        }

        Console.WriteLine($"ADDPART-MERGE: picker {pickup.Id} touched part {pidW}x5 -> destroyed, ACQUIRE_TREASURE(162) to picker only; ack mount0 -> merged 10+5=15, ADD_PART(164) part={pidW} amount=5 mount=0 sector-wide");
    }

    // ── Scenario 2: accepting into an EMPTY mount mounts a FRESH part ───────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Part_pickup_accept_into_empty_mount_mounts_fresh_part()
    {
        Require.Artwork();
        using var server = new Srv();
        var holder = InjectPickup(server, Vector3.Zero);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        var bots = Seat(server, "PA", "PB");
        var pickup = holder.Pickup;
        short pidW = WeaponForMount0(pickup);

        PlacePartTreasure(server, pickup.Position, pidW, 7, 9940);
        foreach (var b in bots) { b.ResetDestroyTreasures(); b.ResetAcquireTreasures(); b.ResetAddParts(); }

        int n = PumpUntil(server, () => bots[0].AcquireTreasures.Count >= 1, 3000);
        Assert.True(n < 3000, "pickup did not offer in time");
        Assert.Empty(pickup.Parts);   // no pre-existing part → fresh mount

        bots[0].SendTreasureAck(0);
        n = PumpUntil(server,
            () => pickup.Parts.SingleOrDefault(p => p.MountId == 0) is { } mp && mp.Amount == 7
               && bots[0].AddParts.Count >= 1 && bots[1].AddParts.Count >= 1,
            3000);
        Assert.True(n < 3000, "fresh mount did not appear in time");

        var mp = pickup.Parts.Single(p => p.MountId == 0);
        Assert.Equal(pidW, mp.PartId);
        Assert.Equal((short)7, mp.Amount);
        Assert.Null(pickup.PendingTreasureOffer);

        var ap = Assert.Single(bots[0].AddParts);
        Assert.Equal(pickup.Id, ap.ShipID);
        Assert.Equal(pidW, ap.NewPartData.PartId);
        Assert.Equal((short)7, ap.NewPartData.Amount);
        Assert.Equal((byte)0, ap.NewPartData.MountId);

        Console.WriteLine($"ADDPART-FRESH: picker {pickup.Id} touched part {pidW}x7 -> ack mount0 -> FRESH part mounted (amount 7), ADD_PART(164) sector-wide");
    }

    // ── Scenario 3: declining re-creates a NEW treasure at the current position ──
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Part_pickup_decline_recreates_treasure_at_current_position()
    {
        Require.Artwork();
        using var server = new Srv();
        var holder = InjectPickup(server, Vector3.Zero);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        var bots = Seat(server, "PA", "PB");
        var pickup = holder.Pickup;
        short pidW = WeaponForMount0(pickup);

        var original = PlacePartTreasure(server, pickup.Position, pidW, 9, 9950);
        foreach (var b in bots) { b.ResetDestroyTreasures(); b.ResetAcquireTreasures(); b.ResetTreasureSpawnsReceived(); }

        int n = PumpUntil(server, () => bots[0].AcquireTreasures.Count >= 1, 3000);
        Assert.True(n < 3000, "pickup did not offer in time");
        Assert.False(SectorHasTreasure(server, original.Id), "original must be destroyed on touch");

        // Move the ship AWAY from the touch point, then decline — the new treasure
        // must appear at the CURRENT (moved) position, not the original touch point.
        Vector3 moved = new(400f, 410f, 405f);
        pickup.Position = moved;
        for (int i = 0; i < 3; i++) server.TickSim();

        bots[0].SendTreasureAck(TreasureAck.MountNA);
        n = PumpUntil(server, () => bots[0].TreasureSpawns.Count >= 1 && pickup.PendingTreasureOffer is null, 3000);
        Assert.True(n < 3000, "decline did not re-create treasure in time");
        Assert.Null(pickup.PendingTreasureOffer);

        var data = Assert.Single(bots[0].TreasureSpawns).Data;
        Assert.Equal(TreasureCode.Part, data.TreasureCode);
        Assert.Equal(pidW, data.TreasureId);
        Assert.Equal((short)9, data.Amount);
        Assert.NotEqual(original.Id, data.ObjectId);   // a NEW object, not the original

        // The re-created treasure is a real model near the ship's CURRENT position.
        Assert.True(SectorHasTreasure(server, data.ObjectId), "re-created treasure must be in the sector");
        Vector3 p0 = new(data.P0[0], data.P0[1], data.P0[2]);
        float radius10 = 0.5f * pickup.HullType.Length + 10f;
        float distToMoved = (p0 - moved).Length();
        float distToOrigin = (p0 - Vector3.Zero).Length();
        Assert.InRange(distToMoved, 0f, radius10 + 6f);      // near the CURRENT position
        Assert.True(distToOrigin > radius10 * 2, "must NOT be at the original touch point");

        Console.WriteLine($"ADDPART-DECLINE: picker {pickup.Id} declined part {pidW}x9 -> re-created treasure obj={data.ObjectId} near moved pos ({distToMoved:F1} away), NOT at origin ({distToOrigin:F1} away)");
    }

    // ── Scenario 4: a second touch while an offer is pending is destroyed but NOT offered ──
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Second_part_touch_while_offer_pending_destroys_but_does_not_reoffer()
    {
        Require.Artwork();
        using var server = new Srv();
        var holder = InjectPickup(server, Vector3.Zero);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        var bots = Seat(server, "PA", "PB");
        var pickup = holder.Pickup;
        short pidW = WeaponForMount0(pickup);

        var t1 = PlacePartTreasure(server, pickup.Position, pidW, 4, 9960);
        foreach (var b in bots) { b.ResetDestroyTreasures(); b.ResetAcquireTreasures(); }

        int n = PumpUntil(server, () => bots[0].AcquireTreasures.Count >= 1, 3000);
        Assert.True(n < 3000, "first pickup did not offer in time");
        Assert.NotNull(pickup.PendingTreasureOffer);

        // Touch a SECOND part-treasure (different object) before acking the first.
        var t2 = PlacePartTreasure(server, pickup.Position, pidW, 6, 9961);
        n = PumpUntil(server,
            () => !SectorHasTreasure(server, t2.Id) && bots[0].DestroyTreasures.Count >= 2,
            3000);
        Assert.True(n < 3000, "second treasure was not destroyed in time");
        Assert.False(SectorHasTreasure(server, t2.Id), "second treasure must STILL be destroyed on touch");

        // But NO second ACQUIRE_TREASURE — still exactly one, with the FIRST offer's data.
        Assert.Single(bots[0].AcquireTreasures);
        var at = bots[0].AcquireTreasures.Single();
        Assert.Equal(pidW, at.TreasureId);
        Assert.Equal((short)4, at.Amount);
        // The buffered offer is UNCHANGED (first offer wins; the second touch was dropped).
        Assert.Equal(pidW, pickup.PendingTreasureOffer.Value.PartTypeId);
        Assert.Equal((short)4, pickup.PendingTreasureOffer.Value.Amount);

        Console.WriteLine($"ADDPART-PENDING: picker {pickup.Id} touched 2nd part treasure {t2.Id} while offer pending -> destroyed (DESTROY_TREASURE x2) but NO 2nd ACQUIRE_TREASURE (still 1, part {pidW}x4)");
    }
}
