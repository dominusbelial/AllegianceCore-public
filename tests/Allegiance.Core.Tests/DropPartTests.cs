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
/// P144 — the <c>FM_CS_DROP_PART(33)</c> loopback proof. Three real bots over
/// real loopback TCP (3 bots / 2 teams round-robin ⇒ a guaranteed same-side
/// pair and a cross-side enemy, all spawned in the same starter sector — the
/// same construction as <see cref="SwapPartTests"/>). The subject mounts a
/// non-empty weapon part and drops it; asserts (a) the part is really removed
/// from the ship's own loadout server-side, (b) the <c>DROP_PART</c> echo
/// reaches <b>every</b> ship in the sector — the same-side observer <em>and</em>
/// the cross-side enemy, proving the sector-wide (not side-wide)
/// <c>GetGroupSectorFlying</c> target of native <c>FedSrv.CPP:6312</c> — with
/// the resolved shipID/et/mount, and (c) a <c>FM_S_TREASURE(28)</c> is sent
/// carrying a <see cref="DataTreasureIGC"/> whose P0/V0 are plausible (offset
/// from the ship by ~radius+10, moving away at ~25) and whose
/// <c>TreasureCode</c>/<c>TreasureId</c>/<c>Amount</c>/<c>Lifespan</c>/
/// <c>CreateNow</c>/<c>ClusterId</c> match the dropped part and the native
/// <c>CreateTreasure</c> maths (<c>FedSrv.CPP:4324-4396</c>). Also exercises
/// the skip case: dropping an empty (<c>Amount == 0</c>) <c>Pack</c> still
/// broadcasts <c>DROP_PART</c> but sends no <c>FM_S_TREASURE</c>.
/// </summary>
[Collection("LiveNetwork")]
public class DropPartTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>The smallest real weapon part id from the loaded core.</summary>
    private static short OneRealWeapon()
    {
        var w = Core().PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon)
            .OrderBy(p => p.PartId)
            .Select(p => p.PartId)
            .FirstOrDefault();
        if (w == 0)
            throw new InvalidOperationException("need >= 1 weapon in representative core");
        return w;
    }

    /// <summary>The smallest real Pack part id from the loaded core.</summary>
    private static short OneRealPack()
    {
        var p = Core().PartTypes.Values
            .Where(pt => pt.EquipmentType == EquipmentType.Pack)
            .OrderBy(pt => pt.PartId)
            .Select(pt => pt.PartId)
            .FirstOrDefault();
        if (p == 0)
            throw new InvalidOperationException("need >= 1 Pack part in representative core");
        return p;
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Drop Part Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 434u, DwCookie = 0x34u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 34800000000000000ul, DrmHash = "dropparttest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Pin the wire layout: fixed 10 B (header 4 + shipID/et shorts
    /// @4/@6 + mount char @8 + 1 B align pad), fmid 33, and a lossless
    /// round-trip of all three fields.</summary>
    [Fact]
    public void DropPart_codec_roundtrips_fixed_10_byte_layout()
    {
        var msg = new DropPart
        {
            ShipID = 5678,
            Et = EquipmentType.Weapon,
            Mount = 3,
        };

        var encoded = msg.Encode();
        Assert.Equal(10, encoded.Length);
        Assert.Equal((ushort)10, FedMessageHeader.Read(encoded).CbMsg);
        Assert.Equal(DropPart.Fmid, FedMessageHeader.Read(encoded).Fmid);

        var again = DropPart.Decode(encoded);
        Assert.Equal((short)5678, again.ShipID);
        Assert.Equal(EquipmentType.Weapon, again.Et);
        Assert.Equal((byte)3, again.Mount);

        // Native structural layout (Messages.h:266-270): bytes @4/5 shipID LE,
        // @6/7 et LE, @8 mount, @9 pad. (5667 = 0x1623; et Weapon=1.)
        byte[] native = [0x0A, 0x00, 0x21, 0x00, 0x2E, 0x16, 0x01, 0x00, 0x03, 0x00];
        var nat = DropPart.Decode(native);
        Assert.Equal((short)5678, nat.ShipID);
        Assert.Equal(EquipmentType.Weapon, nat.Et);
        Assert.Equal((byte)3, nat.Mount);
    }

    /// <summary>Pin the <c>FM_S_TREASURE(28)</c> layout: fixed 48 B (header 4 +
    /// the 44-byte <see cref="DataTreasureIGC"/>), fmid 28, and a lossless
    /// round-trip of the embedded struct.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void TreasureSpawn_codec_roundtrips_fixed_48_byte_layout()
    {
        Require.Artwork();
        var msg = new TreasureSpawn
        {
            Data = new DataTreasureIGC(
                P0: [1f, 2f, 3f],
                V0: [4f, 5f, 6f],
                Lifespan: 120f,
                Time0: 999u,
                ObjectId: 321,
                TreasureId: 987,
                ClusterId: 555,
                Amount: 20,
                TreasureCode: TreasureCode.Part,
                CreateNow: false),
        };

        var encoded = msg.Encode();
        Assert.Equal(48, encoded.Length);
        Assert.Equal(TreasureSpawn.Fmid, FedMessageHeader.Read(encoded).Fmid);

        var again = TreasureSpawn.Decode(encoded);
        Assert.Equal([1f, 2f, 3f], again.Data.P0);
        Assert.Equal([4f, 5f, 6f], again.Data.V0);
        Assert.Equal(120f, again.Data.Lifespan);
        Assert.Equal(999u, again.Data.Time0);
        Assert.Equal((short)321, again.Data.ObjectId);
        Assert.Equal((short)987, again.Data.TreasureId);
        Assert.Equal((short)555, again.Data.ClusterId);
        Assert.Equal((short)20, again.Data.Amount);
        Assert.Equal(TreasureCode.Part, again.Data.TreasureCode);
        Assert.False(again.Data.CreateNow);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void DropPart_drops_weapon_sectorwide_with_treasure_then_empty_pack_skips()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        // P368: default spawn now seats each side in its own home sector. This
        // test's discriminator needs the cross-side enemy CO-LOCATED with the
        // subject (a sector-wide-vs-side-wide broadcast-audience check), so we
        // deliberately co-spawn all three peers in one sector via the injected
        // spawner seam (the same technique Explorer/LiveGainFlag use) — a
        // controlled scene, not the production default.
        int seq = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            short side = mission.Sides[seq % mission.Sides.Count].Id;
            var scouts = core.HullTypes.Values
                .Where(h => h.Name.Contains("Scout", StringComparison.OrdinalIgnoreCase))
                .OrderBy(h => h.HullId)
                .ToList();
            var hull = scouts.Count > 0 ? scouts[0] : core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(288f, 773f, 867f), Vector3.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(ship, mission.Sectors[0]);
            seq++;
            return (ship, mission.Sectors[0]);
        };
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p144_subject", "p144_observer", "p144_enemy" };
        try
        {
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i].Connect(server.BoundPort, names[i]);
                bots[i].SendLogonRequest(Request(names[i]));
            }
            PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            var ships = server.Ships.ToDictionary(s => s.Ship.Id, s => s.Ship.SideId);
            short[] ids = bots.Select(b => b.ShipAssignment!.Ship.ShipId).ToArray();

            int subject = -1, observer = -1, enemy = -1;
            for (int i = 0; i < bots.Length && observer < 0; i++)
                for (int j = 0; j < bots.Length; j++)
                    if (j != i && ships[ids[j]] == ships[ids[i]]) { subject = i; observer = j; break; }
            Assert.True(subject >= 0, "round-robin must yield a same-side pair");
            for (int k = 0; k < bots.Length; k++)
                if (k != subject && k != observer) { enemy = k; break; }
            Assert.True(enemy >= 0 && ships[ids[enemy]] != ships[ids[subject]],
                "3 bots / 2 teams must leave one bot cross-side");

            short subjectId = ids[subject];
            var subBot = bots[subject];
            var obsBot = bots[observer];
            var eneBot = bots[enemy];

            var subjectSector = server.Ships.Single(s => s.Ship.Id == subjectId).Ship.Sector!.Id;
            Assert.Equal(subjectSector, server.Ships.Single(s => s.Ship.Id == ids[enemy]).Ship.Sector!.Id);
            Assert.Equal(subjectSector, server.Ships.Single(s => s.Ship.Id == ids[observer]).Ship.Sector!.Id);

            Ship Subj() => server.Ships.Single(s => s.Ship.Id == subjectId).Ship;

            // ── Phase 1: drop a non-empty mounted WEAPON at mount 0 ────────────
            short pidW = OneRealWeapon();
            var subjectHull = Subj().HullType.HullId;
            // H-06: prepare the flying inventory directly; the wire action
            // under test is DROP_PART, not an in-flight purchase.
            Subj().MountPart(Subj().Mission!.Core.PartTypes[pidW], 20, 0, ExpandedPartData.FullyMounted);
            Assert.Single(Subj().Parts);
            Assert.Equal(pidW, Subj().Parts.Single(p => p.MountId == 0).PartId);

            var shipPos0 = Subj().Position;
            var shipVel0 = Subj().Velocity;
            float radius10 = 0.5f * Subj().HullType.Length + 10f;

            foreach (var b in bots) { b.ResetDropPartsReceived(); b.ResetTreasureSpawnsReceived(); }
            subBot.SendDropPart(EquipmentType.Weapon, mount: 0);

            PumpUntil(server,
                () => Subj().Parts.Count == 0
                   && obsBot.DropParts.Count >= 1
                   && eneBot.DropParts.Count >= 1
                   && subBot.DropParts.Any(d => d.ShipID == subjectId && d.Et == EquipmentType.Weapon && d.Mount == 0)
                   && obsBot.TreasureSpawns.Count >= 1
                   && eneBot.TreasureSpawns.Count >= 1,
                4000);

            // (a) The part is really REMOVED from the ship's own loadout.
            Assert.Empty(Subj().Parts);
            Assert.Null(Subj().Parts.SingleOrDefault(p => p.MountId == 0));

            // (b) DROP_PART(33) reaches every sector peer with resolved fields.
            var obsv = obsBot.DropParts.Single();
            Assert.Equal(subjectId, obsv.ShipID);
            Assert.Equal(EquipmentType.Weapon, obsv.Et);
            Assert.Equal((byte)0, obsv.Mount);
            var enemv = eneBot.DropParts.Single();
            Assert.Equal(subjectId, enemv.ShipID);
            Assert.Equal(EquipmentType.Weapon, enemv.Et);
            Assert.Equal((byte)0, enemv.Mount);
            // The requester's own echo comes back too (it is flying in-cluster).
            Assert.Equal(1, subBot.DropParts.Count(d => d.ShipID == subjectId && d.Et == EquipmentType.Weapon && d.Mount == 0));

            // (c) TREASURE(28): every sector peer saw the part treasure.
            var data = obsBot.TreasureSpawns.Single().Data;
            Assert.Single(eneBot.TreasureSpawns);
            var dataEnemy = eneBot.TreasureSpawns.Single().Data;
            Assert.Equal(data.ObjectId, dataEnemy.ObjectId);

            // Structural fields match the dropped part / native CreateTreasure maths.
            Assert.Equal(TreasureCode.Part, data.TreasureCode);          // c_tcPart (FedSrv.CPP:4367)
            Assert.Equal(pidW, data.TreasureId);                          // part's PartId (FedSrv.CPP:4369)
            Assert.Equal((short)20, data.Amount);                         // dropped part's Amount
            Assert.Equal((short)subjectSector, data.ClusterId);           // pcluster id (FedSrv.CPP:4335)
            Assert.Equal(120f, data.Lifespan);                            // DROP_PART lifespan arg (FedSrv.CPP:6326)
            Assert.False(data.CreateNow);                                 // createNow = false (FedSrv.CPP:4372)
            Assert.NotEqual((short)0, data.ObjectId);                     // Mission.NextObjectId()

            // Plausible kinematics: P0 offset from the ship by ~radius+10, V0
            // moving away from the ship at ~25 (plus inherited ship velocity).
            Vector3 p0 = new(data.P0[0], data.P0[1], data.P0[2]);
            Vector3 v0 = new(data.V0[0], data.V0[1], data.V0[2]);
            Vector3 offset = p0 - shipPos0;
            Vector3 away = v0 - shipVel0;
            float dist = offset.Length();
            Assert.InRange(dist, radius10 - 6f, radius10 + 6f);   // |offset| ≈ radius + 10
            Assert.InRange(away.Length(), 25f - 6f, 25f + 6f);    // |V0 - shipVel| ≈ 25
            // The offset and the away velocity share the same random unit
            // direction → (nearly) parallel, same sense.
            float cosAngle = (offset * away) / (offset.Length() * away.Length());
            Assert.InRange(cosAngle, 0.995f, 1.005f);

            // The treasure is a real model in the sector.
            Assert.Contains(Subj().Sector!.Models.OfType<Treasure>(),
                t => t.Data.ObjectId == data.ObjectId && t.Code == TreasureCode.Part);

            // ── Phase 2: drop an EMPTY (Amount==0) PACK at mount 0 ────────────
            short pidP = OneRealPack();
            Assert.NotEqual(pidW, pidP);
            // H-07 / hullTypeIGC.h:199-213: non-weapons mount at zero.
            Subj().MountPart(Subj().Mission!.Core.PartTypes[pidP], 0, 0, ExpandedPartData.FullyMounted);
            Assert.Single(Subj().Parts);
            Assert.Equal(pidP, Subj().Parts.Single(p => p.MountId == 0).PartId);
            Assert.Equal((short)0, Subj().Parts.Single(p => p.MountId == 0).Amount);

            foreach (var b in bots) { b.ResetDropPartsReceived(); b.ResetTreasureSpawnsReceived(); }
            subBot.SendDropPart(EquipmentType.Pack, mount: 0);

            // DROP_PART still broadcasts sector-wide…
            PumpUntil(server,
                () => Subj().Parts.Count == 0 && obsBot.DropParts.Count >= 1 && eneBot.DropParts.Count >= 1,
                4000);
            Assert.Empty(Subj().Parts);
            Assert.Equal(subjectId, obsBot.DropParts.Single().ShipID);
            Assert.Equal(EquipmentType.Pack, obsBot.DropParts.Single().Et);
            Assert.Equal((byte)0, obsBot.DropParts.Single().Mount);

            // …but NO TREASURE follows (empty Pack is the native skip).
            PumpUntil(server, () => false, 20);   // let any wrongful treasure arrive
            Assert.Empty(obsBot.TreasureSpawns);
            Assert.Empty(eneBot.TreasureSpawns);
            Assert.Empty(subBot.TreasureSpawns);
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }
}
