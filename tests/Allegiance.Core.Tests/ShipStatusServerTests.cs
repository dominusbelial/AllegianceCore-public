using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
// 'Server' and 'Bot' are ALSO namespace names (Allegiance.Server, Allegiance.Bot)
// reachable from this namespace, so the simple names are ambiguous with the
// namespaces. Aliased to distinct, unambiguous identifiers instead.
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// Proves the P40 deliverable at the wire level: the server emits an
/// <c>FM_S_SHIP_STATUS(90)</c> for a peer's OWN ship as part of the join burst
/// (right after <c>MISSION_STAGE(37)</c>), and a real loopback <see cref="FltBot"/>
/// decodes it to exactly the ship it was assigned (same ShipId + sector as its
/// <c>SET_CLUSTER(7)</c> assignment). This is the managed-server half of making
/// <c>ScoutExploreMap</c>'s primary sweep reachable — the native client's
/// handler is gated on <c>FindPlayer(shipID)</c> which this server seeds only
/// for the peer's own ship, so the own-ship status is the correctly-scoped
/// unit to prove.
/// </summary>
[Collection("LiveNetwork")]
public class ShipStatusServerTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Status Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 911u, DwCookie = 0xCAFEu,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 72000000000000000ul, DrmHash = "statustest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>After the peer's ship is assigned and the join burst has flushed,
    /// it must have received exactly one SHIP_STATUS for its OWN ship, carrying
    /// that ship's id and the sector it was placed in.</summary>
    [Fact]
    public void PeerReceivesItsOwnShipStatus_InJoinBurst()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var bot = new FltBot();

        // Connect + logon like the console app; the server bursts on logon.
        bot.Connect(server.BoundPort, "status_probe");
        bot.SendLogonRequest(Request("status_probe"));

        // Wait until the ship is assigned AND the join burst (incl. SHIP_STATUS)
        // has been decoded by the client.
        int ticks = PumpUntil(server,
            () => bot.ShipAssignment is not null && bot.ShipStatuses.Count >= 1,
            4000);

        Assert.NotNull(bot.ShipAssignment);
        Assert.NotEmpty(bot.ShipStatuses);
        var status = bot.ShipStatuses[0];
        Assert.Equal(bot.ShipAssignment!.Ship.ShipId, status.ShipId);
        Assert.Equal(bot.ShipAssignment!.SectorId, status.SectorId);

        // Sanity: the decoded status is a healthy flying scout-less frame per the
        // established simplification (State=Flying, detected, not unknown).
        Assert.Equal(ShipStatusMessage.StateFlying, status.State);
        Assert.False(status.Unknown);
        Assert.True(status.Detected);
        Assert.Equal(-1, status.StationId);   // NA — no docking in this server
        Assert.Equal(-1, status.ParentId);

        // The server also recorded the sector so a future sector-crossing would
        // resend (not asserted here — no warp traversal exists yet).
        Assert.True(ticks > 0, "pump did not run");
    }

    /// <summary>The periodic refresh keeps feeding status frames after the join
    /// burst, so a client-side strategy that attaches late (after the burst) is
    /// guaranteed to receive one — the reason a single join-burst send is not
    /// enough (P40 live finding: ScoutExploreMap wires its MessageReceiver on
    /// attach, StrategyBase.cs:90, racing the burst).</summary>
    [Fact]
    public void ServerRefreshesOwnShipStatusPeriodically()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var bot = new FltBot();

        bot.Connect(server.BoundPort, "refresh_probe");
        bot.SendLogonRequest(Request("refresh_probe"));
        int ticks = PumpUntil(server, () => bot.ShipAssignment is not null && bot.ShipStatuses.Count >= 1, 4000);
        Assert.True(ticks > 0);
        int firstCount = bot.ShipStatuses.Count;

        // Pump well past one refresh interval (40 ticks); more statuses arrive.
        PumpUntil(server, () => bot.ShipStatuses.Count >= firstCount + 2, 200);
        Assert.True(bot.ShipStatuses.Count > firstCount,
            $"expected refresh to add statuses; firstCount={firstCount}, now={bot.ShipStatuses.Count}");
    }

    /// <summary>⭐ P58 — same-side TEAMMATE visibility at the wire level. Three
    /// bots join a 2-team map; default spawn seats them round-robin so any two
    /// share a side and the third is on the other. Each peer's client must
    /// eventually receive a <c>SHIP_STATUS(90)</c> about EVERY ship on its own
    /// side (self included via the existing path) and NONE about ships on the
    /// opposing side — the reference (CFSMission, fsmission.cpp:4303-4323)
    /// sends every ship with no self-filter, here narrowed to same-side. Proves
    /// the commander can learn a docked miner teammate exists; the docked-state
    /// carriage is proven separately (DockedState_propagates_to_same_side_peer).</summary>
    [Fact]
    public void SameSideShips_are_broadcast_to_each_peer_but_enemy_ships_are_not()
    {
        using var server = new Srv();
        server.Boot(Config());
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        try
        {
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i].Connect(server.BoundPort, $"p58_a_{i}");
                bots[i].SendLogonRequest(Request($"p58_a_{i}"));
            }

            PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);

            // Map every peer ship id -> its side, straight from the authoritative
            // server model (not assumed order).
            var ships = server.Ships.ToDictionary(s => s.Ship.Id, s => s.Ship.SideId);
            Assert.True(ships.Count >= 3, $"expected >=3 peer ships, got {ships.Count}");
            foreach (var b in bots)
                Assert.NotNull(ships[b.ShipAssignment!.Ship.ShipId]);

            // The test is only meaningful if it actually exercises both sides
            // (some peer shares a side with another, and one peer is alone).
            var anySameSide = bots.Any(a => bots.Any(o => o != a &&
                ships[a.ShipAssignment!.Ship.ShipId] == ships[o.ShipAssignment!.Ship.ShipId]));
            var anyCrossSide = bots.Any(a => bots.Any(o => o != a &&
                ships[a.ShipAssignment!.Ship.ShipId] != ships[o.ShipAssignment!.Ship.ShipId]));
            Assert.True(anySameSide && anyCrossSide,
                "3-bot / 2-team round-robin must yield both a same-side pair and a cross-side pair");

            // Pump well past one teammate-refresh interval (100 ticks) so every
            // same-side pair has exchanged a status. No bot moves and none polls —
            // visibility must arrive purely from the periodic broadcast.
            PumpUntil(server,
                () => bots.All(b => server.Ships.Where(s => s.Ship.SideId == ships[b.ShipAssignment!.Ship.ShipId])
                                               .All(t => b.ShipStatuses.Any(s => s.ShipId == t.Ship.Id))),
                4000);

            foreach (var b in bots)
            {
                short myId = b.ShipAssignment!.Ship.ShipId;
                short? mySide = ships[myId];
                var receivedIds = b.ShipStatuses.Select(s => s.ShipId).Distinct().ToList();

                // Every ship id this peer received is on ITS side (self + teammates),
                // and every same-side ship is present.
                foreach (var rid in receivedIds)
                    Assert.Equal(mySide, ships[rid]);   // same-side only
                foreach (var t in server.Ships.Where(s => s.Ship.SideId == mySide))
                    Assert.Contains(t.Ship.Id, receivedIds);   // sees every teammate + self

                // No peer on the opposing side is ever reported to this peer.
                foreach (var e in server.Ships.Where(s => s.Ship.SideId != mySide))
                    Assert.DoesNotContain(e.Ship.Id, receivedIds);   // same-side-only scope
            }
        }
        finally { foreach (var b in bots) b.Dispose(); }
    }

    /// <summary>⭐ P58 — a same-side peer's DOCKED state rides the new teammate
    /// broadcast. Two peers forced onto one side (ShipSpawner seats both on the
    /// friendly station's side, far from any station so neither docks); one is
    /// docked server-side (real station id latched, exactly as the dock pass /
    /// DockDockingTests establish); the OTHER peer — without moving or polling —
    /// decodes a <c>SHIP_STATUS(90)</c> for it carrying <c>State = c_ssDocked</c>
    /// and the real <c>StationID</c>. This is the exact datum a commander's
    /// <c>UndockMinersAndStartMining</c> needs: not just that a miner ship exists,
    /// but that it is docked and where.</summary>
    [Fact]
    public void DockedState_propagates_to_same_side_peer()
    {
        using var server = new Srv();
        short? friendlyId = null;
        int spawned = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var friendly = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            friendlyId = friendly.StationId;
            var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
            float x = 620f * (1 + spawned);   // apart, and far from the station — no dock
            var sector = mission.Sectors[0];
            var ship = new Ship(shipId, hull, new Vector3(x, 710f, 910f), Vector3.Zero, Orientation.Identity);
            ship.SideId = friendly.SideId;    // both peers on the same side
            mission.PlaceShip(ship, sector);
            spawned++;
            return (ship, sector);
        };

        server.Boot(Config());
        using var dockee = new FltBot();
        using var observer = new FltBot();
        dockee.Connect(server.BoundPort, "p58_dockee");
        dockee.SendLogonRequest(Request("p58_dockee"));
        observer.Connect(server.BoundPort, "p58_observer");
        observer.SendLogonRequest(Request("p58_observer"));

        PumpUntil(server, () => dockee.ShipAssignment is not null && observer.ShipAssignment is not null, 4000);
        short dockeeId = dockee.ShipAssignment!.Ship.ShipId;
        short observerId = observer.ShipAssignment!.Ship.ShipId;
        Assert.NotNull(friendlyId);

        // Guard: both really are same-side (the premise the broadcast relies on).
        var dockeeSide = server.Ships.First(s => s.Ship.Id == dockeeId).Ship.SideId;
        var observerSide = server.Ships.First(s => s.Ship.Id == observerId).Ship.SideId;
        Assert.Equal(dockeeSide, observerSide);

        // Dock the dockee server-side with the real station id, exactly as the
        // dock pass latches it (Ship.StationId). Observer never moves and never
        // polls — it must still learn the docked state via the periodic teammate
        // broadcast within ~one refresh interval (100 ticks).
        server.Ships.First(s => s.Ship.Id == dockeeId).Ship.StationId = friendlyId.Value;
        PumpUntil(server,
            () => observer.ShipStatuses.Any(s => s.ShipId == dockeeId && s.State == ShipStatusMessage.StateDocked && s.StationId == friendlyId.Value),
            4000);
        Assert.Contains(observer.ShipStatuses,
            s => s.ShipId == dockeeId && s.State == ShipStatusMessage.StateDocked && s.StationId == friendlyId.Value);
    }

    /// <summary>⭐ P62 — a same-side TEAMMATE's ship is exported to a peer as a
    /// real <c>FM_S_EXPORT(26)</c>, so the client's object model actually learns
    /// the teammate ship exists (the pre-P62 gap: only ever the peer's own ship
    /// was exported, and <c>GetShip(shipID)</c> — SHIP_STATUS's lookup — can only
    /// update an object the client already has). Asserts the wire-level facts
    /// precisely on a 3-bot/2-team join (round-robin ⇒ a same-side pair and a
    /// cross-side odd-one-out):
    ///   • the observer receives an EXPORT naming the subject's real ShipId
    ///     (NOT -1/NA — a genuinely new, separate client-side object) and the
    ///     subject's real logon CharacterName;
    ///   • the EXPORT precedes any SHIP_STATUS about that same ship id
    ///     (ordering requirement — polled so the first subject status is never
    ///     observed without the subject export already present);
    ///   • it is sent EXACTLY ONCE per (recipient, subject) pair even across many
    ///     ticks beyond the first teammate-refresh interval (the one-shot set
    ///     actually works — a repeat CreateObject would misbehave client-side);
    ///   • a ship on the opposing side is never exported this way (same-side
    ///     scope limit, matching P58).</summary>
    [Fact]
    public void Teammate_ship_is_exported_once_with_real_id_and_name_but_enemy_never()
    {
        using var server = new Srv();
        server.Boot(Config());
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p62_miner", "p62_commander", "p62_scout" };
        try
        {
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i].Connect(server.BoundPort, names[i]);
                bots[i].SendLogonRequest(Request(names[i]));
            }
            PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            var missing = bots.Select((b, i) => b.ShipAssignment is null ? $"#{i}" : null).Where(x => x is not null);
            Assert.True(!missing.Any(), "some bots never got assigned: " + string.Join(", ", missing));

            // Authoritative side per ship id, straight from the server model.
            var ships = server.Ships.ToDictionary(s => s.Ship.Id, s => s.Ship.SideId);
            short[] ids = bots.Select(b => b.ShipAssignment!.Ship.ShipId).ToArray();
            Assert.True(ids.All(i => ships.ContainsKey(i)), "all three bots seated");

            // Find a same-side pair (subject = teammate, observer = recipient)
            // and a cross-side ship that must never reach the observer as an EXPORT.
            int subject = -1, observer = -1, enemy = -1;
            for (int i = 0; i < bots.Length && observer < 0; i++)
                for (int j = 0; j < bots.Length; j++)
                    if (j != i && ships[ids[j]] == ships[ids[i]])
                    {
                        subject = i; observer = j; break;
                    }
            Assert.True(subject >= 0, "round-robin must yield a same-side pair");
            for (int k = 0; k < bots.Length; k++)
                if (k != subject && k != observer) { enemy = k; break; }
            Assert.True(enemy >= 0 && ships[ids[enemy]] != ships[ids[subject]],
                "3 bots / 2 teams must leave one bot cross-side");

            short subjectId = ids[subject];
            short enemyId = ids[enemy];
            var obsBot = bots[observer];

            // Poll in small pump steps, snapshotting cumulative arrivals, so we
            // can pin down WHEN the first SHIP_STATUS about the subject lands.
            int sawSubjectStatusAtExportCount = -1;
            int ticks = 0;
            const int StepTicks = 3;
            while (ticks < 4000)
            {
                // Advance a few sim ticks; drain messages meanwhile.
                int step = PumpUntil(server, () => false, StepTicks);   // run StepTicks ticks
                _ = step;
                ticks += StepTicks;

                var exports = obsBot.Exports;
                var statuses = obsBot.ShipStatuses;
                bool subjectStatusPresent = statuses.Any(s => s.ShipId == subjectId);
                int exportCountForSubject = exports.Count(e => e.ShipId == subjectId);
                if (subjectStatusPresent)
                {
                    sawSubjectStatusAtExportCount = exportCountForSubject;
                    break;
                }
            }
            Assert.True(sawSubjectStatusAtExportCount >= 1,
                $"first subject SHIP_STATUS arrived but the subject EXPORT was not already present (exportCount={sawSubjectStatusAtExportCount}); ordering violated");

            // The EXPORT itself is well-formed at the wire level.
            var subjectExport = obsBot.Exports.Single(e => e.ShipId == subjectId);
            Assert.Equal(subjectId, subjectExport.ShipId);            // real id, NOT -1/NA
            Assert.True(subjectExport.ShipId > 0, "real assigned ship id, not NA");
            Assert.Equal(names[subject], subjectExport.CharacterName); // real logon name
            Assert.Equal(ships[subjectId], subjectExport.SideId);     // same side as the subject ship
            Assert.Equal(ExportShip.PilotTypePlayer, subjectExport.PilotType);
            Assert.True(subjectExport.HullId > 0, "hull sane (present)");

            // Exactly once per pair — pump well past several teammate-refresh
            // intervals (100 ticks each) and confirm the one-shot set held.
            for (int i = 0; i < 350; i++) { server.TickSim(); Thread.Sleep(1); }
            Assert.Equal(1, obsBot.Exports.Count(e => e.ShipId == subjectId));

            // Same-side only: the enemy ship is never exported to the observer.
            Assert.DoesNotContain(obsBot.Exports, e => e.ShipId == enemyId);
        }
        finally { foreach (var b in bots) b.Dispose(); }
    }
}
