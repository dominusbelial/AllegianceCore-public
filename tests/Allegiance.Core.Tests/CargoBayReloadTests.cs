using Allegiance.Core;
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
/// P192 — the cargo-bay + <c>FM_CS_RELOAD(129)</c> loopback proofs. A
/// separate <c>Ship.CargoHold</c> (capacity <c>c_maxCargo</c> = 5,
/// <c>Igc/igc.h:845</c>) filled/emptied only through the cargo-aware
/// <c>SWAP_PART</c> dispatch (negative wire mounts), then drained by
/// <c>RELOAD</c> into the ship's live pools / equipped launchers.
/// Real bots over real loopback TCP, same construction as
/// <see cref="SwapPartTests"/> (3 bots / 2 teams, one same-sector observer).
/// </summary>
[Collection("LiveNetwork")]
public class CargoBayReloadTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Cargo Bay Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 512u, DwCookie = 0x51u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 51000000000000000ul, DrmHash = "cargotest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A booted 3-bot server plus the subject/observer seats; disposing it
    /// tears down the bots and the server (mirrors the ChaffTests/SwapPartTests
    /// dispose pattern).</summary>
    private sealed class Seat : IDisposable
    {
        public Srv Server;
        public Ship Subject;
        public FltBot SubjectBot;
        public FltBot ObserverBot;
        private readonly FltBot[] _bots;
        public Seat(Srv server, FltBot[] bots, Ship subject, FltBot subjectBot, FltBot observerBot)
        {
            Server = server; Subject = subject; SubjectBot = subjectBot; ObserverBot = observerBot; _bots = bots;
        }
        public void Dispose()
        {
            foreach (var b in _bots) b.Dispose();
            Server.Dispose();
        }
    }

    /// <summary>Boot a 3-bot server and return the seated ships + bots.</summary>
    private static Seat SeatThree()
    {
        var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass ready-up lobby
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p192_subject", "p192_observer", "p192_third" };
        try
        {
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i].Connect(server.BoundPort, names[i]);
                bots[i].SendLogonRequest(Request(names[i]));
            }
            PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            // P647 — robust to async seat ORDER. The old code assumed bots seat
            // strictly in connect order (round-robin sides A,B,A), so bots[0]
            // ("subject") always had a same-side sibling among bots[1..2] (the
            // "observer"). Under full-suite load the three logons seat in a
            // shuffled order, so the subject occasionally lands ALONE on its
            // side and `First(...)` over the siblings throws "Sequence contains
            // no matching element". With 3 bots over 2 sides, some side always
            // holds >= 2 — so select subject+observer from the majority side
            // (same side => same home sector, the P368 invariant these tests
            // need) and the opposite-side bot as the unused third.
            var ships = bots
                .Select(b => (bot: b, ship: server.Ships.Single(s => s.Ship.Id == b.ShipAssignment!.Ship.ShipId).Ship))
                .ToList();
            var pairSide = ships.GroupBy(x => x.ship.SideId).OrderByDescending(g => g.Count()).First();
            Assert.True(pairSide.Count() >= 2, "3 bots over 2 sides must put >= 2 on one side");
            var pair = pairSide.ToList();
            var subject = pair[0].ship;
            var subjectBot = pair[0].bot;
            var observerShip = pair[1].ship;
            var observerBot = pair[1].bot;
            var thirdShip = ships.First(x => x.ship.SideId != subject.SideId).ship;
            Assert.Equal(subject.Sector!.Id, observerShip.Sector!.Id);
            Assert.NotEqual(subject.Sector.Id, thirdShip.Sector!.Id);
            // P653 H-06 docks fresh default-spawn ships (native FedSrv/fsmission.cpp:791-796),
            // but H-22 requires flying/alive/unboarded for wire inventory ops (SwapPart/
            // Reload/DropPart). These tests exercise the reload/swap MECHANICS, so seat the
            // subject as flying (undocked) exactly as a launched pilot would be.
            subject.StationId = null;
            // P653 H-08 clamps pack/launcher quantities to the hull's capacity
            // (packigc.h:102-123, launchertypeigc.h:130-145). The default Scout has a small
            // magazine/fuel/chaff capacity; give the reload-test subject a generous hull so
            // its 10-round packs and launchers survive the clamp (audit gate preserved; this
            // only arms the fixture with realistic headroom for the reload mechanics).
            subject.ChangeHull(subject.HullType with
            {
                MaxAmmo = 2000, MaxFuel = 50,
                CapacityMagazine = 50, CapacityChaffLauncher = 50, CapacityDispenser = 50,
            });
            return new Seat(server, bots, subject, subjectBot, observerBot);
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            server.Dispose();
            throw;
        }
    }

    /// <summary>A constructed ammo pack part (<c>c_packAmmo</c> = 0).</summary>
    private static DataPartTypeIGC AmmoPackType() => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ammo", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
        PartId: 904, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
        Type: new DataPartTypeIGC.Pack(PackType: 0, Amount: 0));   // c_packAmmo

    /// <summary>A constructed fuel pack part (<c>c_packFuel</c> = 1).</summary>
    private static DataPartTypeIGC FuelPackType() => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "fuel", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
        PartId: 905, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
        Type: new DataPartTypeIGC.Pack(PackType: 1, Amount: 0));   // c_packFuel

    /// <summary>A constructed chaff-launcher part naming a real core chaff type
    /// (the port's construction-only launcher mount case, cf. ChaffTests).</summary>
    private static DataPartTypeIGC ChaffLauncherType(StaticCore core)
    {
        var chaff = core.ChaffTypes.Values.OrderBy(c => c.ExpendableTypeId).First();
        return new DataPartTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "chaff", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: 906, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.ChaffLauncher(chaff.ExpendableTypeId));
    }

    /// <summary>A real ammo-consuming weapon part type from the core, for the
    /// mounted-fraction reset assertion.</summary>
    private static DataPartTypeIGC AmmoWeapon(StaticCore core)
    {
        var w = core.PartTypes.Values
            .Where(p => p.Type is DataPartTypeIGC.Weapon { CAmmoPerShot: > 0 })
            .OrderBy(p => p.PartId)
            .First();
        return w;
    }

    /// <summary>Pin the RELOAD wire layout: fixed 10 B (header 4 + ib/cb var pair
    /// @4/@6 + shipID short @8) + 4 B per ReloadData, fmid 129, lossless
    /// round-trip of negative mounts and the NA (-1) amount.</summary>
    [Fact]
    public void Reload_codec_roundtrips_fixed_10_plus_4n_layout()
    {
        var msg = new Reload
        {
            Entries =
            [
                new ReloadData(AmountTransfered: 6, Mount: -1),
                new ReloadData(AmountTransfered: -1, Mount: -5),   // NA = terminate
                new ReloadData(AmountTransfered: 0, Mount: -2),
            ],
            ShipID = 7788,
        };

        var encoded = msg.Encode();
        Assert.Equal(10 + 3 * 4, encoded.Length);
        Assert.Equal((ushort)(10 + 3 * 4), FedMessageHeader.Read(encoded).CbMsg);
        Assert.Equal(Reload.Fmid, FedMessageHeader.Read(encoded).Fmid);

        var again = Reload.Decode(encoded);
        Assert.Equal((short)7788, again.ShipID);
        Assert.Equal(3, again.Entries.Count);
        Assert.Equal(new ReloadData(6, -1), again.Entries[0]);
        Assert.Equal(new ReloadData(-1, -5), again.Entries[1]);
        Assert.Equal(new ReloadData(0, -2), again.Entries[2]);

        // Native structural layout (Messages.h:837-846): @4/5 ibReloads LE,
        // @6/7 cbReloads LE, @8/9 shipID LE, then ReloadData{short,int8,pad}.
        byte[] native = [0x16, 0x00, 0x81, 0x00, 0x0A, 0x00, 0x0C, 0x00, 0x6C, 0x1E, 0x06, 0x00, 0xFF, 0x00, 0xFF, 0xFF, 0xFB, 0x00, 0x00, 0x00, 0xFE, 0x00];
        var nat = Reload.Decode(native);
        Assert.Equal((short)7788, nat.ShipID);
        Assert.Equal(3, nat.Entries.Count);
        Assert.Equal(new ReloadData(6, -1), nat.Entries[0]);
        Assert.Equal(new ReloadData(-1, -5), nat.Entries[1]);
        Assert.Equal(new ReloadData(0, -2), nat.Entries[2]);
    }

    /// <summary>⭐ ROUND-TRIP PROOF: a <c>SWAP_PART</c> with a negative
    /// <c>mountNew</c> moves an equipped part into <c>Ship.CargoHold</c>, and a
    /// subsequent <c>SWAP_PART</c> with a negative <c>mountOld</c> moves it back
    /// out to an equipped slot.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SwapPart_negative_mount_roundtrips_equipped_part_into_and_out_of_cargo()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var ship = seat.Subject;
        var core = Core();

        // Seed an ammo pack at equipped mount 0.
        ship.MountPart(AmmoPackType(), amount: 10, mountId: 0, fractionMounted: 255);
        Assert.Equal(10, ship.Parts.Single(p => p.EquipmentType == EquipmentType.Pack && p.MountId == 0).Amount);

        // Phase 1: move it into cargo slot 0 (wire mount -1).
        seat.SubjectBot.SendSwapPart(EquipmentType.Pack, mountOld: 0, mountNew: -1);
        PumpUntil(seat.Server, () => ship.CargoHold[Ship.CargoSlotForMount(-1)] is not null, 4000);
        Assert.Null(ship.Parts.SingleOrDefault(p => p.EquipmentType == EquipmentType.Pack && p.MountId == 0));  // vacated equipped slot
        var cargo = ship.CargoHold[Ship.CargoSlotForMount(-1)];
        Assert.NotNull(cargo);
        Assert.Equal(10, cargo!.Amount);

        // Phase 2: move it back to equipped mount 0.
        seat.SubjectBot.SendSwapPart(EquipmentType.Pack, mountOld: -1, mountNew: 0);
        PumpUntil(seat.Server, () => ship.Parts.Any(p => p.EquipmentType == EquipmentType.Pack && p.MountId == 0), 4000);
        Assert.Null(ship.CargoHold[Ship.CargoSlotForMount(-1)]);   // cargo slot emptied
        Assert.Equal(10, ship.Parts.Single(p => p.EquipmentType == EquipmentType.Pack && p.MountId == 0).Amount);
    }

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: <c>RELOAD</c> transferring ammo from a
    /// cargo ammo-pack into <c>Ship.Ammo</c> updates the ship's live ammo count,
    /// resets ammo-weapons' mounted-fraction to 0, and relays the broadcast to
    /// the sector.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Reload_transfers_ammo_updates_ship_ammo_resets_weapon_fraction_and_broadcasts()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var ship = seat.Subject;
        var core = Core();

        // A mounted ammo-consuming weapon (fraction 255) to observe the reset.
        var weapon = AmmoWeapon(core);
        ship.MountPart(weapon, amount: 0, mountId: 1, fractionMounted: 255);
        Assert.Equal(255, ship.Parts.Single(p => p.EquipmentType == EquipmentType.Weapon && p.MountId == 1).FractionMounted);

        // Ammo pack in cargo slot 0.
        ship.MountPart(AmmoPackType(), amount: 10, mountId: 0, fractionMounted: 255);
        seat.SubjectBot.SendSwapPart(EquipmentType.Pack, mountOld: 0, mountNew: -1);
        PumpUntil(seat.Server, () => ship.CargoHold[Ship.CargoSlotForMount(-1)] is not null, 4000);

        ship.Ammo = 0;   // P653 H-08 clamps ammo to hull.MaxAmmo (shipIGC.h:597-601); drain first so the +6 transfer has room
        short ammoBefore = ship.Ammo;
        foreach (var b in new[] { seat.SubjectBot, seat.ObserverBot }) b.ResetReloadsReceived();
        seat.SubjectBot.SendReload(new ReloadData(AmountTransfered: 6, Mount: -1));

        PumpUntil(seat.Server,
            () => ship.Ammo == (short)(ammoBefore + 6)
               && ship.Parts.Single(p => p.EquipmentType == EquipmentType.Weapon && p.MountId == 1).FractionMounted < ExpandedPartData.FullyMounted
               && seat.ObserverBot.ReloadBroadcasts.Any(r => r.ShipID == ship.Id),
            4000);

        // Live ammo count bumped by the transferred 6.
        Assert.Equal((short)(ammoBefore + 6), ship.Ammo);
        // Cargo pack drained 10 -> 4.
        Assert.Equal(4, ship.CargoHold[Ship.CargoSlotForMount(-1)]!.Amount);
        // Native resets to zero, then weaponIGC.h:33-49 advances readiness on
        // subsequent flight ticks. The observer receipt happens asynchronously.
        // P641FlightPartsSimTests asserts the exact zero before any update.
        Assert.InRange(ship.Parts.Single(p => p.EquipmentType == EquipmentType.Weapon && p.MountId == 1).FractionMounted,
            (byte)0, (byte)(ExpandedPartData.FullyMounted - 1));
        // Sector relay reached the same-sector observer with shipID filled in.
        var relay = seat.ObserverBot.ReloadBroadcasts.Single(r => r.ShipID == ship.Id);
        Assert.Equal(new ReloadData(6, -1), relay.Entries.Single());
    }

    /// <summary><c>RELOAD</c> with <c>amountTransfered == -1</c> (native <c>NA</c>)
    /// on an ammo pack terminates the pack — removes it from cargo — rather than
    /// transferring a specific amount (native transfers the whole pack, then
    /// drops it, FedSrv.CPP:6553-6556,6572).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Reload_NA_terminates_the_cargo_pack()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var ship = seat.Subject;

        // A kept-equipped part keeps Parts.Count != 0, the native RELOAD NYI gate
        // (FedSrv.CPP:6516) that skips a wholly-emptied loadout.
        ship.MountPart(AmmoWeapon(Core()), amount: 0, mountId: 1, fractionMounted: 255);

        ship.MountPart(AmmoPackType(), amount: 8, mountId: 0, fractionMounted: 255);
        seat.SubjectBot.SendSwapPart(EquipmentType.Pack, mountOld: 0, mountNew: -1);
        PumpUntil(seat.Server, () => ship.CargoHold[Ship.CargoSlotForMount(-1)] is not null, 4000);

        ship.Ammo = 0;   // P653 H-08 clamps ammo to hull.MaxAmmo (shipIGC.h:597-601); drain first so the +8 NA transfer has room
        short ammoBefore = ship.Ammo;
        foreach (var b in new[] { seat.SubjectBot, seat.ObserverBot }) b.ResetReloadsReceived();
        seat.SubjectBot.SendReload(new ReloadData(AmountTransfered: -1, Mount: -1));

        PumpUntil(seat.Server,
            () => ship.CargoHold[Ship.CargoSlotForMount(-1)] is null
               && seat.ObserverBot.ReloadBroadcasts.Any(r => r.ShipID == ship.Id),
            4000);

        // Pack removed from cargo (terminated).
        Assert.Null(ship.CargoHold[Ship.CargoSlotForMount(-1)]);
        // Native NA on an ammo pack transfers the WHOLE amount, then drops the pack.
        Assert.Equal((short)(ammoBefore + 8), ship.Ammo);
    }

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: <c>RELOAD</c> swapping a cargo launcher's
    /// ammo into an equipped, partially-loaded launcher of the same type adds the
    /// transferred amount and resets the equipped launcher's mounted-fraction.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Reload_swaps_cargo_launcher_ammo_into_equipped_partially_loaded_launcher()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var ship = seat.Subject;
        var core = Core();
        var launcher = ChaffLauncherType(core);

        // Equipped target launcher (partially loaded: 2) at mount 0.
        ship.MountPart(launcher, amount: 2, mountId: 0, fractionMounted: 255);
        // Spare launcher (5 rounds) placed directly into cargo slot 1 (mount -2).
        // (P653 H-07/H-22: non-weapon equipment mounts only at 0 — hullTypeIGC.h:199-213 —
        // so a second launcher must live in cargo, never at a positive mount.)
        ship.MountPart(launcher, amount: 5, unchecked((byte)-2), fractionMounted: 255);
        PumpUntil(seat.Server, () => ship.CargoHold[Ship.CargoSlotForMount(-2)] is not null, 4000);

        Assert.Equal(2, ship.Parts.Single(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.MountId == 0).Amount);
        Assert.Equal(5, ship.CargoHold[Ship.CargoSlotForMount(-2)]!.Amount);

        foreach (var b in new[] { seat.SubjectBot, seat.ObserverBot }) b.ResetReloadsReceived();
        // Transfer 3 rounds from cargo to the equipped launcher.
        seat.SubjectBot.SendReload(new ReloadData(AmountTransfered: 3, Mount: -2));

        PumpUntil(seat.Server,
            () => ship.Parts.Single(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.MountId == 0).Amount == 5
               && seat.ObserverBot.ReloadBroadcasts.Any(r => r.ShipID == ship.Id),
            4000);

        // Equipped launcher 2 -> 5 (added the transferred 3) and fraction reset.
        var equipped = ship.Parts.Single(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.MountId == 0);
        Assert.Equal(5, equipped.Amount);
        // dispenserigc.cpp:86-102 advances the native reset on later ticks.
        Assert.InRange(equipped.FractionMounted, (byte)0, (byte)(ExpandedPartData.FullyMounted - 1));
        // Cargo launcher drained 5 -> 2 (did not fully terminate).
        Assert.Equal(2, ship.CargoHold[Ship.CargoSlotForMount(-2)]!.Amount);
        Assert.Equal(new ReloadData(3, -2), seat.ObserverBot.ReloadBroadcasts.Single(r => r.ShipID == ship.Id).Entries.Single());
    }

    /// <summary>⭐ NO-ROLLBACK QUIRK: a malformed <c>RELOAD</c> entry (transferring
    /// more than the cargo item holds) refuses the rest of the batch and sends no
    /// broadcast, but an earlier valid entry in the same message keeps its
    /// applied transfer.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Reload_malformed_entry_refuses_rest_without_rollback_or_broadcast()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var ship = seat.Subject;

        // Two ammo packs in cargo: slot 0 (mount -1) with 10, slot 1 (mount -2) with 3.
        ship.MountPart(AmmoWeapon(Core()), amount: 0, mountId: 2, fractionMounted: 255);   // kept equipped (NYI gate, FedSrv.CPP:6516)
        ship.MountPart(AmmoPackType(), amount: 10, mountId: 0, fractionMounted: 255);
        // Second pack placed directly into cargo slot 1 (mount -2). (P653 H-07/H-22:
        // non-weapon equipment mounts only at 0 — hullTypeIGC.h:199-213.)
        ship.MountPart(AmmoPackType(), amount: 3, unchecked((byte)-2), fractionMounted: 255);
        seat.SubjectBot.SendSwapPart(EquipmentType.Pack, mountOld: 0, mountNew: -1);
        PumpUntil(seat.Server,
            () => ship.CargoHold[Ship.CargoSlotForMount(-1)] is not null && ship.CargoHold[Ship.CargoSlotForMount(-2)] is not null,
            4000);

        ship.Ammo = 0;   // P653 H-08 clamps ammo to hull.MaxAmmo (shipIGC.h:597-601); drain first so the +6 entry has room
        short ammoBefore = ship.Ammo;
        foreach (var b in new[] { seat.SubjectBot, seat.ObserverBot }) b.ResetReloadsReceived();

        // Entry 1 valid (transfer 6 from the 10-pack), entry 2 malformed (transfer
        // 5 > the 3-pack's amount -> native bBoot, FedSrv.CPP:6559).
        seat.SubjectBot.SendReload(
            new ReloadData(AmountTransfered: 6, Mount: -1),
            new ReloadData(AmountTransfered: 5, Mount: -2));

        PumpUntil(seat.Server,
            () => ship.Ammo == (short)(ammoBefore + 6)   // entry 1 already applied
               && ship.CargoHold[Ship.CargoSlotForMount(-1)]!.Amount == 4,
            4000);

        // No relay broadcast (bBoot suppressed it).
        Assert.Empty(seat.ObserverBot.ReloadBroadcasts);

        // Entry 1 applied (ammo +6, 10-pack drained to 4)…
        Assert.Equal((short)(ammoBefore + 6), ship.Ammo);
        Assert.Equal(4, ship.CargoHold[Ship.CargoSlotForMount(-1)]!.Amount);
        // …but the malformed entry 2 was refused: the 3-pack is untouched.
        Assert.Equal(3, ship.CargoHold[Ship.CargoSlotForMount(-2)]!.Amount);
    }

    /// <summary>A fuel-pack <c>RELOAD</c> is accepted (decoded, applied, relayed)
    /// and — since P412 gave the ship a real fuel pool — now visibly TOP-UPS
    /// <c>Ship.Fuel</c> (native <c>SetFuel(GetFuel()+amount)</c>, FedSrv.CPP:6587)
    /// instead of being a no-op. Ammo is untouched.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Reload_fuel_pack_is_accepted_and_topups_the_fuel_pool()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var ship = seat.Subject;

        // Drain the live pool FIRST (while no fuel pack is anywhere in cargo) so
        // the later reload's +4 is observably absorbed. Doing this after a pack is
        // in cargo would trigger the P412 auto-reload (SetFuel's zero-transition)
        // and consume that pack before the test's own reload — a self-fulfilling trap.
        ship.SetFuel(0f);

        // A kept-equipped part keeps Parts.Count != 0 (native NYI gate, FedSrv.CPP:6516).
        ship.MountPart(AmmoWeapon(Core()), amount: 0, mountId: 1, fractionMounted: 255);

        ship.MountPart(FuelPackType(), amount: 10, mountId: 0, fractionMounted: 255);
        seat.SubjectBot.SendSwapPart(EquipmentType.Pack, mountOld: 0, mountNew: -1);
        PumpUntil(seat.Server, () => ship.CargoHold[Ship.CargoSlotForMount(-1)] is not null, 4000);

        short ammoBefore = ship.Ammo;
        foreach (var b in new[] { seat.SubjectBot, seat.ObserverBot }) b.ResetReloadsReceived();
        seat.SubjectBot.SendReload(new ReloadData(AmountTransfered: 4, Mount: -1));

        PumpUntil(seat.Server,
            () => ship.CargoHold[Ship.CargoSlotForMount(-1)]!.Amount == 6
               && seat.ObserverBot.ReloadBroadcasts.Any(r => r.ShipID == ship.Id),
            4000);

        // Accepted and relayed…
        Assert.Equal(new ReloadData(4, -1), seat.ObserverBot.ReloadBroadcasts.Single(r => r.ShipID == ship.Id).Entries.Single());
        // …the fuel pack drained (4 consumed)…
        Assert.Equal(6, ship.CargoHold[Ship.CargoSlotForMount(-1)]!.Amount);
        // …and (P412) the drained 4 went INTO the real fuel pool (was a no-op before).
        Assert.Equal(4f, ship.Fuel, 3);
        Assert.Equal(ammoBefore, ship.Ammo);   // ammo still untouched
    }

    /// <summary>⭐ MUST-FAIL-IF-REMOVED (M5): an out-of-range negative cargo
    /// mount (below <c>-c_maxCargo</c> = -5) is rejected cleanly by
    /// <c>TrySwapPartWithCargo</c> — it returns <c>false</c> without touching
    /// the loadout — instead of throwing <c>IndexOutOfRangeException</c> from
    /// <c>ResolvePart</c>'s <c>_cargo[index]</c>. The wire <c>Mount</c> is a
    /// signed char, so −128..−6 are all reachable from a client but only
    /// [−1,−5] address a real cargo slot.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SwapPart_out_of_range_negative_mount_is_rejected_cleanly_not_thrown()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var ship = seat.Subject;
        ship.MountPart(AmmoWeapon(Core()), amount: 0, mountId: 1, fractionMounted: 255);   // real, non-empty source

        foreach (int bad in new[] { -6, -7, -128 })   // all below -CargoCapacity
        {
            bool threw = false;
            bool result;
            try { result = ship.TrySwapPartWithCargo(EquipmentType.Weapon, bad, 1); }
            catch (IndexOutOfRangeException) { threw = true; result = false; }
            Assert.False(threw, $"source mount {bad} must be rejected, not thrown");
            Assert.False(result, $"source mount {bad} must be a silent no-op");
        }

        // An out-of-range negative DESTINATION mount is rejected the same way.
        bool dstThrew = false, dstResult;
        try { dstResult = ship.TrySwapPartWithCargo(EquipmentType.Weapon, 1, -6); }
        catch (IndexOutOfRangeException) { dstThrew = true; dstResult = false; }
        Assert.False(dstThrew, "out-of-range destination mount must be rejected, not thrown");
        Assert.False(dstResult);

        // The guard must NOT over-reject: a valid in-range cargo mount still swaps.
        bool okThrew = false, okResult;
        try { okResult = ship.TrySwapPartWithCargo(EquipmentType.Weapon, 1, -1); }
        catch (IndexOutOfRangeException) { okThrew = true; okResult = false; }
        Assert.False(okThrew, "valid in-range cargo mount must not throw");
        Assert.True(okResult, "valid in-range cargo mount -1 must still swap (move weapon into cargo)");
        Assert.NotNull(ship.CargoHold[Ship.CargoSlotForMount(-1)]);
    }

    /// <summary>⭐ LIVE-WIRE PROOF (M5): a client sending a real
    /// <c>SWAP_PART</c> with an out-of-range negative mount (moduleOld = -6,
    /// below <c>-c_maxCargo</c> = -5) over the wire is cleanly rejected — the
    /// server performs no swap, broadcasts nothing, and the loadout is
    /// untouched — rather than throwing in the dispatch (which would be
    /// swallowed by the generic catch as a misleading "bad SWAP_PART").</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SwapPart_wire_out_of_range_negative_mount_is_rejected_no_broadcast_no_change()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var ship = seat.Subject;
        ship.MountPart(AmmoWeapon(Core()), amount: 0, mountId: 1, fractionMounted: 255);
        Assert.Equal(1, ship.Parts.Count(p => p.EquipmentType == EquipmentType.Weapon));

        foreach (var b in new[] { seat.SubjectBot, seat.ObserverBot }) b.ResetSwapPartsReceived();
        var before = ship.Parts.Select(p => (p.MountId, p.PartId)).OrderBy(x => x.MountId).ToList();

        seat.SubjectBot.SendSwapPart(EquipmentType.Weapon, (sbyte)-6, (sbyte)2);

        PumpUntil(seat.Server, () => false, 40);   // let any wrongful broadcast arrive

        Assert.Empty(seat.ObserverBot.SwapParts);   // nothing relayed to the sector
        var after = ship.Parts.Select(p => (p.MountId, p.PartId)).OrderBy(x => x.MountId).ToList();
        Assert.Equal(before, after);   // loadout untouched (clean rejection, no state change)
    }
}
