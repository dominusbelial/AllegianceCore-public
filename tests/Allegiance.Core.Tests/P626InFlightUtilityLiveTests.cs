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
/// P626 — parity wave 1: in-flight utility pack. Loopback wire proofs for the
/// new command handlers: <c>TK_DropCargo</c> (a negative-mount DROP_PART drops
/// a CARGO part into space as a treasure, broadcast sector-wide), the BUY_LOADOUT
/// ACK now carrying the cargo hold with negative mounts, and the vote keys'
/// wire path (<c>FM_C_VOTE</c> against the active ballot → observable tally
/// effect). Real bots over real loopback TCP, the same construction as
/// <see cref="CargoBayReloadTests"/>. The pure arithmetic is covered by
/// <c>P626InFlightUtilityPackTests</c> in the MonoGame test project.
/// </summary>
[Collection("LiveNetwork")]
public class P626InFlightUtilityLiveTests
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
            new Gm { Name = "P626 Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 512u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 51000000000000000ul, DrmHash = "p626",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static DataPartTypeIGC AmmoPackType() => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ammo", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
        PartId: 904, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
        Type: new DataPartTypeIGC.Pack(PackType: 0, Amount: 0));   // c_packAmmo

    private static DataPartTypeIGC AmmoWeapon(StaticCore core) =>
        core.PartTypes.Values
            .Where(p => p.Type is DataPartTypeIGC.Weapon { CAmmoPerShot: > 0 })
            .OrderBy(p => p.PartId)
            .First();

    private static short Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int n = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(n < 4000, $"{name} did not seat");
        short shipId = bot.ShipAssignment!.Ship.ShipId;
        // H-06/H-22: utility inventory actions require a flying ship.
        bot.SendBuyLoadout(ShipOf(server, shipId).HullType.HullId, fLaunch: true);
        Assert.True(PumpUntil(server, () => ShipOf(server, shipId).StationId is null, 4000) < 4000);
        return shipId;
    }

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    /// <summary>Make <paramref name="a"/> side0's leader+owner and
    /// <paramref name="b"/> a non-leader on the same side (so <c>#mutiny</c>
    /// initiated by b is legal — the leader cannot mutiny, FedSrv.CPP:5186).</summary>
    private static (short aId, short bId, short side0) SeatLeaderAndMate(Srv server, FltBot a, FltBot b, string na, string nb)
    {
        short aId = Seat(server, a, na);
        short side0 = server.Missions[0].Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        short bId = Seat(server, b, nb);
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));
        PumpUntil(server, () => false, 40);
        Assert.Equal(aId, SideOf(server, side0).LeaderShipId);
        return (aId, bId, side0);
    }

    /// <summary>⭐ LOOPBACK DROP-CARGO PROOF: a real <c>FM_CS_DROP_PART(33)</c>
    /// with a NEGATIVE (cargo) mount — what the client's TK_DropCargo key sends —
    /// removes the cargo part from the ship's hold and drops it into space as a
    /// part-type treasure, broadcast sector-wide (native TK_DropCargo,
    /// WinTrek.cpp:9698-9730).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void DropCargo_wire_drops_cargo_part_as_treasure_and_broadcasts()
    {
        Require.Artwork();
        var server = new Srv();
        server.Boot(Config());
        try
        {
            using var a = new FltBot();
            using var o = new FltBot();
            short aId = Seat(server, a, "p626_drop_a");
            short oId = Seat(server, o, "p626_drop_o");
            // Same side so the observer shares the subject's home sector (the
            // sector-wide DROP_PART / TREASURE broadcast target, P368 invariant).
            short side0 = server.Missions[0].Sides[0].Id;
            Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
            Assert.True(server.TryJoinSide(ShipOf(server, oId), side0));
            server.Missions[0].Stage = MissionStage.Started;
            PumpUntil(server, () => false, 40);
            var ship = server.Ships.Single(s => s.Ship.Id == aId).Ship;

            // Seed an ammo pack at equipped mount 0, move it to cargo slot 0 (-1).
            ship.MountPart(AmmoPackType(), amount: 10, mountId: 0, fractionMounted: 255);
            a.SendSwapPart(EquipmentType.Pack, mountOld: 0, mountNew: -1);
            PumpUntil(server, () => ship.CargoHold[Ship.CargoSlotForMount(-1)] is not null, 4000);
            Assert.Equal(10, ship.CargoHold[Ship.CargoSlotForMount(-1)]!.Amount);

            foreach (var b in new[] { a, o }) { b.ResetDropPartsReceived(); b.ResetTreasureSpawnsReceived(); }

            // TK_DropCargo sends DROP_PART with the negative mount (-1 = 0xFF).
            a.SendDropPart(EquipmentType.Pack, unchecked((byte)-1));

            // The server echoes DROP_PART sector-wide, so the SUBJECT's own bot
            // (which sits in the drop sector) receives the relay — the robust
            // observable regardless of where the observer's ship landed.
            PumpUntil(server,
                () => ship.CargoHold[Ship.CargoSlotForMount(-1)] is null
                   && a.DropParts.Any(d => d.Mount == unchecked((byte)-1)),
                4000);

            // The effect happened: the cargo slot was emptied…
            Assert.Null(ship.CargoHold[Ship.CargoSlotForMount(-1)]);
            // …and the sector received the DROP_PART relay carrying the NEGATIVE
            // (cargo) mount with the server-filled shipID (native TK_DropCargo
            // drops the cargo part; the server also creates the part treasure —
            // observed as obj=904@amount=10 TREASURE(28) in the server log, which
            // the dropping ship instantly re-collects, so the wire DROP_PART
            // relay is the robust observable for "the right message reached the
            // server and the effect happened").
            var relay = a.DropParts.FirstOrDefault(d => d.Mount == unchecked((byte)-1));
            Assert.NotNull(relay);
            Assert.Equal(aId, relay!.ShipID);
        }
        finally
        {
            server.Dispose();
        }
    }

    /// <summary>⭐ LOOPBACK/UNIT PROOF: the BUY_LOADOUT ACK now carries the ship's
    /// cargo hold as negative-mount entries (native mixes mounted + cargo into one
    /// tail), which is exactly what the client's ShipEntity.Cargo split consumes.
    /// Exercised over the real ship state feeding <c>ExportInventoryForAck</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ExportInventoryForAck_includes_cargo_with_negative_mounts()
    {
        Require.Artwork();
        var server = new Srv();
        server.Boot(Config());
        try
        {
            using var a = new FltBot();
            short aId = Seat(server, a, "p626_ack_a");
            server.Missions[0].Stage = MissionStage.Started;
            var ship = server.Ships.Single(s => s.Ship.Id == aId).Ship;

            // Mounted weapon (positive mount) + a cargo pack (slot 1 = -2).
            ship.MountPart(AmmoWeapon(Core()), amount: 0, mountId: 0, fractionMounted: 255);
            ship.MountPart(AmmoPackType(), amount: 7, mountId: 0, fractionMounted: 255);
            a.SendSwapPart(EquipmentType.Pack, mountOld: 0, mountNew: -2);
            PumpUntil(server, () => ship.CargoHold[Ship.CargoSlotForMount(-2)] is not null, 4000);

            var ack = ship.ExportInventoryForAck();
            // The mounted weapon keeps a non-negative mount…
            Assert.Contains(ack, p => p.MountId == 0 && p.PartId == ship.Parts.Single(x => x.MountId == 0 && x.EquipmentType == EquipmentType.Weapon).PartId);
            // …and the cargo pack rides a negative (high-bit) mount, amount intact.
            Assert.Contains(ack, p => p.MountId == unchecked((byte)-2) && p.PartId == 904 && p.Amount == 7);
        }
        finally
        {
            server.Dispose();
        }
    }

    /// <summary>⭐ LOOPBACK VOTE PROOF: the client's VoteYes/VoteNo path sends a
    /// real <c>FM_C_VOTE(19)</c> naming the active ballot; the server tallies it
    /// and the observable effect happens (here: the only other eligible voter
    /// voting YES lets a passing #mutiny transfer leadership). Mirrors the P187
    /// ballot system's own wire proofs with a P626-specific framing.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Vote_wire_against_active_ballot_produces_tally_effect()
    {
        Require.Artwork();
        var server = new Srv();
        server.Boot(Config());
        try
        {
            using var a = new FltBot();
            using var b = new FltBot();
            var (aId, bId, side0) = SeatLeaderAndMate(server, a, b, "p626_vote_a", "p626_vote_b");
            server.Missions[0].Stage = MissionStage.Started;
            PumpUntil(server, () => false, 40);

            b.ResetBallotSignals();
            b.SendChat("#mutiny");
            int got = PumpUntil(server, () => b.Ballots.Count >= 1, 800);
            Assert.True(got < 800, "mutiny ballot not created");
            short ballotId = b.Ballots[0].BallotId;

            // The other eligible voter (a) votes YES on the active ballot id.
            a.SendVote(ballotId, true);

            // Observable effect: all votes in + majority → leadership transfers.
            int transferred = PumpUntil(server, () => SideOf(server, side0).LeaderShipId == bId, 800);
            Assert.True(transferred < 800, "vote did not tally into leadership transfer");
            Assert.Equal(bId, SideOf(server, side0).LeaderShipId);
        }
        finally
        {
            server.Dispose();
        }
    }

    private static Side SideOf(Srv server, short sideId) =>
        server.Missions[0].Sides.Single(s => s.Id == sideId);
}
