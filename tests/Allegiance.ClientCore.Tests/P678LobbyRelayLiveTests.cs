using System.Diagnostics;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P678LobbyRelayLiveTests
{
    private static void Until(Srv server, Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            server.TickSim();
            Thread.Sleep(2);
        }
        Assert.True(done(), "Loopback lobby condition did not arrive within 15 seconds");
    }

    private static bool Read(ConnectionSession session, Func<ClientWorld, bool> predicate)
    {
        lock (session.Gate) return predicate(session.World);
    }

    private static void Connect(Srv server, ConnectionSession session, string name, short side)
    {
        session.Connect(server.BoundPort, name);
        session.SendLogonRequest(new LogonRequest
        {
            CharacterName = name, ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            FedsrvVer = LogonRequest.MsgVer, Time = 1, CrcFileList = -1, SteamAuthTicket = new byte[16],
            SteamID = 83100000000000000ul, DrmHash = "p678",
        });
        session.SendPositionReq(side);
        Until(server, () => Read(session, w => w.MyShipId is not null && w.MySideId == side));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Real_lobby_relays_track_requests_rejections_locks_and_auto_accept_sweep()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(new global::Allegiance.Server.ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
            UpdatesPerSecond = 20,
            Games = [new() { Name = "P678 lobby", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        using var leader = new ConnectionSession();
        using var applicant = new ConnectionSession();
        Connect(server, leader, "p678_leader", 0);
        Connect(server, applicant, "p678_applicant", 1);
        // The current join burst sends initial leadership before registering the
        // first peer. Rejoin the same empty-of-others side through real controls
        // to receive a fresh leadership announcement (no injected client state).
        leader.SendAutoAccept(0, true);
        Until(server, () => Read(leader, w => w.AutoAcceptBySideId.GetValueOrDefault((short)0)));
        leader.SendPositionReq(0);
        Until(server, () => Read(leader, w => w.IsTeamLeader && w.AmMissionOwner));
        leader.SendAutoAccept(0, false);
        Until(server, () => Read(leader, w => !w.AutoAcceptBySideId.GetValueOrDefault((short)0))
            && Read(applicant, w => w.AutoAcceptBySideId.TryGetValue(0, out var accept) && !accept));
        short applicantId = applicant.World.MyShipId!.Value;
        var pending = new ClientWorld.PositionRequest(0, applicantId);

        applicant.SendPositionReq(0);
        Until(server, () => Read(leader, w => w.PendingPositionRequests.Contains(pending))
            && Read(applicant, w => w.PendingPositionRequests.Contains(pending)));
        Assert.True(Read(leader, w => w.FeedbackLines.Any(f => f.IsCritical && f.Text.Contains("wishes to join your team"))));

        leader.SendPositionAck(applicantId, 0, false);
        Until(server, () => Read(leader, w => !w.PendingPositionRequests.Contains(pending))
            && Read(applicant, w => w.MyPositionRequestResult?.Reason == DelPositionReqReason.Rejected));
        Assert.Equal((short)1, applicant.World.MySideId);

        leader.SendLockLobby(true);
        leader.SendLockSides(true);
        Until(server, () => Read(leader, w => w.LobbyLocked && w.SidesLocked)
            && Read(applicant, w => w.LobbyLocked && w.SidesLocked));
        applicant.SendPositionReq(0);
        Until(server, () => Read(applicant, w => w.MyPositionRequestResult?.Reason == DelPositionReqReason.SidesLocked));

        leader.SendLockLobby(false);
        leader.SendLockSides(false);
        Until(server, () => Read(leader, w => !w.LobbyLocked && !w.SidesLocked)
            && Read(applicant, w => !w.LobbyLocked && !w.SidesLocked));
        applicant.SendPositionReq(0);
        Until(server, () => Read(leader, w => w.PendingPositionRequests.Contains(pending)));
        applicant.SendDelPositionReq(applicantId, 0, DelPositionReqReason.Canceled);
        Until(server, () => Read(leader, w => !w.PendingPositionRequests.Contains(pending))
            && Read(applicant, w => w.MyPositionRequestResult?.Reason == DelPositionReqReason.Canceled));

        applicant.SendPositionReq(0);
        Until(server, () => Read(leader, w => w.PendingPositionRequests.Contains(pending)));
        leader.SendAutoAccept(0, true);
        Until(server, () => Read(leader, w => w.AutoAcceptBySideId.GetValueOrDefault((short)0) && !w.PendingPositionRequests.Contains(pending))
            && Read(applicant, w => w.AutoAcceptBySideId.GetValueOrDefault((short)0) && w.MySideId == 0 && !w.PendingPositionRequests.Contains(pending)));
        leader.SendAutoAccept(0, false);
        Until(server, () => Read(leader, w => !w.AutoAcceptBySideId.GetValueOrDefault((short)0))
            && Read(applicant, w => !w.AutoAcceptBySideId.GetValueOrDefault((short)0)));
        Assert.DoesNotContain((ushort)40, leader.UnknownFmids);
        Assert.DoesNotContain((ushort)42, applicant.UnknownFmids);
    }
}
