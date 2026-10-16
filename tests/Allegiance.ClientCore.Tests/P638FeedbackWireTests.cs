using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P638 — loopback wire tests for the five "player feedback" message families
/// (7 fmids): <c>TELEPORT_ACK(102)</c>, <c>BOARD_NACK(178)</c>,
/// <c>MISSILE_SPOOFED(110)</c>/<c>END_SPOOFING(111)</c>,
/// <c>ENTER_LIFEPOD(166)</c>/<c>LEAVE_SHIP(94)</c>, and <c>ASTEROID_MINED(198)</c>.
/// Each test feeds the message's REAL wire bytes through the real client
/// dispatch (<see cref="ConnectionSession.OnReceive"/> → <see cref="ClientWorld.Apply"/>,
/// no sockets — the same <see cref="ITransportSite.OnReceive"/> loop the
/// coverage test uses) and asserts the native-faithful client effect: a system
/// line, a queued sound event, a spoofed-missile retarget, a forced-lifepod
/// hull/parent change, or an asteroid ore-fraction update.
/// </summary>
public class P638FeedbackWireTests
{
    private static ServerShipUpdate MakeShip(short id) => ServerShipUpdate.FromValues(
        id, 0, default, 1u, Vector3.Zero, new Orientation(), Vector3.Zero,
        new float[3], 1f, 1f, 1f, 1f, 1f, 1f, 1f);

