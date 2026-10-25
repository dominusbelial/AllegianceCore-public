using System.Diagnostics;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P788cScriptedWarpTests
{
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Scripted_pilots_rearm_through_real_updates_after_each_warp(bool native, bool session)
    {
        Require.Artwork();
        using var server = new Srv();
        server.ShipSpawner = (core, mission, id) =>
        {
            var sector = mission.Sectors.First(s => s.Warps.Any(w => w.Partner() is not null));
            var ship = new Ship(id, core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod)), new Vector3(2000, 2000, 2000),
                Vector3.Zero, new Orientation()) { SideId = 0 };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(P788cNativeTickOrderTests.Config(native));
        server.TestMission!.Stage = MissionStage.Started;
        server.TestMission.CrowdingPenaltyEnabled = false;
        using var bot = session ? null : new Allegiance.Bot.Bot();
        using var client = session ? new ConnectionSession() : null;
        var req = new LogonRequest { CharacterName = "p788c-script", FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1 };
        if (client is not null)
        { client.Connect(server.BoundPort, req.CharacterName); client.SendLogonRequest(req); }
        else { bot!.Connect(server.BoundPort, req.CharacterName); bot.SendLogonRequest(req); }
        void Until(Func<bool> done, Action? command = null)
        {
            var timer = Stopwatch.StartNew();
            while (!done() && timer.Elapsed < TimeSpan.FromSeconds(8))
            { command?.Invoke(); server.TickSim(); Thread.Sleep(5); }
            string diagnostic = "";
            if (!done())
            {
                var live = server.Ships.FirstOrDefault()?.Ship;
                diagnostic = $"state={live?.PlayerWarpState}, next={live?.TimeNextWarp}, now={server.SimNow}, position={live?.Position}, station={live?.StationId}";
                var map = (System.Collections.IDictionary)typeof(Srv).GetField("_peerShipUpdate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(server)!;
                foreach (var value in map.Values)
                    foreach (var field in value!.GetType().GetFields())
                        diagnostic += $" {field.Name}={field.GetValue(value)}";
            }
            Assert.True(done(), "scripted native warp handshake did not complete: " + diagnostic);
        }
        if (client is not null)
        { Until(() => client.Ack is not null); client.SendPositionReq(-1); }
        Until(() => server.Ships.Count > 0);
        var ship = server.Ships.Single().Ship;
        Until(() => bot?.ShipAssignment is not null || client?.World.MyShipId is not null);
        for (int hop = 0; hop < 2; hop++)
        {
            var warp = ship.Sector!.Warps.First(w => w.Destination is not null && w.Partner() is not null);
            var source = ship.Sector;
            server.Host.HitWarpEvent(ship, warp);
            Assert.NotSame(source, ship.Sector);
            Assert.Equal(WarpState.NoUpdate, ship.PlayerWarpState);
            ship.Position = new Vector3(2000, 2000, 2000);
            ship.Velocity = Vector3.Zero;
            if (hop == 0)
            {
                // Ejection rotates the cookie while NoUpdate is still armed.
                // The scripted stream must follow it without a new SET_CLUSTER.
                ship.Mission!.Site.KillShipEvent(ship.Mission.LastUpdate, ship, null, 1f, ship.Position, Vector3.Zero);
                Until(() => ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod));
            }
            Until(() => ship.PlayerWarpState == WarpState.Ready);
        }
    }
}
