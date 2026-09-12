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
/// P154 (the real-player <c>c_cmdAccepted</c> half of the generic non-NPC
/// ORDER_CHANGE path) — live loopback proof that a REAL player's
/// <c>c_cmdAccepted</c> ORDER_CHANGE (the client's <c>SetAccepted</c>
/// menu-order action, <c>WinTrek.cpp:10681-10682</c> — fired on every menu
/// command execution, alongside <c>SetTarget(pmodel, cid)</c>, and cleared by
/// the <c>TK_ClearCommand</c> keybind, <c>WinTrek.cpp:10395-10406</c>) finally
/// has a production write path for the new <see cref="Ship.AcceptedShipTarget"/>/
/// <see cref="Ship.AcceptedShipCommandId"/> pair, via the same cross-side
/// resolver <c>Server.FindShipAnySide</c> and the same extended generic
/// <c>else</c> in <c>Server.HandleOrderChange</c> that P153 wired for
/// <c>c_cmdCurrent</c> (native <c>FedSrv.CPP:7335-7339</c>). Builds on the
/// P147/P148/P150/P153 booted 2-bot harness (no Windows VM): two REAL connected
/// peers on DIFFERENT sides.
///
/// Four tests:
///   1. ⭐ cross-side accept — A (side Alpha) commands its own ship to
///      c_cmdAccepted-target B (side Bravo); server sets
///      <c>A.AcceptedShipTarget</c> to B's real <c>Ship</c> reference and
///      stores the command id (proves cross-side resolution AND the separate
///      ship-typed slot — <c>A.AcceptedTarget</c>, the NPC DataStationIGC?
///      slot of P150, is NOT touched).
///   2. clearing — after accepting B, A sends objectID=NA (the SetAccepted
///      pmodel==NULL sentinel) → <c>A.AcceptedShipTarget</c> becomes null and
///      <c>A.AcceptedShipCommandId</c> reverts to <c>CidNone</c>.
///   3. NPC target — A c_cmdAccepted-targets an ownerless NPC
///      (P147 <c>SpawnNpcShip</c>, NO <c>_peerShips</c> entry) → resolved via
///      the sector model-list scan and set on <c>A.AcceptedShipTarget</c>.
///   4. coexistence — c_cmdCurrent (→ <c>CommandTarget</c>, P153),
///      c_cmdAccepted (→ <c>AcceptedShipTarget</c>, P154) and the NPC-specific
///      <c>AcceptedTarget</c>/<c>AcceptedCommandId</c> (P150) all occupy
///      independent slots: setting one never disturbs the others, and an
///      NA-clear on the accepted pair leaves <c>CommandTarget</c> intact.
/// </summary>
[Collection("LiveNetwork")]
public sealed class OrderChangeAcceptedLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Order Change Accepted Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 994u, DwCookie = 0x994u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99000000000000001ul, DrmHash = "orderchange154",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A booted 2-bot / 2-side live server. Slot 0 = <b>side A</b> at
    /// the origin; slot 1 = <b>side B</b> a little way off — both in
    /// <c>Sectors[0]</c>, both real connected peers on different sides (the
    /// P139/P153 cross-side harness).</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship A = null!, B = null!;
        public FltBot ABot = null!, BBot = null!;
        public Sector Sector = null!;
        public Mission Mission = null!;

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
            seat.Mission = mission;
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
        string[] names = { "p154_a", "p154_b" };
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
            Assert.True(seat.A.SideId != seat.B.SideId, "A and B must be on different sides (cross-side resolution)");
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

    /// <summary>⭐ THE P154 proof. A real player (A) issues a real
    /// <c>c_cmdAccepted</c> ORDER_CHANGE naming the OTHER side's peer ship (B) —
    /// the everyday SetAccepted "accept this order toward the enemy" action
    /// (WinTrek.cpp:10681-10682). The server resolves B cross-side
    /// (<c>FindShipAnySide</c> — same-side-only would fail) and sets
    /// <c>A.AcceptedShipTarget</c> to B's real <c>Ship</c> reference, storing
    /// the command id — WITHOUT touching A's NPC-specific
    /// <c>AcceptedTarget</c>/<c>AcceptedCommandId</c> (P150).</summary>
    [Fact]
    public void Real_player_accepts_enemy_peer_across_sides_via_c_cmdAccepted()
    {
        using var seat = SeatTwo();
        Assert.Null(seat.A.AcceptedShipTarget);

        // SetAccepted semantics: ShipID = the COMMANDED ship = A's own ship;
        // objectType/objectID = the TARGET = B (the enemy, other side). A
        // command id (here c_cidGoto=5) rides in CommandId.
        seat.ABot.SendOrderChange(seat.A.Id, OrderChange.CidGoto, OrderChange.CmdAccepted, IgcObjectType.Ship, seat.B.Id);

        var pumped = PumpUntil(seat.Server, () => ReferenceEquals(seat.A.AcceptedShipTarget, seat.B), 8000);
        Assert.True(ReferenceEquals(seat.A.AcceptedShipTarget, seat.B),
            $"A commanding c_cmdAccepted toward B({seat.B.Id}, side {seat.B.SideId}) must set A.AcceptedShipTarget to B server-side (pumped {pumped} ticks; FedSrv.CPP:7335-7339)");
        Assert.Equal((short)OrderChange.CidGoto, seat.A.AcceptedShipCommandId);

        // Sanity: A's own slot was mutated, not B's (native SetCommand writes
        // the commanded ship pship = A's ship, FedSrv.CPP:7338).
        Assert.Null(seat.B.AcceptedShipTarget);

        // The real-player accept must NOT bleed into the NPC-specific slot
        // (P150) — distinct typing decision (Model? vs DataStationIGC?).
        Assert.Null(seat.A.AcceptedTarget);
        Assert.Equal(OrderChange.CidNone, seat.A.AcceptedCommandId);

        Console.WriteLine($"P154-CROSS: A({seat.A.Id}, side {seat.A.SideId}) c_cmdAccepted cmdID={OrderChange.CidGoto} -> enemy B({seat.B.Id}, side {seat.B.SideId}) : A.AcceptedShipTarget now points at B (reference equal), NPC AcceptedTarget untouched");
    }

    /// <summary>Clearing. After A accepts B, A re-issues <c>c_cmdAccepted</c>
    /// with the <c>objectID=NA</c> sentinel (SetAccepted's <c>pmodel==NULL</c>
    /// case, WinTrek.cpp:8834-8836 — the <c>TK_ClearCommand</c> keybind,
    /// WinTrek.cpp:10404, calls <c>SetAccepted(NULL, c_cidNone)</c>) —
    /// <c>A.AcceptedShipTarget</c> reverts to null and
    /// <c>A.AcceptedShipCommandId</c> to <c>CidNone</c>.</summary>
    [Fact]
    public void Real_player_clears_accepted_ship_target_with_na_sentinel()
    {
        using var seat = SeatTwo();

        seat.ABot.SendOrderChange(seat.A.Id, OrderChange.CidGoto, OrderChange.CmdAccepted, IgcObjectType.Ship, seat.B.Id);
        var locked = PumpUntil(seat.Server, () => ReferenceEquals(seat.A.AcceptedShipTarget, seat.B), 8000);
        Assert.True(locked > 0, $"precondition: A.AcceptedShipTarget must accept B first ({seat.B.Id}); FedSrv.CPP:7335-7339");

        // Clear: objectID = NA(-1), objectType = OT_invalid(-1) — the exact pair
        // the client sends when pmodel is null (WinTrek.cpp:8834-8836).
        seat.ABot.SendOrderChange(seat.A.Id, OrderChange.CidNone, OrderChange.CmdAccepted, (IgcObjectType)(-1), -1);

        var cleared = PumpUntil(seat.Server, () => seat.A.AcceptedShipTarget is null, 8000);
        Assert.True(cleared > 0,
            $"c_cmdAccepted with objectID=NA must clear A.AcceptedShipTarget to null (pumped {cleared} ticks; WinTrek.cpp:8834-8836)");
        Assert.Null(seat.A.AcceptedShipTarget);
        Assert.Equal(OrderChange.CidNone, seat.A.AcceptedShipCommandId);

        Console.WriteLine($"P154-CLEAR: A({seat.A.Id}) cleared AcceptedShipTarget via NA sentinel -> null, cmdID={OrderChange.CidNone}");
    }

    /// <summary>An ownerless NPC target. A <c>c_cmdAccepted</c>-targets a P147
    /// <c>SpawnNpcShip</c> (NO <c>_peerShips</c> entry — the gap the P148/P153
    /// sector-scan closes). The NPC is on the other side, exercising the
    /// cross-side half against an NPC for the accepted path.</summary>
    [Fact]
    public void Real_player_accepts_ownerless_npc_ship_via_c_cmdAccepted()
    {
        using var seat = SeatTwo();

        var npc = seat.Server.SpawnNpcShip(seat.Mission, seat.Sector, seat.B.SideId!.Value, new Vector3(910f, 890f, 860f));
        Assert.True(npc.IsNpc, "spawned NPC must carry the IsNpc marker");
        Assert.False(seat.Server.Ships.Any(s => ReferenceEquals(s.Ship, npc)), "NPC must have no _peerShips entry (P147)");

        seat.ABot.SendOrderChange(seat.A.Id, OrderChange.CidGoto, OrderChange.CmdAccepted, IgcObjectType.Ship, npc.Id);

        var pumped = PumpUntil(seat.Server, () => ReferenceEquals(seat.A.AcceptedShipTarget, npc), 8000);
        Assert.True(ReferenceEquals(seat.A.AcceptedShipTarget, npc),
            $"A c_cmdAccepted toward ownerless NPC {npc.Id} (side {npc.SideId}) must set A.AcceptedShipTarget to it via the sector scan (pumped {pumped} ticks; FedSrv.CPP:7335-7339)");

        Console.WriteLine($"P154-NPC: A({seat.A.Id}) c_cmdAccepted -> ownerless NPC {npc.Id} (side {npc.SideId}) : A.AcceptedShipTarget set (sector model-list scan)");
    }

    /// <summary>Coexistence. c_cmdCurrent (P153 → <c>CommandTarget</c>),
    /// c_cmdAccepted (P154 → <c>AcceptedShipTarget</c>/<c>AcceptedShipCommandId</c>)
    /// and the NPC-specific <c>AcceptedTarget</c>/<c>AcceptedCommandId</c>
    /// (P150, <c>DataStationIGC</c>?) must all occupy independent slots: A
    /// sets the current and accepted ship slots simultaneously, proves neither
    /// bleeds into the NPC accepted slot, and an NA-clear on the accepted pair
    /// leaves <c>CommandTarget</c> intact.</summary>
    [Fact]
    public void Accepted_current_and_npc_slots_coexist_independently()
    {
        using var seat = SeatTwo();

        // Establish both real-player slots on A: CommandTarget via c_cmdCurrent
        // (P153), AcceptedShipTarget via c_cmdAccepted (P154) — same target B.
        seat.ABot.SendOrderChange(seat.A.Id, OrderChange.CidNone, OrderChange.CmdCurrent, IgcObjectType.Ship, seat.B.Id);
        var cur = PumpUntil(seat.Server, () => ReferenceEquals(seat.A.CommandTarget, seat.B), 8000);
        Assert.True(cur > 0, $"precondition: c_cmdCurrent must set A.CommandTarget to B ({seat.B.Id}); FedSrv.CPP:7335-7339");

        seat.ABot.SendOrderChange(seat.A.Id, OrderChange.CidGoto, OrderChange.CmdAccepted, IgcObjectType.Ship, seat.B.Id);
        var acc = PumpUntil(seat.Server, () => ReferenceEquals(seat.A.AcceptedShipTarget, seat.B), 8000);
        Assert.True(acc > 0, $"precondition: c_cmdAccepted must set A.AcceptedShipTarget to B ({seat.B.Id}); FedSrv.CPP:7335-7339");

        // Both slots live on A simultaneously, each pointing at B.
        Assert.True(ReferenceEquals(seat.A.CommandTarget, seat.B));
        Assert.True(ReferenceEquals(seat.A.AcceptedShipTarget, seat.B));
        Assert.Equal((short)OrderChange.CidGoto, seat.A.AcceptedShipCommandId);

        // Neither real-player write may bleed into the NPC-specific P150 slot.
        Assert.Null(seat.A.AcceptedTarget);
        Assert.Equal(OrderChange.CidNone, seat.A.AcceptedCommandId);

        // Now clear ONLY the accepted slot; CommandTarget must stay locked on B.
        seat.ABot.SendOrderChange(seat.A.Id, OrderChange.CidNone, OrderChange.CmdAccepted, (IgcObjectType)(-1), -1);
        var clearedAcc = PumpUntil(seat.Server, () => seat.A.AcceptedShipTarget is null, 8000);
        Assert.True(clearedAcc > 0, "NA-clear on c_cmdAccepted must null A.AcceptedShipTarget");
        Assert.Null(seat.A.AcceptedShipTarget);
        Assert.Equal(OrderChange.CidNone, seat.A.AcceptedShipCommandId);
        Assert.True(ReferenceEquals(seat.A.CommandTarget, seat.B),
            "clearing the accepted slot must NOT disturb CommandTarget (c_cmdCurrent path, P153)");
        Assert.Null(seat.A.AcceptedTarget);

        Console.WriteLine($"P154-COEXIST: A({seat.A.Id}) CommandTarget=B AND AcceptedShipTarget=B coexist; accepted cleared, CommandTarget still B; NPC AcceptedTarget untouched throughout");
    }
}
