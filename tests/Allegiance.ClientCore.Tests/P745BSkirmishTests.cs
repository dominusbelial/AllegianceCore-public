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
    public async Task Offline_owned_host_drop_in_sees_no_last_base_objective_in_the_opening_state()
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
            Time = 1, DwCookie = uint.MaxValue, CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = 745505,
        });
        pilot.WaitForAck(10000); pilot.SendPositionReq(0);
        await Until(() => { lock (pilot.Gate) return pilot.World.MyShipId is not null; });
        pilot.SendPlayerReady(true, pilot.World.MyShipId!.Value);
        // Every side starts with exactly one Garrison: that is the normal opening state, not "our last base".
        // The objective only appears once a finale is armed or a side that had more restart bases is down to one
        // (Domingo's skirmish, 2026-10-19: "Our last Garrison: defend it" from minute one was misleading).
        await Until(() => { lock (pilot.Gate) return pilot.World.MyShipId is not null && pilot.World.Mission is not null; });
        await Task.Delay(3000);
        lock (pilot.Gate)
        {
            Assert.Empty(pilot.World.LastBaseObjectives());
            Assert.Empty(pilot.World.LastBaseObjectives(spectating: true));
        }
        Assert.Null(host.Failure);
    }
}
