using System.Reflection;
using Allegiance.Core;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P774 — the live end-to-end proof that the in-mission roster is derived, as
/// native, from PLAYERINFO(10) seeds plus KILL_SHIP(105) counter updates
/// (<c>clintlib.h:346-361</c>; <c>clintlib/appmsg.cpp:2766-2773,2805-2808</c>;
/// <c>FedSrv/FedSrv.CPP:2056-2135</c>), with no port-only fmid 209/219 frames
/// anywhere on the wire. Native sends PLAYERINFO for every mission ship to
/// every mission peer (<c>FedSrv.CPP:2129-2134</c>); the port seeds enemy
/// ships at the cross-side EXPORT latch (HUD-20), so the fixture parks every
/// ship in one sector to make the whole mission mutually visible — the same
/// precondition <see cref="Hud20PlayerInfoSeedTests"/> seeds under.
/// </summary>
[Collection("LiveNetwork")]
public sealed class ScoreRosterLiveTests
{
    private static void Pump(Srv server, Func<bool> done, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < deadline) { server.TickSim(); Thread.Sleep(2); }
        Assert.True(done(), "Timed out waiting for scoreboard/session state");
    }

    private static ConnectionSession Seat(Srv server, string name)
    {
        var session = new ConnectionSession();
        session.Connect(server.BoundPort, name);
        session.SendLogonRequest(new LogonRequest { CharacterName = name, FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1 });
        Pump(server, () => session.Ack is not null);
        session.SendPositionReq(-1);
        Pump(server, () => { lock (session.Gate) return session.World.MyShipId is not null; });
        return session;
    }

    /// <summary>Park every mission ship in one sector, so every seated peer's
    /// side sees every enemy (the HUD-20 cross-side EXPORT + PLAYERINFO seed
    /// fires for each (peer, ship) pair — native's whole-mission audience,
    /// FedSrv.CPP:2129-2134, is emulated by mutual visibility).</summary>
    private static void ParkEveryoneInOneSector(Srv server)
    {
        var mission = server.Missions[0];
        var hub = server.Ships.Select(s => s.Ship).First(sh => sh.Mission == mission && sh.SideId is not null).Sector!;
        foreach (var entry in server.Ships)
            if (entry.Ship.Mission == mission && !ReferenceEquals(entry.Ship.Sector, hub))
                mission.PlaceShip(entry.Ship, hub);
    }

    private static ScoreRosterEntry[] Roster(ConnectionSession session)
    { lock (session.Gate) return session.World.ScoreRoster.ToArray(); }

    private static ScoreRosterEntry? Row(ConnectionSession session, string name) =>
        Roster(session).FirstOrDefault(e => e.CharacterName == name);

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Scoreboard_tracks_real_kills_from_PLAYERINFO_and_KILL_SHIP_without_fmids_209_219()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(new ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
            Games = [new GameConfig { Name = "P774", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }],
        });
        server.Missions[0].Stage = MissionStage.Started;

        // Record EVERY frame (ordered + coalescing lanes) from before the first
        // seat, so the no-209/219 assertion covers the join bursts too.
        var frames = new List<(ushort Fmid, int Bytes)>();
        void capture(byte[] packet)
        {
            foreach (var m in FedPacket.Unpack(packet))
                frames.Add((FedMessageHeader.Read(m).Fmid, m.Length));
        }
        server._testSendCapture = (_, packet) => capture(packet);
        server._testSendCoalescingCapture = (_, packet, _, _) => capture(packet);

        using var a = Seat(server, "Ace");
        using var b = Seat(server, "Bee");
        using var c = Seat(server, "Cee");
        using var harness = Seat(server, "mcp-p774");
        ParkEveryoneInOneSector(server);

        // Native audience: every mission peer gets one PLAYERINFO for every
        // mission ship (FedSrv.CPP:2129-2134), so the harness session sees the
        // same derived rows as everybody else.
        string[] names = ["Ace", "Bee", "Cee", "mcp-p774"];
        Pump(server, () => names.All(n => Roster(a).Any(e => e.CharacterName == n))
            && names.All(n => Roster(c).Any(e => e.CharacterName == n))
            && names.All(n => Roster(harness).Any(e => e.CharacterName == n)));

        var attacker = server.Ships.Single(s => s.Ship.Id == a.World.MyShipId).Ship;
        var victimEntry = server.Ships.Single(s => s.Ship.Id == b.World.MyShipId);
        var victim = victimEntry.Ship;
        var victimScore = victimEntry.PlayerScore;
        var attackerScore = server.Ships.Single(s => ReferenceEquals(s.Ship, attacker)).PlayerScore;
        // Same real-damage fixture as PlayerScoreCombatScoringTests: a side
        // without a lifepod makes lethal hull damage a death rather than eject.
        short victimRealSide = victim.SideId!.Value;
        victim.SideId = 774;
        victim.ReceiveDamage(0, 1e9f, server.SimNow, victim.Position, Vector3.Zero, attacker);
        Assert.Equal(1, victimScore.Deaths);
        Assert.Equal(1, attackerScore.Kills);
        victim.SideId = victimRealSide;   // back on its real side so the P439 reseat finds it

        // KILL_SHIP(105) updates the derived counters on every client —
        // clintlib/appmsg.cpp:2766-2767 (AddDeath) and :2805-2808 (AddKill).
        Pump(server, () => Roster(a).Any(e => e.CharacterName == "Ace" && e.Kills == 1)
            && Roster(a).Any(e => e.CharacterName == "Bee" && e.Deaths == 1)
            && Roster(c).Any(e => e.CharacterName == "Ace" && e.Kills == 1)
            && Roster(c).Any(e => e.CharacterName == "Bee" && e.Deaths == 1));

        // A real eject: Cee's ship keeps its lifepod-backed real side, so lethal
        // damage is an ejection (bDeathCredit = false, FedSrv.CPP:11797-11798);
        // the client counts AddEjection (appmsg.cpp:2770-2771) — P774 Fix 2.
        // The launcher must be CROSS-SIDE (friendly fire is suppressed with FF
        // off, shipIGC.cpp:1222-1228), so spawn a throwaway enemy drone as the
        // damage source (the KillBonusLiveTests pattern).
        var ceeEntry = server.Ships.Single(s => s.Ship.Id == c.World.MyShipId);
        var ejectMission = ceeEntry.Ship.Mission!;
        short ceeSide = ceeEntry.Ship.SideId!.Value;
        var enemyHull = ejectMission.Core.HullTypes.Values.First();
        var ejectLauncher = new Ship((short)(980 + Random.Shared.Next(64)), enemyHull,
            ceeEntry.Ship.Position + new Vector3(50, 0, 0), Vector3.Zero, Orientation.Identity)
        { SideId = ceeSide == ejectMission.Sides[0].Id ? ejectMission.Sides[1].Id : ejectMission.Sides[0].Id };
        ejectMission.PlaceShip(ejectLauncher, ceeEntry.Ship.Sector!);
        ceeEntry.Ship.ReceiveDamage(0, 1e9f, server.SimNow, ceeEntry.Ship.Position, Vector3.Zero, ejectLauncher);
        Assert.Equal(1, ceeEntry.PlayerScore.Ejections);   // the server counted the eject (FedSrv.CPP:11918-11921)
        Pump(server, () => (Row(a, "Cee")?.Ejections ?? 0) >= 1 && (Row(c, "Cee")?.Ejections ?? 0) >= 1);

        // Cee leaves: the row goes on every client.
        c.SendLogoff();
        Pump(server, () => Roster(a).All(e => e.CharacterName != "Cee"));

        // Cee rejoins: the PLAYERINFO seed ALONE carries Ace's kill (native
        // fsmission.cpp:3958-3968 sends one PLAYERINFO per ship to a joiner).
        using var rejoined = Seat(server, "Cee");
        ParkEveryoneInOneSector(server);
        Pump(server, () => Roster(rejoined).Any(e => e.CharacterName == "Ace" && e.Kills == 1)
            && Roster(a).Any(e => e.CharacterName == "Cee"));

        // Go Over → the P439 rotation reseats everyone into a fresh mission; the
        // clients cleared their ships at BeginNextMission (ClientWorld.cs), so
        // the server must re-send PLAYERINFO (fsmission.cpp:3958-3968) — the
        // roster refills with every seated human in the next mission.
        typeof(Srv).GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [server.Missions[0], (short)GameOver.NA, "P774 test game over"]);
        Pump(server, () =>
        {
            lock (a.Gate) return a.World.Mission?.Stage is (int)MissionStage.Over or (int)MissionStage.NotStarted;
        });
        ParkEveryoneInOneSector(server);   // the fresh ships spawn docked: re-create mutual visibility
        // Ready up every migrated session so the fresh mission can start.
        foreach (var s in new[] { a, b, rejoined, harness })
        {
            short? shipId; lock (s.Gate) shipId = s.World.MyShipId;
            if (shipId is { } id) s.SendPlayerReady(true, id);
        }
        Pump(server, () => server.Missions[0].Stage == MissionStage.Started);
        ParkEveryoneInOneSector(server);
        Pump(server, () =>
        {
            var ra = Roster(a); var rc = Roster(rejoined);
            return names.All(n => ra.Any(e => e.CharacterName == n))
                && new[] { "Ace", "Bee", "Cee" }.All(n => rc.Any(e => e.CharacterName == n));
        });

        // P661 egress accounting for the report: 209/219 = 0 bytes, and the
        // PLAYERINFO(10) traffic the derivation costs instead.
        var rosterFrames = frames.Where(f => f.Fmid is 209 or 219).ToList();
        var playerInfoFrames = frames.Where(f => f.Fmid == PlayerInfo.Fmid).ToList();
        Console.WriteLine($"P774-TRAFFIC: frames captured={frames.Count}, " +
                          $"score-roster 209/219 frames={rosterFrames.Count} bytes={rosterFrames.Sum(f => f.Bytes)}, " +
                          $"PLAYERINFO(10) frames={playerInfoFrames.Count} bytes={playerInfoFrames.Sum(f => f.Bytes)} " +
                          $"avg={(playerInfoFrames.Count > 0 ? playerInfoFrames.Average(f => f.Bytes) : 0):0.0} B/frame");

        Assert.Empty(rosterFrames);   // 209/219 are retired — no frame may carry them
        Assert.True(playerInfoFrames.Count > 0, "the derivation needs PLAYERINFO seeds on the wire");
        foreach (var s in new[] { a, b, rejoined, harness })
            Assert.DoesNotContain(s.UnknownFmids, f => f is 209 or 219);

        server._testSendCapture = null;
        server._testSendCoalescingCapture = null;
    }
}
