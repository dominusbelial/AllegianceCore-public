using System.Diagnostics;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P745BSkirmishTests
{
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed.TotalSeconds < 90) await Task.Delay(25);
        Assert.True(condition(), "Local Skirmish objective transition timed out");
    }

    [SkippableFact]
    public async Task Offline_owned_host_spectates_then_drop_in_receives_last_base_objective()
    {
        Require.Artwork();
        using var host = await SkirmishHost.StartAsync(new()
        { StartAsSpectator = true, TechMode = "research" });
        var observer = new ClientWorld();
        var observed = host.Observe(-1);
        observer.ApplyLocalObservation(observed.Objects, observed.Projectiles, observed.Missiles);
        Assert.Empty(observer.LastBaseObjectives());
        await host.SetPlayerSideAsync(0);
        using var pilot = new ConnectionSession();
        pilot.Connect(host.Port, "P745B-drop-in");
        pilot.SendLogonRequest(new()
        {
            CharacterName = "P745B-drop-in", FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            Time = 1, DwCookie = 745, CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = 745505,
        });
        pilot.WaitForAck(10000); pilot.SendPositionReq(0);
        await Until(() => { lock (pilot.Gate) return pilot.World.MyShipId is not null; });
        pilot.SendPlayerReady(true, pilot.World.MyShipId!.Value);
        await Until(() => { lock (pilot.Gate) return pilot.World.LastBaseObjectives().Any(o => !o.Defending); });
        lock (pilot.Gate)
        {
            Assert.Contains(pilot.World.LastBaseObjectives(), o => o.Defending);
            Assert.Empty(pilot.World.LastBaseObjectives(spectating: true));
        }
        Assert.Null(host.Failure);
    }
}
