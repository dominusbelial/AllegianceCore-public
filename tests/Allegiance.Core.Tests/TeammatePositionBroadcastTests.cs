using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P85: <see cref="Srv.BroadcastShipStates"/>'s teammate loop (P58/P62/P63) let a
/// same-side peer learn a teammate ship exists (EXPORT), who it is (PLAYERINFO),
/// and its coarse structural state (SHIP_STATUS) — but never its live position.
/// <c>SINGLE_SHIP_UPDATE</c> was sent only to a ship's own peer, so a teammate's
/// ship would exist and report correct status yet silently never be observed to
/// move — found live while building AllegianceClientCore's entity-table client
/// (P3), which correctly diagnosed the gap as server-side, not a client bug.
/// Fixed by broadcasting a teammate's <c>SINGLE_SHIP_UPDATE</c> to same-side
/// peers under the identical dirty-position gate the own-ship send already
/// uses, once that peer has already been sent the teammate's EXPORT.
/// </summary>
[Collection("LiveNetwork")]
public class TeammatePositionBroadcastTests
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
            new Gm { Name = "Teammate Position Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 85u, DwCookie = 0x85u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 85000000000000000ul, DrmHash = "teampostest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ The real regression: a same-side teammate's commanded movement
    /// must actually reach the observer as real SINGLE_SHIP_UPDATE traffic, with
    /// a position that visibly changes over time — not just a one-shot EXPORT/
    /// STATUS pair. 3 bots / 2 teams (round-robin) gives a guaranteed same-side
    /// pair and a cross-side enemy; the subject thrusts, the observer must see
    /// its position move, and the enemy must never appear in the observer's
    /// any-ship observations at all (same-side scope, matching P58/P62/P63).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Teammates_commanded_movement_is_broadcast_to_observers_but_enemy_movement_is_not()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p85_subject", "p85_observer", "p85_enemy" };
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
            short enemyId = ids[enemy];
            var obsBot = bots[observer];

            // Command real, sustained thrust on both the subject (teammate) and
            // the enemy, so both are genuinely "dirty" every tick.
            bots[subject].CommandControls(new ControlData(0f, 0f, 0f, 0.7f), 0);
            bots[enemy].CommandControls(new ControlData(0f, 0f, 0f, 0.7f), 0);

            // Wait until the observer has actually received the subject's EXPORT
            // (the documented ordering precondition for a position broadcast).
            PumpUntil(server, () => obsBot.Exports.Any(e => e.ShipId == subjectId), 4000);
            Assert.Contains(obsBot.Exports, e => e.ShipId == subjectId);

            // Pump enough real ticks for real movement + several broadcast passes.
            PumpUntil(server, () => obsBot.AnyShipObservations.Count(o => o.ShipId == subjectId) >= 2, 4000);

            var subjectObs = obsBot.AnyShipObservations.Where(o => o.ShipId == subjectId).ToList();
            Assert.True(subjectObs.Count >= 2, "observer must receive multiple SINGLE_SHIP_UPDATEs for the teammate's real ship");

            var first = subjectObs.First();
            var last = subjectObs.Last();
            Assert.True((last.Position - first.Position).Length() > 0.01f,
                $"teammate's observed position must actually change under real thrust (first={first.Position}, last={last.Position})");

            // Same-side scope: the enemy's movement must never reach the observer
            // as a SINGLE_SHIP_UPDATE, no matter how long we wait.
            Assert.DoesNotContain(obsBot.AnyShipObservations, o => o.ShipId == enemyId);
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }

    /// <summary>⭐ P332 regression — the exact bug this task fixes, in its
    /// starkest form: a teammate that NEVER moves (no thrust, no drift) must
    /// still have its position broadcast at least once to a same-side observer.
    /// Before P332 the server seeded <c>LastBroadcast*</c> from the ship's own
    /// real spawn values, so a stationary ship's position-orientation dirty
    /// check read a permanent zero diff and its SINGLE_SHIP_UPDATE was never
    /// sent to ANYONE — the ship existed client-side (EXPORT/PLAYERINFO/STATUS
    /// are gated on one-shot sets and periodic ticks, not motion) but was never
    /// placed anywhere in 3D space: invisible forever. This proves the at-least-
    /// once guarantee: the observer must see the idle teammate's real spawn
    /// position, with no movement, exactly once-or-more — and the enemy stays
    /// out (same-side scope preserved).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Stationary_teammate_is_still_broadcast_to_observer_once()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass ready-up lobby
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p332_subject", "p332_observer", "p332_enemy" };
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
            short enemyId = ids[enemy];
            var obsBot = bots[observer];

            // THE bug trigger: the subject is commanded to NOTHING. No thrust,
            // so its ship never moves and would previously never cross the
            // position dirty gate — invisible to the observer forever.
            bots[subject].CommandControls(ControlData.Neutral, 0);

            // Wait for the observer to learn the teammate exists (EXPORT), then
            // pump plenty of real ticks: if the P332 guarantee holds, the idle
            // teammate's spawn position arrives as at least one SINGLE_SHIP_UPDATE.
            PumpUntil(server, () => obsBot.Exports.Any(e => e.ShipId == subjectId), 4000);
            Assert.Contains(obsBot.Exports, e => e.ShipId == subjectId);
            PumpUntil(server, () => obsBot.AnyShipObservations.Count(o => o.ShipId == subjectId) >= 1, 8000);

            var subjectObs = obsBot.AnyShipObservations.Where(o => o.ShipId == subjectId).ToList();
            Assert.NotEmpty(subjectObs);
            // It must be parked at the subject's real spawn, not drifting.
            var pos = server.Ships.First(s => s.Ship.Id == subjectId).Ship.Position;
            Assert.True((subjectObs[0].Position - pos).Length() < 1e-2f,
                $"observer must see the idle teammate at its real spawn; got {subjectObs[0].Position}, server {pos}");
            Assert.True((subjectObs[0].Velocity).Length() < 1e-3f,
                $"idle teammate's broadcast must carry ~zero velocity; got {subjectObs[0].Velocity}");

            // Same-side scope preserved: enemy never leaks a position update.
            Assert.DoesNotContain(obsBot.AnyShipObservations, o => o.ShipId == enemyId);
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }
}
