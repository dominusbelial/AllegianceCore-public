using System.Reflection;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using static Allegiance.ClientCore.MonoGame.Tests.P910TrainingIntegrationTests;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("Training")]
public sealed class P911TrainingArenaClientTests : IDisposable
{
    public P911TrainingArenaClientTests() => TrainingClock.ResetForTests();
    public void Dispose() { TrainingController.EndMission(); TrainingClock.ResetForTests(); TrainingOverlay.Current = 0; }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic, false)] [InlineData(HudStyle.Modern, false)]
    [InlineData(HudStyle.Classic, true)] [InlineData(HudStyle.Modern, true)]
    public void Connected_client_runs_native_arena_to_49_projectile_deaths_once_each(HudStyle style, bool free)
    {
        Require.Artwork(); using var host = TrainingHost.Start(free ? TrainingHost.FreeFlight : TrainingHost.PracticeArena);
        using var connection = new ConnectionSession();
        var game = Headless(connection, style);
        Set(game, "_embeddedHost", host); Set(game, "_commandCamera", new CommandCamera());
        var site = new ClientTrainingSite(game.CreateTrainingFacilities(free ? 10 : 6));
        TrainingMission lesson = free ? new Mission10(site) : new Mission6(site);
        var session = new TrainingSession(site, lesson);
        Set(game, "_trainingSession", session); host.SetActiveMission(lesson);
        host.StartBackgroundTicker();
        try
        {
            connection.Connect(host.Port, "p911");
            connection.SendLogonRequest(new LogonRequest { CharacterName = "p911", FedsrvVer = LogonRequest.MsgVer,
                ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", CrcFileList = -1,
                SteamAuthTicket = new byte[16], DrmHash = "p911" });
            Assert.True(connection.WaitForAck(15000).FValidated);
            connection.SendPositionReq(0);
            Assert.True(SpinWait.SpinUntil(() => site.HasPlayerShip, 15000));
            host.Paused = true;
            TrainingController.BeginMission(); session.Start();
            var clock = typeof(Game1).GetField("_trainingClock", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int step = 0; step < 100000 && !session.Completed; step++)
            {
                Assert.NotNull(host.PlayerShip.Sector);
                P911ArenaFiringRange.PrepareFrame(host);
                host.StepLight(); clock.SetValue(game, host.Mission.LastUpdate);
                session.Advance();
            }
            Assert.True(session.Completed, $"credits={lesson.GetKillCount()} goal={lesson.ActiveGoalPath}");
            Assert.Equal(49, lesson.GetKillCount()); // catches duplicate network/local death delivery
            Assert.Contains(site.Messages, text => text.Contains("defeat all waves", StringComparison.OrdinalIgnoreCase)
                || text.Contains("completed", StringComparison.OrdinalIgnoreCase));
        }
        finally { session.DetachClientHooks(); }
    }
}
