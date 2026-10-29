using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P761 — the TEAM LOBBY's configuration controls' visibility/enable matrix
/// (native UpdateButtonStates, WinTrek/teamscreen.cpp:1554-1573) and the wire
/// messages the toggles post (OnButtonAutoAccept/OnButtonLockLobby/
/// OnButtonLockSides, teamscreen.cpp:2795-2815 — AUTO_ACCEPT(52)/LOCK_LOBBY(40)/
/// LOCK_SIDES(44), all already handled by the port's server: Server.Dispatch.cs
/// :1432, :1239, :1289).
/// </summary>
public class P761TeamLobbyControlsTests
{
    /// <summary>A seated own-side leader, ship 10 on side 0, pre-start mission.</summary>
    private static ClientWorld World(short stage = (short)MissionStage.NotStarted, bool lockGameOpen = false)
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromValues(10, 0, default, 0, global::Allegiance.Core.Collision.Vector3.Zero,
                Orientation.Identity, global::Allegiance.Core.Collision.Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1),
        });
        w.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "comm" });
        // The server's dummy own-ship EXPORT (ShipId = NA) is the wire source of
        // MySideId (ClientWorld's export handler) — the leader/owner checks need it.
        w.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        // P774 — PLAYERINFO(10) seeds the roster (the retired 209 is gone).
        w.Apply(new PlayerInfo { ShipId = 10, ISide = 0, CharacterName = "comm" });
        w.Apply(new SetTeamLeader { SideId = 0, ShipId = 10 });
        w.Apply(new SetMissionOwner { SideId = 0, ShipId = 10 });
        w.Apply(new MissionDef
        {
            SzIGCStaticFile = "PCore001",
            VerIgcCore = 1,
            DwCookie = uint.MaxValue,
            ISideMissionOwner = 0,
            Stage = stage,
            BLockGameOpen = lockGameOpen,
        });
        return w;
    }

    [Fact]
    public void Leader_ViewingOwnSide_SeesTeamReadyAndAutoAccept()
    {
        var controls = LobbyScreenState.BuildTeamControls(World(), viewedSideId: 0);
        Assert.True(controls.TeamReady.Visible);
        Assert.True(controls.TeamReady.Enabled);     // not in progress + leader
        Assert.True(controls.AutoAccept.Visible);
        Assert.True(controls.AutoAccept.Enabled);
        // The owner sees the lock controls (never hidden natively).
        Assert.True(controls.LockLobby.Visible);
        Assert.True(controls.LockLobby.Enabled);     // mission owner + game open
        Assert.True(controls.LockSides.Enabled);
        // Nothing checked yet.
        Assert.False(controls.TeamReady.Checked);
        Assert.False(controls.AutoAccept.Checked);
        Assert.False(controls.LockLobby.Checked);
        Assert.False(controls.LockSides.Checked);
    }

    [Fact]
    public void NonLeader_DoesNotSeeTheLeaderControls()
    {
        var w = World();
        w.Apply(new SetTeamLeader { SideId = 0, ShipId = 11 });   // someone else commands
        var controls = LobbyScreenState.BuildTeamControls(w, viewedSideId: 0);
        Assert.False(controls.TeamReady.Visible);
        Assert.False(controls.AutoAccept.Visible);
        Assert.False(controls.TeamReady.Enabled);
        // The lock pair is MISSION-OWNER scoped, not leader scoped — an owner
        // who isn't the leader still sees them (native :1568-1572).
        Assert.True(controls.LockLobby.Visible);
        Assert.True(controls.LockSides.Enabled);
    }

    [Fact]
    public void InProgress_TeamReadyIsCheckedButDisabled_AutoAcceptDisabled()
    {
        var controls = LobbyScreenState.BuildTeamControls(World(stage: (short)MissionStage.Started), viewedSideId: 0);
        Assert.True(controls.TeamReady.Checked);     // checked while in progress
        Assert.False(controls.TeamReady.Enabled);
        Assert.False(controls.AutoAccept.Enabled);   // not before the mission is in progress
    }

    [Fact]
    public void ForceReady_MirrorsInTheCheckedState()
    {
        var w = World();
        w.Apply(new ForceTeamReady { SideId = 0, FForceReady = true });
        var controls = LobbyScreenState.BuildTeamControls(w, viewedSideId: 0);
        Assert.True(controls.TeamReady.Checked);
    }

    [Fact]
    public void LockGameOpen_GatesAutoAcceptAndLockLobbyOff()
    {
        var controls = LobbyScreenState.BuildTeamControls(World(lockGameOpen: true), viewedSideId: 0);
        Assert.False(controls.AutoAccept.Enabled);   // needs !bLockGameOpen (teamscreen.cpp:1565)
        Assert.False(controls.LockLobby.Enabled);    // needs !bLockGameOpen (teamscreen.cpp:1569)
        // Lock Sides only needs the ownership.
        Assert.True(controls.LockSides.Enabled);
    }

    [Fact]
    public void AutoAcceptAndLocks_MirrorTheAuthoritativeWorldState()
    {
        var w = World();
        w.Apply(new AutoAccept { SideId = 0, FAutoAccept = true });
        w.Apply(new LockLobby { FLock = true });
        w.Apply(new LockSides { FLock = true });
        var controls = LobbyScreenState.BuildTeamControls(w, viewedSideId: 0);
        Assert.True(controls.AutoAccept.Checked);    // SideAutoAccept (teamscreen.cpp:1566)
        Assert.True(controls.LockLobby.Checked);     // GetLockLobby() (teamscreen.cpp:1570)
        Assert.True(controls.LockSides.Checked);     // GetLockSides() (teamscreen.cpp:1573)
    }

    [Fact]
    public void DisplayLines_OmitHiddenControls_KeepVisibleOnes()
    {
        var leader = LobbyScreenState.BuildTeamControls(World(), viewedSideId: 0);
        Assert.Contains(leader.DisplayLines(), l => l.Contains("TEAM READY"));
        Assert.Contains(leader.DisplayLines(), l => l.Contains("AUTO-ACCEPT"));

        var w = World();
        w.Apply(new SetTeamLeader { SideId = 0, ShipId = 11 });
        var follower = LobbyScreenState.BuildTeamControls(w, viewedSideId: 0);
        Assert.DoesNotContain(follower.DisplayLines(), l => l.Contains("TEAM READY"));
        Assert.DoesNotContain(follower.DisplayLines(), l => l.Contains("AUTO-ACCEPT"));
        Assert.Contains(follower.DisplayLines(), l => l.Contains("LOCK SIDES"));
    }

    [Fact]
    public void ViewingAnotherSide_HidesTheLeaderControls_UnlessItIsTheOwnSide()
    {
        // Native gates the pair on m_sideCurrent == GetSideID() — the side being
        // VIEWED (:1558-1564). The port's ready-check views its own side; a
        // different viewed side hides the pair even for a leader.
        var controls = LobbyScreenState.BuildTeamControls(World(), viewedSideId: 1);
        Assert.False(controls.TeamReady.Visible);
        Assert.False(controls.AutoAccept.Visible);
    }

    // --- the sent messages (teamscreen.cpp:2795-2815) -----------------------

    [Fact]
    public void AutoAccept_WireShapeCarriesFAutoAcceptAndISide()
    {
        // OnButtonAutoAccept (teamscreen.cpp:2795-2807): fAutoAccept = the
        // button's checked state, iSide = the viewer's side. The port sends the
        // same message; the codec pins the native layout.
        var encoded = new AutoAccept { SideId = 2, FAutoAccept = true }.Encode();
        var decoded = AutoAccept.Decode(encoded);
        Assert.Equal(2, decoded.SideId);
        Assert.True(decoded.FAutoAccept);
    }

    [Fact]
    public void LockLobby_WireShapeCarriesFLock()
    {
        // OnButtonLockLobby (teamscreen.cpp:2809-2817): fLock = checked state.
        var decoded = LockLobby.Decode(new LockLobby { FLock = true }.Encode());
        Assert.True(decoded.FLock);
        var off = LockLobby.Decode(new LockLobby { FLock = false }.Encode());
        Assert.False(off.FLock);
    }

    [Fact]
    public void MissionDef_DecodesBLockGameOpen_FromTheParamsBitfields()
    {
        // igc.h:1139: bLockGameOpen is bit 18 of the bool:1 block at MissionParams
        // offset 108 (MSVC LSB-first) — byte +2, bit 2. The port's server encoder
        // zeroes it; a foreign server's set bit must reach the client gate.
        var def = new MissionDef { SzIGCStaticFile = "PCore001", VerIgcCore = 1, BLockGameOpen = true };
        var buf = def.Encode();
        Assert.True(MissionDef.Decode(buf).BLockGameOpen);
        var off = new MissionDef { SzIGCStaticFile = "PCore001", VerIgcCore = 1 };
        Assert.False(MissionDef.Decode(off.Encode()).BLockGameOpen);
    }
}
