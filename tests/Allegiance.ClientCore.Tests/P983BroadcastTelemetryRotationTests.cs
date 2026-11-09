using System.Runtime.CompilerServices;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P983 — the broadcast update-age table across game-over rotation. Native keeps
/// one mission object and resets it (<c>FedSrv/fsmission.cpp:3490-3527</c>), so
/// every per-ship native latch dies with the same reset; the port rotates to a
/// freshly assembled world, so the diagnostic's (peer, ship) pose stamps must be
/// retired with the old world (P884's complement rule) and must NOT grow with the
/// number of matches played.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P983BroadcastTelemetryRotationTests(ITestOutputHelper output)
{
    private const int Peers = 8;
    private const int Rotations = 3;

    private static ServerConfig Config() => new()
    {
        Port = 0,
        SideSelectAutoSeatSeconds = 0.2,   // ConnectionSession peers never send POSITIONREQ
        Profiler = new() { BroadcastBudget = true },
        // The PORT teammate stream exercises the legacy HEAVY builder as well as
        // the ranked one seeded by the loopback join; the diagnostic must count
        // both (P887 keeps "native" as the default for real servers).
        Broadcast = new() { ShipUpdateModel = "port" },
        Transport = new() { MaxConnections = 64, MaxConnectionsPerIp = 64 },
        LiveSettings = new() { TimeDilation = "off", AutoNextMatch = new() { Enabled = false } },
        Games = [new()
        {
            Name = "P983", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"],
            MaxTeamImbalance = 32767, MaxPlayersPerTeam = Peers,
            MapGen = new() { Type = "SingleRing", PlayerSectorAsteroids = 20, PlayerSectorSpecialAsteroids = 8 },
        }],
    };

    private static bool Read(ConnectionSession peer, Func<bool> predicate)
    { lock (peer.Gate) return predicate(); }

    private static T ReadValue<T>(ConnectionSession peer, Func<T> get)
    { lock (peer.Gate) return get(); }

    private static void Pump(Srv server, Func<bool> done, int max = 20_000)
    {
        for (int i = 0; i < max; i++)
        {
            if (done()) return;
            server.TickSim();
            Thread.Sleep(1);
        }
        Assert.True(done(), "loopback condition timed out");
    }

    private static bool PendingRotationsEmpty(Srv server)
    {
        var list = (System.Collections.ICollection)typeof(Srv)
            .GetField("_pendingRotations", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
        return list.Count == 0;
    }

    private static void End(Srv server, string reason) => typeof(Srv)
        .GetMethod("DeclareGameOver", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(server, [server.Missions[0], (short)0, reason]);

    private static ConnectionSession Connect(Srv server, string name, ulong id)
    {
        var peer = new ConnectionSession();
        peer.Connect(server.BoundPort, name);
        peer.SendLogonRequest(new()
        {
            CharacterName = name, FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            Time = 1, DwCookie = uint.MaxValue, CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = id,
        });
        Pump(server, () => Read(peer, () => peer.World.MyShipId is not null && peer.World.MySideId is not null));
        return peer;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Rotate(Srv server, int rot)
    {
        var old = server.Missions[0];
        old.Stage = MissionStage.Started;
        End(server, $"P983 rotation {rot}");
        Pump(server, () => PendingRotationsEmpty(server));
        // Let the fresh match broadcast for a moment so its own stamps populate
        // the table (otherwise "no growth" would be trivially true).
        for (int i = 0; i < 60; i++) { server.TickSim(); Thread.Sleep(2); }
        old = null!;
    }

    [SkippableFact, Trait("Tier", "Live"), Trait("Requires", "Artwork")]
    public void Rotation_retires_pose_stamps_and_does_not_grow_the_table()
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter();
        Console.SetOut(log);   // keep the boot/report lines out of the test console
        try
        {
            using var server = new Srv();
            server.SpawnInitialMinersAtBoot = false;
            server.Boot(Config());
            Assert.NotNull(server.BroadcastBudgetTelemetry);
            var conns = new List<ConnectionSession>();
            for (int i = 0; i < Peers; i++)
                conns.Add(Connect(server, $"P983-p{i:D3}", 983_000ul + (ulong)i));

            var rows = new List<string>();
            for (int rot = 1; rot <= Rotations; rot++)
            {
                Pump(server, () => conns.TrueForAll(peer => Read(peer, () => peer.World.MyShipId is not null)));
                foreach (var peer in conns)
                    peer.SendPlayerReady(true, ReadValue(peer, () => peer.World.MyShipId!.Value));
                Pump(server, () => server.Missions[0].Stage == MissionStage.Started);
                // Fly the match for a moment so the pose send path actually runs:
                // a docked ship is absent from the cluster's candidate list (native
                // cluster->GetShips()), and these scripted peers never send LAUNCH.
                foreach (var ship in server.Missions[0].GetShips()) ship.StationId = null;
                for (int i = 0; i < 120; i++) { server.TickSim(); Thread.Sleep(1); }
                var played = server.BroadcastTelemetryCounts();

                // Capture the old world's ship ids so the rotation can be checked
                // for retirement (native loses every CFShip with the same reset,
                // FedSrv/fsmission.cpp:3490-3527).
                var oldIds = server.Missions[0].GetShips().Select(ship => ship.Id).ToArray();
                Rotate(server, rot);
                var retired = server.BroadcastTelemetryCounts();
                rows.Add($"rot={rot} played.peers={played.Peers} played.poseStamps={played.PoseStamps} " +
                         $"retired.peers={retired.Peers} retired.poseStamps={retired.PoseStamps} probes={server.TestBroadcastTelemetryProbes}");
                Assert.True(played.PoseStamps > 0,
                    $"rotation {rot}: the update-age table is empty while the match was flying — " +
                    $"the pose send path was not exercised (peers={played.Peers} probes={server.TestBroadcastTelemetryProbes} " +
                    $"ships={server.Missions[0].GetShips().Count()} sectors=[" +
                    string.Join(",", server.Missions[0].Sectors.Select(s => s.GetShipUpdateCandidates().Count)) + "])");
                // One stamp per (peer, visible ship): the table is bounded by the
                // live world's population, never by the number of matches played.
                Assert.True(played.PoseStamps <= played.Peers * oldIds.Length,
                    $"rotation {rot}: {played.PoseStamps} pose stamps exceeds peers x ships " +
                    $"({played.Peers} x {oldIds.Length}); the table accumulates per-match state");
                Assert.True(retired.PoseStamps <= played.PoseStamps,
                    $"rotation {rot}: the rotation left {retired.PoseStamps} pose stamps behind " +
                    $"(a flying match had {played.PoseStamps})");
                Assert.DoesNotContain(oldIds, server.BroadcastTelemetryTracksShip);
            }
            output.WriteLine(string.Join(Environment.NewLine, rows));
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally { Console.SetOut(saved); }
    }
}
