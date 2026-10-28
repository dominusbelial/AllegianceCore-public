using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P90: the live loopback proof that a directly-fired missile is broadcast as a
/// real <c>FM_CS_FIRE_MISSILE(70)</c> to the launcher's same-side peers — and,
/// when it expires, a real <c>FM_S_MISSILE_DESTROYED(83)</c> naming that same
/// missile and where it expired — and that a cross-side enemy receives neither.
/// Construction mirrors <see cref="ProjectileBroadcastTests"/> (3 bots /
/// round-robin sides, a guaranteed same-side pair plus a cross-side enemy), but
/// the missile is triggered by calling <c>Ship.FireMissile</c> directly on the
/// shooter's server-side <c>Ship</c> (the public, deliberately-not-dispatch-
/// wired entry point this slice adds), with a real <c>DataMissileTypeIGC</c>
/// pulled from the loaded <c>StaticCore</c> (<c>mission.Core.MissileTypes</c>).
/// Four things are asserted for real:
/// (1) the same-side observer receives a decoded <see cref="FireMissile"/> with
/// exactly one <see cref="MissileLaunchData"/> entry whose fields are real
/// (position near the shooter, velocity along the shooter's forward at the
/// missile's decoded <c>InitialSpeed</c>, and a real missile id);
/// (2) after pumping ticks past the missile's decoded <c>Lifespan</c>, the
/// observer receives a <see cref="MissileDestroyed"/> naming that same id with a
/// real position ≈ <c>launchPosition + velocity·Lifespan</c> — where it
/// expired, not the launch position again;
/// (3) the cross-side enemy receives neither message, no matter how long ticks
/// are pumped past expiry;
/// (4) firing twice yields two distinct missile ids and two distinct destroy
/// events — not one reused/cached pair.
/// </summary>
[Collection("LiveNetwork")]
public class MissileBroadcastTests
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
            new Gm { Name = "Missile Broadcast Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 852u, DwCookie = 0x852u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 86000000000000000ul, DrmHash = "misstest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A real missile type the shooter will fire — one whose decoded
    /// <c>Lifespan</c> falls in [2 s, 8 s), comfortably long enough to pump the
    /// creation broadcast, then pump past expiry for the destruction broadcast,
    /// without either racing. Initial speed must be nonzero so the wire
    /// velocity direction is meaningful.</summary>
    private static DataMissileTypeIGC RealMissileType(StaticCore core)
    {
        var t = core.MissileTypes.Values
            .Where(m => m.InitialSpeed > 0f && m.Lifespan >= 2.0f && m.Lifespan < 8.0f)
            .OrderBy(m => m.Lifespan)
            .FirstOrDefault();
        if (t.ExpendableTypeId == 0 && core.MissileTypes.Count > 0)
            throw new InvalidOperationException("representative core has no missile with speed>0 and 2<=lifespan<8");
        return t;
    }

    /// <summary>⭐ A teammate must see a real, decoded FIRE_MISSILE(70) with one
    /// real MissileLaunchData entry for a missile its same-side ally fires, and
    /// later a real MISSILE_DESTROYED(83) naming that same missile at the point
    /// it expired — while the cross-side enemy receives neither, no matter how
    /// long we pump. Firing twice yields two distinct ids / two distinct destroy
    /// events. The missiles are fired by calling <c>Ship.FireMissile</c> directly
    /// (the not-yet-dispatch-wired entry point).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Directly_fired_missile_broadcasts_create_to_teammate_and_destroy_to_whole_mission()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p90_shooter", "p90_observer", "p90_enemy" };
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
            var obsBot = bots[observer];
            var enemyBot = bots[enemy];

            var live = server.Ships.First(l => l.Ship.Id == subjectId);
            var shooter = live.Ship;
            var mission = shooter.Mission ?? throw new InvalidOperationException("shooter must have a mission");
            var mType = RealMissileType(mission.Core);

            // Gate precondition: the observer must already hold the shooter's
            // EXPORT, else the server (correctly) withholds the teammate send.
            PumpUntil(server, () => obsBot.Exports.Any(e => e.ShipId == subjectId), 8000);
            Assert.Contains(obsBot.Exports, e => e.ShipId == subjectId);

            // Fresh default seats now start in the hangar. Exercise emissions
            // from a genuinely flying source through the real undock wire path.
            bots[subject].SendDocked();
            PumpUntil(server, () => shooter.StationId is null, 4000);
            Assert.Null(shooter.StationId);
            Assert.True(shooter.Fraction > 0);
            // The launch kick points out of the station. The default identity
            // nose points back into its hull; aim along that real outward kick
            // so this expiry test measures a flight, rather than an immediate hit.
            Assert.True(shooter.Velocity.LengthSquared() > 0);
            shooter.Orientation.Set(shooter.Velocity);

            // Fire TWO missiles directly at the current sim time. Distinct ids
            // (NextObjectId increments per call) and, since both share `now`, both
            // expire together — yielding two distinct destroys, proving no cache.
            var launchPosition = shooter.Position; // immutable launch-time reference, before receive pumping moves the ship
            double now = mission.LastUpdate;
            shooter.FireMissile(mType, now);
            shooter.FireMissile(mType, now);

            // (1) the teammate receives two decoded FireMissile(70), each with
            // exactly one launch-data entry whose fields are real.
            PumpUntil(server, () => obsBot.FireMissiles.Count >= 2, 6000);
            var created = obsBot.FireMissiles;
            Assert.True(created.Count >= 2, $"observer must receive ≥ 2 FIRE_MISSILE messages (one per launch), got {created.Count}");
            var byId = new Dictionary<short, MissileLaunchData>();
            foreach (var fm in created)
            {
                Assert.Single(fm.Entries);                       // one missile per fire — never a salvo this slice
                var e = fm.Entries[0];
                byId[e.MissileId] = e;
                Assert.Equal(mType.ExpendableTypeId, fm.MissileTypeId);
                Assert.Equal(subjectId, fm.LauncherId);
                Assert.Equal(shooter.Sector?.Id, fm.ClusterId);
                Assert.True((e.VecPosition - launchPosition).Length() < 50f,
                    $"missile spawn {e.VecPosition} must be near the launch-time shooter {launchPosition}");
                float speed = e.VecVelocity.Length();
                Assert.True(speed > 0f, "missile velocity must be nonzero");
                float dot = e.VecVelocity.X * e.VecForward.X + e.VecVelocity.Y * e.VecForward.Y + e.VecVelocity.Z * e.VecForward.Z;
                Assert.True(dot / speed > 0.996f,
                    $"missile velocity {e.VecVelocity} must lie along the launch forward {e.VecForward} (± direction of inherited launcher velocity)");
            }
            Assert.True(byId.Keys.Distinct().Count() == 2,
                $"two firings must yield two distinct missile ids, got [{string.Join(", ", byId.Keys)}]");

            // (2) pumping past the decoded Lifespan reaches the destruction
            // broadcast: one real MissileDestroyed per created missile, naming the
            // same id and at ≈ launchPosition + velocity·Lifespan (not the launch
            // point). Tolerance covers up to ~one sim chunk's overshoot + float.
            PumpUntil(server, () => obsBot.MissileDestroyeds.Count >= 2, 12000);
            var dest = obsBot.MissileDestroyeds;
            Assert.True(dest.Count >= 2, $"observer must receive ≥ 2 MISSILE_DESTROYED (one per expired missile), got {dest.Count}");
            foreach (var d in dest)
            {
                Assert.True(byId.TryGetValue(d.MissileId, out var e),
                    $"destroy {d.MissileId} must name a missile we saw launched");
                Assert.Equal(shooter.Sector?.Id, d.ClusterId);
                float speed = e.VecVelocity.Length();
                float expectedTravel = speed * mType.Lifespan;
                float fromLaunch = (d.Position - e.VecPosition).Length();
                Assert.True(MathF.Abs(fromLaunch - expectedTravel) < speed * 0.091f + 5f,
                    $"destroy position {d.Position} must be ≈ launch + velocity·Lifespan (" +
                    $"{e.VecPosition} + {speed}m/s × {mType.Lifespan}s ⇒ {expectedTravel} m from launch); got {fromLaunch} m");
                Assert.True(fromLaunch > speed * 0.5f,
                    "destroy position must not be the launch position again");
            }
            Assert.True(dest.Select(d => d.MissileId).Distinct().Count() == 2,
                $"two expiries must yield two distinct destroy ids, got [{string.Join(", ", dest.Select(d => d.MissileId))}]");

            // (3) P866 / SIM-0659 — native KillMissileEvent sends
            // MISSILE_DESTROYED to GetGroupRealSides() (FedSrv.CPP:12520-12521):
            // the cross-side enemy DOES receive the destruction notice even though
            // it never got the missile's creation message (native's client only
            // explodes a missile it knows, clintlib/appmsg.cpp:1795-1804). It must
            // still receive NO creation message, no matter how long we pump.
            PumpUntil(server, () => enemyBot.MissileDestroyeds.Count >= 2, 400);
            Assert.Empty(enemyBot.FireMissiles);
            Assert.Equal(2, enemyBot.MissileDestroyeds.Select(d => d.MissileId).Distinct().Count());
            foreach (var d in enemyBot.MissileDestroyeds)
                Assert.Equal(shooter.Sector?.Id, d.ClusterId);
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }
}