    private static void Feed(ConnectionSession session, IFedMessage m)
        => ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack(new[] { m.Encode() }));

    /// <summary>Give the world an own ship (id <paramref name="id"/>) via the
    /// real SET_CLUSTER(7) — the only wire source of MyShipId.</summary>
    private static void SetOwnShip(ConnectionSession session, short id)
        => Feed(session, new SetCluster { SectorId = 1, Cookie = 1, Ship = MakeShip(id) });

    private static string LastLine(ConnectionSession session)
    {
        var chats = session.World.Events.Chats;
        return chats.Count == 0 ? "" : chats[^1].Message;
    }

    // ── 1. TELEPORT_ACK(102) ───────────────────────────────────────────────

    [Fact]
    public void TeleportAck_Denied_PostsRefusalSystemLine()
    {
        var s = new ConnectionSession();
        SetOwnShip(s, 5);

        // stationID == NA (-1) → native ignores the request (appmsg.cpp:1856-1874);
        // P638 surfaces the refusal so the pilot isn't left wondering.
        Feed(s, new TeleportAck { StationId = -1, BNewHull = false });

        Assert.DoesNotContain((ushort)102, s.UnknownFmids);          // consumed
        Assert.Contains("Teleport failed", LastLine(s));
        Assert.Empty(s.World.TakeSoundEvents());                     // no arrival jump
    }

    [Fact]
    public void TeleportAck_Accepted_QueuesArrivalJump()
    {
        var s = new ConnectionSession();
        SetOwnShip(s, 5);

        // Accepted → native plays the personalJumpSound (appmsg.cpp:1901).
        Feed(s, new TeleportAck { StationId = 930, BNewHull = false });

        var ev = Assert.Single(s.World.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.TeleportArrived, ev.Kind);
        Assert.Equal(930, ev.Id);
        Assert.Equal("", LastLine(s));                               // no denial line
    }

    // ── 2. BOARD_NACK(178) ────────────────────────────────────────────────

    [Fact]
    public void BoardNack_PostsNativeBoardingRefusalLine()
    {
        var s = new ConnectionSession();
        SetOwnShip(s, 5);

        // Native: popup "Teleportation failed." (teleportpane.cpp:276-293) →
        // port system line.
        Feed(s, new BoardNack { SidRequestedParent = 777 });

        Assert.DoesNotContain((ushort)178, s.UnknownFmids);
        Assert.Equal("Teleportation failed.", LastLine(s));
    }

    // ── 3. MISSILE_SPOOFED(110) / END_SPOOFING(111) ────────────────────────

    [Fact]
    public void MissileSpoofed_RetargetsMissileOntoLastChaff()
    {
        var s = new ConnectionSession();
        var w = s.World;

        // Enter the sector before deploying its chaff. SET_CLUSTER resets
        // sector effects (P698); a previous sector's decoy cannot spoof here.
        w.Apply(new SetCluster { SectorId = 1, Cookie = 1, Ship = MakeShip(5) });

        // Deploy a chaff decoy (CREATE_CHAFF,109) — this becomes the spoof target.
        var chaffPos = new Vector3(50f, 0f, -60f);
        w.Apply(new CreateChaff { Etid = 801, P0 = chaffPos, V0 = Vector3.Zero });

        // Launch a missile (FIRE_MISSILE,70) into the same sector as our own ship.
        w.Apply(new FireMissile
        {
            MissileTypeId = 802,
            ClusterId = 1,
            Entries = new[] { new MissileLaunchData(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, -1f), Vector3.Zero, 901) },
        });

        Feed(s, new MissileSpoofed { MissileId = 901 });

        Assert.DoesNotContain((ushort)110, s.UnknownFmids);
        Assert.True(w.Missiles.TryGetValue(901, out var ms), "missile should exist");
        Assert.NotNull(ms.SpoofTargetPosition);
        Assert.Equal(chaffPos, ms.SpoofTargetPosition!.Value);   // retargeted onto the decoy
    }

    [Fact]
    public void EndSpoofing_ForgetsLastChaff_SoLaterSpoofDoesNotRetarget()
    {
        var s = new ConnectionSession();
        var w = s.World;

        w.Apply(new SetCluster { SectorId = 1, Cookie = 1, Ship = MakeShip(5) });
        w.Apply(new CreateChaff { Etid = 801, P0 = new Vector3(10f, 0f, 0f), V0 = Vector3.Zero });
        w.Apply(new FireMissile
        {
            MissileTypeId = 803,
            ClusterId = 1,
            Entries = new[] { new MissileLaunchData(Vector3.Zero, new Vector3(0f, 0f, -1f), Vector3.Zero, 911) },
        });

        // First spoof resolves against the chaff.
        Feed(s, new MissileSpoofed { MissileId = 911 });
        Assert.NotNull(w.Missiles[911].SpoofTargetPosition);

        // END_SPOOFING clears the "last created chaff" (appmsg.cpp:1686-1690).
        Feed(s, new EndSpoofing());
        Assert.DoesNotContain((ushort)111, s.UnknownFmids);

        // A second missile spoofed AFTER the window is closed must NOT resolve.
        w.Apply(new FireMissile
        {
            MissileTypeId = 804,
            ClusterId = 1,
            Entries = new[] { new MissileLaunchData(new Vector3(1f, 1f, 1f), new Vector3(0f, 0f, -1f), Vector3.Zero, 912) },
        });
        Feed(s, new MissileSpoofed { MissileId = 912 });
        Assert.Null(w.Missiles[912].SpoofTargetPosition);
    }

    // ── 4. ENTER_LIFEPOD(166) / LEAVE_SHIP(94) ──────────────────────────────

    [Fact]
    public void EnterLifepod_ForcesOwnShipIntoPod()
    {
        var s = new ConnectionSession();
        var w = s.World;
        w.LifepodHullResolver = _ => 940;                 // a known lifepod hull id
        SetOwnShip(s, 500);

        Feed(s, new EnterLifepod { ShipId = 500 });

        Assert.DoesNotContain((ushort)166, s.UnknownFmids);
        var me = w.Ships[500];
        Assert.Equal(-1, me.ParentId);                    // SetParentShip(NULL)
        Assert.Equal(940, me.HullId);                     // SetBaseHullType(lifepod)
        Assert.Contains("entered a lifepod", LastLine(s));
    }

    [Fact]
    public void LeaveShip_ForOwnShip_DetachesAndBecomesPod()
    {
        var s = new ConnectionSession();
        var w = s.World;
        w.LifepodHullResolver = _ => 941;
        SetOwnShip(s, 510);
        w.Apply(new ShipStatusMessage { ShipId = 510, ParentId = 611, State = ShipStatusMessage.StateFlying });   // currently a boarded gunner

        Feed(s, new LeaveShip { SidChild = 510 });

        Assert.DoesNotContain((ushort)94, s.UnknownFmids);
        Assert.Equal(-1, w.Ships[510].ParentId);
        Assert.Equal(941, w.Ships[510].HullId);
        Assert.Contains("left your ship", LastLine(s));
    }

    [Fact]
    public void LeaveShip_ForSomeoneElse_AnnouncesWhoLeftOurShip()
    {
        var s = new ConnectionSession();
        var w = s.World;
        SetOwnShip(s, 515);
        // A teammate currently riding our ship as a gunner (parent == our ship).
        w.Apply(new ShipStatusMessage { ShipId = 621, ParentId = 515, State = ShipStatusMessage.StateFlying });

        Feed(s, new LeaveShip { SidChild = 621 });

        Assert.DoesNotContain((ushort)94, s.UnknownFmids);
        Assert.Equal(-1, w.Ships[621].ParentId);          // detached everywhere
        Assert.Contains("has left your ship", LastLine(s));
    }

    // ── 5. ASTEROID_MINED(198) ─────────────────────────────────────────────

    [Fact]
    public void AsteroidMined_UpdatesLiveOreFraction()
    {
        var s = new ConnectionSession();
        var w = s.World;
        w.Apply(new ExportAsteroid
        {
            Asteroid = new DataAsteroidIGC(
                Signature: 0f,
                Position: [0f, 0f, 0f], Up: [0f, 1f, 0f], Forward: [0f, 0f, 1f], Rotation: [0f, 0f, 1f, 0f],
                Ore: 8600f, OreMax: 8600f, AabmCapabilities: 0,
                AsteroidId: 921, HitPoints: 4600f, Radius: 550,
                ModelName: "rock", TextureName: "rock", IconName: "rock",
                ClusterId: 922, Name: "he3", Fraction: 255),
        });

        // bpOreFraction = div-240 BytePercentage of remaining ore. 120/240 = 0.5.
        Feed(s, new AsteroidMined { ClusterId = 970, AsteroidId = 980, BpOreFraction = 120 });

        Assert.DoesNotContain((ushort)198, s.UnknownFmids);
        Assert.True(w.AsteroidLive.TryGetValue(980, out var live), "live state exists");
        Assert.Equal(0.5, (double)live.OreFraction, 3);
    }
}
