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
/// P143 — the <c>FM_CS_SWAP_PART(34)</c> loopback proof. Three real bots over
/// real loopback TCP (3 bots / 2 teams round-robin ⇒ a guaranteed same-side
/// pair and a cross-side enemy, all spawned in the same starter sector — the
/// same construction as <see cref="TeammateLoadoutBroadcastTests"/>). The
/// subject mounts two <b>distinct</b> weapon parts at mounts 0 and 1, then
/// issues <c>SWAP_PART</c>. Asserts (a) the ship's own two parts really
/// exchange mount slots server-side, (b) the broadcast reaches <b>every</b>
/// ship in the sector — the same-side observer <em>and</em> the cross-side
/// enemy, proving the sector-wide (not side-wide) <c>GetGroupSectorFlying</c>
/// target of native <c>FedSrv.CPP:6498</c> — and (c) the broadcast's
/// shipID/etOld/mountOld/mountNew match. Also exercises the plain-move case
/// (destination slot empty) and the silent-no-op case (source slot empty ⇒ no
/// broadcast to anyone).
/// </summary>
[Collection("LiveNetwork")]
public class SwapPartTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>Two distinct real weapon part ids from the loaded core, the
    /// smallest two (ordered by PartId), so we can distinguish which part ended
    /// up on which mount after a swap.</summary>
    private static (short a, short b) TwoRealWeapons()
    {
        var weapons = Core().PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon && p.PartMask == 1)
            .OrderBy(p => p.PartId)
            .Select(p => p.PartId)
            .Take(2)
            .ToArray();
        if (weapons.Length < 2)
            throw new InvalidOperationException("need >= 2 distinct weapons in representative core");
        return (weapons[0], weapons[1]);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Swap Part Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 434u, DwCookie = 0x34u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 34000000000000000ul, DrmHash = "swapparttest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Pin the wire layout: fixed 10 B (header 4 + shipID/etOld shorts
    /// @4/@6 + mountOld/mountNew chars @8/@9; no tail padding), fmid 34, and a
    /// lossless round-trip of all four fields.</summary>
    [Fact]
    public void SwapPart_codec_roundtrips_fixed_10_byte_layout()
    {
        var msg = new SwapPart
        {
            ShipID = 4321,
            EtOld = EquipmentType.Weapon,
            MountOld = 0,
            MountNew = 1,
        };

        var encoded = msg.Encode();
        Assert.Equal(10, encoded.Length);
        Assert.Equal((ushort)10, FedMessageHeader.Read(encoded).CbMsg);
        Assert.Equal(SwapPart.Fmid, FedMessageHeader.Read(encoded).Fmid);

        var again = SwapPart.Decode(encoded);
        Assert.Equal((short)4321, again.ShipID);
        Assert.Equal(EquipmentType.Weapon, again.EtOld);
        Assert.Equal((sbyte)0, again.MountOld);
        Assert.Equal((sbyte)1, again.MountNew);

        // Native structural layout (Messages.h:273-278): bytes @4/5 shipID LE,
        // @6/7 etOld LE, @8 mountOld, @9 mountNew; no tail padding.
        byte[] native = [0x0A, 0x00, 0x22, 0x00, 0xE1, 0x10, 0x01, 0x00, 0x00, 0x01];
        Assert.Equal(native, encoded);
        var nat = SwapPart.Decode(native);
        Assert.Equal((short)4321, nat.ShipID);
        Assert.Equal(EquipmentType.Weapon, nat.EtOld);
        Assert.Equal((sbyte)0, nat.MountOld);
        Assert.Equal((sbyte)1, nat.MountNew);
    }

    /// <summary>Key the requesting ship's live <c>_parts</c> by mount slot →
    /// partId, so we can assert slot exchanges concretely.</summary>
    private static Dictionary<byte, short> MountsByPartId(Ship ship) =>
        ship.Parts.ToDictionary(p => p.MountId, p => p.PartId);

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SwapPart_swaps_two_occupied_slots_sectorwide_moves_and_silent_noop()
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
            // H-07 / hullTypeIGC.h:199-213: all three requested gun slots
            // must accept both real weapons (a one-hardpoint Scout cannot).
            var guns = TwoRealWeapons();
            var hull = core.HullTypes.Values.First(h => h.GroupId >= 0 &&
                Enumerable.Range(0, 3).All(m => h.CanMountPart(core.PartTypes[guns.a], m) && h.CanMountPart(core.PartTypes[guns.b], m)));
            var ship = new Ship(shipId, hull, new Vector3(288f, 771f, 866f), Vector3.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(ship, mission.Sectors[0]);
            seq++;
            return (ship, mission.Sectors[0]);
        };
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p143_subject", "p143_observer", "p143_enemy" };
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

            // Round-robin side assignment ⇒ a same-side pair and a cross-side enemy.
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
            short observerId = ids[observer];
            short enemyId = ids[enemy];
            var subBot = bots[subject];
            var obsBot = bots[observer];
            var eneBot = bots[enemy];

            // The cross-side enemy must actually share the subject's sector for the
            // sector-wide (not side-wide) assertion to be meaningful — confirm it.
            var subjectSector = server.Ships.Single(s => s.Ship.Id == subjectId).Ship.Sector!.Id;
            Assert.Equal(subjectSector, server.Ships.Single(s => s.Ship.Id == enemyId).Ship.Sector!.Id);
            Assert.Equal(subjectSector, server.Ships.Single(s => s.Ship.Id == observerId).Ship.Sector!.Id);

            // Seat the subject with two DISTINCT weapons at mounts 0 and 1.
            var (pidA, pidB) = TwoRealWeapons();
            var subjectHull = server.Ships.Single(s => s.Ship.Id == subjectId).Ship.HullType.HullId;
            var twoParts = new[]
            {
                new ExpandedPartData(pidA, Amount: 20, MountId: 0, FractionMounted: ExpandedPartData.FullyMounted),
                new ExpandedPartData(pidB, Amount: 20, MountId: 1, FractionMounted: ExpandedPartData.FullyMounted),
            };
            Ship Subj() => server.Ships.Single(s => s.Ship.Id == subjectId).Ship;
            // The test action is SWAP_PART; set up its legal flying inventory
            // directly rather than abusing an in-flight BUY_LOADOUT (H-06).
            foreach (var p in twoParts)
                Subj().MountPart(Subj().Mission!.Core.PartTypes[p.PartId], p.Amount, p.MountId, p.FractionMounted);
            Assert.Equal(2, Subj().Parts.Count);
            Assert.Equal(pidA, Subj().Parts.Single(p => p.MountId == 0).PartId);
            Assert.Equal(pidB, Subj().Parts.Single(p => p.MountId == 1).PartId);

            // ── Phase 1: SWAP the two occupied slots 0 <-> 1 ──────────────────
            foreach (var b in bots) b.ResetSwapPartsReceived();
            subBot.SendSwapPart(EquipmentType.Weapon, mountOld: 0, mountNew: 1);

            PumpUntil(server,
                () => Subj().Parts.SingleOrDefault(p => p.MountId == 0)?.PartId == pidB
                   && Subj().Parts.SingleOrDefault(p => p.MountId == 1)?.PartId == pidA
                   && obsBot.SwapParts.Count >= 1
                   && eneBot.SwapParts.Count >= 1
                   && subBot.SwapParts.Any(s => s.MountOld == 0 && s.MountNew == 1),
                4000);

            // (a) Server-side: the two parts really exchanged mount slots.
            Assert.Equal(pidB, Subj().Parts.Single(p => p.MountId == 0).PartId);
            Assert.Equal(pidA, Subj().Parts.Single(p => p.MountId == 1).PartId);
            Assert.Equal(2, Subj().Parts.Count);   // nothing added/removed

            // (c) Broadcast fields match (observed by the same-side observer)…
            var obsv = obsBot.SwapParts.Single();
            Assert.Equal(subjectId, obsv.ShipID);
            Assert.Equal(EquipmentType.Weapon, obsv.EtOld);
            Assert.Equal((sbyte)0, obsv.MountOld);
            Assert.Equal((sbyte)1, obsv.MountNew);

            // (b) Sector-wide, not side-wide: the cross-side enemy received the
            // same SWAP_PART (GetGroupSectorFlying(pcluster), FedSrv.CPP:6498) —
            // a same-side-only relay like HIGHLIGHT_CLUSTER would never reach it.
            var enemv = eneBot.SwapParts.Single();
            Assert.Equal(subjectId, enemv.ShipID);
            Assert.Equal(EquipmentType.Weapon, enemv.EtOld);
            Assert.Equal((sbyte)0, enemv.MountOld);
            Assert.Equal((sbyte)1, enemv.MountNew);

            // The requester's own echo also comes back (it is flying in-cluster too).
            Assert.Equal(1, subBot.SwapParts.Count(s => s.MountOld == 0 && s.MountNew == 1));

            // ── Phase 2: plain move — destination mount 2 is EMPTY ────────────
            // (after phase 1: pidB@0, pidA@1; move the mount-1 part to empty mount 2.)
            foreach (var b in bots) b.ResetSwapPartsReceived();
            subBot.SendSwapPart(EquipmentType.Weapon, mountOld: 1, mountNew: 2);
            PumpUntil(server,
                () => Subj().Parts.SingleOrDefault(p => p.MountId == 1) is null
                   && Subj().Parts.SingleOrDefault(p => p.MountId == 2)?.PartId == pidA
                   && obsBot.SwapParts.Count >= 1
                   && subBot.SwapParts.Any(s => s.MountOld == 1 && s.MountNew == 2),
                4000);

            Assert.Equal(pidB, Subj().Parts.Single(p => p.MountId == 0).PartId);   // untouched
            Assert.Null(Subj().Parts.SingleOrDefault(p => p.MountId == 1));         // vacated
            Assert.Equal(pidA, Subj().Parts.Single(p => p.MountId == 2).PartId);    // moved
            Assert.Equal(2, Subj().Parts.Count);
            var mv = obsBot.SwapParts.Single();
            Assert.Equal(subjectId, mv.ShipID);
            Assert.Equal((sbyte)1, mv.MountOld);
            Assert.Equal((sbyte)2, mv.MountNew);

            // ── Phase 3: silent no-op — SOURCE mount 9 is EMPTY ────────────────
            // (after phase 2: pidB@0, pidA@2; no part at mount 9 ⇒ no broadcast at all.)
            foreach (var b in bots) b.ResetSwapPartsReceived();
            var before = MountsByPartId(Subj());
            subBot.SendSwapPart(EquipmentType.Weapon, mountOld: 9, mountNew: 2);
            PumpUntil(server, () => false, 20);   // let any (wrongful) broadcast arrive
            Assert.Empty(obsBot.SwapParts);
            Assert.Empty(eneBot.SwapParts);
            Assert.Equal(before, MountsByPartId(Subj()));   // server loadout unchanged
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }
}
