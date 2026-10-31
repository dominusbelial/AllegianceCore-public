using System.Net;
using System.Reflection;
using System.Text.Json;
using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P903ProductionReachabilityTests
{
    // Native GenerateMission marks the first generation, then increments on reuse
    // (Igc/missionigc.cpp:3846-3857). RecordGameResults consumes GetContextName
    // (FedSrv/fsmission.cpp:2766), whose suffix consumes GetReplayCount
    // (Igc/missionigc.cpp:4211-4238).
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public async Task Boot_and_two_rotations_report_distinct_native_contexts_with_replay_counts()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(new ServerConfig
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
            Games = [new GameConfig { Name = "P903", Map = "m2xspiderw.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        using var handler = new CaptureResults();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var heartbeat = new LobbyHeartbeat(http, new LobbyConfig(), () => 0,
            "P903", "PCore001.igc", "test", server.BoundPort);
        typeof(LobbyHeartbeat).GetField("_enrollment", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(heartbeat, new ServerEnrollment("p903-test", "test-only"));
        typeof(Srv).GetField("_heartbeat", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(server, heartbeat);

        var initial = server.Missions[0];
        var created = initial.CreatedWallTime;
        string prefix = $"U{created:MMddHHmmss}{initial.MissionId % 10000:0000}";
        for (short replay = 0; replay <= 2; replay++)
        {
            var mission = server.Missions[0];
            Assert.True(mission.HasGenerated);
            Assert.Equal(replay, mission.ReplayCount);
            Assert.Equal(initial.MissionId, mission.MissionId);
            Assert.Equal(created, mission.CreatedWallTime);
            // A qualifying squad result reaches the real server result builder
            // without needing a verified player or a background lobby connection.
            mission.SquadGame = true;
            mission.Sides[0].SquadId = "alpha";
            mission.Sides[1].SquadId = "bravo";
            handler.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(Srv).GetMethod("ReportMissionResultsToLobby", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(server, [mission, (short)0]);
            var report = await handler.Next.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(prefix + replay.ToString("00"), report.Game!.GameId);
            Assert.Equal(mission.GetContextName(), report.Game.GameId);
            Assert.Same(mission.GetContextName(), mission.GetContextName());
            if (replay < 2)
                typeof(Srv).GetMethod("RotateMissionAfterGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(server, [mission]);
        }
    }

    private sealed class CaptureResults : HttpMessageHandler
    {
        public TaskCompletionSource<LobbyMatchReport> Next { get; set; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/career/results")
            {
                string json = await request.Content!.ReadAsStringAsync(cancellationToken);
                Next.TrySetResult(JsonSerializer.Deserialize(json, ServerJsonContext.Default.LobbyMatchReport)!);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
