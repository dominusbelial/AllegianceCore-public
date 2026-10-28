using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Tests;

public sealed class P868DepartureSoundTests
{
    // Every branch of clintlib/clintlib.cpp:3934-4001, through the actual
    // QUIT_SIDE/QUIT_MISSION handlers. Native suppresses duplicate-local
    // departure notifications and voices duplicate-remote departures.
    [Theory]
    [InlineData(QuitSideReason.LeaderBooted, ClientSoundEventKind.SalQuit)]
    [InlineData(QuitSideReason.OwnerBooted, ClientSoundEventKind.SalQuit)]
    [InlineData(QuitSideReason.AdminBooted, ClientSoundEventKind.SalQuit)]
    [InlineData(QuitSideReason.SquadChange, ClientSoundEventKind.SalQuit)]
    [InlineData(QuitSideReason.RankLimits, ClientSoundEventKind.SalQuit)]
    [InlineData(QuitSideReason.TeamSizeLimits, ClientSoundEventKind.SalQuit)]
    [InlineData(QuitSideReason.Quit, ClientSoundEventKind.SalQuit)]
    [InlineData(QuitSideReason.DuplicateRemoteLogon, ClientSoundEventKind.SalQuit)]
    [InlineData(QuitSideReason.LinkDead, ClientSoundEventKind.SalMia)]
    [InlineData(QuitSideReason.SwitchingSides, ClientSoundEventKind.SalChangeSides)]
    [InlineData(QuitSideReason.ServerShutdown, null)]
    [InlineData(QuitSideReason.SideDestroyed, null)]
    [InlineData(QuitSideReason.DuplicateLocalLogon, null)]
    [InlineData(QuitSideReason.DuplicateCDKey, null)]
    [InlineData(QuitSideReason.BannedBySteam, null)]
    [InlineData(QuitSideReason.RandomizeSides, null)]
    [InlineData(QuitSideReason.FlushSides, ClientSoundEventKind.SalMia)]
    [InlineData(QuitSideReason.BalanceSides, ClientSoundEventKind.SalMia)]
    public void Same_side_departure_matches_native_reason_switch(QuitSideReason reason, ClientSoundEventKind? expected)
    {
        foreach (bool missionDeparture in new[] { false, true })
        {
            var world = World();
            if (missionDeparture) world.Apply(new QuitMission { ShipId = 21, Reason = reason });
            else world.Apply(new QuitSide { ShipId = 21, Reason = reason });
            var sounds = world.TakeSoundEvents();
            if (expected is { } cue)
            {
                var sound = Assert.Single(sounds);
                Assert.Equal(cue, sound.Kind);
                if (cue == ClientSoundEventKind.SalQuit)
                {
                    Assert.NotNull(sound.Voice);
                    Assert.Equal("vo_sal_mia", sound.Voice.Stem);
                    Assert.Equal("MutexSal", sound.Voice.Mutex);
                    Assert.Equal(-20, sound.Voice.GainDb);
                }
            }
            else Assert.Empty(sounds);
            // A subsequent mission departure must not repeat the side cue.
            world.Apply(new QuitMission { ShipId = 21, Reason = reason });
            Assert.Empty(world.TakeSoundEvents());
        }
    }

    [Theory]
    [InlineData(false, true, PlayerInfo.PilotTypePlayer)]
    [InlineData(true, false, PlayerInfo.PilotTypePlayer)]
    [InlineData(true, true, 0)]
    public void Native_own_stage_and_human_guards_suppress_departure(bool started, bool remote, byte pilot)
    {
        var world = World(started, pilot);
        world.Apply(new QuitSide { ShipId = remote ? (short)21 : (short)20, Reason = QuitSideReason.LeaderBooted });
        Assert.Empty(world.TakeSoundEvents());
    }

    private static ClientWorld World(bool started = true, byte pilot = PlayerInfo.PilotTypePlayer)
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        world.Apply(new SetCluster
        {
            SectorId = 0,
            Ship = ServerShipUpdate.FromValues(20, 0, default, 1, Vector3.Zero,
                Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 0),
        });
        foreach (short id in new short[] { 20, 21 })
        {
            world.Apply(new ExportShip { ShipId = id, SideId = 0 });
            world.Apply(new PlayerInfo { ShipId = id, ISide = 0, CharacterName = $"pilot{id}", PilotType = pilot });
        }
        world.Apply(new MissionStageMessage { Stage = started ? MissionStageMessage.StageStarted : MissionStageMessage.StageNotStarted });
        world.TakeSoundEvents();
        return world;
    }
}
