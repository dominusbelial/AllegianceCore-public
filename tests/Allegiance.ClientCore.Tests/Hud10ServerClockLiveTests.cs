using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>HUD-10 — the game-state clock needs native <c>MissionParams</c>
/// <c>dtGameLength</c> on the wire. <c>MissionDef.GameLengthSeconds</c> already
/// exists (igc.h:1526-1564; MissionDef.cs:98), but the server used to leave it 0;
/// it now fills it from the configured game length so
/// <c>ClientWorld.Mission.GameLengthSeconds</c> and
/// <c>ClientWorld.MissionElapsedSeconds()</c> can drive MissionClock.</summary>
[Collection("LiveNetwork")]
public sealed class Hud10ServerClockLiveTests
{
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mission_def_carries_the_configured_countdown_length_to_the_client()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = P660NextMatchLiveTests.Config(false);
        config.Games[0].GameLengthSeconds = 600;
        server.Boot(config);
        using var client = P660NextMatchLiveTests.Connect(server, "HUD10-clock", 91010);

        var mission = client.World.Mission;
        Assert.NotNull(mission);
        Assert.Equal(600f, mission!.GameLengthSeconds);
        // Both the epoch and a PING server-time sample are needed.
        client.SendPing();
        P660NextMatchLiveTests.Pump(server, () => client.World.MissionElapsedSeconds() is not null);
        Assert.NotNull(client.World.MissionElapsedSeconds());
    }
}
