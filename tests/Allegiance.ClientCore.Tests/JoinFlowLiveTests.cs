using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

// xUnit collection definitions do not cross assemblies: AllegianceCore's own
// LiveNetworkCollection lives in its test assembly, so we need our own copy
// here. Same reason/name — these live network tests boot a real in-process
// server and drain loopback TCP on transport threads paced by wall-clock
// sleeps, so they must run sequentially relative to each other.
[CollectionDefinition("LiveNetwork", DisableParallelization = true)]
public class LiveNetworkCollection
{
}

/// <summary>
/// Integration proof against a real in-process server over real loopback TCP:
/// a <see cref="ConnectionSession"/> connects, logs on, gets its SET_CLUSTER(7)
/// assignment, and after a forward-thrust command observes its own ship's
/// position/tick advance in the entity table. This is what proves the table
/// tracks a real, ticking server — not a canned fixture.
/// </summary>
[Collection("LiveNetwork")]
public class JoinFlowLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Join Flow", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 911u, DwCookie = 0x511u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 81100000000000000ul, DrmHash = "joinflow",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    [Fact]
    public void EntityTable_TracksARealTickingServer()
    {
        using var server = new Srv();
        server.Boot(Config());

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "join_probe");
        session.SendLogonRequest(Request("join_probe"));

        // The server only drains its transport (and thus processes the logon)
        // inside a tick, so we must pump until the ACK lands — the same reason
        // PlayerReadyTests pumps before asserting on any assignment.
        int ackTicks = PumpUntil(server, () => session.Ack is not null, 4000);
        Assert.True(ackTicks < 4000, "timed out waiting for LOGONACK");
        Assert.True(session.Ack!.FValidated, $"logon rejected: {session.Ack.CharNameOrFailureReason}");

        // Wait for the SET_CLUSTER assignment to populate the world.
        int joinTicks = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(joinTicks < 4000, "timed out waiting for ship assignment");

        short id = session.World.MyShipId!.Value;

        // Just-assigned state, before any thrust, from the real server.
        var initial = session.World.Ships[id].Latest!.Position;
        uint initialTick = session.World.Ships[id].LastUpdateTick;
        short assignedSector = session.World.Ships[id].SectorId;

        Assert.Single(session.World.Ships);                       // exactly one ship so far
        Assert.Equal(session.World.MyShipId, session.World.Ships.Keys.Single());
        Assert.True(session.World.Ships[id].IsMine);
        // The server's real SET_CLUSTER sector (captured at assignment; no warp
        // means ShipStatus later reaffirms the same value).
        Assert.True(assignedSector >= 0);

        // Command nonzero forward thrust, then pump several real server ticks.
        session.CommandControls(new ControlData(0f, 0f, 0f, 1f), ShipControlState.ForwardButton);

        int moveTicks = PumpUntil(server,
            () => session.World.Ships[id].LastUpdateTick > initialTick &&
                  (session.World.Ships[id].Latest!.Position - initial).Length() > 1e-3f,
            4000);

        Assert.True(moveTicks < 4000, "ship never advanced under forward thrust");

        var final = session.World.Ships[id].Latest!.Position;
        Assert.True((final - initial).Length() > 1e-3f, "position should have changed");
        Assert.True(session.World.Ships[id].LastUpdateTick > initialTick, "sim tick should have advanced");

        // Still one entity — all three message types collapsed onto one ship.
        Assert.Single(session.World.Ships);
        Assert.Equal(id, session.World.Ships.Keys.Single());
        // Sector reaffirmed by the real ShipStatus(90); no warp happened.
        Assert.Equal(assignedSector, session.World.Ships[id].SectorId);
    }
}
