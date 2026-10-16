using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P692EquipmentLiveTests
{
    [SkippableTheory]
    [InlineData("mine")] [InlineData("probe")] [InlineData("chaff")]
    public void Held_dispenser_input_uses_production_callback_and_delivers_real_deployment(string kind)
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var owner = P660NextMatchLiveTests.Connect(server, "P692-owner", 69201);
        var ship = server.Ships.Single(s => s.Ship.Id == owner.World.MyShipId).Ship;
        ship.StationId = null; ship.Mission!.Stage = MissionStage.Started;
        var core = ship.Mission.Core;
        DataPartTypeIGC.Subtype subtype = kind switch
        {
            "mine" => new DataPartTypeIGC.MineLauncher(core.MineTypes.Keys.First()),
            "probe" => new DataPartTypeIGC.ProbeLauncher(core.ProbeTypes.Keys.First()),
            _ => new DataPartTypeIGC.ChaffLauncher(core.ChaffTypes.Keys.First())
        };
        var part = ship.MountPart(new(0, 0, "", "", "P692", "", 0, [], [], 0, 0, 32000, -1, 0, "", subtype), 1, 0, 240)!;
        Assert.Equal(1, part.Amount);
        ship.StateM = kind == "chaff" ? ShipControlState.ChaffFire : ShipControlState.MineFire;
        ship.Update(ship.LastUpdate + .01); ship.StateM = 0;
        Assert.Equal(0, ship.Parts.Single(p => p.PartId == part.PartId).Amount);
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(owner, () => kind switch
        {
            "mine" => owner.World.Mines.Count > 0,
            "probe" => owner.World.Probes.Count > 0,
            _ => owner.World.Chaff.Count > 0
        }));
        Assert.Contains(ship.Sector!.Models, m => kind switch { "mine" => m is Mine, "probe" => m is Probe, _ => m is Chaff });
    }

    // WinTrek/trekigc.cpp:3019-3067: all receivers get activation; only the
    // owner's allies get a critical loss message. Real TCP -> ClientWorld HUD.
    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void Teleport_activation_and_loss_reach_owner_and_enemy_with_native_criticality(bool allied)
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var owner = P660NextMatchLiveTests.Connect(server, "P692-owner", 69211);
        using var other = P660NextMatchLiveTests.Connect(server, "P692-other", 69212);
        var ship = server.Ships.Single(s => s.Ship.Id == owner.World.MyShipId).Ship;
        var viewer = server.Ships.Single(s => s.Ship.Id == other.World.MyShipId).Ship;
        Assert.NotEqual(ship.SideId, viewer.SideId);
        var mission = ship.Mission!;
        mission.Stage = MissionStage.Started;
        if (allied) mission.AlliedSidePairs.Add((ship.SideId!.Value, viewer.SideId!.Value));
        var type = mission.Core.ProbeTypes.Values.First() with
        { DtRipcord = 2, Lifespan = 100, HitPoints = 1, Radius = 2, Signature = 1, ProjectileTypeId = -1, LauncherName = "P692 Teleport" };
        var probe = ship.DeployProbe(type, mission.LastUpdate);
        // The enemy must discover the probe before the native client can
        // announce it. Move its real scanner into the deployment cluster.
        viewer.Position = probe.Position + new Vector3(100, 0, 0);
        mission.PlaceShip(viewer, ship.Sector!);
        P660NextMatchLiveTests.Pump(server, () => probe.SeenBySide(viewer.SideId!.Value));
        probe.Update(probe.DeployTime + 2);
        void WaitFor(ConnectionSession session, string text) => P660NextMatchLiveTests.Pump(server,
            () => P660NextMatchLiveTests.Read(session, () => session.World.FeedbackLines.Any(l => l.Text.Contains(text))));
        WaitFor(owner, "P692 Teleport active"); WaitFor(other, "P692 Teleport active");
        Assert.True(P660NextMatchLiveTests.Read(other, () => other.World.FeedbackLines.Single(l => l.Text.Contains("P692 Teleport active")).IsCritical));
        probe.ReceiveDamage(0, 100000, probe.DeployTime + 3, probe.Position, Vector3.Zero, null);
        WaitFor(owner, "was destroyed"); WaitFor(other, "was destroyed");
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(owner,
            () => !owner.World.Probes.ContainsKey(probe.Id)));
        lock (owner.Gate)
        {
            var lines = owner.World.FeedbackLines.Where(l => l.Text.Contains("P692 Teleport")).ToArray();
            Assert.Equal(2, lines.Length); Assert.All(lines, l => Assert.True(l.IsCritical));
            Assert.DoesNotContain(probe.Id, owner.World.Probes.Keys);
        }
        lock (other.Gate)
        {
            var line = Assert.Single(other.World.FeedbackLines, l => l.Text.Contains("was destroyed"));
            Assert.Equal(allied, line.IsCritical);
            Assert.Equal(!allied, line.Text.StartsWith(mission.Sides.Single(s => s.Id == ship.SideId).Name + " ", StringComparison.Ordinal));
        }
    }

    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void Dispenser_mount_completion_delivers_owner_only_native_ready_sound(bool chaff)
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var owner = P660NextMatchLiveTests.Connect(server, "P692-owner", 69221);
        using var other = P660NextMatchLiveTests.Connect(server, "P692-other", 69222);
        var ship = server.Ships.Single(s => s.Ship.Id == owner.World.MyShipId).Ship;
        ship.StationId = null;
        DataPartTypeIGC.Subtype subtype = chaff
            ? new DataPartTypeIGC.ChaffLauncher(ship.Mission!.Core.ChaffTypes.Keys.First())
            : new DataPartTypeIGC.ProbeLauncher(ship.Mission!.Core.ProbeTypes.Keys.First());
        ship.MountPart(new(0, 0, "", "", "P692 mount", "", 0, [], [], 0, 0, 32000, -1, 0, "", subtype), 1, 0, 0);
        var advance = typeof(Ship).GetMethod("AdvanceFlightMounts", BindingFlags.Instance | BindingFlags.NonPublic)!;
        advance.Invoke(ship, [1000f]); advance.Invoke(ship, [1000f]);
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(owner,
            () => owner.World.Events.Chats.Any(c => c.Message == "P692 mount ready." && c.VoiceOver == WeaponReadyCue.SoundId)));
        other.TakePingEchoes(); other.SendPing();
        P660NextMatchLiveTests.Pump(server, () => other.TakePingEchoes().Count > 0);
        lock (owner.Gate) Assert.Single(owner.World.Events.Chats, c => c.Message == "P692 mount ready.");
        Assert.True(P660NextMatchLiveTests.Read(other, () => other.World.Events.Chats.All(c => c.Message != "P692 mount ready.")));
    }
    [SkippableFact]
    public void Undiscovered_enemy_teleport_does_not_disclose_its_location_in_feedback()
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var owner = P660NextMatchLiveTests.Connect(server, "P692-owner", 69231);
        using var enemy = P660NextMatchLiveTests.Connect(server, "P692-enemy", 69232);
        var ship = server.Ships.Single(s => s.Ship.Id == owner.World.MyShipId).Ship;
        var viewer = server.Ships.Single(s => s.Ship.Id == enemy.World.MyShipId).Ship;
        Assert.NotEqual(ship.Sector, viewer.Sector);
        var type = ship.Mission!.Core.ProbeTypes.Values.First() with
            { DtRipcord = 0, Lifespan = 100, ProjectileTypeId = -1, LauncherName = "P692 hidden" };
        var probe = ship.DeployProbe(type, ship.Mission.LastUpdate);
        ship.Sector!.Remove(probe);
        enemy.TakePingEchoes(); enemy.SendPing();
        P660NextMatchLiveTests.Pump(server, () => enemy.TakePingEchoes().Count > 0);
        Assert.True(P660NextMatchLiveTests.Read(enemy,
            () => enemy.World.FeedbackLines.All(l => !l.Text.Contains("P692 hidden"))));
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(owner,
            () => owner.World.FeedbackLines.Count(l => l.Text.Contains("P692 hidden")) == 2));
    }

}
