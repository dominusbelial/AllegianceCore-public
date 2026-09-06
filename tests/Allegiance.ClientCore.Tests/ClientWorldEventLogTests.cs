using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;
using DataBuoyIGC = Allegiance.Core.DataBuoyIGC;
using IgcObjectType = Allegiance.Core.IgcObjectType;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Pure-domain tests for <see cref="ClientWorld.Events"/> (the transient event
/// log) and <see cref="ClientWorld.Mission"/> (mission-info state). No sockets,
/// no server: each message is constructed directly and fed through
/// <see cref="ClientWorld.Apply"/>. The event-log tests prove every event type
/// lands in the right arrival-ordered list, that the log is append-only (never
/// deduped, never overwritten), and that the <see cref="EventLog.Bound"/> window
/// actually bounds memory. The mission-info tests prove both codec paths
/// populate/refine the same snapshot and that the last-arrived <c>Stage</c> wins.
/// </summary>
public class ClientWorldEventLogTests
{
    private static ChatMessage Chat(string text, short sender = 700) => new()
    {
        Message = text,
        SidSender = sender,
        ChatTarget = (int)ChatTarget.Everyone,
        OidRecipient = 0,
        VoiceOver = 0,
        CommandID = 0,
        BObjectModel = false,
        OidTarget = -1,
        OtTarget = -1,
    };

    private static ChatBuoy BuoyChat(string text, short sender = 701) => new()
    {
        Message = text,
        Cd = new ChatData
        {
            SidSender = sender,
            ChatTarget = (int)ChatTarget.Individual,
            OidRecipient = 812,
            VoiceOver = 0,
            CommandID = 0,
            BObjectModel = false,
        },
        Db = new DataBuoyIGC([0f, 0f, 0f], 3, 0, 9, true),
    };

    [Fact]
    public void FiveEventTypes_LandInTheirOwnLists_InArrivalOrder()
    {
        var world = new ClientWorld();

        world.Apply(Chat("hi"));
        world.Apply(new Payday { DMoney = 520 });
        world.Apply(KillShip.FromVictim(11, IgcObjectType.Ship, 22));
        world.Apply(BuoyChat("target asteroid"));
        world.Apply(new PlayerReady { FReady = true, ShipId = 810 });

        Assert.Single(world.Events.Chats);
        Assert.Equal("hi", world.Events.Chats[0].Message);
        Assert.Equal((short)700, world.Events.Chats[0].SidSender);

        Assert.Single(world.Events.Paydays);
        Assert.Equal(520, world.Events.Paydays[0].DMoney);

        Assert.Single(world.Events.Kills);
        Assert.Equal((short)11, world.Events.Kills[0].ShipId);
        Assert.True(world.Events.Kills[0].BDeathCredit);

        Assert.Single(world.Events.ChatBuoys);
        Assert.Equal("target asteroid", world.Events.ChatBuoys[0].Message);

        Assert.Single(world.Events.PlayerReadyEchoes);
        Assert.True(world.Events.PlayerReadyEchoes[0].FReady);
        Assert.Equal((short)810, world.Events.PlayerReadyEchoes[0].ShipId);
    }

    [Fact]
    public void EventLog_IsAppendOnly_AcrossRepeatedCalls()
    {
        var world = new ClientWorld();

        // Repeated deliveries of the same logical event are NOT deduped or
        // overwritten — a chat re-relayed, or two payday ticks with equal money,
        // both accumulate. Pure arrival-order.
        world.Apply(new Payday { DMoney = 10 });
        world.Apply(new Payday { DMoney = 10 });
        world.Apply(Chat("repeat"));
        world.Apply(Chat("repeat"));

        Assert.Equal(2, world.Events.Paydays.Count);
        Assert.Equal(2, world.Events.Chats.Count);
        // Both appended, in order, not collapsed into one.
        Assert.Equal(world.Events.Chats[0].Message, world.Events.Chats[1].Message);
        Assert.Equal(world.Events.Paydays[0].DMoney, world.Events.Paydays[1].DMoney);
    }

    [Fact]
    public void EventLog_RespectsItsBoundaryWindow()
    {
        var world = new ClientWorld();

        // Fill one list well past the cap; the oldest entries must be evicted
        // and memory stay bounded at EventLog.Bound, newest retained.
        for (int i = 0; i < EventLog.Bound + 17; i++)
            world.Apply(new Payday { DMoney = i });

        Assert.Equal(EventLog.Bound, world.Events.Paydays.Count);
        // The first (oldest) overflow evictions drop the smallest dmoney values…
        Assert.DoesNotContain(world.Events.Paydays, p => p.DMoney == 0);
        Assert.Contains(world.Events.Paydays, p => p.DMoney == EventLog.Bound + 16);  // newest survives
    }

    [Fact]
    public void MissionDef_PopulatesAllFields_AndStage_LastArrivalWins()
    {
        var world = new ClientWorld();

        var def = new MissionDef
        {
            SzIGCStaticFile = "PCore001",
            VerIgcCore = 955,
            DwCookie = 0x2A2Au,
            ISideMissionOwner = 2,
            Stage = MissionStageMessage.StageNotStarted,
            NTems = 2,
            RgCivId = [1, 3],
        };
        world.Apply(def);

        Assert.NotNull(world.Mission);
        Assert.Equal("PCore001", world.Mission!.StaticCoreFile);
        Assert.Equal(955, world.Mission.StaticCoreVersion);
        Assert.Equal(0x2A2Au, world.Mission.Cookie);
        Assert.Equal((short)2, world.Mission.SideMissionOwner);
        Assert.Equal(MissionStageMessage.StageNotStarted, world.Mission.Stage);

        // A later MISSION_STAGE refines only the stage…
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        Assert.Equal(MissionStageMessage.StageStarted, world.Mission.Stage);
        Assert.Equal("PCore001", world.Mission.StaticCoreFile);   // core fields preserved
        Assert.Equal(955, world.Mission.StaticCoreVersion);

        // …and a still-later MISSIONDEF (whatever its stage) wins the stage by
        // virtue of being applied most recently (per the code comment: BOTH set
        // it, so last Apply owns it).
        world.Apply(new MissionDef
        {
            SzIGCStaticFile = "PCore002",
            VerIgcCore = 988,
            DwCookie = 0x7,
            ISideMissionOwner = 0,
            Stage = MissionStageMessage.StageOver,
        });
        Assert.Equal(MissionStageMessage.StageOver, world.Mission.Stage);
        Assert.Equal("PCore002", world.Mission.StaticCoreFile);
        Assert.Equal(988, world.Mission.StaticCoreVersion);
    }

    [Fact]
    public void MissionStage_BeforeAnyMissionDef_MintsASnapshot_WithDefaultCoreFields()
    {
        var world = new ClientWorld();
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });

        Assert.NotNull(world.Mission);
        Assert.Equal(MissionStageMessage.StageStarted, world.Mission!.Stage);
        Assert.Equal("", world.Mission.StaticCoreFile);   // defaults until a MISSIONDEF lands
        Assert.Equal(0, world.Mission.StaticCoreVersion);
    }

    [Fact]
    public void Mission_IsNull_UntilFirstMissionMessage()
    {
        var world = new ClientWorld();
        Assert.Null(world.Mission);   // entity traffic alone creates no mission
    }
}
