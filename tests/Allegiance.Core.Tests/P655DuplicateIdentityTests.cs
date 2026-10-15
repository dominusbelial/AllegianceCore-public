using System.Reflection;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
[Trait("Requires", "Artwork")]
public sealed class P655DuplicateIdentityTests
{
    private sealed class World : IDisposable
    {
        public readonly Srv Server = new() { SpawnInitialMinersAtBoot = false };
        public readonly List<(ConnectionId Conn, byte[] Message)> Output = [];
        public World()
        {
            Require.Artwork();
            Server.Boot(new ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
                Games = [new GameConfig { Name = "P655d", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
            });
            Server._testSendCapture = (c, p) => Output.AddRange(FedPacket.Unpack(p).Select(m => (c, m)));
        }
        public void Call(string method, params object[] args) => typeof(Srv).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Server, args);
        public void Send(int conn, IFedMessage message) => Call("ProcessDatagram", new ConnectionId((uint)conn), FedPacket.Pack([message.Encode()]));
        public LogonAck Logon(int conn, string name)
        {
            Send(conn, new LogonRequest { CharacterName = name, FedsrvVer = LogonRequest.MsgVer, CDKey = "untrusted" });
            return LogonAck.Decode(Output.Last(o => o.Conn.Value == conn && FedMessageHeader.Read(o.Message).Fmid == LogonAck.Fmid).Message);
        }
        public void Select(int conn, short side)
        {
            Send(conn, new PositionReq { SideId = side });
            Call("AdvanceSideSelection", Server.SimNow);
        }
        public void Dispose() => Server.Dispose();
    }

    [SkippableTheory]
    [InlineData(false, "mcp-probe")]
    [InlineData(false, "MCP-PROBE")]
    [InlineData(true, "mcp-probe")]
    [InlineData(true, "MCP-PROBE")]
    public void Duplicate_name_is_refused_before_allocation_whether_first_peer_pending_or_seated(bool seated, string duplicate)
    {
        using var w = new World();
        var first = w.Logon(1, "mcp-probe");
        Assert.True(first.FValidated);
        if (seated) w.Select(1, 0);
        var second = w.Logon(2, duplicate);
        Assert.False(second.FValidated);
        Assert.False(second.FRetry);
        Assert.Equal(0, second.ShipID);
        Assert.Contains("character name already in use", second.CharNameOrFailureReason);
        Assert.Equal(seated ? 0 : 1, w.Server.ConnectionScopedCacheCounts().PendingSideSelect);
        w.Select(2, 1); // A refused identity cannot proceed to seating.
        Assert.Equal(seated ? 1 : 0, w.Server.Ships.Count());
        var other = w.Logon(3, "distinct-name");
        Assert.True(other.FValidated);
        Assert.Equal(first.ShipID + 1, other.ShipID);
        if (!seated) w.Select(1, 0);
        w.Select(3, 1);
        Assert.Equal(2, w.Server.Ships.Count());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disconnect_releases_pending_or_seated_name(bool seated)
    {
        using var w = new World();
        Assert.True(w.Logon(1, "mcp-probe").FValidated);
        if (seated) w.Select(1, 0);
        w.Call("ReleasePeer", new ConnectionId(1));
        Assert.True(w.Logon(2, "MCP-PROBE").FValidated);
        w.Select(2, 0);
        Assert.Single(w.Server.Ships);
    }

    [SkippableFact]
    public void P655_balance_gate_refuses_second_distinct_unprivileged_side_zero_pick_and_auto_pick_seats_it()
    {
        using var w = new World();
        w.Server.Missions[0].Stage = MissionStage.Started;
        Assert.True(w.Logon(1, "mcp-probe-w1").FValidated);
        w.Select(1, 0);
        Assert.Single(w.Server.Ships);
        Assert.True(w.Logon(2, "mcp-probe-w2").FValidated);
        w.Select(2, 0);
        Assert.Single(w.Server.Ships);
        var refusal = Assert.Single(w.Output, o => o.Conn.Value == 2 && FedMessageHeader.Read(o.Message).Fmid == DelPositionReq.Fmid);
        Assert.Equal(DelPositionReqReason.TeamBalance, DelPositionReq.Decode(refusal.Message).Reason);
        Assert.Equal(1, w.Server.ConnectionScopedCacheCounts().PendingSideSelect);
        w.Select(2, -1);
        Assert.Equal(2, w.Server.Ships.Count());
        Assert.Equal(new short?[] { 0, 1 }, w.Server.Ships.Select(l => l.Ship.SideId).Order());
    }
}
